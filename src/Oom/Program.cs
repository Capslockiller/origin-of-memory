using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oom.Contracts;

namespace Oom;

internal static class Program
{
    private const string RecursionGuard = "OOM_INVOKED_BY";

    private const int ExtensionLineChars = 200;

    private static readonly TimeSpan ExtensionTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly string[] GuardedCommands = ["context", "retrieve", "nudge", "flush", "sweep", "compile"];

    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    private static int Main(string[] args) => Main(args, Dispatch);

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

        VaultPaths.UseVault(Value(args, "--vault"));

        var command = Command(args);
        if (command.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (GuardedCommands.Contains(command) && Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return 0;

        if (VaultPaths.ReadVault() is not { } vault)
        {
            Console.Error.WriteLine("vault: yok");
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

            case "doctor":
                return Health(args, vault, settings, now);

            case "save":
                return RunSave(args, vault);

            case "mcp":
            {
                if (!settings.McpEnabled)
                {
                    Console.Error.WriteLine("mcp: kapalı");
                    return 1;
                }

                new Mcp(new Guards(), MakeRetrieve(vault, settings, 5), vault).Run(Console.In, Console.Out);
                return 0;
            }

            case "install":
                return RunInstall(args, vault);

            case "nudge":
                return RunNudge(args, vault, settings, now);

            default:
                PrintUsage();
                return 1;
        }
    }

    private static int Announce(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var hook = HookPayload.Read(ReadStandardInput());
        using var state = OpenStateForUpdate();
        var options = settings.Context with { PendingNotification = state?.TakeReflectionDebt() };
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

    private static int RunRetrieve(string[] args, string vault, OomSettings settings)
    {
        var top = ReadInt(args, "--top") ?? settings.Retrieve.Top;
        var retrieve = MakeRetrieve(vault, settings, top);
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
            Console.Error.WriteLine(child == 0 ? "flush: başlatılamadı" : $"flush ayrıldı: {child}");
            return 0;
        }

        using var state = OpenState();
        var result = MakeFlush(vault, settings, state).FlushSession(session, transcript, Reason(reason));
        state.RecordFlush(Clock.Now, session, reason, result.Outcome.ToString().ToLowerInvariant(), 0, 0, "runner");
        if (Reason(reason) is FlushReason.SessionEnd)
            RecordReflectionDebt(state, vault, settings, session);
        Console.WriteLine($"flush: {result.Outcome}{(result.DailyPath is null ? string.Empty : " → " + result.DailyPath)}");
        return 0;
    }

    internal static void RecordReflectionDebt(State state, string vault, OomSettings settings, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var (prompts, firstSeen) = state.ReadSessionActivity(sessionId);
        if (!Nudge.OwesReflection(vault, settings.Context.CompanionDir, firstSeen, prompts, settings.ReflectionMinPrompts))
            return;

        state.WriteHealthConcurrently([new HealthItem("hafiza", HealthLevel.Warning, "yansima-borcu", sessionId, Nudge.ReflectionDebt)]);
    }

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

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        ReportIndex(MakeRetrieve(vault, settings, settings.Retrieve.Top).Build());
        Console.WriteLine($"indeks: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms");

        var decision = new Compile(vault).MaybeCompile(now, LastCompile(state), Pending(vault, state).Count > 0);
        Console.WriteLine(decision.ShouldCompile ? $"derleme gerekli: {decision.Reason}" : $"derleme atlandı: {decision.Reason}");
        if (decision.ShouldCompile)
            DetachedProcess.Start(Executable(), ["compile", "--vault", vault], Path.GetTempPath());

        var health = new Doctor(null, moment => Snapshot(moment, vault, settings, state), null).Check(now);
        var loud = health.Items.Where(item => item.Level is not HealthLevel.Info).ToArray();
        Console.WriteLine($"doctor: kapsama {Doctor.CoverageText(health.Coverage)} · ret {health.RejectionRate:P0} · {loud.Length} uyarı");
        return 0;
    }

    internal static int RunCompile(string[] args, string vault, OomSettings settings, DateTimeOffset now, Runner? modelRunner = null)
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

