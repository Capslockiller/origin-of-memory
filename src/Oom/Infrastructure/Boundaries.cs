using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oom.Contracts;


public static class VaultPaths
{
    private static string? _override;

    /// <summary>
    /// Sets the vault path for this process. Canonicalized once here (SPEC-3.1.0.md
    /// F6-3, NB-2): every form the path can arrive in — as typed, lower-cased,
    /// forward-slashed — resolves to the SAME on-disk-cased path, so <see cref="VaultIdentity.Hash"/>
    /// (also self-canonicalizing, defense in depth) always names the same state directory.
    /// </summary>
    public static void UseVault(string? vault) =>
        _override = string.IsNullOrWhiteSpace(vault) ? null : VaultFiles.CanonicalPath(vault);

    public static string? ReadVault()
    {
        if (_override is not null)
            return _override;

        var descriptor = Path.Combine(AppContext.BaseDirectory, "vault.json");
        if (!File.Exists(descriptor))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(descriptor, new UTF8Encoding(false)));
            if (!document.RootElement.TryGetProperty("vault", out var value) || value.ValueKind is not JsonValueKind.String)
                return null;

            var vault = value.GetString();
            return string.IsNullOrWhiteSpace(vault) ? null : VaultFiles.CanonicalPath(vault);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? StateDatabase() =>
        ReadVault() is { } vault ? VaultIdentity.DatabasePath(vault) : null;

    public static string? EnsureStateDatabase() =>
        ReadVault() is { } vault ? VaultIdentity.EnsureDatabase(vault) : null;
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    /// <summary>
    /// OOM_FAKE_NOW is the single test seam for "now" (SPEC-3.1.0.md R1). When set to a
    /// round-trip (O) timestamp, <see cref="Now"/> returns it in any process, production
    /// included; an unparsable value is ignored and the real clock is used. Only test
    /// harnesses set it (KabulHarness, InstallScars); never set it in a real environment.
    /// </summary>
    public DateTimeOffset Now
    {
        get
        {
            var fake = Environment.GetEnvironmentVariable("OOM_FAKE_NOW");
            return fake is not null
                && DateTimeOffset.TryParse(fake, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTimeOffset.Now;
        }
    }
}

public sealed class WindowsFileOperations : IFileOperations
{
    public void Replace(string source, string destination)
    {
        if (File.Exists(destination))
            File.Replace(source, destination, null);
        else
            File.Move(source, destination, overwrite: true);
    }
}

public sealed class WindowsProcessRunner : IProcessRunner
{
    public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);

