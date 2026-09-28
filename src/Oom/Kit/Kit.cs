using System.Security.Cryptography;
using System.Text.Json;

namespace Oom.Contracts;

public enum KitState { Kuru, Guncel, Farkli, Bozuk }

public static class KitStateExtensions
{
    public static string Text(this KitState state) => state switch
    {
        KitState.Kuru => "kuru",
        KitState.Guncel => "güncel",
        KitState.Farkli => "farklı",
        KitState.Bozuk => "bozuk",
        _ => "bilinmiyor"
    };
}

public sealed record KitRow(string Name, string Target, KitState State, string Detail);

public sealed record KitInstallOutcome(string Name, string Target, string Action, string? Detail);

public sealed record KitInstallResult(IReadOnlyList<KitInstallOutcome> Outcomes, int ExitCode);

/// Thrown by <see cref="Kit.EnumerateFilesNoLinks"/> when a reparse point (junction or
/// symlink) is found anywhere under a tree that is being walked, compared or copied
/// without following links. It is an <see cref="IOException"/> so every existing
/// install-time catch clause (IOException/UnauthorizedAccessException) already turns it
/// into a refused "hata" outcome instead of silently following the link.
public sealed class LinkedPathException(string linkedPath) : IOException($"bağlantı: {linkedPath}")
{
    public string LinkedPath { get; } = linkedPath;
}

public sealed class Kit
{
    private static readonly string[] KnownTargets = ["claude-skills", "codex-skills", "claude-agents", "claude-rules"];
    private readonly string kitRoot;
    private readonly string homeRoot;

