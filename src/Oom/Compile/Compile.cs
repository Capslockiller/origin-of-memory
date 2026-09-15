using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed class Compile
{
    private const string DoneMarker = "=== DONE ===";
    private const string EndFileMarker = "=== END FILE ===";
    private const int RegistryLineCap = 400;
    private const int RegistryRecentCap = 50;
    private const int MaxAttempts = 3;

    private static readonly Regex ConceptPath = new(@"^knowledge/concepts/[a-z0-9-]+\.md$", RegexOptions.CultureInvariant);
    private static readonly Regex FileHeader = new(@"^===\s*FILE:\s*(?<path>.+?)\s*===\s*$", RegexOptions.CultureInvariant);
    private static readonly string[] MeasuredCompileBackends = ["claude"];
    private static int _runCounter;

    private readonly string _vault;
    private readonly string _stateRoot;
    private readonly IClock _clock;
    private readonly IFileOperations _fileOperations;
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
        _guards = guards ?? new Guards();
        _rootMap = rootMap ?? new RootMap(vaultRoot, notes, files: _fileOperations);
        _notes = notes ?? new Notes(_rootMap.HubIds, _rootMap.TagVocabulary());
        _retrieve = retrieve ?? new Retrieve(new RetrieveOptions(VaultPath: vaultRoot, IndexPath: Path.Combine(_stateRoot, "state.db")));
        _bridge = bridge ?? new Bridge(vaultRoot, _rootMap, _fileOperations);
        _settings = CompileSettings.Load(vaultRoot);
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

        using var runLock = TakeLock();
        if (runLock is null)
            return new CompileResult("skip:locked", [], false, false);

        var gated = _guards.Gate(modelOutput, Direction.Out, ComponentKind.Compile);
        if (gated.Refused)
            return new CompileResult("quarantined", [], false, false, Quarantine(dailyName, modelOutput, gated.Findings), GuardReason(gated.Findings));

        var truncated = !gated.Text.Contains(DoneMarker, StringComparison.Ordinal);

        IReadOnlyDictionary<string, string> files;
        try
        {
            files = ParseFiles(gated.Text, allowTrailingIncomplete: truncated);
            if (truncated && files.Count == 0)
                return new CompileResult("retry", [], false, false, null, $"model çıktısında '{DoneMarker}' imi yok");
            foreach (var (path, body) in files)
            {
                var note = _guards.Gate(body, Direction.Out, ComponentKind.Compile);
                if (note.Refused)
                    return new CompileResult("quarantined", [], false, false, Quarantine(dailyName, modelOutput, note.Findings), GuardReason(note.Findings));
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
        return new CompileResult("ok", publication.VisibleNotes, true, true, Reason: truncated ? "truncated=1" : null);
    }

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

    internal IReadOnlyDictionary<string, string> ParseFiles(string modelOutput)
        => ParseFiles(modelOutput, allowTrailingIncomplete: false);

    private IReadOnlyDictionary<string, string> ParseFiles(string modelOutput, bool allowTrailingIncomplete)
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
                    break;
                throw new ArgumentException($"'{path}' bloğu '{EndFileMarker}' ile kapatılmamış; bütün koşum reddedildi.", nameof(modelOutput));
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
        LaneCVaultPaths.WriteAtomic(path, text, _fileOperations);
        return path;
    }

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
                }
                return new CompileRunLock(mutex);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or NotSupportedException)
            {
            }
        return new CompileRunLock(null);
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
