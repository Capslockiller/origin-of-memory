using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

/// <summary>
/// Shared vault and state locations for the compile side (spec 4 and 8). No path is
/// hard-coded: the vault comes from <c>.oom/vault.json</c>, the state root from
/// %LOCALAPPDATA%. Every text boundary uses UTF-8 without BOM (spec 3).
/// </summary>
internal static class LaneCVaultPaths
{
    internal static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Vault root from the nearest <c>.oom/vault.json</c>; a local workspace when unconfigured.</summary>
    internal static string ResolveVault()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var manifest = Path.Combine(directory.FullName, ".oom", "vault.json");
            if (!File.Exists(manifest))
                continue;
            var configured = ReadVaultField(manifest);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }
        return Path.Combine(LocalAppData(), "oom", "workspace");
    }

    private static string? ReadVaultField(string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest, Utf8));
            return document.RootElement.TryGetProperty("vault", out var value) ? value.GetString() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>%LOCALAPPDATA%\oom\&lt;vault-hash&gt; — state, backup and logs live outside the synced vault (D9).</summary>
    internal static string StateRoot(string vault) => Path.Combine(LocalAppData(), "oom", Hash(vault));

    internal static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value.TrimEnd(Path.DirectorySeparatorChar))), 0, 8).ToLowerInvariant();

    private static string LocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? Path.Combine(Path.GetTempPath(), "oom-local") : local;
    }

    /// <summary>Temp file + replace, five attempts 200 ms apart (scar 10.1 #24: sync and antivirus share violations).</summary>
    internal static void WriteAtomic(string path, string content, IFileOperations operations)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = path + ".oom-tmp";
        try
        {
            File.WriteAllText(temporary, content, Utf8);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        operations.Replace(temporary, path);
                    else
                        File.Move(temporary, path);
                    return;
                }
                catch (Exception error) when (attempt < 5 && error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static string ReadText(string path) => File.ReadAllText(path, Utf8).TrimStart('﻿');
}

/// <summary>Real <see cref="IFileOperations"/>: Windows <c>File.Replace</c> is the atomic publication primitive.</summary>
internal sealed class VaultFileOperations : IFileOperations
{
    public void Replace(string source, string destination) => File.Replace(source, destination, null);
}

/// <summary>Real <see cref="IClock"/>; <c>OOM_FAKE_NOW</c> is the only clock override (spec 4.1).</summary>
internal sealed class VaultClock : IClock
{
    public DateTimeOffset Now
        => DateTimeOffset.TryParse(Environment.GetEnvironmentVariable("OOM_FAKE_NOW"), out var faked) ? faked : DateTimeOffset.Now;
}
