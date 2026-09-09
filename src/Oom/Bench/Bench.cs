using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Oom.Contracts;

/// <summary>What one <c>oom bench</c> run was asked to measure (spec 6.12).</summary>
public sealed record BenchOptions(string Backend = "local", int Transcripts = 30, int Dailies = 5,
    string? TranscriptDirectory = null, string? DailyDirectory = null, string? Out = null,
    bool DryRun = false, bool Judge = false);

/// <summary>One graded input: which file, whether it conformed, why not, how long it took.</summary>
public sealed record BenchRecord(string Source, bool Ok, string Reason, long Ms, string? Answer);

/// <summary>Leg (b)'s outcome; <c>Score</c> is null whenever the judge did not run.</summary>
public sealed record JudgeReport(double? Score, string Status, string Why);

/// <summary>
/// Spec 6.12's measurement, as a product command rather than a harness beside it: three legs —
/// (a) five-section shape conformance of flush summaries, (b) the double-blind judge, (c)
/// text-mode compile conformance — one decision rule, one results file, one Markdown table. It
/// measures the shipped path instead of restating it (<see cref="Flush"/> summaries,
/// <see cref="CompilePrompt"/>, <see cref="Compile.ValidateOutputPaths"/>), so the grader cannot
/// drift from the code it grades. It writes the results JSON and nothing else; the vault is read.
/// </summary>
public sealed class Bench
{
    private const double ShapeThreshold = 0.95;
    private const double JudgeThreshold = 3.5;
    private const double CompileThreshold = 0.95;
    private const string DoneMarker = "=== DONE ===";
    private const int RegistryCap = 400;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly IClock _clock;

    public Bench(IClock? clock = null) => _clock = clock ?? SystemClock.Instance;

    public int Run(BenchOptions options, string vault, OomSettings settings, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Backend is not ("claude" or "local"))
            throw new ArgumentException($"--backend yalnız 'claude' ya da 'local' olabilir: '{options.Backend}'", nameof(options));
        var transcripts = Transcripts(options, settings);
        var dailies = Dailies(options, vault);
        // Leg (a) is a fast-tier call and leg (c) a smart-tier one, exactly as flush and compile
        // make them in production, so the report names both ids: a number read against the wrong
        // model id is worse than no number at all.
        var model = options.Backend == "local"
            ? $"{settings.Backend.Local.Fast} / {settings.Backend.Local.Smart}"
            : $"{settings.Backend.Claude.Fast} / {settings.Backend.Claude.Smart}";
        var results = options.Out ?? Path.Combine(Directory.GetCurrentDirectory(), "bench", "results", Today() + ".json");
        output.WriteLine($"ölçüm: backend={options.Backend} · model={model} · transkript={transcripts.Count} · daily={dailies.Count}");

        if (options.DryRun)
        {
            output.WriteLine($"kuru koşum: model çağrılmadı, dosya yazılmadı (yazılacaktı: {results})");
            foreach (var name in transcripts.Select(Path.GetFileName).Concat(dailies.Select(daily => daily.Name)))
                output.WriteLine($"  girdi {name}");
            Table(output, model, 0, null, 0, transcripts.Count, dailies.Count, dry: true);
            return 0;
        }

        if (transcripts.Count == 0 && dailies.Count == 0)
        {
            output.WriteLine("ölçülecek girdi yok: --transcript-dir ve --daily-dir verin");
            return 1;
        }

        var flush = MeasureFlush(transcripts, MakeRunner(options.Backend, vault, settings));
        var compile = MeasureCompile(dailies, vault, MakeRunner(options.Backend, vault, settings));
        double shape = Rate(flush), conformance = Rate(compile);
        var judge = options.Judge
            ? RunJudge(options, transcripts, flush, vault, settings)
            : new JudgeReport(null, "not run", "--judge verilmedi: çift-kör yargı Claude kotası harcar (spec 6.12-b)");

