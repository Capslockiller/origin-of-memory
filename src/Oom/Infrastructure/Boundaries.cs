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

    public static void UseVault(string? vault) =>
        _override = string.IsNullOrWhiteSpace(vault) ? null : Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);

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
            return string.IsNullOrWhiteSpace(vault) ? null : vault;
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

