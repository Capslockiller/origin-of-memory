using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>One row of the <c>locks</c> table (machine, pid, timestamp) as compile reads it.</summary>
public sealed record CompileLock(string Machine, int Pid, DateTimeOffset Timestamp);

/// <summary>
/// Compile (spec 6.5): turns a daily into concept notes in text mode. The model never
/// touches the file system; every path, guard and publication decision lives here.
/// </summary>
public sealed class Compile
{
    private const string DoneMarker = "=== DONE ===";
    private const string EndFileMarker = "=== END FILE ===";
    private const int RegistryLineCap = 400;
    private const int RegistryRecentCap = 50;
    private const int CandidateCeiling = 40;
    private const int StaleLockMinutes = 120;
    private const int MaxAttempts = 3;

    private static readonly Regex ConceptPath = new(@"^knowledge/concepts/[a-z0-9-]+\.md$", RegexOptions.CultureInvariant);
    private static readonly Regex FileHeader = new(@"^===\s*FILE:\s*(?<path>.+?)\s*===\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex WordCharacter = new(@"[^\W_]", RegexOptions.CultureInvariant);
    private static readonly string[] MeasuredCompileBackends = ["claude"];
    private static int _runCounter;

    private readonly string _vault;
    private readonly string _stateRoot;
    private readonly IClock _clock;
    private readonly IFileOperations _fileOperations;
    private readonly INotifier? _notifier;
    private readonly Guards _guards;
    private readonly Notes _notes;
    private readonly RootMap _rootMap;
    private readonly Retrieve _retrieve;
    private readonly Bridge _bridge;
    private readonly CompileSettings _settings;

    public Compile() : this(LaneCVaultPaths.ResolveVault())
    {
    }

    public Compile(
        string vaultRoot,
        IClock? clock = null,
        IFileOperations? fileOperations = null,
        INotifier? notifier = null,
        Guards? guards = null,
        Notes? notes = null,
        RootMap? rootMap = null,
        Retrieve? retrieve = null,
        Bridge? bridge = null)
    {
        _vault = vaultRoot;
        _stateRoot = LaneCVaultPaths.StateRoot(vaultRoot);
        _clock = clock ?? SystemClock.Instance;
        _fileOperations = fileOperations ?? new VaultFileOperations();
        _notifier = notifier;
        _guards = guards ?? new Guards();
        _notes = notes ?? new Notes();
        _rootMap = rootMap ?? new RootMap(vaultRoot, _notes, files: _fileOperations);
        _retrieve = retrieve ?? new Retrieve(new RetrieveOptions(VaultPath: vaultRoot, IndexPath: Path.Combine(_stateRoot, "state.db")));
        _bridge = bridge ?? new Bridge(vaultRoot, _rootMap, _fileOperations);
        _settings = CompileSettings.Load(vaultRoot);
    }

    public CompileResult Run(string dailyName, string dailyText, string modelOutput) => Run(dailyName, dailyText, modelOutput, 0);

    /// <summary>
    /// Y-127: compile's send boundary. <see cref="CompilePrompt"/> renders the root map, the
    /// dedupe registry and the whole daily body, and the daily body is summarised conversation
    /// — every credential the owner ever pasted into a session lands in it. That text went to
    /// the smart model untouched while the model's reply was guarded twice on the way back, so
    /// the vault was protected and the boundary was not. The chain now runs on the prompt
    /// before the runner sees it, exactly as flush does (Y-126).
    ///
    /// Refusal on the way out: there is none, and that is deliberate. Compile is the one
    /// component whose gate can refuse, because compile turns text into files — but nothing
    /// on this path becomes a file. A directive-shaped line in the owner's own daily would
    /// otherwise quarantine his compile run before the model ever saw it, every evening, with
    /// no way out but editing the daily. The decision about whether this daily may become
    /// notes is still taken where it belongs, on admission: the gate on the model's reply and
    /// the gate on each note body below are untouched, and the daily is fenced as untrusted
    /// data in the prompt either way. So <see cref="Direction.Egress"/> redacts and sends.
    /// </summary>
    /// <param name="purpose">
    /// The call-ledger tag. It is a parameter only so <c>oom bench</c> can route its leg (c)
    /// through this same boundary without its measurement calls being filed as production
    /// compiles; every shipped compile leaves it at <c>concepts</c>.
    /// </param>
    public RunResult Send(Runner runner, CompilePlan plan, string purpose = "concepts")
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(purpose);

        var outbound = _guards.Gate(plan.Prompt, Direction.Egress, ComponentKind.Compile);
        RecordBoundary(plan.Daily, outbound);
        return runner.Run(outbound.Text, ModelTier.Smart, ComponentKind.Compile, purpose);
    }

