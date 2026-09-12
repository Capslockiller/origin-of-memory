using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

internal static class LaneCVaultPaths
{
    internal static readonly UTF8Encoding Utf8 = new(false);

    internal static string ResolveVault()
    {
        if (VaultPaths.ReadVault() is { Length: > 0 } declared)
            return declared;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var manifest = Path.Combine(directory.FullName, ".oom", "vault.json");
            if (!File.Exists(manifest))
                continue;
            var configured = ReadVaultField(manifest);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
        }
        return Path.Combine(Path.GetTempPath(), "oom", $"workspace-{Environment.ProcessId}");
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

    internal static string StateRoot(string vault) => Path.Combine(LocalAppData(), "oom", Hash(vault));

    internal static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value.TrimEnd(Path.DirectorySeparatorChar))), 0, 8).ToLowerInvariant();

    private static string LocalAppData()
    {
        if (Environment.GetEnvironmentVariable("OOM_LOCALAPPDATA") is { Length: > 0 } redirected)
            return redirected;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? Path.Combine(Path.GetTempPath(), "oom-local") : local;
    }

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

internal sealed class VaultFileOperations : IFileOperations
{
    public void Replace(string source, string destination) => File.Replace(source, destination, null);
}
