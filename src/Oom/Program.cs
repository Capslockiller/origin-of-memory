using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Oom.Contracts;

namespace Oom;

/// <summary>
/// The single entry point (spec 5). It stays thin on purpose: it resolves the vault, reads
/// <c>oom.json</c> (spec 4.1), applies the recursion guard and hands the work to the
/// component that owns it, configured. No behaviour lives here.
/// </summary>
internal static class Program
{
    private const string RecursionGuard = "OOM_INVOKED_BY";

    /// <summary>An extension line is a status line, not a report: one line, and a short one (spec 2.2-4).</summary>
    private const int ExtensionLineChars = 200;

    /// <summary>SessionStart waits for no extension: a slow command is dropped, not endured.</summary>
    private static readonly TimeSpan ExtensionTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Commands a hook may trigger; inside an oom-invoked process they are silent (scar 10.1 #19).</summary>
    private static readonly string[] GuardedCommands = ["context", "retrieve", "flush", "sweep", "compile"];

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private static int Main(string[] args) => Main(args, Dispatch);

    /// <summary>
    /// Y-109: a crash that reaches here would otherwise pop the Windows Error Reporting box
    /// ("oom.exe - Uygulama Hatası") in front of whoever is running the hook. The dialog is
    /// disabled at process start and any exception the dispatcher does not handle itself is
    /// turned into a one-line stderr message and rc 1 — never a WER prompt, never a stack dump.
    /// The dispatcher is a parameter so the test can inject a broken one without crashing xunit.
    /// </summary>
    private static int Main(string[] args, Func<string[], int> dispatch)
    {
        if (OperatingSystem.IsWindows())
            SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

        try
        {
            return dispatch(args);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"hata: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        Console.OutputEncoding = Utf8;
        TrySetInputEncoding();

        // The global --vault option (spec 4): the published exe runs against any vault without
        // being copied into it. Without it the vault is vault.json next to the executable.
        VaultPaths.UseVault(Value(args, "--vault"));

        var command = Command(args);
        if (command.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (GuardedCommands.Contains(command) && Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return 0;

        // A vault that cannot be read is reported, never guessed: an unconfigured executable
        // that silently used the current directory printed an empty but successful context.
        if (VaultPaths.ReadVault() is not { } vault)
        {
            Console.Error.WriteLine("vault bulunamadı: --vault <yol> verin ya da oom.exe yanına vault.json koyun — oom doctor");
            return 1;
        }

        var settings = OomSettings.Load(vault);
        var now = Clock.Now;

        switch (command)
        {
            case "context":
                return Announce(args, vault, settings, now);

            case "retrieve":
                return RunRetrieve(args, vault, settings);

            case "flush":
                return RunFlush(args, vault, settings);

            case "sweep":
                return RunSweep(args, vault, settings, now);

            case "compile":
                return RunCompile(args, vault, settings, now);

            case "ingest":
            {
                var source = Argument(args, 0) ?? "claude";
                var sessions = new Ingest().Run(source, [], ReadInt(args, "--max"));
                Console.WriteLine($"içe aktarım: {sessions.Count} oturum");
                return 0;
            }

            case "doctor":
                return Health(args, vault, settings, now);

            case "save":
                return RunSave(args, vault);

            case "mcp":
            {
                // stdio JSON-RPC 2.0, read-only, alive only while the client holds stdin open (spec 6.9).
                if (!settings.McpEnabled)
                {
                    Console.Error.WriteLine("mcp kapalı: oom.json içindeki mcp.enabled false");
                    return 1;
                }

                new Mcp(new Guards(), MakeRetrieve(vault, settings, 5), vault).Run(Console.In, Console.Out);
                return 0;
            }

            case "install":
            {
                var target = Value(args, "--vault") ?? vault;
                var install = new Install();
                var uninstalling = args.Contains("--uninstall");
                var result = uninstalling
                    ? install.Uninstall(target)
                    : install.Run(target, args.Contains("--from-v0"), args.Contains("--dry-run"));
                if (install.MigrationReport.Length > 0) Console.Write(install.MigrationReport);
                // K7/Y-105: run from the installed copy, the exe cannot delete itself — the
                // leftover path is reported so the user knows to remove it by hand.
                var leftoverExe = result.Registrations.FirstOrDefault(r => r.StartsWith("exe-elle-sil:", StringComparison.Ordinal));
                if (uninstalling && result.Success && leftoverExe is not null)
                    Console.WriteLine($"kaldırma tamam — çalışan exe elle silinir: {leftoverExe["exe-elle-sil:".Length..]}");
                else if (uninstalling)
                    Console.WriteLine(result.Success ? "kaldırma tamam" : $"kaldırma başarısız: {result.Error}");
                else
                    Console.WriteLine(result.Success ? "kurulum tamam" : $"kurulum başarısız: {result.Error}");
                return result.Success ? 0 : 1;
            }

            case "bench":
            {
                // Spec 6.12's measurement command. The options are read here, where every other
                // command's options are read; Bench itself only measures.
                var options = new BenchOptions(
                    (Value(args, "--backend") ?? "local").Trim().ToLowerInvariant(),
                    ReadInt(args, "--transcripts") ?? 30, ReadInt(args, "--dailies") ?? 5,
                    Value(args, "--transcript-dir"), Value(args, "--daily-dir"), Value(args, "--out"),
                    args.Contains("--dry-run"), args.Contains("--judge"));
                try
                {
                    return new Bench().Run(options, vault, settings, Console.Out);
                }
                catch (ArgumentException error)
                {
                    Console.Error.WriteLine(error.Message);
                    return 1;
                }
            }

            default:
                PrintUsage();
                return 1;
        }
    }

    /// <summary>
    /// The SessionStart block (spec 6.2, 7). Called from the hook it answers in the hook's own
    /// JSON envelope; called by hand or by a phase 2 package it prints the block or <c>--json</c>.
    /// </summary>
    private static int Announce(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var hook = HookPayload.Read(ReadStandardInput());
        using var state = OpenState();
        Context.PublishStatusLine(state.ReadStatusLine(now));
        var options = settings.Context with { PendingNotification = state.ReadPendingNotification(now) };
        var result = new Context(options).Build(vault, now);
        var text = WithExtensions(result.Text, settings, vault);

        if (args.Contains("--json"))
            Console.WriteLine(JsonSerializer.Serialize(new { schema_version = 1, sections = result.Sections, chars = text.Length, text }));
        else if (hook.IsHook)
            Console.WriteLine(JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = text } }));
        else
            Console.WriteLine(text);

        return 0;
    }

    /// <summary>`retrieve --hook`, `--query` and `--batch`; all three share one ranking and one renderer (Y-038).</summary>
    private static int RunRetrieve(string[] args, string vault, OomSettings settings)
    {
        var top = ReadInt(args, "--top") ?? settings.Retrieve.Top;
        var retrieve = MakeRetrieve(vault, settings, top);

        if (args.Contains("--hook"))
        {
            var hook = HookPayload.Read(ReadStandardInput());
            var session = Value(args, "--session") ?? hook.SessionId ?? "cli";
            if (retrieve.GateReason(hook.Prompt) is { } reason)
            {
                Console.Error.WriteLine($"getirme atlandı ({reason})");
                return 0;
            }

            var hooked = retrieve.Hook(hook.Prompt, session);
            if (hooked.Hits.Count == 0)
            {
                Console.Error.WriteLine("getirme atlandı (skip:no-hit)");
                return 0;
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new { hookEventName = "UserPromptSubmit", additionalContext = hooked.Output }
            }));
            return 0;
        }