    /// <summary>
    /// One compile run over a single daily. All-or-nothing: unless every note passes the
    /// note validator and the guard chain, and unless the root map and the search index are
    /// rebuilt, nothing is published and the daily stays unconsumed (scars Y-029, Y-030).
    /// </summary>
    public CompileResult Run(string dailyName, string dailyText, string modelOutput, int attempts)
    {
        ArgumentNullException.ThrowIfNull(dailyName);
        ArgumentNullException.ThrowIfNull(dailyText);
        ArgumentNullException.ThrowIfNull(modelOutput);

        // A source that carries an unmeasured fallback backend at normal confidence never
        // reaches the compiler at all, so it is checked before the run lock is taken (Y-016).
        if (!IsPromotable(dailyText))
            return new CompileResult("low-confidence", [], false, false);

        using var runLock = TakeLock();
        if (runLock is null)
            return new CompileResult("skip:locked", [], false, false);

        // The guard chain runs before anything is parsed or promoted: a directive-shaped
        // reply quarantines the whole run rather than only warning about it (Y-027).
        var gated = _guards.Gate(modelOutput, Direction.Out, ComponentKind.Compile);
        RecordBoundary(dailyName, gated);
        if (gated.Refused)
            return new CompileResult("quarantined", [], false, false, Quarantine(dailyName, modelOutput, gated.Findings));

        if (!gated.Text.Contains(DoneMarker, StringComparison.Ordinal))
            return new CompileResult("retry", [], false, false);

        IReadOnlyDictionary<string, string> files;
        try
        {
            files = ParseFiles(gated.Text);
            foreach (var (path, body) in files)
            {
                var note = _guards.Gate(body, Direction.Out, ComponentKind.Compile);
                RecordBoundary(dailyName, note);
                if (note.Refused)
                    return new CompileResult("quarantined", [], false, false, Quarantine(dailyName, modelOutput, note.Findings));
                _notes.Validate(_notes.Parse(path, note.Text));
            }
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            return Rejected(dailyName, attempts, error.Message);
        }

        var publication = Publish(dailyName, files, failDuringRebuild: false);
        if (publication.SourcePending)
            return new CompileResult("fail:rebuild", [], false, false);

        AppendLog(dailyName, publication.VisibleNotes);
        _bridge.Refresh();
        return new CompileResult("ok", publication.VisibleNotes, true, true);
    }

    /// <summary>
    /// Evening hour <em>or</em> a successful compile at least <c>minIntervalHours</c> ago; a fresh
    /// install has no interval to fall back on and waits for the evening (scar Y-093).
    /// </summary>
    public CompileDecision MaybeCompile(DateTimeOffset now, DateTimeOffset? lastSuccess, bool hasPending)
    {
        if (!hasPending)
            return new CompileDecision(false, "skip:no-pending");
        if (now.Hour >= _settings.EveningHour)
            return new CompileDecision(true, "ok:evening");
        if (lastSuccess is null)
            return new CompileDecision(false, "skip:fresh-install");
        return now - lastSuccess.Value >= TimeSpan.FromHours(_settings.MinIntervalHours)
            ? new CompileDecision(true, "ok:interval")
            : new CompileDecision(false, "skip:early");
    }

