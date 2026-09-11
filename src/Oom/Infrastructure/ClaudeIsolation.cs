// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

/// <summary>Owns the off-vault, per-user Claude CLI isolation copy.</summary>
internal static class ClaudeIsolation
{
    private const string Credentials = ".credentials.json";
    private const string Ownership = ".oom-owned.json";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private sealed record OwnershipMarker(int Schema, string Vault, string Directory);

    /// <summary>
    /// Derives one directory per canonical vault beneath LOCALAPPDATA. Both the candidate and
    /// every comparison root are resolved through existing symlinks/junctions before acceptance.
    /// </summary>
    internal static string ConfigurationDirectory(string vault, string? localAppData = null,
        IReadOnlyList<string>? knownSyncRoots = null)
    {
        var local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("LOCALAPPDATA bulunamadı.");

        var canonicalVault = ResolvePath(vault);
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalVault.ToUpperInvariant())))[..24].ToLowerInvariant();
        var candidate = Path.Combine(local, "oom", "claude-config", identity);
        var canonicalCandidate = ResolvePath(candidate);

        if (!IsWithin(canonicalCandidate, ResolvePath(local)) ||
            IsWithinAny(canonicalCandidate, (knownSyncRoots ?? KnownSyncRoots()).Append(canonicalVault)))
            throw new InvalidOperationException("Claude kimlik dizini LOCALAPPDATA dışında veya kasa içindedir.");
        if (InsideGitWorkingTree(candidate) || InsideGitWorkingTree(canonicalCandidate))
            throw new InvalidOperationException("Claude kimlik dizini bir git çalışma ağacının içindedir.");
        return candidate;
    }

    /// <summary>Creates or content-refreshes a regular-file copy and reports one Turkish outcome.</summary>
    internal static string Prepare(string configDirectory) => Safely(() =>
    {
        var markerPath = Path.Combine(configDirectory, Ownership);
        RequireRegularFileOrMissing(markerPath);
        var marker = JsonSerializer.Deserialize<OwnershipMarker>(File.ReadAllText(markerPath, Utf8));
        return marker is not null && PathEquals(marker.Directory, ResolvePath(configDirectory))
            ? Prepare(configDirectory, marker.Vault)
            : "hazırlanamadı";
    }, "hazırlanamadı");

    internal static string Prepare(string configDirectory, string vault, string? sourceConfigDirectory = null,
        string? localAppData = null, IReadOnlyList<string>? knownSyncRoots = null) => Safely(() =>
    {
        var expected = ConfigurationDirectory(vault, localAppData, knownSyncRoots);
        if (!PathEquals(ResolvePath(configDirectory), ResolvePath(expected)))
            throw new InvalidOperationException("Claude kimlik dizini beklenen ürün yolu değildir.");

        ClaimDirectory(configDirectory, vault);
        var settings = Path.Combine(configDirectory, "settings.json");
        RequireRegularFileOrMissing(settings);
        if (!File.Exists(settings))
            File.WriteAllText(settings, "{}\n", Utf8);

        var source = Path.Combine(sourceConfigDirectory ?? UserConfigDirectory(), Credentials);
        var target = Path.Combine(configDirectory, Credentials);
        RequireRegularFileOrMissing(target);
        if (!File.Exists(source))
            return File.Exists(target) ? "mevcut" : "kimlik yok";
        if (File.Exists(target) && ContentEquals(source, target))
            return "güncel";

        if (File.Exists(target)) File.Delete(target); // deleting an old hard link cannot mutate its source
        var temporary = Path.Combine(configDirectory, $"{Credentials}.{Guid.NewGuid():N}.tmp");
        File.Copy(source, temporary, overwrite: false);
        File.Move(temporary, target);
        return "kopyalandı";
    }, "hazırlanamadı");

    /// <summary>
    /// Removes only regular files with product-owned names, then the now-empty directory.
    /// It never recursively deletes and refuses links, a missing/foreign marker, or a moved path.
    /// </summary>
    internal static bool RemoveOwned(string configDirectory, string vault, string? localAppData = null,
        IReadOnlyList<string>? knownSyncRoots = null)
    {
        var expected = ConfigurationDirectory(vault, localAppData, knownSyncRoots);
        var actual = ResolvePath(configDirectory, out var containsLink);
        if (!Directory.Exists(configDirectory) || !PathEquals(actual, ResolvePath(expected)) || containsLink ||
            !HasValidMarker(configDirectory, vault))
            return false;

        foreach (var file in Directory.EnumerateFiles(configDirectory).Where(IsOwnedRegularFile))
            File.Delete(file);
        if (Directory.EnumerateFileSystemEntries(configDirectory).Any()) return false;
        Directory.Delete(configDirectory, recursive: false);
        return true;
    }

    internal static string UserConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static void ClaimDirectory(string directory, string vault)
    {
        var existed = Directory.Exists(directory);
        _ = ResolvePath(directory, out var containsLink);
        if (existed && (containsLink || !HasValidMarker(directory, vault)))
            throw new InvalidOperationException("Claude kimlik dizininin ürün sahipliği doğrulanamadı.");
        if (existed) return;

        Directory.CreateDirectory(directory);
        _ = ResolvePath(directory, out containsLink);
        if (containsLink)
            throw new InvalidOperationException("Claude kimlik dizini oluşturulurken başka bir konuma yönlendirildi.");
        using var stream = new FileStream(Path.Combine(directory, Ownership), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, new OwnershipMarker(1, ResolvePath(vault), ResolvePath(directory)));
    }

    private static bool HasValidMarker(string directory, string vault) => Safely(() =>
    {
        var path = Path.Combine(directory, Ownership);
        RequireRegularFileOrMissing(path);
        if (!File.Exists(path)) return false;
        var marker = JsonSerializer.Deserialize<OwnershipMarker>(File.ReadAllText(path, Utf8));
        return marker == new OwnershipMarker(1, ResolvePath(vault), ResolvePath(directory));
    }, false);

    private static T Safely<T>(Func<T> action, T failure)
    {
        try { return action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            return failure;
        }
    }

    private static bool ContentEquals(string first, string second)
    {
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(firstStream), SHA256.HashData(secondStream));
    }

    private static void RequireRegularFileOrMissing(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Ürün dosyası bir bağlantı olamaz.");
        if (Directory.Exists(path)) throw new InvalidOperationException("Ürün dosyası bir dizin olamaz.");
    }

    private static bool IsOwnedRegularFile(string file)
    {
        var name = Path.GetFileName(file);
        return (name is Credentials or "settings.json" or Ownership || name.StartsWith(Credentials + ".", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)) &&
            (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0;
    }

    internal static IReadOnlyList<string> KnownSyncRoots()
    {
        string[] variables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial", "Dropbox", "GoogleDrive"];
        var roots = variables.Select(Environment.GetEnvironmentVariable).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToList();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            roots.AddRange(new[] { "OneDrive", "Dropbox", "Google Drive" }.Select(name => Path.Combine(profile, name)).Where(Directory.Exists));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool InsideGitWorkingTree(string path)
    {
        for (var current = Directory.Exists(path) ? new DirectoryInfo(path) : new DirectoryInfo(Path.GetDirectoryName(path)!);
             current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return true;
        return false;
    }

    private static string ResolvePath(string path) => ResolvePath(path, out _);

    private static string ResolvePath(string path, out bool foundReparsePoint)
    {
        foundReparsePoint = false;
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("Yol kökü yok.", nameof(path));
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) && !File.Exists(current)) continue;
            var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            foundReparsePoint = true;
            current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ??
                throw new IOException($"Bağlantı çözülemedi: {current}");
        }
        return Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsWithin(string child, string parent)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool IsWithinAny(string child, IEnumerable<string> roots) =>
        roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(ResolvePath).Any(root => IsWithin(child, root));

    private static bool PathEquals(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