    public Kit(string kitRoot, string homeRoot)
    {
        this.kitRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(kitRoot));
        this.homeRoot = homeRoot;
    }

    private string ManifestPath => Path.Combine(kitRoot, "manifest.json");

    public bool KitRootMissing => !Directory.Exists(kitRoot);

    public IReadOnlyList<KitRow> Status() =>
        [.. Plan().Select(entry => new KitRow(entry.Name, entry.Target, entry.State, entry.Detail))];

    public bool HasComponent(string name) =>
        Plan().Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

    public KitInstallResult Install(bool dryRun, bool force, string? only)
    {
        var plan = Plan();
        var considered = only is null ? plan : [.. plan.Where(entry => string.Equals(entry.Name, only, StringComparison.Ordinal))];
        var outcomes = new List<KitInstallOutcome>();
        var ok = true;

        foreach (var entry in considered)
        {
            string? backup = null;
            try
            {
                switch (entry.State)
                {
                    case KitState.Bozuk:
                        outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "bozuk", entry.Detail));
                        ok = false;
                        break;

                    case KitState.Guncel:
                        outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "değişmedi", entry.Detail));
                        break;

                    case KitState.Kuru:
                        if (dryRun)
                            outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "kurulacak", entry.Destination));
                        else
                        {
                            CopyEntry(entry);
                            var recomputedKuru = CompareRow(entry.Name, entry.Kind, entry.Target, entry.Source!, entry.Destination!, entry.ExpectedHashes!);
                            if (recomputedKuru.State == KitState.Guncel)
                                outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "kuruldu", entry.Destination));
                            else
                            {
                                outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "hata", $"kurulum sonrası güncel değil: {recomputedKuru.Detail}"));
                                ok = false;
                            }
                        }
                        break;

                    case KitState.Farkli when !force:
                        outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "atlandı", $"farklı, --force gerekli: {entry.Detail}"));
                        ok = false;
                        break;

                    case KitState.Farkli when dryRun:
                        outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "yedeklenip kurulacak", entry.Destination));
                        break;

                    case KitState.Farkli:
                        backup = MoveToBackup(entry.Target, entry.Destination!);
                        CopyEntry(entry);
                        var recomputed = CompareRow(entry.Name, entry.Kind, entry.Target, entry.Source!, entry.Destination!, entry.ExpectedHashes!);
                        if (recomputed.State == KitState.Guncel)
                            outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "yedeklenip kuruldu", backup));
                        else
                        {
                            outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "hata", $"kurulum sonrası güncel değil: {recomputed.Detail} (yedek: {backup})"));
                            ok = false;
                        }
                        break;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var detail = backup is null ? error.Message : $"{error.Message} (yedek: {backup})";
                outcomes.Add(new KitInstallOutcome(entry.Name, entry.Target, "hata", detail));
                ok = false;
            }
        }

        return new KitInstallResult(outcomes, ok ? 0 : 1);
    }

    private IReadOnlyList<KitPlanEntry> Plan()
    {
        if (KitRootMissing)
            return [];

        var manifest = ReadManifest();
        var entries = new List<KitPlanEntry>();
        foreach (var component in manifest)
            entries.AddRange(PlanFor(component));

        return MarkDestinationCollisions(entries);
    }

    /// Two manifest entries that resolve to the same normalised destination must not
    /// both try to write there: whichever "wins" would silently overwrite the other with
    /// no backup, and no --force gate would ever have been consulted. Both become bozuk
    /// and nothing is written for either.
    private static IReadOnlyList<KitPlanEntry> MarkDestinationCollisions(List<KitPlanEntry> entries)
    {
        var colliding = entries
            .Where(entry => entry.Destination is not null && entry.State != KitState.Bozuk)
            .GroupBy(entry => Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Destination!)), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();

        if (colliding.Count == 0)
            return entries;

        return [.. entries.Select(entry => colliding.Contains(entry)
            ? entry with { State = KitState.Bozuk, Detail = "hedef çakışması" }
            : entry)];
    }

    /// A component with a missing/empty name, kind or path is rejected outright as a
    /// single bozuk row (never silently defaulted to a placeholder like "(adsız)" and
    /// then processed as if it were valid) — mirrors the existing "hedef listesi eksik"
    /// shape for a missing targets array.
    private IEnumerable<KitPlanEntry> PlanFor(ManifestComponent component)
    {
        if (string.IsNullOrWhiteSpace(component.Name))
        {
            yield return new KitPlanEntry("(adsız)", component.Kind ?? string.Empty, "-", KitState.Bozuk, "ad eksik", null, null);
            yield break;
        }

        var name = component.Name;

        if (string.IsNullOrWhiteSpace(component.Kind))
        {
            yield return new KitPlanEntry(name, string.Empty, "-", KitState.Bozuk, "tür eksik", null, null);
            yield break;
        }

        var kind = component.Kind;
        var kindValid = kind is "skill" or "agent" or "rule";

        if (string.IsNullOrWhiteSpace(component.Path))
        {
            yield return new KitPlanEntry(name, kind, "-", KitState.Bozuk, "yol eksik", null, null);
            yield break;
        }

        if (component.Targets is not { Count: > 0 } targets)
        {
            yield return new KitPlanEntry(name, kind, "-", KitState.Bozuk, "hedef listesi eksik", null, null);
            yield break;
        }

        var source = string.Empty;
        var sourceReason = string.Empty;
        var sourceOk = kindValid && TryResolveSource(component.Path, kind, out source, out sourceReason);
        IReadOnlyDictionary<string, string>? expectedHashes = null;
        var hashReason = string.Empty;
        var hashesOk = sourceOk && TryReadExpectedHashes(component, source, out expectedHashes, out hashReason);

        foreach (var target in targets)
        {
            if (!kindValid)
            {
                yield return new KitPlanEntry(name, kind, target, KitState.Bozuk, $"bilinmeyen tür: {kind}", null, null);
                continue;
            }

            if (!IsCompatible(kind, target))
            {
                yield return new KitPlanEntry(name, kind, target, KitState.Bozuk,
                    KnownTargets.Contains(target) ? $"'{kind}' türü '{target}' hedefiyle uyumsuz" : $"bilinmeyen hedef: {target}",
                    null, null);
                continue;
            }

            if (!sourceOk)
            {
                yield return new KitPlanEntry(name, kind, target, KitState.Bozuk, sourceReason, null, null);
                continue;
            }

            if (!hashesOk)
            {
                yield return new KitPlanEntry(name, kind, target, KitState.Bozuk, hashReason, source, null);
                continue;
            }

            KitPlanEntry row;
            try
            {
                row = CompareRow(name, kind, target, source, Destination(target, source), expectedHashes!);
            }
            catch (Exception error) when (error is ArgumentException or PathTooLongException or NotSupportedException)
            {
                row = new KitPlanEntry(name, kind, target, KitState.Bozuk, $"geçersiz hedef yolu: {error.Message}", source, null);
            }

            yield return row;
        }
    }

    private KitPlanEntry CompareRow(
        string name,
        string kind,
        string target,
        string source,
        string destination,
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        if (TryFindLinkedAncestorOrSelf(destination, out var linkedDestination))
            return new KitPlanEntry(name, kind, target, KitState.Bozuk, $"bağlantı: {linkedDestination}", source, destination, expectedHashes);

        if (TryFindLinkedAncestorOrSelf(BackupRoot(target), out var linkedBackupRoot))
            return new KitPlanEntry(name, kind, target, KitState.Bozuk, $"yedek yolunda bağlantı: {linkedBackupRoot}", source, destination, expectedHashes);

        if (kind == "skill")
        {
            if (File.Exists(destination))
                return new KitPlanEntry(name, kind, target, KitState.Farkli, "hedef beklenmeyen türde", source, destination, expectedHashes);

            if (!Directory.Exists(destination))
                return new KitPlanEntry(name, kind, target, KitState.Kuru, "kurulu değil", source, destination, expectedHashes);

            try
            {
                var diff = DirectoryHashDiff(destination, expectedHashes);
                return diff is null
                    ? new KitPlanEntry(name, kind, target, KitState.Guncel, "aynı", source, destination, expectedHashes)
                    : new KitPlanEntry(name, kind, target, KitState.Farkli, diff, source, destination, expectedHashes);
            }
            catch (LinkedPathException error)
            {
                return new KitPlanEntry(name, kind, target, KitState.Bozuk, error.Message, source, destination, expectedHashes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new KitPlanEntry(name, kind, target, KitState.Bozuk, $"hedef taranamadı: {error.Message}", source, destination, expectedHashes);
            }
        }

        if (Directory.Exists(destination))
        {
            // The destination is a directory where an agent/rule file was expected. Before
            // reporting this as a merely "farklı" mismatch that --force is allowed to move
            // to backup, walk the tree without following links: any link found anywhere
            // inside it makes the whole row bozuk, so --force can never touch it.
            try
            {
                EnumerateFilesNoLinks(destination);
            }
            catch (LinkedPathException error)
            {
                return new KitPlanEntry(name, kind, target, KitState.Bozuk, error.Message, source, destination, expectedHashes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new KitPlanEntry(name, kind, target, KitState.Bozuk, $"hedef taranamadı: {error.Message}", source, destination, expectedHashes);
            }

            return new KitPlanEntry(name, kind, target, KitState.Farkli, "hedef beklenmeyen türde", source, destination, expectedHashes);
        }

        if (!File.Exists(destination))
            return new KitPlanEntry(name, kind, target, KitState.Kuru, "kurulu değil", source, destination, expectedHashes);

        try
        {
            return HashMatches(destination, expectedHashes[string.Empty])
                ? new KitPlanEntry(name, kind, target, KitState.Guncel, "aynı", source, destination, expectedHashes)
                : new KitPlanEntry(name, kind, target, KitState.Farkli, "içerik farklı", source, destination, expectedHashes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new KitPlanEntry(name, kind, target, KitState.Bozuk, $"hedef sha256 hesaplanamadı: {error.Message}", source, destination, expectedHashes);
        }
    }

    private static bool TryReadExpectedHashes(
        ManifestComponent component,
        string source,
        out IReadOnlyDictionary<string, string>? expectedHashes,
        out string reason)
    {
        expectedHashes = null;
        reason = string.Empty;

        try
        {
            if (component.Kind != "skill")
            {
                if (component.Sha256.ValueKind != JsonValueKind.String ||
                    !IsSha256(component.Sha256.GetString()))
                {
                    reason = "sha256 eksik veya geçersiz";
                    return false;
                }

                var expected = component.Sha256.GetString()!;
                if (!HashMatches(source, expected))
                {
                    reason = "kaynak sha256 uyuşmuyor";
                    return false;
                }

                expectedHashes = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [string.Empty] = expected
                };
                return true;
            }

            if (component.Sha256.ValueKind != JsonValueKind.Object)
            {
                reason = "sha256 eksik veya geçersiz";
                return false;
            }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in component.Sha256.EnumerateObject())
            {
                if (!hashes.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : string.Empty) ||
                    !IsSha256(hashes[property.Name]))
                {
                    reason = $"sha256 geçersiz: {property.Name}";
                    return false;
                }
            }

            var sourceFiles = EnumerateFilesNoLinks(source).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var extraHash = hashes.Keys.Except(sourceFiles, StringComparer.Ordinal).FirstOrDefault();
            if (extraHash is not null)
            {
                reason = $"sha256 bilinmeyen dosya: {extraHash}";
                return false;
            }

            var missingHash = sourceFiles.Except(hashes.Keys, StringComparer.Ordinal).FirstOrDefault();
            if (missingHash is not null)
            {
                reason = $"sha256 eksik: {missingHash}";
                return false;
            }

            foreach (var relative in sourceFiles)
            {
                if (!HashMatches(Path.Combine(source, relative), hashes[relative]))
                {
                    reason = $"kaynak sha256 uyuşmuyor: {relative}";
                    return false;
                }
            }

            expectedHashes = hashes;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            reason = $"kaynak sha256 hesaplanamadı: {error.Message}";
            return false;
        }
    }

    /// Canonical form is lower-case hex, matching tools/kit/manifest-hash.ps1's
    /// ToLowerInvariant() generator; any other form (upper-case, mixed case) is rejected
    /// outright, not normalised. This is a drift/corruption check on values that sit in
    /// the same folder as the files they cover — it is not an authenticity or egress
    /// gate, and must not be described as one.
    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool TryResolveSource(string? relativePath, string kind, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            reason = "yol eksik";
            return false;
        }

        try
        {
            if (Path.IsPathRooted(relativePath))
            {
                reason = "mutlak yol";
                return false;
            }

            var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == ".."))
            {
                reason = "yol kit kökünün dışına çıkıyor";
                return false;
            }

            if (segments.Length == 0 || segments[^1] == ".")
            {
                reason = "geçersiz kaynak yolu";
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(kitRoot, relativePath));
            if (string.Equals(candidate, kitRoot, StringComparison.OrdinalIgnoreCase))
            {
                reason = "kaynak yolu kit kökünün kendisi";
                return false;
            }

            if (!IsWithinRoot(candidate))
            {
                reason = "yol kit kökünün dışına çıkıyor";
                return false;
            }

            if (!(kind == "skill" ? Directory.Exists(candidate) : File.Exists(candidate)))
            {
                reason = "kaynak eksik";
                return false;
            }

            if (SourceIsLinked(candidate, out var linkReason))
            {
                reason = linkReason;
                return false;
            }

            if (kind == "skill")
            {
                try
                {
                    EnumerateFilesNoLinks(candidate);
                }
                catch (LinkedPathException error)
                {
                    reason = $"kaynak alt ağacında {error.Message}";
                    return false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A scan error (permission denied, a directory that vanished mid-walk, …)
                    // must reject only this component's source, not bubble out of Plan() and
                    // abort every other component — see PlanFor's catch around CompareRow for
                    // the destination-side counterpart.
                    reason = $"kaynak alt ağacı taranamadı: {error.Message}";
                    return false;
                }
            }

            fullPath = candidate;
            reason = string.Empty;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or PathTooLongException or NotSupportedException)
        {
            fullPath = string.Empty;
            reason = $"geçersiz kaynak yolu: {error.Message}";
            return false;
        }
    }

    private bool IsWithinRoot(string path) =>
        string.Equals(path, kitRoot, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(kitRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private bool SourceIsLinked(string candidate, out string reason)
    {
        var current = candidate;
        while (true)
        {
            if (IsReparsePoint(current))
            {
                var resolved = ResolveFinalTarget(current);
                reason = resolved is not null && !IsWithinRoot(resolved)
                    ? $"kaynak bağlantı kit kökü dışına çözülüyor: {current}"
                    : $"kaynak bağlantı: {current}";
                return true;
            }

            if (string.Equals(current, kitRoot, StringComparison.OrdinalIgnoreCase))
                break;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length)
                break;
            current = parent;
        }

        reason = string.Empty;
        return false;
    }

    /// Walks from <paramref name="path"/> up to (and including) the home root, without
    /// following any link, so a junction anywhere between the two — the destination
    /// itself, `.claude`, `.claude/skills`, `.agents/skills`, or the kit backup root —
    /// is caught before anything is read from or written through it.
    private bool TryFindLinkedAncestorOrSelf(string path, out string linked)
    {
        string current;
        string normalizedHome;
        try
        {
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            normalizedHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeRoot));
        }
        catch (Exception error) when (error is ArgumentException or PathTooLongException or NotSupportedException)
        {
            linked = string.Empty;
            return false;
        }

        while (true)
        {
            if (IsReparsePoint(current))
            {
                linked = current;
                return true;
            }

            if (string.Equals(current, normalizedHome, StringComparison.OrdinalIgnoreCase))
                break;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Length >= current.Length)
                break;
            current = parent;
        }

        linked = string.Empty;
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return false;
        return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }

    private static string? ResolveFinalTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string Destination(string target, string resolvedSourcePath)
    {
        var lastSegment = Path.GetFileName(Path.TrimEndingDirectorySeparator(resolvedSourcePath));
        return target switch
        {
            "claude-skills" => Path.Combine(homeRoot, ".claude", "skills", lastSegment),
            "codex-skills" => Path.Combine(homeRoot, ".agents", "skills", lastSegment),
            "claude-agents" => Path.Combine(homeRoot, ".claude", "agents", lastSegment),
            "claude-rules" => Path.Combine(homeRoot, ".claude", "rules", lastSegment),
            _ => throw new InvalidOperationException(target)
        };
    }

    private static bool IsCompatible(string kind, string target) => (kind, target) switch
    {
        ("skill", "claude-skills") => true,
        ("skill", "codex-skills") => true,
        ("agent", "claude-agents") => true,
        ("rule", "claude-rules") => true,
        _ => false
    };

    private static string? DirectoryHashDiff(
        string destinationDirectory,
        IReadOnlyDictionary<string, string> expectedHashes)
    {
        var destinationFiles = EnumerateFilesNoLinks(destinationDirectory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var expectedFiles = expectedHashes.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();

        var extra = destinationFiles.Except(expectedFiles, StringComparer.Ordinal).ToArray();
        if (extra.Length > 0)
            return $"hedefte fazladan {extra.Length} dosya";

        var missing = expectedFiles.Except(destinationFiles, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            return $"hedefte eksik {missing.Length} dosya";

        foreach (var relative in expectedFiles)
            if (!HashMatches(Path.Combine(destinationDirectory, relative), expectedHashes[relative]))
                return $"içerik farklı: {relative}";

        return null;
    }

    /// Enumerates every file under <paramref name="root"/> without ever descending into
    /// or reading through a reparse point (junction/symlink); throws
    /// <see cref="LinkedPathException"/> the moment one is found anywhere in the tree,
    /// so a link nested arbitrarily deep — not just at the component's top level — is
    /// never silently compared or copied.
    private static IReadOnlyList<string> EnumerateFilesNoLinks(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsReparsePoint(entry))
                    throw new LinkedPathException(entry);

                if (Directory.Exists(entry))
                    pending.Push(entry);
                else
                    files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }

        return files;
    }

    private static bool HashMatches(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
    }

    /// Copies without ever overwriting: a file destination is created with
    /// `overwrite:false` (fails if something else created it meanwhile), and a
    /// directory (skill) destination is built in a temp sibling inside the same parent
    /// and only `Directory.Move`d into place once the destination still does not exist —
    /// so a losing manifest collision or a race with another writer never partially
    /// overwrites an existing tree.
    private static void CopyEntry(KitPlanEntry entry)
    {
        var destination = entry.Destination!;
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);

        if (entry.Kind != "skill")
        {
            File.Copy(entry.Source!, destination, overwrite: false);
            return;
        }

        var staging = Path.Combine(parent, $".oom-kit-tmp-{Path.GetFileName(destination)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            CopyTree(entry.Source!, staging);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException($"hedef kurulum sırasında oluşturuldu: {destination}");
            Directory.Move(staging, destination);
        }
        catch
        {
            TryDeleteStaging(staging);
            throw;
        }
    }

    private static void TryDeleteStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // best effort cleanup only; the install already failed for a different reason
        }
    }

    private static void CopyTree(string source, string destination)
    {
        foreach (var relative in EnumerateFilesNoLinks(source))
        {
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, relative), target, overwrite: false);
        }
    }

    private string BackupRoot(string target) => Path.Combine(homeRoot, ".claude", ".oom-kit-yedek", target);

    private string MoveToBackup(string target, string destination)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(destination));
        var backupDirectory = BackupRoot(target);
        Directory.CreateDirectory(backupDirectory);

        var stamp = DateTimeOffset.Now;
        var backup = Path.Combine(backupDirectory, $"{name}.bak-{stamp:yyyyMMddHHmmss}");
        while (Directory.Exists(backup) || File.Exists(backup))
            backup = Path.Combine(backupDirectory, $"{name}.bak-{(stamp = stamp.AddSeconds(1)):yyyyMMddHHmmss}");

        if (Directory.Exists(destination))
            Directory.Move(destination, backup);
        else
            File.Move(destination, backup);

        return backup;
    }

    private IReadOnlyList<ManifestComponent> ReadManifest()
    {
        var text = File.ReadAllText(ManifestPath);
        var root = JsonSerializer.Deserialize<ManifestRoot>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (root?.Components is null)
            throw new JsonException("components alanı eksik");
        if (root.Components.Any(component => component is null))
            throw new JsonException("components içinde boş (null) bileşen var");
        return [.. root.Components.Select(component => component!)];
    }

    private sealed record ManifestRoot(int Schema, IReadOnlyList<ManifestComponent?>? Components);

    private sealed record ManifestComponent(
        string? Name,
        string? Kind,
        string? Path,
        IReadOnlyList<string>? Targets,
        JsonElement Sha256);

    private sealed record KitPlanEntry(
        string Name,
        string Kind,
        string Target,
        KitState State,
        string Detail,
        string? Source,
        string? Destination,
        IReadOnlyDictionary<string, string>? ExpectedHashes = null);
}
