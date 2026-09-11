using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
            Table(output, model, 0, null, 0, transcripts.Count, dailies.Count, dry: true, verdictSuppressed: false);
            return 0;
        }

        if (transcripts.Count == 0 && dailies.Count == 0)
        {
            output.WriteLine("ölçülecek girdi yok: --transcript-dir ve --daily-dir verin");
            return 1;
        }

        var (flush, excludedSubagent, excludedNoTurns) = MeasureFlush(transcripts, MakeRunner(options.Backend, vault, settings));
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
        // PROVENANCE.md §3: a rate over zero graded inputs (or a skipped stage --judge asked for)
        // is not a measurement, so a verdict computed from it is a claim, not evidence. Write can
        // still upgrade "ok" to "invalid" itself (the raw log it must keep failed to write), so the
        // status actually printed below is the one Write returns, not the one computed here.
        var (status, reasons) = ComputeRunStatus(flush, compile, options, judge);
        var (finalStatus, finalReasons) = Write(results, options, model, settings, flush, compile, shape, conformance, judge,
            flushDecision, compileDecision, excludedSubagent, excludedNoTurns, status, reasons);
        Table(output, model, shape, judge.Score, conformance, flush.Count, compile.Count, dry: false, verdictSuppressed: finalStatus != "ok");
        if (finalStatus == "ok")
            output.WriteLine($"\nkarar: backend.flush = {flushDecision} · backend.compile = {compileDecision} · dışlanan: {excludedSubagent} subagent, {excludedNoTurns} turnsuz");
        else
        {
            output.WriteLine();
            output.WriteLine("==================== ÖLÇÜM REDDEDİLDİ ====================");
            output.WriteLine($"run_status: {finalStatus}");
            foreach (var reason in finalReasons)
                output.WriteLine($"  - {reason}");
            output.WriteLine("hiçbir karar hesaplanmadı — yukarıdaki nedenler yüzünden bu koşu ölçüm sayılmıyor.");
            output.WriteLine($"sonuç dosyası yine de yazıldı (kanıt olarak): {results}");
            output.WriteLine("===========================================================");
        }
        foreach (var record in flush.Concat(compile).Where(record => !record.Ok))
            output.WriteLine($"  ıska {record.Source}: {record.Reason}");
        output.WriteLine($"sonuç dosyası: {results}");
        return finalStatus == "ok" ? 0 : 2;
    }
    /// <summary>Leg (a): the shipped write function grades the model; <c>Empty</c> is a correct
    /// <c>FLUSH_BOS</c>, so it counts as shape. Y-116: a sub-agent trace (<see cref="Ingest.IsSubagentTranscript"/>,
    /// reused from Y-112) or a file with no turns never reaches the model, so it is excluded, not scored a failure.</summary>
    internal static (List<BenchRecord> Records, int Subagent, int NoTurns) MeasureFlush(IReadOnlyList<string> transcripts, Runner runner)
    {
        var flush = new Flush(new FlushOptions(MinTurns: 1), null, runner);
        List<BenchRecord> records = [];
        int subagent = 0, noTurns = 0;
        foreach (var path in transcripts)
        {
            if (Ingest.IsSubagentTranscript(path)) { subagent++; continue; }
            var name = Path.GetFileName(path);
            var started = Stopwatch.StartNew();
            var session = flush.ReadSessionFile(Path.GetFileNameWithoutExtension(path), path, "claude");
            if (session is null) { noTurns++; continue; }

            var result = flush.FlushSession(session, path, FlushReason.Sweep);
            if (result.Outcome is FlushOutcome.NoNewTurns) { noTurns++; continue; }
            var ok = result.Outcome is FlushOutcome.Ok or FlushOutcome.Empty;
            records.Add(new BenchRecord(name, ok, ok ? "ok" : result.Error ?? result.Outcome.ToString(),
                started.ElapsedMilliseconds, result.Summary));
        }
        return (records, subagent, noTurns);
    }

    /// <summary>Leg (c): the shipped compile prompt in, the shipped path allowlist plus
    /// <c>=== DONE ===</c> out. The <see cref="Compile"/> instance is anchored outside the vault on
    /// purpose — its validator and its send boundary are used, and a measurement never publishes.
    /// Y-128: the send goes through <see cref="Compile.Send"/> rather than straight to the runner.
    /// The dailies this leg feeds the model are the owner's real dailies, so an ungated bench
    /// shipped exactly the credentials production compile now masks — and it would have measured
    /// a prompt the product no longer sends, which is the one thing a harness may never do.</summary>
    private static List<BenchRecord> MeasureCompile(IReadOnlyList<(string Name, string Text)> dailies, string vault, Runner runner)
    {
        var compile = new Compile(Path.Combine(Path.GetTempPath(), "oom-bench"));
        string rootMap = Read(Path.Combine(vault, "knowledge", "index.md")), registry = Registry(vault);
        List<BenchRecord> records = [];
        foreach (var (name, text) in dailies)
        {
            var started = Stopwatch.StartNew();
            var plan = CompilePrompt.Build(name, text, rootMap, registry);
            var run = compile.Send(runner, plan, "bench");
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
        var (referenceRecords, _, _) = MeasureFlush(transcripts, MakeRunner(reference, vault, settings));
        var judge = MakeRunner("claude", vault, settings);
        // Y-128: the judge prompt is two summaries of the owner's own sessions, so it leaves the
        // machine under the same rule as every other prompt. Egress redacts and never refuses,
        // and both sides cross the same gate, so a mask cannot tilt the A/B comparison.
        var guards = new Guards();
        List<double> scores = [];
        for (var index = 0; index < Math.Min(measured.Count, referenceRecords.Count); index++)
        {
            if (measured[index].Answer is not { Length: > 0 } mine || referenceRecords[index].Answer is not { Length: > 0 } theirs)
                continue;
            var mineIsA = index % 2 == 0;
            var prompt = guards.Gate(string.Join('\n',
                "Aşağıdaki iki oturum özetini birbirinden bağımsız olarak 1–5 arasında puanla.",
                "Yalnız tek satır yaz, başka hiçbir metin olmasın: A=<puan> B=<puan>",
                "--- A ---", mineIsA ? mine : theirs, "--- B ---", mineIsA ? theirs : mine),
                Direction.Egress, ComponentKind.Compile).Text;
            var run = judge.Run(prompt, ModelTier.Smart, ComponentKind.Compile, "judge");
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

    /// <summary>PROVENANCE.md §3: a rate over zero graded inputs, or a stage --judge asked for that
    /// never ran, must not read as a completed measurement. English reasons — this lands in the
    /// machine-read <c>run_status_reasons</c> JSON field, not this command's Turkish user output.
    /// "invalid" wins over "degraded" when both apply, by returning before "degraded" is ever
    /// checked.</summary>
    private static (string Status, IReadOnlyList<string> Reasons) ComputeRunStatus(
        List<BenchRecord> flush, List<BenchRecord> compile, BenchOptions options, JudgeReport judge)
    {
        List<string> invalid = [];
        if (flush.Count == 0)
            invalid.Add("flush_shape: conformance rate would be computed over zero graded inputs (flush.Count == 0)");
        if (compile.Count == 0)
            invalid.Add("compile_conformance: conformance rate would be computed over zero graded inputs (compile.Count == 0)");
        if (invalid.Count > 0)
            return ("invalid", invalid);

        if (options.Judge && judge.Status == "not run")
        {
            List<string> degraded = ["judge: --judge was requested but the judge stage did not run"];
            return ("degraded", degraded);
        }
        List<string> none = [];
        return ("ok", none);
    }

    /// <summary>PROVENANCE.md §2: at least one of <c>source_commit</c> or <c>binary_sha256</c> is
    /// required. A published single-file exe knows its own hash but not the git commit it was built
    /// from, so this is the field this command satisfies the requirement with; <c>source_commit</c>
    /// stays null. Null (never a throw) when the process has no path to hash — a bench run must not
    /// crash over its own provenance field.</summary>
    private static string? BinarySha256()
    {
        if (Environment.ProcessPath is not { } path) return null;
        try
        {
            // Streamed, not ReadAllBytes: a self-contained single-file publish is tens of megabytes
            // and a provenance field may not cost the measurement a copy of the binary in memory.
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// PROVENANCE.md §2's reproducible invocation, with the executable reduced to its file name.
    /// <see cref="Environment.CommandLine"/> carries the full path the process was launched from,
    /// and these files are committed to the repository: the reader needs the arguments, not the
    /// owner's home directory.
    /// </summary>
    private static string Invocation() =>
        string.Join(' ', ["oom", .. Environment.GetCommandLineArgs().Skip(1)]);

    /// <summary>The results file of spec 6.12, under PROVENANCE.md's contract. Every record is
    /// reduced to name, verdict, reason and duration: <c>Answer</c> holds the model's own text and
    /// stays in memory for leg (b), because a measurement artefact — the results file, and the raw
    /// log this method also writes — must never become a second copy of the material it measured.
    /// Returns the run status actually recorded: if the raw log fails to write, an "ok" status is
    /// itself downgraded to "invalid" here, after the caller already decided on "ok" — so the caller
    /// must print whatever status this method hands back, not the one it passed in.</summary>
    private (string Status, IReadOnlyList<string> Reasons) Write(string path, BenchOptions options, string model, OomSettings settings,
        List<BenchRecord> flush, List<BenchRecord> compile, double shape, double conformance, JudgeReport judge,
        string flushDecision, string compileDecision, int excludedSubagent, int excludedNoTurns,
        string status, IReadOnlyList<string> reasons)
    {
        object[] Public(List<BenchRecord> records) => records
            .Select(object (x) => new { source = x.Source, ok = x.Ok, reason = x.Reason, ms = x.Ms }).ToArray();

        var directory = Path.GetDirectoryName(path);
        if (directory is { Length: > 0 })
            Directory.CreateDirectory(directory);

        // PROVENANCE.md §2 requires a raw artifact distinct from the results file. This log carries
        // names, verdicts, reasons and durations only — never Answer, the model's own text — for the
        // same reason the class comment above gives: a measurement artefact must never become a
        // second copy of the material it measured. Written before the results JSON, as PROVENANCE.md
        // implies a summary is derived from its raw artifact rather than the other way round.
        var logName = Path.GetFileNameWithoutExtension(path) + ".log";
        var finalStatus = status;
        List<string> finalReasons = [.. reasons];
        string rawArtifact;
        try
        {
            var logPath = string.IsNullOrEmpty(directory) ? logName : Path.Combine(directory, logName);
            var lines = flush.Select(r => $"flush {r.Source} {r.Ok} {r.Reason} {r.Ms}")
                .Concat(compile.Select(r => $"compile {r.Source} {r.Ok} {r.Reason} {r.Ms}"));
            File.WriteAllLines(logPath, lines, Utf8);
            rawArtifact = logName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            rawArtifact = "none-kept";
            finalStatus = "invalid";
            finalReasons.Add($"raw_artifact: raw log could not be written: {error.Message}");
        }

        // PROVENANCE.md §3 (binding): if run_status is not "ok", every "pass" field and the whole
        // "decision" object MUST be null — never false, which would itself read as a verdict.
        bool? flushPass = finalStatus == "ok" ? shape >= ShapeThreshold : null;
        bool? compilePass = finalStatus == "ok" ? conformance >= CompileThreshold : null;
        object? decision = finalStatus == "ok" ? new { flush = flushDecision, compile = compileDecision } : null;

        var payload = new
        {
            measured_by = "oom bench",
            command = Invocation(),
            run_status = finalStatus,
            run_status_reasons = finalReasons,
            raw_artifact = rawArtifact,
            source_commit = (string?)null,
            binary_sha256 = BinarySha256(),
            // §4: structural even when nothing was substituted — a Dictionary so the JSON key is
            // literally "for" (a C# member would need the @for escape and an emit guarantee this
            // file cannot verify without a build).
            substitute = new Dictionary<string, object?> { ["active"] = false, ["for"] = null, ["reason"] = null },
            gate = 10, backend = options.Backend,
            measured = Today(),
            model = new { name = model, url = settings.Backend.Local.Url },
            thresholds = new { flush_shape = ShapeThreshold, judge = JudgeThreshold, compile_conformance = CompileThreshold },
            flush_shape = new { n = flush.Count, excluded_subagent = excludedSubagent, excluded_noturns = excludedNoTurns, conformance = shape, threshold = ShapeThreshold, pass = flushPass, records = Public(flush) },
            judge = new { status = judge.Status, score = judge.Score, why = judge.Why, threshold = JudgeThreshold },
            compile_conformance = new { n = compile.Count, conformance, threshold = CompileThreshold, pass = compilePass, records = Public(compile) },
            decision
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, Json) + "\n", Utf8);
        return (finalStatus, finalReasons);
    }

    /// <summary>The one table spec 6.12 asks the command to print; the same numbers as the JSON.
    /// <paramref name="verdictSuppressed"/> renders the same "—" a dry run does: PROVENANCE.md §3
    /// forbids asserting a verdict once <c>run_status</c> is not "ok", so nothing here may show one
    /// either.</summary>
    private static void Table(TextWriter output, string model, double shape, double? judge, double conformance, int flushCount, int compileCount, bool dry, bool verdictSuppressed)
    {
        var pending = dry ? "kuru koşum" : null;
        string Mark(bool passed) => dry || verdictSuppressed ? "—" : passed ? "evet" : "hayır";
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
