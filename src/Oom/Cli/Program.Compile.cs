using System.Globalization;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    internal static int RunCompile(string[] args, string vault, OomSettings settings, DateTimeOffset now, Runner? modelRunner = null)
    {
        var compile = new Compile(vault);
        if (args.Contains("--dry-run"))
            return PrintCompilePlan(compile, vault, settings, now);

        using var runLock = compile.TakeLock();
        if (runLock is null)
        {
            Console.Error.WriteLine("kilit alınamadı");
            return 3;
        }

        using var state = OpenState();
        var pending = Pending(vault, state).Take(settings.Compile.MaxDailiesPerRun).ToArray();
        var corpus = Corpus(vault);
        var rootMap = ReadIfPresent(Path.Combine(vault, "knowledge", "index.md"));

        if (pending.Length == 0)
        {
            Console.WriteLine("derleme: atlandı");
            return 0;
        }

        var runner = modelRunner ?? MakeRunner(vault, settings);
        var exitCode = 0;
        foreach (var daily in pending)
        {
            var name = Path.GetFileName(daily);
            var dailyText = File.ReadAllText(daily, Utf8);

            // Should-finding: a day that fails IsPromotable will fail it identically every
            // future run too (it is a property of the day's own text), so check it BEFORE
            // spending a model call, write the terminal status directly, and move on —
            // Pending() now treats 'low-confidence' as done, so this day leaves the queue.
            if (!Compile.IsPromotable(dailyText))
            {
                state.WriteDailyIngest(name, "low-confidence", now, digest: CompileQueue.ContentDigest(dailyText));
                Console.WriteLine($"derleme {name}: low-confidence · 0 not");
                continue;
            }

            var plan = Plan(compile, vault, daily, corpus, rootMap);
            var run = compile.Send(runner, plan);
            if (!string.IsNullOrEmpty(run.Error))
            {
                // Nit fix: also to stderr (kept on stdout too — existing Kabul tests read
                // it there). A runner error is a runner-wide failure, not a per-daily one
                // (R16), so it stops the whole batch instead of repeating the same failing
                // smoke call for every remaining daily.
                Console.WriteLine($"derleme başarısız: {run.Error} ({name})");
                Console.Error.WriteLine($"derleme başarısız: {run.Error} ({name})");
                exitCode = Math.Max(exitCode, 2);
                break;
            }

            var result = compile.Run(name, dailyText, run.Text, CompileQueue.Rejections(state, name));
            state.WriteDailyIngest(name, result.Status, now, result.Reason, CompileQueue.ContentDigest(dailyText));
            Console.WriteLine(string.IsNullOrWhiteSpace(result.Reason)
                ? $"derleme {name}: {result.Status} · {result.WrittenPaths.Count} not"
                : $"derleme {name}: {result.Status} · {result.WrittenPaths.Count} not · {result.Reason}");
            // Nit fix: an explicit precedence order via Math.Max instead of "last write
            // wins" — fail:rebuild (publication rolled back, nothing written) is the
            // worst outcome (1); quarantined/retry/partial/rejected/parked all mean "this
            // run did not fully compile the day" (2, was previously silently exit 0 for
            // fail:rebuild/quarantined/retry — should-finding).
            exitCode = result.Status switch
            {
                "fail:rebuild" => Math.Max(exitCode, 1),
                "quarantined" or "retry" or "partial" or "rejected" or "parked" => Math.Max(exitCode, 2),
                _ => exitCode,
            };
        }

        return exitCode;
    }

    // R24(b): a dry run is a read-only command — state is opened read-only (absent state
    // stays absent), so it never migrates a legacy state.db or writes a health row.
    private static int PrintCompilePlan(Compile compile, string vault, OomSettings settings, DateTimeOffset now)
    {
        using var state = OpenStateForReading();
        // Nit fix: compute the FULL pending list once and only slice it for what this run
        // will actually send — printing the slice's length as "bekleyen daily" (B8/F5's
        // honest-count goal) undercounted whenever pending exceeded maxDailiesPerRun.
        var pendingAll = Pending(vault, state);
        var pending = pendingAll.Take(settings.Compile.MaxDailiesPerRun).ToArray();
        var corpus = Corpus(vault);
        var rootMap = ReadIfPresent(Path.Combine(vault, "knowledge", "index.md"));

        var last = LastCompile(state, now);
        Console.WriteLine($"derleme planı (kuru koşum): son derleme={(last is null ? "hiç" : last.Value.ToString("O", CultureInfo.InvariantCulture))} · " +
                          $"bekleyen daily={pendingAll.Count} · bu koşumda={pending.Length} · kavram={corpus.Count}");
        foreach (var daily in pending)
        {
            var plan = Plan(compile, vault, daily, corpus, rootMap);
            Console.WriteLine($"  {Path.GetFileName(daily)}: kayıt defteri={plan.RegistryChars} karakter/{plan.RegistryLines} satır · " +
                              $"kök harita={plan.RootMapChars} · daily={plan.DailyChars} · istem={plan.PromptChars} karakter");
        }

        Console.WriteLine($"backend: claude {settings.Backend.Claude.Smart}");
        return 0;
    }

    private static CompilePlan Plan(Compile compile, string vault, string daily, IReadOnlyList<Note> corpus, string rootMap)
    {
        var body = File.ReadAllText(daily, Utf8);
        var map = new RootMap(vault);
        var hubs = map.Assign(body);
        return CompilePrompt.Build(Path.GetFileName(daily), body, rootMap, compile.BuildRegistry(corpus, hubs),
            map.HubLines, map.TagVocabulary(corpus));
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

    private static DateTimeOffset? LastCompile(State? state, DateTimeOffset now) => CompileQueue.LastCompile(state, now);

    private static IReadOnlyList<string> Pending(string vault, State? state) => CompileQueue.Pending(vault, state);
}
