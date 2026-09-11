using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

/// <summary>
/// The one answer to "which state directory belongs to this vault" (spec 8, D9).
///
/// There used to be two. The runtime asked <see cref="VaultPaths.StateDatabase"/>, which hashes
/// the vault string exactly as <c>vault.json</c> spells it; the installer asked its own private
/// <c>Install.StateRoot</c>, which hashed <c>Path.GetFullPath(vault)</c> first. Two functions
/// means two directories are reachable for one vault, and the one the installer provisions is
/// not necessarily the one a hook opens. The runtime's answer wins here: a hook runs twenty
/// times a day and the installer runs once, so the installer must provision where the hooks
/// will look, never the other way round.
///
/// Nothing on this type creates a directory except <see cref="EnsureDatabase"/>. A read may not
/// bring a state root into existence — that is how 26 stray roots were collected on one machine.
/// </summary>
public static class VaultIdentity
{
    /// <summary>The state database's file name inside a state root.</summary>
    public const string DatabaseName = "state.db";

    /// <summary>
    /// The descriptor a state root carries so it can be told apart from an abandoned one. Without
    /// it a root is an eight-hex-digit directory that names no vault and nobody can attribute.
    /// </summary>
    public const string DescriptorName = "vault.json";

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// The hash that names a vault's state directory. This is the runtime's function, unchanged:
    /// changing it would orphan every state root on every machine, which is a migration, not a fix.
    /// </summary>
    public static string Hash(string vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return LaneCVaultPaths.Hash(vault);
    }

    /// <summary><c>%LOCALAPPDATA%\oom\&lt;vault-hash&gt;</c>. Creates nothing.</summary>
    public static string StateRoot(string vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return LaneCVaultPaths.StateRoot(vault);
    }

    /// <summary><c>%LOCALAPPDATA%\oom\&lt;vault-hash&gt;\state.db</c>. Creates nothing.</summary>
    public static string DatabasePath(string vault) => Path.Combine(StateRoot(vault), DatabaseName);

    /// <summary>Where this vault's state root records which vault it serves. Creates nothing.</summary>
    public static string DescriptorPath(string vault) => Path.Combine(StateRoot(vault), DescriptorName);

    /// <summary>
    /// The directory every state root sits in. Derived from <see cref="StateRoot"/> rather than
    /// recomputed, so there is still exactly one definition of where state lives; the parameter
    /// exists so a test can point the scan at a fixture instead of the real profile.
    /// </summary>
    public static string StateRootsDirectory(string? localAppData = null) =>
        localAppData is { Length: > 0 }
            ? Path.Combine(localAppData, "oom")
            : Path.GetDirectoryName(StateRoot(string.Empty))!;

    /// <summary>
    /// Whether a subdirectory of <see cref="StateRootsDirectory"/> is a state root at all — the
    /// sixteen lowercase hex digits <see cref="Hash"/> produces, nothing else.
    /// </summary>
    /// <remarks>
    /// Y-163: the roots directory also holds <c>backup</c> and <c>claude-config</c>, which are
    /// not state roots. Scanning every subdirectory reported both as empty stray roots, and a
    /// report that invites the owner to delete his Claude configuration is worse than no report.
    /// </remarks>
    public static bool IsStateRootName(string? name) =>
        name is { Length: 16 } && name.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// The spelling of a vault path that two spellings of the same vault agree on. It is used to
    /// <em>detect</em> a second reachable root, never to name one: <see cref="Hash"/> stays the
    /// identity, so a vault written as <c>E:/x</c> and as <c>E:\x</c> still hashes apart and the
    /// mismatch is reported instead of being silently repointed at another database.
    /// </summary>
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

    /// <summary>Whether this spelling of the vault names the same root its canonical spelling does.</summary>
    public static bool IsCanonical(string vault) =>
        string.Equals(Hash(vault), Hash(Canonical(vault)), StringComparison.Ordinal);

    /// <summary>
    /// The vault's state database if it already exists, otherwise <c>null</c>. This is the read
    /// path's entry point: unlike <see cref="VaultPaths.StateDatabase"/> it creates no directory,
    /// so a command that only reads leaves no root behind.
    /// </summary>
    public static string? ExistingDatabase()
    {
        if (VaultPaths.ReadVault() is not { } vault)
            return null;
        var path = DatabasePath(vault);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The write path's entry point: creates the state root and returns the database path.</summary>
    public static string EnsureDatabase(string vault)
    {
        var root = StateRoot(vault);
        Directory.CreateDirectory(root);
        return Path.Combine(root, DatabaseName);
    }

    /// <summary>
    /// Stamps the state root with the vault it serves. Written once at install; an existing
    /// descriptor is left exactly as it is, because rewriting it would erase the evidence of a
    /// root that was created for a different spelling of the path.
    /// </summary>
    public static void WriteDescriptor(string vault)
    {
        var path = DescriptorPath(vault);
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { vault, schema = 1 }) + Environment.NewLine, Utf8);
    }

    /// <summary>The vault a state root says it serves, or <c>null</c> when it carries no descriptor.</summary>
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
