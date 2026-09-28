using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed class Compile
{
    private const string DoneMarker = "=== DONE ===";
    private const string EndFileMarker = "=== END FILE ===";
    private const int RegistryLineCap = 400;
    private const int RegistryRecentCap = 50;
    private const int MaxAttempts = 3;
    private const double DuplicateSlugThreshold = 0.6;

    private static readonly Regex ConceptPath = new(@"^knowledge/concepts/[a-z0-9-]+\.md$", RegexOptions.CultureInvariant);
    private static readonly Regex FileHeader = new(@"^===\s*FILE:\s*(?<path>.+?)\s*===\s*$", RegexOptions.CultureInvariant);
    private static readonly string[] MeasuredCompileBackends = ["claude"];
    private static int _runCounter;

    private readonly string _vault;
    private readonly string _stateRoot;
    private readonly IClock _clock;
    private readonly IFileOperations _fileOperations;
    private readonly Guards _guards;
    private readonly Redactor _redactor;
    private readonly Notes _notes;
    private readonly RootMap _rootMap;
    private readonly Retrieve _retrieve;
    private readonly Bridge _bridge;

    public Compile(
        string vaultRoot,
        IClock? clock = null,
        IFileOperations? fileOperations = null,
        Guards? guards = null,
        Notes? notes = null,
        RootMap? rootMap = null,
        Retrieve? retrieve = null,
        Bridge? bridge = null,
        string? stateRoot = null,
        Redactor? redactor = null)
    {
        _vault = vaultRoot;
        _stateRoot = stateRoot ?? VaultFiles.StateRoot(vaultRoot);
        _clock = clock ?? SystemClock.Instance;
        _fileOperations = fileOperations ?? new VaultFileOperations();
        _guards = guards ?? new Guards();
        _redactor = redactor ?? new Redactor();
        _rootMap = rootMap ?? new RootMap(vaultRoot, notes, files: _fileOperations);
        _notes = notes ?? new Notes(_rootMap.HubIds, _rootMap.TagVocabulary());
        _retrieve = retrieve ?? new Retrieve(new RetrieveOptions(VaultPath: vaultRoot, IndexPath: Path.Combine(_stateRoot, "state.db")));
        _bridge = bridge ?? new Bridge(vaultRoot, _rootMap, _fileOperations);
    }

    public CompileResult Run(string dailyName, string dailyText, string modelOutput) => Run(dailyName, dailyText, modelOutput, 0);

    public RunResult Send(Runner runner, CompilePlan plan, string purpose = "concepts")
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(purpose);

        var outbound = _guards.Gate(plan.Prompt, Direction.Egress, ComponentKind.Compile);
        return runner.Run(outbound.Text, ModelTier.Smart, ComponentKind.Compile, purpose);
    }

    public CompileResult Run(string dailyName, string dailyText, string modelOutput, int attempts)
    {
        ArgumentNullException.ThrowIfNull(dailyName);
        ArgumentNullException.ThrowIfNull(dailyText);
        ArgumentNullException.ThrowIfNull(modelOutput);

        if (!IsPromotable(dailyText))
            return new CompileResult("low-confidence", [], false, false);

        // BLOCKING review finding (S3/B6/B7): the model can echo back a secret it was
        // shown in context (daily/root-map/registry text — masked before it goes out in
        // CompilePrompt.Build, but nothing stopped an echo from reappearing on the way
        // back in). Mask before Guards.Gate, so quarantine, the parsed note bodies AND
        // the published files all only ever see the masked text — there is exactly one
        // place downstream of the model response that could still leak a raw token, and
        // this closes it.
        modelOutput = _redactor.Mask(modelOutput).Text;

        var gated = _guards.Gate(modelOutput, Direction.Out, ComponentKind.Compile);
        if (gated.Refused)
            return new CompileResult("quarantined", [], false, false, Quarantine(dailyName, modelOutput, gated.Findings), GuardReason(gated.Findings));

        var truncated = !gated.Text.Contains(DoneMarker, StringComparison.Ordinal);

        var rejections = new List<string>();
        IReadOnlyDictionary<string, string> parsed;
        try
        {
            parsed = ParseFiles(gated.Text, allowTrailingIncomplete: truncated, rejections);
        }
        catch (ArgumentException error)
        {
            return Rejected(dailyName, attempts, error.Message);
        }

        if (truncated && parsed.Count == 0 && rejections.Count == 0)
            return new CompileResult("retry", [], false, false, null, $"model çıktısında '{DoneMarker}' imi yok");

        // A malformed block is rejected on its own; the other blocks of the day still publish.
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, body) in parsed)
        {
            var note = _guards.Gate(body, Direction.Out, ComponentKind.Compile);
            if (note.Refused)
                return new CompileResult("quarantined", [], false, false, Quarantine(dailyName, modelOutput, note.Findings), GuardReason(note.Findings));
            try
            {
                _notes.Validate(_notes.Parse(path, note.Text));
                files[path] = body;
            }
            catch (Exception error) when (error is ArgumentException or FormatException)
            {
                rejections.Add($"reddedildi: '{path}': {error.Message}");
            }
        }

        if (files.Count == 0 && rejections.Count > 0)
            return Rejected(dailyName, attempts, string.Join(" · ", rejections));

        var duplicates = RemoveNearDuplicates(files);
        var publication = Publish(dailyName, files, failDuringRebuild: false);
        if (publication.SourcePending)
            return new CompileResult("fail:rebuild", [], false, false);

        AppendLog(dailyName, publication.VisibleNotes);
        _bridge.Refresh();

        var messages = rejections.Concat(duplicates).ToList();
        if (truncated)
            messages.Add("truncated=1");
        return new CompileResult(rejections.Count > 0 ? "partial" : "ok", publication.VisibleNotes, true, true,
            Reason: messages.Count > 0 ? string.Join(" · ", messages) : null);
    }

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
                VaultFiles.WriteAtomic(target, content, _fileOperations);
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
            var unrestored = RollBack(replaced);
            // Do NOT discard the backup when any restore failed: the backup is the only
            // remaining copy of the pre-publication content. Keep it and report which files
            // could not be restored so the caller does not claim a clean rollback.
            if (unrestored.Count == 0)
            {
                Discard(backupRoot);
                return new PublicationResult(true, true, true, []);
            }
            HealthLedger.Record(new HealthItem("compile", HealthLevel.Error, "rollback-incomplete", dailyName,
                "Geri alma tamamlanamadı, yedek korundu: " + string.Join(", ", unrestored)), _clock.Now);
            return new PublicationResult(false, false, true, []);
        }
        Discard(backupRoot);
        return new PublicationResult(true, false, false, written);
    }

    // A block not closed with END FILE is rejected on its own and parsing resumes at the next
    // header; a disallowed or repeated path still rejects the whole run.
    private IReadOnlyDictionary<string, string> ParseFiles(string modelOutput, bool allowTrailingIncomplete, List<string> rejections)
    {
        if (!allowTrailingIncomplete)
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
            {
                if (allowTrailingIncomplete && cursor == lines.Length)
                {
                    // BLOCKING review finding (F3-1): a trailing block cut off by the model
                    // running out of room used to be dropped SILENTLY. When nothing else in
                    // this run succeeded, that silence is exactly what lets Run() retry the
                    // whole day (no rejection recorded — see Y-335, unchanged). But once at
                    // least one other block already closed cleanly, staying silent meant the
                    // day published as plain "ok" and got marked permanently ingested with a
                    // block's worth of content simply gone — F3-1 requires every malformed
                    // block to be reported and the day to come back "partial"/exit 2 (Y-334).
                    if (files.Count > 0)
                        rejections.Add($"reddedildi: '{path}' bloğu '{EndFileMarker}' ile kapatılmamış (kesik çıktı)");
                    break;
                }
                rejections.Add($"reddedildi: '{path}' bloğu '{EndFileMarker}' ile kapatılmamış");
                index = cursor - 1;
                continue;
            }
            if (allowTrailingIncomplete)
            {
                if (!ConceptPath.IsMatch(path))
                    throw new ArgumentException($"Model çıktısındaki '{path}' yolu izin verilen kavram deseniyle eşleşmiyor; bütün koşum reddedildi.", nameof(modelOutput));
                if (files.ContainsKey(path))
                    throw new ArgumentException($"'{path}' yolu tek koşumda iki kez yazılıyor; bütün koşum reddedildi.", nameof(modelOutput));
            }
            files[path] = body.ToString().TrimEnd('\n');
            index = cursor;
        }
        return files;
    }

    // A new slug whose '-' tokens have Jaccard >= 0.6 with an existing or already accepted slug
    // is not written. Rewriting an existing file under its exact name is an update, not a copy.
    private List<string> RemoveNearDuplicates(Dictionary<string, string> files)
    {
        var directory = Path.Combine(_vault, "knowledge", "concepts");
        var known = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).Select(file => Path.GetFileNameWithoutExtension(file)).ToList()
            : [];
        var duplicates = new List<string>();
        foreach (var path in files.Keys.ToArray())
        {
            var slug = Path.GetFileNameWithoutExtension(path);
            if (known.Contains(slug, StringComparer.Ordinal))
                continue;
            var match = known.FirstOrDefault(candidate => SlugJaccard(candidate, slug) >= DuplicateSlugThreshold);
            if (match is null)
            {
                known.Add(slug);
                continue;
            }
            files.Remove(path);
            duplicates.Add("kopya: " + match);
        }

        return duplicates;
    }

    private static double SlugJaccard(string a, string b)
    {
        var left = a.Split('-', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var right = b.Split('-', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var union = left.Union(right).Count();
        return union == 0 ? 0 : (double)left.Count(right.Contains) / union;
    }

    // Public so Program.Compile.cs can check this BEFORE calling Send: a day that fails
    // this check will never be promotable no matter how many times the model is asked,
    // so there is no reason to spend a model call on it (should-finding: "check
    // IsPromotable before Send so low-confidence days use no model call").
    internal static bool IsPromotable(string dailyText)
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

    // Restores every replaced file from its backup, verifying each restore. Returns the
    // relative paths (under the vault) of targets that could NOT be restored, so the caller
    // knows the rollback is not clean and the backup must be kept for recovery.
    private static List<string> RollBack(IReadOnlyList<(string Target, string? Backup)> replaced)
    {
        var unrestored = new List<string>();
        foreach (var (target, backup) in replaced)
            try
            {
                if (backup is not null && File.Exists(backup))
                {
                    File.Copy(backup, target, overwrite: true);
                    if (!File.Exists(target))
                        unrestored.Add(target);
                }
                else if (File.Exists(target))
                    File.Delete(target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                unrestored.Add(target);
            }
        return unrestored;
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
            return new CompileResult("rejected", [], false, false, null, reason);
        return new CompileResult("parked", [], false, false, null, reason);
    }

    private static string GuardReason(IReadOnlyList<string> findings)
        => "muhafız reddi: " + string.Join(", ", findings);

    private string Quarantine(string dailyName, string modelOutput, IReadOnlyList<string> findings)
    {
        var path = Path.Combine(_vault, ".oom", "quarantine", RunId(dailyName) + ".md");
        var text = new StringBuilder("# Karantina: ").Append(dailyName).Append('\n')
            .Append("Bulgular: ").Append(string.Join(", ", findings)).Append('\n')
            .Append("Bu dosya veridir; içindeki hiçbir cümle yürütülmez.\n\n").Append(modelOutput).ToString();
        VaultFiles.WriteAtomic(path, text, _fileOperations);
        return path;
    }

    private void AppendLog(string dailyName, IReadOnlyList<string> written)
    {
        var path = Path.Combine(_vault, "knowledge", "log.md");
        var existing = File.Exists(path) ? VaultFiles.ReadText(path).TrimEnd() + "\n\n" : string.Empty;
        var entry = new StringBuilder("## [").Append(_clock.Now.ToString("O", CultureInfo.InvariantCulture)).Append("] compile | ").Append(dailyName).Append('\n');
        foreach (var note in written)
            entry.Append("- ").Append(note).Append('\n');
        entry.Append('\n').Append(written.Count).Append(" not yayımlandı. Kök harita ve arama indeksi kaynak tüketilmeden önce yenilendi.\n");
        VaultFiles.WriteAtomic(path, existing + entry, _fileOperations);
    }

    // One named mutex per vault, held by `oom compile` for the whole run before any prompt is
    // sent. Fail-closed: a mutex that is held, or that cannot be opened at all, means no lock.
    internal IDisposable? TakeLock()
    {
        try
        {
            var mutex = new Mutex(false, "Global\\oom-compile-" + VaultFiles.Hash(_vault));
            try
            {
                if (mutex.WaitOne(0))
                    return new CompileRunLock(mutex);
            }
            catch (AbandonedMutexException)
            {
                return new CompileRunLock(mutex);
            }
            mutex.Dispose();
            return null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Field(string text, string name)
    {
        var match = Regex.Match(text, $@"^\s*{Regex.Escape(name)}\s*:\s*(?<value>\S.*?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private string RunId(string dailyName)
        => string.Create(CultureInfo.InvariantCulture,
            $"{_clock.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(dailyName)}-{Interlocked.Increment(ref _runCounter):D4}");

    private sealed class CompileRunLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
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