        // Spec 6.12: flush needs both legs, compile only its own. A judge that did not run leaves
        // the flush list undecided rather than quietly passing it.
        var flushDecision = judge.Score is not { } score ? "undecided — leg (b) not run"
            : shape >= ShapeThreshold && score >= JudgeThreshold ? "keep" : "drop";
        var compileDecision = conformance >= CompileThreshold ? "keep" : "drop";
        Write(results, options, model, settings, flush, compile, shape, conformance, judge, flushDecision, compileDecision);
        Table(output, model, shape, judge.Score, conformance, flush.Count, compile.Count, dry: false);
        output.WriteLine($"\nkarar: backend.flush = {flushDecision} · backend.compile = {compileDecision}");
        foreach (var record in flush.Concat(compile).Where(record => !record.Ok))
            output.WriteLine($"  ıska {record.Source}: {record.Reason}");
        output.WriteLine($"sonuç dosyası: {results}");
        return 0;
    }
    /// <summary>Leg (a): the shipped write function grades the model. With no vault path, no state
    /// and no rejection directory the summary meets exactly the production validator and nothing
    /// reaches disk; <c>Empty</c> is a correct <c>FLUSH_BOS</c>, so it counts as shape.</summary>
    private static List<BenchRecord> MeasureFlush(IReadOnlyList<string> transcripts, Runner runner)
    {
        var flush = new Flush(new FlushOptions(MinTurns: 1), null, runner);
        List<BenchRecord> records = [];
        foreach (var path in transcripts)
        {
            var name = Path.GetFileName(path);
            var started = Stopwatch.StartNew();
            var session = flush.ReadSessionFile(Path.GetFileNameWithoutExtension(path), path, "claude");
            if (session is null)
            {
                records.Add(new BenchRecord(name, false, "özetlenecek tur yok", started.ElapsedMilliseconds, null));
                continue;
            }

            var result = flush.FlushSession(session, path, FlushReason.Sweep);
            var ok = result.Outcome is FlushOutcome.Ok or FlushOutcome.Empty;
            records.Add(new BenchRecord(name, ok, ok ? "ok" : result.Error ?? result.Outcome.ToString(),
                started.ElapsedMilliseconds, result.Summary));
        }
        return records;
    }

    /// <summary>Leg (c): the shipped compile prompt in, the shipped path allowlist plus
    /// <c>=== DONE ===</c> out. The <see cref="Compile"/> instance is anchored outside the vault on
    /// purpose — only its pure validator is used, and a measurement never publishes.</summary>
    private static List<BenchRecord> MeasureCompile(IReadOnlyList<(string Name, string Text)> dailies, string vault, Runner runner)
    {
        var compile = new Compile(Path.Combine(Path.GetTempPath(), "oom-bench"));
        string rootMap = Read(Path.Combine(vault, "knowledge", "index.md")), registry = Registry(vault);
        List<BenchRecord> records = [];
        foreach (var (name, text) in dailies)
        {
            var started = Stopwatch.StartNew();
            var prompt = CompilePrompt.Build(name, text, rootMap, registry).Prompt;
            var run = runner.Run(prompt, ModelTier.Smart, ComponentKind.Compile, "bench");
            string reason;
            IReadOnlyList<string> paths = [];
            if (!string.IsNullOrEmpty(run.Error))
                reason = run.Error!;
            else
                try
                {
                    paths = compile.ValidateOutputPaths(run.Text);
                    reason = paths.Count == 0 ? "kavram bloğu üretilmedi"
                        : run.Text.Contains(DoneMarker, StringComparison.Ordinal) ? "ok" : $"'{DoneMarker}' yok";
                }
                catch (ArgumentException error) { reason = error.Message; paths = []; }

            records.Add(new BenchRecord(name, reason == "ok", reason, started.ElapsedMilliseconds, string.Join(", ", paths)));
        }
        return records;
    }

    /// <summary>Leg (b), double-blind: the measured backend's summaries are paired with the other
    /// backend's over the same transcripts, the side assignment stays out of the prompt, and the
    /// Claude <c>smart</c> judge scores both sides 1–5. Off unless <c>--judge</c> is given: a pair
    /// spends Claude quota twice, once for the reference summary and once for the verdict.</summary>
    private static JudgeReport RunJudge(BenchOptions options, IReadOnlyList<string> transcripts, List<BenchRecord> measured, string vault, OomSettings settings)
    {
        var reference = options.Backend == "claude" ? "local" : "claude";
        var referenceRecords = MeasureFlush(transcripts, MakeRunner(reference, vault, settings));
        var judge = MakeRunner("claude", vault, settings);
        List<double> scores = [];
        for (var index = 0; index < Math.Min(measured.Count, referenceRecords.Count); index++)
        {
            if (measured[index].Answer is not { Length: > 0 } mine || referenceRecords[index].Answer is not { Length: > 0 } theirs)
                continue;
            var mineIsA = index % 2 == 0;
            var run = judge.Run(string.Join('\n',
                "Aşağıdaki iki oturum özetini birbirinden bağımsız olarak 1–5 arasında puanla.",
                "Yalnız tek satır yaz, başka hiçbir metin olmasın: A=<puan> B=<puan>",
                "--- A ---", mineIsA ? mine : theirs, "--- B ---", mineIsA ? theirs : mine),
                ModelTier.Smart, ComponentKind.Compile, "judge");
            var at = string.IsNullOrEmpty(run.Error) ? run.Text.IndexOf(mineIsA ? "A=" : "B=", StringComparison.OrdinalIgnoreCase) : -1;
            if (at >= 0 && at + 2 < run.Text.Length && double.TryParse(run.Text.AsSpan(at + 2, 1), CultureInfo.InvariantCulture, out var score))
                scores.Add(score);
        }
        return scores.Count == 0
            ? new JudgeReport(null, "not run", "yargıç hiçbir çiftte puan döndürmedi")
            : new JudgeReport(Math.Round(scores.Average(), 3), "run", $"{scores.Count} çift, {reference} referansına karşı çift-kör");
    }

    /// <summary>One backend only: a bench that fell back would measure the fallback, not the backend.</summary>
    private static Runner MakeRunner(string backend, string vault, OomSettings settings) => new(
        new RunnerProfile(vault, settings.ClaudeConfigDirectory(vault), settings.Backend.Claude, settings.Backend.Local,
            new Dictionary<ComponentKind, IReadOnlyList<string>>
            { [ComponentKind.Flush] = [backend], [ComponentKind.Compile] = [backend] }));

    /// <summary>Newest first, so a partial run measures the most recent material (spec 6.12).</summary>
    private static List<string> Transcripts(BenchOptions options, OomSettings settings) =>
        Files(options.TranscriptDirectory ?? settings.Sweep.Roots.FirstOrDefault(), "*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(options.Transcripts).ToList();

    /// <summary>A directory that is not there is an empty measurement, never a crash.</summary>
    private static IEnumerable<string> Files(string? directory, string pattern, SearchOption depth) =>
        directory is not null && Directory.Exists(directory) ? Directory.EnumerateFiles(directory, pattern, depth) : [];

    private static List<(string Name, string Text)> Dailies(BenchOptions options, string vault) =>
        Files(options.DailyDirectory ?? Path.Combine(vault, "daily"), "*.md", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.Ordinal).Take(options.Dailies)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path, Utf8))).ToList();

    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path, Utf8) : string.Empty;
    /// <summary>Note names only; the dedupe registry never needs a note body and never gets one.</summary>
    private static string Registry(string vault) => string.Join('\n',
        Files(Path.Combine(vault, "knowledge", "concepts"), "*.md", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).Take(RegistryCap));

    /// <summary>The results file of spec 6.12. Every record is reduced to name, verdict, reason and
    /// duration: <c>Answer</c> holds the model's own text and stays in memory for leg (b), because a
    /// measurement artefact must never become a second copy of the material it measured.</summary>
    private void Write(string path, BenchOptions options, string model, OomSettings settings, List<BenchRecord> flush,
        List<BenchRecord> compile, double shape, double conformance, JudgeReport judge, string flushDecision, string compileDecision)
    {
        object[] Public(List<BenchRecord> records) => records
            .Select(object (x) => new { source = x.Source, ok = x.Ok, reason = x.Reason, ms = x.Ms }).ToArray();
        var payload = new
        {
            gate = 10, command = "oom bench", backend = options.Backend,
            measured = Today(),
            model = new { name = model, url = settings.Backend.Local.Url },
            thresholds = new { flush_shape = ShapeThreshold, judge = JudgeThreshold, compile_conformance = CompileThreshold },
            flush_shape = new { n = flush.Count, conformance = shape, threshold = ShapeThreshold, pass = shape >= ShapeThreshold, records = Public(flush) },
            judge = new { status = judge.Status, score = judge.Score, why = judge.Why, threshold = JudgeThreshold },
            compile_conformance = new { n = compile.Count, conformance, threshold = CompileThreshold, pass = conformance >= CompileThreshold, records = Public(compile) },
            decision = new { flush = flushDecision, compile = compileDecision }
        };
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, Json) + "\n", Utf8);
    }

    /// <summary>The one table spec 6.12 asks the command to print; the same numbers as the JSON.</summary>
    private static void Table(TextWriter output, string model, double shape, double? judge, double conformance, int flushCount, int compileCount, bool dry)
    {
        var pending = dry ? "kuru koşum" : null;
        string Mark(bool passed) => dry ? "—" : passed ? "evet" : "hayır";
        void Row(string leg, int n, string value, double threshold, string mark) =>
            output.WriteLine($"| {leg} | {n} | {value} | {Fixed(threshold, 2)} | {mark} |");
        output.WriteLine($"\n# Kapı 10 — {model}\n\n| ayak | n | sonuç | eşik | geçti |\n| --- | ---: | ---: | ---: | --- |");
        Row("(a) flush beş bölümlü şekil", flushCount, pending ?? Fixed(shape, 3), ShapeThreshold, Mark(shape >= ShapeThreshold));
        Row("(b) çift-kör yargı", judge is null ? 0 : flushCount, judge is { } score ? Fixed(score, 2) : pending ?? "koşulmadı",
            JudgeThreshold, judge is null ? "—" : Mark(judge >= JudgeThreshold));
        Row("(c) compile uyumu", compileCount, pending ?? Fixed(conformance, 3), CompileThreshold, Mark(conformance >= CompileThreshold));
    }

    private string Today() => _clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Fixed(double value, int digits) => value.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static double Rate(List<BenchRecord> records) =>
        records.Count == 0 ? 0.0 : Math.Round((double)records.Count(record => record.Ok) / records.Count, 4);
}