    /// <summary>
    /// Every <c>=== FILE: … ===</c> path in the reply. One path outside
    /// <c>knowledge/concepts/&lt;slug&gt;.md</c> — a subdirectory, a traversal or an absolute
    /// path — rejects the whole output (scars Y-026, Y-028).
    /// </summary>
    public IReadOnlyList<string> ValidateOutputPaths(string modelOutput)
    {
        ArgumentNullException.ThrowIfNull(modelOutput);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines(modelOutput))
        {
            var header = FileHeader.Match(line);
            if (!header.Success)
                continue;
            var path = header.Groups["path"].Value.Trim();
            if (!ConceptPath.IsMatch(path))
                throw new ArgumentException($"Model çıktısındaki '{path}' yolu izin verilen kavram deseniyle eşleşmiyor; bütün koşum reddedildi.", nameof(modelOutput));
            if (!seen.Add(path))
                throw new ArgumentException($"'{path}' yolu tek koşumda iki kez yazılıyor; bütün koşum reddedildi.", nameof(modelOutput));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// Bounded dedupe registry for the prompt (spec 6.5-3, scar Y-016 of the v0 list): the
    /// concepts of the hubs this daily touches plus the fifty most recently updated notes,
    /// at most 400 <c>name | aliases</c> lines. When it is cut, the first line says so; the
    /// ceiling is never raised to silence the warning.
    /// </summary>
    public string BuildRegistry(IReadOnlyList<Note> notes, IReadOnlyList<string> assignedHubs)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(assignedHubs);
        var hubs = assignedHubs.ToHashSet(StringComparer.Ordinal);
        var selected = new List<Note>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in notes.Where(note => _rootMap.HubsForNote(note).Any(hubs.Contains)))
            if (seen.Add(note.Name))
                selected.Add(note);
        foreach (var note in notes.OrderByDescending(note => note.Updated).ThenBy(note => note.Name, StringComparer.Ordinal).Take(RegistryRecentCap))
            if (seen.Add(note.Name))
                selected.Add(note);