        var encoding = new UTF8Encoding(false);
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = Directory.Exists(request.WorkingDirectory) ? request.WorkingDirectory : Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };

        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in request.Environment)
            startInfo.Environment[key] = value;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            return new ProcessResult(exception.NativeErrorCode == 0 ? 127 : exception.NativeErrorCode, string.Empty,
                $"çalıştırılamadı ({request.FileName}): {exception.Message}", true);
        }
        catch (InvalidOperationException exception)
        {
            return new ProcessResult(127, string.Empty, $"çalıştırılamadı ({request.FileName}): {exception.Message}", true);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        try
        {
            process.StandardInput.Write(request.StandardInput);
            process.StandardInput.Flush();
        }
        catch (IOException)
        {
        }
        finally
        {
            process.StandardInput.Close();
        }

        if (!process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
        {
            TryKill(process);
            return new ProcessResult(-1, Read(output), Read(error), true, true);
        }

        return new ProcessResult(process.ExitCode, Read(output), Read(error), true);
    }

    private static string Read(Task<string> stream)
    {
        try
        {
            return stream.Wait(TimeSpan.FromSeconds(5)) ? stream.Result : string.Empty;
        }
        catch (AggregateException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// Vault-relative file I/O and path resolution: atomic writes, BOM-trimmed reads, the
/// vault discovery fallback for the remaining no-argument constructor (<c>new RootMap()</c>
/// — <c>Compile</c> now always requires an explicit vault root), and the state-directory
/// hash. Renamed and moved here from RootMap/VaultPaths.cs
/// (SPEC-3.1.0.md, Cut: dead-code name) — nothing about this class is RootMap-specific.
/// </summary>
internal static class VaultFiles
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

    /// <summary>
    /// Hashes the CANONICAL form of <paramref name="value"/> (SPEC-3.1.0.md F6-3, NB-2):
    /// every form the same vault path can arrive in resolves to one on-disk-cased string
    /// before hashing, so 'E:\OdenaOS', 'e:\odenaos' and 'E:/OdenaOS/' all name the same
    /// state directory. Self-canonicalizing (not just relying on callers) because this is
    /// also called directly, e.g. in tests, on a bare path string.
    /// </summary>
    internal static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(CanonicalPath(value))), 0, 8).ToLowerInvariant();

    /// <summary>
    /// Normalizes <paramref name="vault"/> to its on-disk spelling: <see cref="Path.GetFullPath(string)"/>,
    /// trim the trailing separator, then walk each path segment through the directory
    /// listing to recover the case the filesystem actually has on disk. A missing segment
    /// (including a missing leaf) keeps its typed spelling from that point down —
    /// <see cref="ResolveOnDiskCase"/> is always called, and its per-segment walk already
    /// falls back to the typed segment wherever the listing has no match — so only the
    /// unresolved tail is left as given, not the whole path.
    /// </summary>
    internal static string CanonicalPath(string vault)
    {
        string full;
        try
        {
            full = Path.GetFullPath(vault);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return vault.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        // A bare root ('E:\', '\\server\share\') must keep its trailing separator: both
        // the TrimEnd below and ResolveOnDiskCase's own TrimEnd on return would otherwise
        // strip it to 'E:', which is drive-RELATIVE (resolves against that drive's current
        // directory, not its root) — a different, and dangerous, path. Regression guard:
        // CanonicalPath(@"C:\") must equal @"C:\".
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal))
            return NormalizeDriveLetter(full);

        full = NormalizeDriveLetter(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return ResolveOnDiskCase(full);
    }

    /// <summary>
    /// A drive letter is not itself a directory entry, so the on-disk-case walk below has
    /// nothing to resolve it against; <see cref="Path.GetFullPath(string)"/> preserves
    /// whatever case the caller typed ('e:\...' stays lower-case). Fix it to upper-case —
    /// the form every other reference hash in SPEC-3.1.0.md ('E:\OdenaOS') uses — so
    /// 'e:\odenaos' and 'E:\OdenaOS' hash the same.
    /// </summary>
    private static string NormalizeDriveLetter(string full) =>
        full.Length >= 2 && full[1] == ':' && char.IsAsciiLetterLower(full[0])
            ? char.ToUpperInvariant(full[0]) + full[1..]
            : full;

    private static string ResolveOnDiskCase(string full)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var relative = full.Length > root.Length ? full[root.Length..] : string.Empty;
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        // Walk from the root DOWN, re-enumerating each level's actual directory listing
        // so every segment (not just the leaf) picks up its real on-disk spelling.
        var current = root;
        foreach (var segment in segments)
        {
            string? onDisk = null;
            try
            {
                // Narrowed by search pattern (not a full directory listing): Windows
                // matches a search pattern case-insensitively, so this already returns
                // only `segment`'s case-siblings, not every entry in `current`.
                // Ordinal exact match first: on a filesystem with per-directory case
                // sensitivity enabled (WSL, or NTFS with fsutil.exe file setCaseSensitiveInfo),
                // a directory can hold two entries differing only in case (e.g. 'Vault' and
                // 'vault'). Falling straight to OrdinalIgnoreCase could then resolve an
                // exact, existing path to a case-sibling's name instead of itself.
                var entries = Directory.EnumerateFileSystemEntries(current, segment).Select(Path.GetFileName).ToList();
                onDisk = entries.FirstOrDefault(name => string.Equals(name, segment, StringComparison.Ordinal))
                    ?? entries.FirstOrDefault(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Two on-disk spellings of the same vault path then hash to two different
                // state directories with no diagnostic (NB-2) — surface it rather than
                // silently keeping the typed spelling from this segment down.
                if (Directory.Exists(current))
                    Console.Error.WriteLine($"kasa yolu harf çözümlenemedi: {current}");
            }
            current = Path.Combine(current, onDisk ?? segment);
        }
        return current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

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