            Console.WriteLine($"backend: claude {settings.Backend.Claude.Smart}");
            return 0;
        }

        if (pending.Length == 0)
        {
            Console.WriteLine("derleme: atlandı");
            return 0;
        }

        var runner = modelRunner ?? MakeRunner(vault, settings);
        foreach (var daily in pending)
        {
            var plan = Plan(compile, vault, daily, corpus, rootMap);
            var run = compile.Send(runner, plan);
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

    private static int Health(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var fixing = args.Contains("--fix");
        using var state = fixing ? OpenState() : OpenStateForReading();
        var doctor = new Doctor(null,
            moment => Snapshot(moment, vault, settings, state),
            () => { if (state is not null) Repair(vault, settings, state); });
        var result = fixing ? doctor.Fix() : doctor.Check(now);
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

        Console.WriteLine($"kapsama {Doctor.CoverageText(result.Coverage)} · ret {result.RejectionRate:P0} · bekleyen {result.Pending}");
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

    internal static int RunInstall(string[] args, string vault)
    {
        var uninstalling = args.Contains("--uninstall");
        var executable = Executable();
        var oom = Path.Combine(vault, ".oom");
        if (!uninstalling)
        {
            Directory.CreateDirectory(oom);
            WriteIfAbsent(Path.Combine(oom, "vault.json"), JsonSerializer.Serialize(new { vault, schema = 1 }) + "\n");
            WriteIfAbsent(Path.Combine(oom, "oom.json"), OomSettings.DefaultJson());
        }

        var settingsPath = Path.Combine(vault, ".claude", "settings.json");
        JsonObject root;
        try
        {
            root = (File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath, Utf8).TrimStart('﻿')) : null) as JsonObject ?? [];
        }
        catch (JsonException error)
        {
            Console.Error.WriteLine($"kurulum: {settingsPath}: {error.Message}");
            return 1;
        }

        var hooks = root["hooks"] as JsonObject ?? [];
        foreach (var registration in HookTemplates.Build(executable))
        {
            var kept = new JsonArray();
            if (hooks[registration.Event] is JsonArray existing)
                foreach (var entry in existing)
                    if (entry is not null && !IsOurs(entry, executable))
                        kept.Add(entry.DeepClone());

            if (!uninstalling)
                kept.Add(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = registration.Command,
                        ["timeout"] = registration.TimeoutSeconds
                    })
                });

            if (kept.Count > 0)
                hooks[registration.Event] = kept;
            else
                hooks.Remove(registration.Event);
        }

        if (hooks.Count > 0)
            root["hooks"] = hooks;
        else
            root.Remove("hooks");

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8);
        Console.WriteLine(uninstalling
            ? $"kaldırma: {settingsPath}"
            : $"kurulum: {oom} · {settingsPath}");
        return 0;
    }

    private static void WriteIfAbsent(string path, string content)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, content, Utf8);
    }

    private static bool IsOurs(JsonNode entry, string executable) =>
        entry["hooks"] is JsonArray commands
        && commands.Any(hook => hook?["command"]?.GetValue<string>() is { } text
            && text.Contains(executable, StringComparison.OrdinalIgnoreCase));

    private static int RunNudge(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        _ = vault;
        var hook = HookPayload.Read(ReadStandardInput());
        var session = Value(args, "--session") ?? hook.SessionId ?? "cli";
        using var state = OpenState();
        if (Nudge.Envelope(Nudge.Count(state, session, now, settings.NudgeEvery)) is { Length: > 0 } envelope)
            Console.WriteLine(envelope);

        return 0;
    }

    private static CompilePlan Plan(Compile compile, string vault, string daily, IReadOnlyList<Note> corpus, string rootMap)
    {
        var body = File.ReadAllText(daily, Utf8);
        var hubs = new RootMap(vault).Assign(body);
        return CompilePrompt.Build(Path.GetFileName(daily), body, rootMap, compile.BuildRegistry(corpus, hubs));
    }

    private static DoctorSnapshot Snapshot(DateTimeOffset now, string vault, OomSettings settings, State? state)
    {
        long Count(string sql) => state is null ? 0 : state.Scalar(sql);

        var flushes = Count("SELECT COUNT(*) FROM flush_log WHERE outcome <> 'summary'");
        var rejected = Count("SELECT COUNT(*) FROM flush_log WHERE outcome IN ('retry','parked')");
        var invalid = Concepts(vault).Count - Corpus(vault).Count;
        var database = VaultPaths.StateDatabase();
        var reach = new Doctor().CheckClaudeReachability(Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        List<DoctorObservation> observations =
        [
            state is null
                ? new DoctorObservation(new HealthItem("state", HealthLevel.Warning, "state-yok", "state.db",
                    $"Durum veritabanı yok: {database} — bu vault için hiçbir yazan komut koşmamış. Salt okunur komutlar onu yaratmaz; `oom sweep` ya da `oom doctor --fix` yaratır."), now)
                : Observe(now, "state", "state-db", "state.db", $"{database} · {(database is not null && File.Exists(database) ? new FileInfo(database).Length : 0)} bayt"),
            Observe(now, "state", "fts5-ok", "notes_fts", $"İndekste {(state is null ? 0 : Rows(state, "notes_fts"))} not"),
            Observe(now, "notes", "corpus", "concepts", $"{Corpus(vault).Count} geçerli kavram notu"),
            Observe(now, "runner", "backend", "claude", $"{settings.Backend.Claude.Fast} / {settings.Backend.Claude.Smart}"),
            Observe(now, "sweep", "roots", "sweep.roots", string.Join(" · ", settings.Sweep.Roots)),
            Observe(now, "sweep", "flush-log", "rows", $"{flushes} flush_log satırı"),
            .. settings.UnknownKeys.Select(key => new DoctorObservation(
                new HealthItem("config", HealthLevel.Warning, "unknown-key", key, $"oom.json içinde bilinmeyen anahtar: {key}"), now)),
            .. settings.LoadError is null ? Array.Empty<DoctorObservation>() : [new DoctorObservation(
                new HealthItem("config", HealthLevel.Error, "hata", "json",
                    $"oom.json okunamadı, varsayılanlar kullanılıyor — {settings.LoadError}"), now)],
            .. HealthLedger.Read().Where(o => reach.Level != HealthLevel.Info || o.Item.Component != "hooks" || o.Item.Code != "hook-failed" || o.Item.Key != reach.Key)
        ];

        var window = state?.LastCoverage();
        var coverage = window is null ? double.NaN : window.Value.Total == 0 ? 1.0 : (double)window.Value.Covered / window.Value.Total;
        return new DoctorSnapshot(observations,
            coverage,
            flushes == 0 ? 0.0 : (double)rejected / flushes,
            Pending(vault, state).Count,
            (int)Count("SELECT COUNT(*) FROM retry_queue WHERE attempts >= 5"),
            (int)Count("SELECT COUNT(*) FROM retry_queue"),
            (int)Count("SELECT COUNT(*) FROM quarantine"),
            Math.Max(0, invalid),
            window?.Total ?? 0);
    }

    private static void Repair(string vault, OomSettings settings, State state)
    {
        Directory.CreateDirectory(Path.Combine(state.WorkDirectory, "logs"));
        state.SeedCursors(new SweepRun(vault, settings, MakeFlush(vault, settings, state), state).ReadAnchors());
        state.SweepRetention(Clock.Now);
        new RootMap(vault).Regenerate();
        ReportIndex(MakeRetrieve(vault, settings, settings.Retrieve.Top).Build());
    }

    internal static string WithExtensions(string text, OomSettings settings, string vault)
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
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var gated = new Guards().Gate(line, Direction.Egress, ComponentKind.Context);
            if (gated.Findings.Contains("directive"))
                return $"[{extension.Name}] (uzanti satiri gonderim kapisinda dusuruldu: directive)\n";

            line = gated.Text.Trim();
            return string.IsNullOrEmpty(line)
                ? string.Empty
                : $"[{extension.Name}] {(line.Length > ExtensionLineChars ? line[..ExtensionLineChars] : line)}\n";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

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

    private static Retrieve MakeRetrieve(string vault, OomSettings settings, int top) => new(settings.Retrieve with
    {
        Top = top,
        TotalChars = Math.Max(settings.Retrieve.TotalChars, top * settings.Retrieve.PerNoteChars),
        VaultPath = vault,
        IndexPath = VaultPaths.StateDatabase()
    });

    private static Runner MakeRunner(string vault, OomSettings settings) =>
        new(new RunnerProfile(vault, ClaudeConfigDirectory(vault), settings.Backend.Claude));

    private static string ClaudeConfigDirectory(string vault) => Path.Combine(vault, ".oom", "claude-config");

    private static Flush MakeFlush(string vault, OomSettings settings, State? state)
    {
        var options = new FlushOptions(MinTurns: settings.Sweep.MinTurns, VaultPath: vault, RejectionPath: state?.WorkDirectory, Mode: settings.Flush.Mode, SliceTurns: settings.Flush.SliceTurns);
        return new Flush(options, null, MakeRunner(vault, settings), null, null, state);
    }

    private static State OpenState() => new(null, null, VaultPaths.EnsureStateDatabase(), StateAccess.ReadWrite);

    private static State? OpenStateForReading() => State.OpenReadOnly();

    private static State? OpenStateForUpdate() =>
        VaultIdentity.ExistingDatabase() is { } path ? new State(null, null, path, StateAccess.ReadWrite) : null;

    private static void ReportIndex(VerifyResult index)
    {
        if (index.ExitCode != 0)
            Console.Error.WriteLine($"indeks: eksik {index.Missing.Count}, fazla {index.Extra.Count}"
                + (index.Missing.Count > 0 ? $" · eksik: {string.Join(", ", index.Missing.Take(5))}" : string.Empty)
                + (index.Extra.Count > 0 ? $" · fazla: {string.Join(", ", index.Extra.Take(5))}" : string.Empty));
    }

    private static DateTimeOffset? LastCompile(State state)
    {
        var last = state.Scalar("SELECT COUNT(*) FROM daily_ingest WHERE status = 'ingested'");
        return last == 0 ? null : Clock.Now.AddHours(-1);
    }

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
            }
        }

        return parsed;
    }

    private static IReadOnlyList<string> Pending(string vault, State? state)
    {
        var directory = Path.Combine(vault, "daily");
        if (!Directory.Exists(directory))
            return [];

        var ingested = (state?.ReadColumn("SELECT name FROM daily_ingest WHERE status IN ('ingested','adopted')") ?? Array.Empty<string>())
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

    private static string Command(string[] args) => CommandLine.Command(args);

    private static string? Argument(string[] args, int index) => CommandLine.Argument(args, index);

    private static string? Value(string[] args, string name) => CommandLine.Value(args, name);

    private static int? ReadInt(string[] args, string name) =>
        int.TryParse(Value(args, name), out var value) ? value : null;

    private static string ReadStandardInput()
    {
        if (!Console.IsInputRedirected)
            return string.Empty;

        var read = Task.Run(Console.In.ReadToEnd);
        return read.Wait(TimeSpan.FromSeconds(2)) ? read.Result : string.Empty;
    }

    private static IClock Clock { get; } = SystemClock.Instance;

    private static void TrySetInputEncoding()
    {
        try
        {
            Console.InputEncoding = Utf8;
        }
        catch (Exception error) when (error is IOException or PlatformNotSupportedException)
        {
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            oom [--vault <path>] <command>

              context [--json]                                   print the session-start memory block
              nudge [--session <id>]                             count prompts, remind the session to record
              flush [--session <id>] [--reason <r>]              summarise one session into daily/
              sweep [--dry-run]                                  flush every changed transcript under sweep.roots
              compile [--dry-run]                                fold daily/ into knowledge/ concept notes
              retrieve --query <q> [--json] [--top N] [--batch <f>]   BM25 search over the notes
              doctor [--fix] [--json] [--quiet]                  health table
              save "<text>" | --session-json                     append a record to today's daily
              mcp                                                read-only MCP server over stdio
              install [--uninstall]                              write .oom/ and the four hooks in .claude/settings.json
            """);
    }
}
