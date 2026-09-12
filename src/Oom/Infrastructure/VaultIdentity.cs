using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

public static class VaultIdentity
{
    public const string DatabaseName = "state.db";

    public const string DescriptorName = "vault.json";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static string Hash(string vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return LaneCVaultPaths.Hash(vault);
    }

    public static string StateRoot(string vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return LaneCVaultPaths.StateRoot(vault);
    }

    public static string DatabasePath(string vault) => Path.Combine(StateRoot(vault), DatabaseName);

    public static string DescriptorPath(string vault) => Path.Combine(StateRoot(vault), DescriptorName);

    public static string StateRootsDirectory(string? localAppData = null) =>
        localAppData is { Length: > 0 }
            ? Path.Combine(localAppData, "oom")
            : Path.GetDirectoryName(StateRoot(string.Empty))!;

    public static bool IsStateRootName(string? name) =>
        name is { Length: 16 } && name.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static string Canonical(string vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        try
        {
            return Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return vault.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static bool IsCanonical(string vault) =>
        string.Equals(Hash(vault), Hash(Canonical(vault)), StringComparison.Ordinal);

    public static string? ExistingDatabase()
    {
        if (VaultPaths.ReadVault() is not { } vault)
            return null;
        var path = DatabasePath(vault);
        return File.Exists(path) ? path : null;
    }

    public static string EnsureDatabase(string vault)
    {
        var root = StateRoot(vault);
        Directory.CreateDirectory(root);
        return Path.Combine(root, DatabaseName);
    }

    public static void WriteDescriptor(string vault)
    {
        var path = DescriptorPath(vault);
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { vault, schema = 1 }) + Environment.NewLine, Utf8);
    }

    public static string? ReadDescriptor(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        var path = Path.Combine(stateRoot, DescriptorName);
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8).TrimStart('\uFEFF'));
            return document.RootElement.TryGetProperty("vault", out var value) && value.ValueKind is JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