        var builder = new StringBuilder();
        if (selected.Count > RegistryLineCap)
            builder.Append("registry truncated: ").Append(RegistryLineCap).Append(" of ").Append(notes.Count).Append('\n');
        foreach (var note in selected.Take(RegistryLineCap))
            builder.Append(note.Name).Append(" | ").Append(string.Join(", ", note.Aliases)).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Duplicate/update candidates for an incoming note. The search covers the whole local
    /// corpus — the 400-line prompt registry is a prompt budget, never the decision set
    /// (scar Y-025): an exact title/alias dictionary pass plus a bounded overlap ranking.
    /// </summary>
    public IReadOnlyList<string> SelectCandidates(Note incoming, IReadOnlyList<Note> corpus)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(corpus);
        var haystack = string.Join('\n', [incoming.Title, .. incoming.Aliases, incoming.Body]);
        var incomingTokens = Tokens(haystack);
        var dictionary = new List<string>();
        var ranked = new List<(string Name, double Score)>();
        foreach (var note in corpus)
        {
            if (string.Equals(note.Name, incoming.Name, StringComparison.Ordinal))
                continue;
            if (ContainsPhrase(haystack, note.Title) || note.Aliases.Any(alias => ContainsPhrase(haystack, alias)))
            {
                dictionary.Add(note.Name);
                continue;
            }
            var noteTokens = Tokens(note.Title + "\n" + string.Join('\n', note.Aliases) + "\n" + note.Body);
            var shared = noteTokens.Count(incomingTokens.Contains);
            if (shared > 0)
                ranked.Add((note.Name, shared / Math.Sqrt(noteTokens.Count)));
        }
        return
        [
            .. dictionary,
            .. ranked.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Take(CandidateCeiling).Select(candidate => candidate.Name)
        ];
    }

    /// <summary>
    /// Atomic publication (spec 6.5-6): temp file plus <c>File.Replace</c>, previous versions
    /// into <c>backup/&lt;run&gt;/</c>, then the root map and the search index. If any step
    /// fails everything is rolled back and the daily stays pending — a half-written run must
    /// never consume its source (scars Y-029, Y-030).
    /// </summary>
    public PublicationResult Publish(string dailyName, IReadOnlyDictionary<string, string> files, bool failDuringRebuild)
    {
        ArgumentNullException.ThrowIfNull(dailyName);
        ArgumentNullException.ThrowIfNull(files);
        var run = RunId(dailyName);
        var backupRoot = Path.Combine(_stateRoot, "backup", run);
        var replaced = new List<(string Target, string? Backup)>();
        var written = new List<string>();
        try
        {
            foreach (var (relative, content) in files)
            {
                var target = ResolveVaultPath(relative);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = Path.Combine(backupRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                }
                LaneCVaultPaths.WriteAtomic(target, content, _fileOperations);
                replaced.Add((target, backup));
                written.Add(relative);
            }
            Rebuild(failDuringRebuild);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var code = failDuringRebuild ? "rebuild-failed" : "publication-failed";
            HealthLedger.Record(new HealthItem("compile", HealthLevel.Error, code, dailyName,
                $"Yayın yenilemesi başarısız oldu: {error.Message}"), _clock.Now);
            RollBack(replaced);
            Discard(backupRoot);
            return new PublicationResult(true, true, true, []);
        }
        Discard(backupRoot);
        return new PublicationResult(true, false, false, written);
    }

    /// <summary>
    /// An approved correction updates the note that carries the stale claim and stamps it with
    /// a <c>Güncelleme (YYYY-MM-DD)</c> line; a second, contradicting note is never opened and
    /// the invalidated claim does not survive in the body (spec 7, scar Y-022).
    /// </summary>
    public Note ApplyCorrection(Note stale, Note replacement, string source)
    {
        ArgumentNullException.ThrowIfNull(stale);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(source);
        var corrected = CorrectionDate(source, stale, replacement);
        var body = new StringBuilder(replacement.Body.TrimEnd());
        body.Append("\n\nGüncelleme (").Append(corrected.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("): Önceki iddia ").Append(source).Append(" ile geçersiz kılındı; geçerli hâli yukarıdadır.");
        return new Note(
            stale.Name,
            stale.Title,
            Union(stale.Aliases, replacement.Aliases),
            Union(stale.Tags, replacement.Tags),
            Union(stale.Sources, replacement.Sources, [source]),
            stale.Created,
            corrected,
            body.ToString());
    }

    /// <summary>A lock row older than two hours whose owning process is gone is taken over (spec 6.5-1).</summary>
    internal static string ResolveLock(CompileLock? existing, DateTimeOffset now, Func<int, bool> processAlive)
    {
        if (existing is null)
            return "ok";
        if (now - existing.Timestamp >= TimeSpan.FromMinutes(StaleLockMinutes) && !processAlive(existing.Pid))
            return "warn:stale-lock";
        return "skip:locked";
    }

    /// <summary>The file transcript, path-validated first so that one bad path rejects everything.</summary>
    internal IReadOnlyDictionary<string, string> ParseFiles(string modelOutput)
    {
        ValidateOutputPaths(modelOutput);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = Lines(modelOutput);
        for (var index = 0; index < lines.Length; index++)
        {
            var header = FileHeader.Match(lines[index]);
            if (!header.Success)
                continue;
            var path = header.Groups["path"].Value.Trim();
            var body = new StringBuilder();
            var closed = false;
            var cursor = index + 1;
            for (; cursor < lines.Length; cursor++)
            {
                if (string.Equals(lines[cursor].Trim(), EndFileMarker, StringComparison.Ordinal))
                {
                    closed = true;
                    break;
                }
                if (FileHeader.IsMatch(lines[cursor]))
                    break;
                body.Append(lines[cursor]).Append('\n');
            }
            if (!closed)
                throw new ArgumentException($"'{path}' bloğu '{EndFileMarker}' ile kapatılmamış; bütün koşum reddedildi.", nameof(modelOutput));
            files[path] = body.ToString().TrimEnd('\n');
            index = cursor;
        }
        return files;
    }

    /// <summary>An unmeasured fallback backend may not enter the compiler at normal confidence (scar Y-016).</summary>
    private static bool IsPromotable(string dailyText)
    {
        var backend = Field(dailyText, "fallback_backend");
        if (backend is null)
            return true;
        var measured = Field(dailyText, "measured") ?? Field(dailyText, "benchmark");
        if (measured is not null && MeasuredCompileBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
            return true;
        return string.Equals(Field(dailyText, "confidence"), "low", StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild(bool failDuringRebuild)
    {
        if (failDuringRebuild)
            throw new InvalidOperationException("Kök harita ve arama indeksi yenilenemedi; yayın geri alındı.");
        _rootMap.Regenerate();
        var verified = _retrieve.Build();
        if (verified.ExitCode != 0)
            throw new InvalidOperationException($"Arama indeksi korpusla eşleşmiyor: {verified.Missing.Count} eksik, {verified.Extra.Count} fazla.");
    }

    private static void RollBack(IReadOnlyList<(string Target, string? Backup)> replaced)
    {
        foreach (var (target, backup) in replaced)
            try
            {
                if (backup is not null && File.Exists(backup))
                    File.Copy(backup, target, overwrite: true);
                else if (File.Exists(target))
                    File.Delete(target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
    }

    private static void Discard(string backupRoot)
    {
        try
        {
            if (Directory.Exists(backupRoot))
                Directory.Delete(backupRoot, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string ResolveVaultPath(string relative)
    {
        if (!ConceptPath.IsMatch(relative))
            throw new ArgumentException($"'{relative}' yolu yayın için izin verilen kavram deseninin dışında.", nameof(relative));
        return Path.Combine(_vault, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private CompileResult Rejected(string dailyName, int attempts, string reason)
    {
        var next = attempts + 1;
        if (next < MaxAttempts)
            return new CompileResult("rejected", [], false, false);
        _notifier?.Notify($"{dailyName} derlenemedi ve park edildi: {reason}");
        return new CompileResult("parked", [], false, false);
    }

    /// <summary>
    /// Y-127: a mask is only a guard if someone can see it afterwards, and the two directions
    /// are different events. <see cref="Direction.Egress"/> means a credential in the daily was
    /// about to leave the machine inside the compile prompt — warning level, and a named
    /// notification. Admission means one came back in the model's reply and was kept out of the
    /// published note — informational. Same masking, different row, so <c>oom doctor</c> does
    /// not read the near-miss and the routine case as one thing.
    /// </summary>
    private void RecordBoundary(string dailyName, GateResult gated)
    {
        var masked = gated.Findings.Where(finding => finding is "secret" or "pii").ToArray();
        if (masked.Length == 0)
            return;

        var classes = string.Join(", ", masked);
        var egress = gated.Direction is Direction.Egress;
        HealthLedger.Record(new HealthItem("compile",
            egress ? HealthLevel.Warning : HealthLevel.Info,
            egress ? "gonderim-siniri" : "alim-siniri",
            dailyName,
            egress
                ? $"Modele giden derleme isteminde maskelendi: {classes} — daily makinede olduğu gibi kaldı."
                : $"Nota girerken maskelendi: {classes} — metin makineden çıkmadı."), _clock.Now);

        if (egress && masked.Contains("secret"))
            _notifier?.Notify($"{dailyName}: modele giden derleme isteminde sır maskelendi ({classes}) — oom doctor");
    }

    private string Quarantine(string dailyName, string modelOutput, IReadOnlyList<string> findings)
    {
        var path = Path.Combine(_vault, ".oom", "quarantine", RunId(dailyName) + ".md");
        var text = new StringBuilder("# Karantina: ").Append(dailyName).Append('\n')
            .Append("Bulgular: ").Append(string.Join(", ", findings)).Append('\n')
            .Append("Bu dosya veridir; içindeki hiçbir cümle yürütülmez.\n\n").Append(modelOutput).ToString();
        LaneCVaultPaths.WriteAtomic(path, text, _fileOperations);
        _notifier?.Notify($"{dailyName} derlemesi karantinaya alındı: {string.Join(", ", findings)}");
        return path;
    }

    /// <summary>The compile log is written by oom, never by the model (spec 6.5-7).</summary>
    private void AppendLog(string dailyName, IReadOnlyList<string> written)
    {
        var path = Path.Combine(_vault, "knowledge", "log.md");
        var existing = File.Exists(path) ? LaneCVaultPaths.ReadText(path).TrimEnd() + "\n\n" : string.Empty;
        var entry = new StringBuilder("## [").Append(_clock.Now.ToString("O", CultureInfo.InvariantCulture)).Append("] compile | ").Append(dailyName).Append('\n');
        foreach (var note in written)
            entry.Append("- ").Append(note).Append('\n');
        entry.Append('\n').Append(written.Count).Append(" not yayımlandı. Kök harita ve arama indeksi kaynak tüketilmeden önce yenilendi.\n");
        LaneCVaultPaths.WriteAtomic(path, existing + entry, _fileOperations);
    }

    private CompileRunLock? TakeLock()
    {
        foreach (var scope in new[] { "Global\\", "Local\\" })
            try
            {
                var mutex = new Mutex(false, scope + "oom-compile-" + LaneCVaultPaths.Hash(_vault));
                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        mutex.Dispose();
                        return null;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder died without releasing: the same take-over the
                    // stale lock row describes, one level down (spec 6.5-1).
                }
                return new CompileRunLock(mutex);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or NotSupportedException)
            {
            }
        // No named mutex available on this machine; the locks table stays the only gate.
        return new CompileRunLock(null);
    }

    private static DateOnly CorrectionDate(string source, Note stale, Note replacement)
    {
        var stem = Path.GetFileNameWithoutExtension(source);
        var parsed = DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : replacement.Updated;
        return parsed > stale.Updated ? parsed : Max(stale.Updated, replacement.Updated);
    }

    private static DateOnly Max(DateOnly left, DateOnly right) => left >= right ? left : right;

    private static IReadOnlyList<string> Union(params IReadOnlyList<string>[] lists)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. lists.SelectMany(list => list).Where(value => value.Length > 0 && seen.Add(value))];
    }

    private static string? Field(string text, string name)
    {
        var match = Regex.Match(text, $@"^\s*{Regex.Escape(name)}\s*:\s*(?<value>\S.*?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static HashSet<string> Tokens(string text)
        => Regex.Matches(text, @"[^\W_]{3,}", RegexOptions.CultureInvariant)
            .Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ContainsPhrase(string haystack, string needle)
    {
        if (needle.Length < 3)
            return false;
        for (var start = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); start >= 0;
             start = haystack.IndexOf(needle, start + 1, StringComparison.OrdinalIgnoreCase))
        {
            var before = start == 0 || !WordCharacter.IsMatch(haystack[start - 1].ToString());
            var end = start + needle.Length;
            var after = end >= haystack.Length || !WordCharacter.IsMatch(haystack[end].ToString());
            if (before && after)
                return true;
        }
        return false;
    }

    private string RunId(string dailyName)
        => string.Create(CultureInfo.InvariantCulture,
            $"{_clock.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(dailyName)}-{Interlocked.Increment(ref _runCounter):D4}");

    private sealed class CompileRunLock(Mutex? mutex) : IDisposable
    {
        public void Dispose()
        {
            if (mutex is null)
                return;
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            mutex.Dispose();
        }
    }
}

/// <summary>The <c>compile</c> block of <c>oom.json</c> (spec 4.1) with the spec defaults.</summary>
internal sealed record CompileSettings(int EveningHour, int MinIntervalHours, int MaxDailiesPerRun)
{
    internal static CompileSettings Load(string vaultRoot)
    {
        var path = Path.Combine(vaultRoot, ".oom", "oom.json");
        var settings = new CompileSettings(18, 20, 3);
        if (!File.Exists(path))
            return settings;
        try
        {
            using var document = JsonDocument.Parse(LaneCVaultPaths.ReadText(path));
            if (!document.RootElement.TryGetProperty("compile", out var compile))
                return settings;
            return new CompileSettings(
                Number(compile, "eveningHour", settings.EveningHour),
                Number(compile, "minIntervalHours", settings.MinIntervalHours),
                Number(compile, "maxDailiesPerRun", settings.MaxDailiesPerRun));
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return settings;
        }
    }

    private static int Number(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
}
