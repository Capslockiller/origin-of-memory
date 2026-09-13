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

}