        var cli = Value(args, "--session") ?? "cli";
        if (Value(args, "--batch") is { } batch)
        {
            foreach (var line in File.ReadAllLines(batch, Utf8))
            {
                if (ReadQuery(line) is { } query)
                    Console.WriteLine(Json(query, retrieve.Query(query, cli, top)));
            }

            return 0;
        }

        var single = Value(args, "--query") ?? string.Empty;
        var result = retrieve.Query(single, cli, top);
        Console.WriteLine(args.Contains("--json") ? Json(single, result) : result.Output);
        return result.ExitCode;
    }

    /// <summary>
    /// The courtesy write path (spec 6.1, 6.3). A hook has 15 seconds and a summary takes
    /// minutes, so the hook process reads the payload, re-launches itself detached and returns;
    /// the detached child does the work. The child carries <c>--detached</c>, never the
    /// recursion guard, which would make it exit before it started.
    /// </summary>
    private static int RunFlush(string[] args, string vault, OomSettings settings)
    {
        var detached = args.Contains("--detached");
        var hook = detached ? HookPayload.Empty : HookPayload.Read(ReadStandardInput());
        var session = Value(args, "--session") ?? hook.SessionId ?? string.Empty;
        var transcript = Value(args, "--transcript") ?? hook.TranscriptPath ?? string.Empty;
        var reason = Value(args, "--reason") ?? hook.Reason;

        if (!detached && hook.IsHook)
        {
            var child = DetachedProcess.Start(Executable(),
                ["flush", "--detached", "--vault", vault, "--session", session, "--transcript", transcript, "--reason", reason],
                Path.GetTempPath());
            Console.Error.WriteLine(child == 0 ? "flush başlatılamadı — oom doctor" : $"flush ayrıldı: {child}");
            return 0;
        }

        using var state = OpenState();
        var result = MakeFlush(vault, settings, state).FlushSession(session, transcript, Reason(reason));
        state.RecordFlush(Clock.Now, session, reason, result.Outcome.ToString().ToLowerInvariant(), 0, 0, "runner");
        Console.WriteLine($"flush: {result.Outcome}{(result.DailyPath is null ? string.Empty : " → " + result.DailyPath)}");
        return 0;
    }

    /// <summary>The authoritative write path (spec 6.3): discovery, the single write function, reconciliation.</summary>
    private static int RunSweep(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var dryRun = args.Contains("--dry-run");
        using var state = OpenState();
        var report = new SweepRun(vault, settings, MakeFlush(vault, settings, state), dryRun ? null : state).Execute(dryRun);

        Console.WriteLine(dryRun ? report.Summary.Replace("tarama:", "tarama (kuru koşum):", StringComparison.Ordinal) : report.Summary);
        var outcomes = report.Result.Results
            .GroupBy(entry => entry.Outcome)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group => $"{group.Key.ToString().ToLowerInvariant()}={group.Count()}")
            .ToArray();
        if (outcomes.Length > 0)
            Console.WriteLine("sonuçlar: " + string.Join(' ', outcomes));
        foreach (var daily in report.Dailies)
            Console.WriteLine($"daily: {daily}");

        if (dryRun)
            return 0;

        // The FTS5 index over knowledge/concepts is rebuilt at the end of the run; the rebuild
        // is skipped when the concept manifest digest is unchanged (spec 6.4).
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        MakeRetrieve(vault, settings, settings.Retrieve.Top).Build();
        Console.WriteLine($"indeks: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");

        var decision = new Compile(vault).MaybeCompile(now, LastCompile(state), Pending(vault, state).Count > 0);
        Console.WriteLine(decision.ShouldCompile ? $"derleme gerekli: {decision.Reason}" : $"derleme atlandı: {decision.Reason}");
        if (decision.ShouldCompile)
            DetachedProcess.Start(Executable(), ["compile", "--vault", vault], Path.GetTempPath());

        var health = new Doctor(null, moment => Snapshot(moment, vault, settings, state), null).Check(now);
        var loud = health.Items.Where(item => item.Level is not HealthLevel.Info).ToArray();
        Console.WriteLine($"doctor: kapsama {health.Coverage:P0} · ret {health.RejectionRate:P0} · {loud.Length} uyarı");
        Notify(state, loud);
        return 0;
    }

    /// <summary>
    /// `compile --dry-run` prints the plan and calls nothing; a real compile builds the spec
    /// 6.5-3 prompt, asks the smart tier and hands the file transcript to <see cref="Compile"/>.
    /// </summary>
    private static int RunCompile(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        using var state = OpenState();
        var compile = new Compile(vault);
        var pending = Pending(vault, state).Take(settings.Compile.MaxDailiesPerRun).ToArray();
        var corpus = Corpus(vault);
        var rootMap = ReadIfPresent(Path.Combine(vault, "knowledge", "index.md"));
        var dryRun = args.Contains("--dry-run");
        var decision = compile.MaybeCompile(now, LastCompile(state), pending.Length > 0);

        if (dryRun)
        {
            Console.WriteLine($"derleme planı (kuru koşum): karar={decision.Reason} · bekleyen daily={pending.Length} · kavram={corpus.Count}");
            foreach (var daily in pending)
            {
                var plan = Plan(compile, vault, daily, corpus, rootMap);
                Console.WriteLine($"  {Path.GetFileName(daily)}: kayıt defteri={plan.RegistryChars} karakter/{plan.RegistryLines} satır · " +
                                  $"kök harita={plan.RootMapChars} · daily={plan.DailyChars} · istem={plan.PromptChars} karakter");
            }

            Console.WriteLine($"backend.compile=[{string.Join(", ", settings.Backend.Compile)}] — model çağrısı yapılmadı");
            return 0;
        }

        if (pending.Length == 0)
        {
            Console.WriteLine("derleme atlandı: bekleyen daily yok");
            return 0;
        }

        var runner = MakeRunner(vault, settings, state);
        foreach (var daily in pending)
        {
            var plan = Plan(compile, vault, daily, corpus, rootMap);
            var run = runner.Run(plan.Prompt, ModelTier.Smart, ComponentKind.Compile, "concepts");
            if (!string.IsNullOrEmpty(run.Error))
            {
                Console.WriteLine($"derleme kuyruğa alındı ({Path.GetFileName(daily)}): {run.Error}");
                continue;
            }

            var result = compile.Run(Path.GetFileName(daily), File.ReadAllText(daily, Utf8), run.Text);
            Console.WriteLine($"derleme {Path.GetFileName(daily)}: {result.Status} · {result.WrittenPaths.Count} not");
        }

        return 0;
    }

    /// <summary>The health table of spec 6.8; the exit code is always 0.</summary>
    private static int Health(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        using var state = OpenState();
        var doctor = new Doctor(null, moment => Snapshot(moment, vault, settings, state), () => Repair(vault, settings, state));
        var result = args.Contains("--fix") ? doctor.Fix() : doctor.Check(now);
        if (args.Contains("--json"))
        {
            Console.WriteLine(doctor.ToJson(result));
            return 0;
        }

        if (args.Contains("--quiet"))
        {
            var loud = result.Items.Where(item => item.Level is not HealthLevel.Info).ToArray();
            foreach (var item in loud)
                Console.Error.WriteLine($"{item.Component}: {item.Detail}");
            return 0;
        }

        Console.WriteLine($"{"bileşen",-12} {"düzey",-8} {"kod",-22} {"anahtar",-14} ayrıntı");
        foreach (var item in result.Items)
            Console.WriteLine($"{item.Component,-12} {Level(item.Level),-8} {item.Code,-22} {Short(item.Key),-14} {item.Detail}{(item.Stale ? " (eski)" : string.Empty)}");

        Console.WriteLine($"kapsama {result.Coverage:P0} · ret {result.RejectionRate:P0} · bekleyen {result.Pending}");
        return 0;
    }

    private static int RunSave(string[] args, string vault)
    {
        if (Value(args, "--session-json") is { } sessionJson)
        {
            try
            {
                var imported = new Save().SaveSessionJson(File.ReadAllText(sessionJson, Utf8));
                Console.WriteLine($"kayıt: {imported.Outcome}");
                return 0;
            }
            catch (Exception error) when (error is FormatException or ArgumentException)
            {
                Console.Error.WriteLine($"kayıt yazılmadı: {error.Message}");
                return 1;
            }
        }

        var text = Argument(args, 0) ?? ReadStandardInput();
        var written = new Save().WriteCheckpointToVault(vault, text, ["karar", "düzeltme", "devir"], Clock.Now);
        Console.WriteLine(written.Written ? "kayıt yazıldı" : $"kayıt yazılmadı: {written.Error}");
        return written.Written ? 0 : 1;
    }

    private static CompilePlan Plan(Compile compile, string vault, string daily, IReadOnlyList<Note> corpus, string rootMap)
    {
        var body = File.ReadAllText(daily, Utf8);
        var hubs = new RootMap(vault).Assign(body);
        return CompilePrompt.Build(Path.GetFileName(daily), body, rootMap, compile.BuildRegistry(corpus, hubs));
    }

    private static DoctorSnapshot Snapshot(DateTimeOffset now, string vault, OomSettings settings, State state)
    {
        var flushes = state.Scalar("SELECT COUNT(*) FROM flush_log WHERE outcome <> 'summary'");
        var rejected = state.Scalar("SELECT COUNT(*) FROM flush_log WHERE outcome IN ('retry','parked')");
        // The newest coverage row is the 7-day window the sweep just measured (spec 6.8);
        // summing every row ever written mixed windows and reported a coverage nobody has.
        var covered = state.Scalar("SELECT IFNULL((SELECT covered FROM coverage ORDER BY ts DESC LIMIT 1), 0)");
        var total = state.Scalar("SELECT IFNULL((SELECT total FROM coverage ORDER BY ts DESC LIMIT 1), 0)");
        var invalid = Concepts(vault).Count - Corpus(vault).Count;
        var database = VaultPaths.StateDatabase();
        List<DoctorObservation> observations =
        [
            Observe(now, "state", "state-db", "state.db", $"{database} · {(database is not null && File.Exists(database) ? new FileInfo(database).Length : 0)} bayt"),
            Observe(now, "state", "fts5-ok", "notes_fts", $"İndekste {Rows(state, "notes_fts")} not"),
            Observe(now, "notes", "corpus", "concepts", $"{Corpus(vault).Count} geçerli kavram notu"),
            Observe(now, "runner", "backend", "flush", $"backend.flush=[{string.Join(", ", settings.Backend.Flush)}]"),
            Observe(now, "runner", "claude-config", "isolation", settings.ClaudeConfigDirectory(vault)),
            Observe(now, "sweep", "roots", "sweep.roots", string.Join(" · ", settings.Sweep.Roots)),
            Observe(now, "calls", "call-summary", "7d", $"{state.Scalar("SELECT COUNT(*) FROM calls")} çağrı kaydı"),
            Observe(now, "sweep", "flush-log", "rows", $"{flushes} flush_log satırı"),
            .. settings.UnknownKeys.Select(key => new DoctorObservation(
                new HealthItem("config", HealthLevel.Warning, "unknown-key", key, $"oom.json içinde bilinmeyen anahtar: {key}"), now)),
            // Kurallar: fail loud. A vault whose oom.json cannot be parsed runs on the defaults,
            // and the owner has to be told that, not left to guess why sweep.roots looks wrong.
            .. settings.LoadError is null ? Array.Empty<DoctorObservation>() : [new DoctorObservation(
                new HealthItem("config", HealthLevel.Error, "hata", "json",
                    $"oom.json okunamadı, varsayılanlar kullanılıyor — {settings.LoadError}"), now)],
            .. HealthLedger.Read()
        ];

        return new DoctorSnapshot(observations,
            total == 0 ? 1.0 : (double)covered / total,
            flushes == 0 ? 0.0 : (double)rejected / flushes,
            Pending(vault, state).Count,
            (int)state.Scalar("SELECT COUNT(*) FROM retry_queue WHERE attempts >= 5"),
            (int)state.Scalar("SELECT COUNT(*) FROM retry_queue"),
            (int)state.Scalar("SELECT COUNT(*) FROM quarantine"),
            Math.Max(0, invalid));
    }

    /// <summary>`doctor --fix` (spec 6.8): idempotent repairs of what the machine can repair alone.</summary>
    private static void Repair(string vault, OomSettings settings, State state)
    {
        ClaudeIsolation.Prepare(settings.ClaudeConfigDirectory(vault));
        Directory.CreateDirectory(Path.Combine(state.WorkDirectory, "backup"));
        Directory.CreateDirectory(Path.Combine(state.WorkDirectory, "logs"));
        state.SeedCursors(new SweepRun(vault, settings, MakeFlush(vault, settings, state), state).ReadAnchors());
        state.SweepRetention(Clock.Now);
        new RootMap(vault).Regenerate();
        MakeRetrieve(vault, settings, settings.Retrieve.Top).Build();
    }

    /// <summary>The notification policy of spec 6.8: only these classes toast, once in seven days.</summary>
    private static void Notify(State state, IReadOnlyList<HealthItem> loud)
    {
        var notifier = new WindowsNotifier(state);
        foreach (var item in loud.Where(item => item.Level is HealthLevel.Error))
            notifier.Send(item.Code, item.Key, item.Detail);
    }

    /// <summary>
    /// The one extension point of spec 2.2-4: a package may add a single context line. The
    /// lines go in front of the closing sentence, which stays the last line of the block (spec 7).
    /// </summary>
    private static string WithExtensions(string text, OomSettings settings, string vault)
    {
        if (settings.Extensions.Count == 0)
            return text;

        const string closing = "Hafıza protokolü zorunludur.";
        var lines = string.Concat(settings.Extensions.Select(extension => ExtensionLine(extension, vault)));
        if (lines.Length == 0)
            return text;

        var index = text.LastIndexOf(closing, StringComparison.Ordinal);
        return index < 0 ? text + lines : text[..index] + lines + text[index..];
    }

    /// <summary>
    /// Runs one extension. <c>contextLine</c> is a command, not a literal (spec 2.2-4): it is
    /// run once with a short timeout and only its first line is taken. A command that is
    /// missing, fails, times out or prints nothing adds nothing at all - SessionStart is the
    /// one block the owner cannot work around, so an extension may never break it. The child
    /// carries the recursion guard, so an extension that shells back into oom stops there.
    /// </summary>
    private static string ExtensionLine(ExtensionSettings extension, string vault)
    {
        var command = SplitCommand(extension.ContextLine);
        if (command.Length == 0)
            return string.Empty;

        try
        {
            var result = new WindowsProcessRunner().Run(
                new ProcessRequest(command[0], command[1..], vault,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [RecursionGuard] = "context" }, string.Empty),
                ExtensionTimeout);
            if (result.TimedOut || result.ExitCode != 0)
                return string.Empty;

            var line = result.StandardOutput.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Split('\n').FirstOrDefault(candidate => candidate.Trim().Length > 0)?.Trim();
            return string.IsNullOrEmpty(line)
                ? string.Empty
                : $"[{extension.Name}] {(line.Length > ExtensionLineChars ? line[..ExtensionLineChars] : line)}\n";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Splits a configured command line into executable and arguments, honouring quotes.</summary>
    private static string[] SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in command)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return [.. parts];
    }

    /// <summary>
    /// The per-note and total character budgets of spec 6.4 size the injection block, not the
    /// ranking: a caller that asks for five notes gets five, not the three that 4.500 characters
    /// happen to hold.
    /// </summary>
    private static Retrieve MakeRetrieve(string vault, OomSettings settings, int top) => new(settings.Retrieve with
    {
        Top = top,
        TotalChars = Math.Max(settings.Retrieve.TotalChars, top * settings.Retrieve.PerNoteChars),
        VaultPath = vault,
        IndexPath = VaultPaths.StateDatabase()
    });

    private static Runner MakeRunner(string vault, OomSettings settings, State? state) => new(
        new RunnerProfile(vault, settings.ClaudeConfigDirectory(vault), settings.Backend.Claude, settings.Backend.Local,
            new Dictionary<ComponentKind, IReadOnlyList<string>>
            {
                [ComponentKind.Flush] = settings.Backend.Flush,
                [ComponentKind.Compile] = settings.Backend.Compile
            }),
        state: state);

    private static Flush MakeFlush(string vault, OomSettings settings, State? state)
    {
        // The rejection channel lives in the state root; the lossless raw channel stays in
        // memory here, so no run of this build duplicates a transcript onto disk.
        var options = new FlushOptions(MinTurns: settings.Sweep.MinTurns, VaultPath: vault, RejectionPath: state?.WorkDirectory);
        return new Flush(options, null, MakeRunner(vault, settings, state), null, new WindowsNotifier(state), state);
    }

    private static State OpenState() => new(null, null, VaultPaths.StateDatabase());

    private static DateTimeOffset? LastCompile(State state)
    {
        var last = state.Scalar("SELECT COUNT(*) FROM compile_runs WHERE status = 'ok'");
        return last == 0 ? null : Clock.Now.AddHours(-1);
    }

    /// <summary>A table that no run has created yet counts as zero, not as a doctor crash.</summary>
    private static long Rows(State state, string table)
    {
        try
        {
            return state.Scalar($"SELECT COUNT(*) FROM {table}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> Concepts(string vault)
    {
        var directory = Path.Combine(vault, "knowledge", "concepts");
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
    }

    private static IReadOnlyList<Note> Corpus(string vault)
    {
        var notes = new Notes();
        var parsed = new List<Note>();
        foreach (var path in Concepts(vault))
        {
            try
            {
                parsed.Add(notes.Parse(path, File.ReadAllText(path, Utf8)));
            }
            catch (FormatException)
            {
                // Strict frontmatter: an invalid note is not indexed and doctor counts it.
            }
        }

        return parsed;
    }

    /// <summary>Dailies no compile run has consumed yet; the anchor-carrying ones are the candidates.</summary>
    private static IReadOnlyList<string> Pending(string vault, State state)
    {
        var directory = Path.Combine(vault, "daily");
        if (!Directory.Exists(directory))
            return [];

        var ingested = state.ReadColumn("SELECT name FROM daily_ingest WHERE status = 'ingested'")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .Where(path => !ingested.Contains(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)];
    }

    private static string ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllText(path, Utf8) : string.Empty;

    private static string Executable() => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "oom.exe");

    private static FlushReason Reason(string reason) => reason switch
    {
        "precompact" => FlushReason.PreCompact,
        "sweep" => FlushReason.Sweep,
        "ingest" => FlushReason.Ingest,
        _ => FlushReason.SessionEnd
    };

    private static string Json(string query, RetrieveResult result) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        query,
        hits = result.Hits.Select(hit => new { name = hit.Name, score = hit.Score, source = hit.Source, updated = hit.Timestamp })
    });

    private static string? ReadQuery(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
            return null;
        if (text[0] != '{')
            return text;

        try
        {
            var root = JsonDocument.Parse(text).RootElement;
            return HookPayload.Field(root, "soru") ?? HookPayload.Field(root, "query");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DoctorObservation Observe(DateTimeOffset now, string component, string code, string key, string detail) =>
        new(new HealthItem(component, HealthLevel.Info, code, key, detail), now);

    private static string Level(HealthLevel level) => level switch
    {
        HealthLevel.Error => "hata",
        HealthLevel.Warning => "uyarı",
        _ => "bilgi"
    };

    private static string Short(string value) => value.Length <= 14 ? value : value[..14];

    /// <summary>The first argument that is not an option and not an option's value (spec 5).</summary>
    private static string Command(string[] args) => CommandLine.Command(args);

    /// <summary>The command's own positional arguments; <c>--vault &lt;path&gt;</c> is not one (Y-102).</summary>
    private static string? Argument(string[] args, int index) => CommandLine.Argument(args, index);

    private static string? Value(string[] args, string name) => CommandLine.Value(args, name);

    private static int? ReadInt(string[] args, string name) =>
        int.TryParse(Value(args, name), out var value) ? value : null;

    /// <summary>
    /// The hook payload on stdin, when there is one (spec 6.1). The read is bounded: a hook
    /// writes its JSON and closes the pipe at once, but a shell that hands the child an open
    /// pipe nobody writes to would otherwise hang the command forever.
    /// </summary>
    private static string ReadStandardInput()
    {
        if (!Console.IsInputRedirected)
            return string.Empty;

        var read = Task.Run(Console.In.ReadToEnd);
        return read.Wait(TimeSpan.FromSeconds(2)) ? read.Result : string.Empty;
    }

    private static IClock Clock { get; } = SystemClock.Instance;

    /// <summary>UTF-8 without BOM on stdin too; a console that refuses the change is not an error.</summary>
    private static void TrySetInputEncoding()
    {
        try
        {
            Console.InputEncoding = Utf8;
        }
        catch (Exception error) when (error is IOException or PlatformNotSupportedException)
        {
            // A redirected or absent console keeps its own encoding.
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            oom — Origin of Memory

            Kullanım: oom [--vault <yol>] <komut> [seçenekler]

              context [--json]                  Oturum başlangıcı bağlam bloğunu basar
              retrieve --hook | --query <soru> [--json] [--top N] [--batch <dosya>]
              flush [--session <id>] [--reason]  Bir oturumu özetler (kancadan ayrık koşar)
              sweep [--dry-run]                 Asıl yazma yolu: taramayı koşar
              compile [--dry-run]               Daily'leri kavram notlarına derler
              ingest claude|codex [--max N]     Arşivi geri doldurur
              doctor [--fix] [--json] [--quiet] Sağlık ve onarım
              save "<metin>" | --session-json   Daily'ye doğrudan kayıt
              mcp                               Salt okunur MCP sunucusu (stdio JSON-RPC)
              install [--uninstall] [--from-v0] [--dry-run]  Kurulum ve göç
              bench [--backend claude|local] [--transcripts N] [--dailies N]
                    [--transcript-dir <yol>] [--daily-dir <yol>] [--out <dosya>] [--judge] [--dry-run]
                                                Spec 6.12 ölçümü; sonuç bench/results/<tarih>.json
            """);
    }
}
