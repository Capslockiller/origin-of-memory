using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

// The real implementations of the boundary interfaces declared in Models.cs.
// Every component takes these through its constructor so that the clock, child
// processes, HTTP and the file replace can be substituted in a test.

/// <summary>
/// Where the vault is, read from <c>vault.json</c> next to the executable — the
/// only place a path may come from. No user path is ever compiled in (spec 4,
/// scar Y-067). An executable that has not been installed yet has no vault, and
/// every caller must handle that instead of guessing a directory.
/// </summary>
public static class VaultPaths
{
    private static string? _override;

    /// <summary>
    /// The global <c>--vault &lt;path&gt;</c> option (spec 4). It exists so the published
    /// executable can be run from anywhere against any vault without being copied into it;
    /// when it is absent the vault is still only ever <c>vault.json</c> next to the exe.
    /// </summary>
    public static void UseVault(string? vault) =>
        _override = string.IsNullOrWhiteSpace(vault) ? null : Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>The vault root, or <c>null</c> when this executable is not installed.</summary>
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

    /// <summary>
    /// <c>%LOCALAPPDATA%\oom\&lt;vault-hash&gt;\state.db</c>, or <c>null</c> with no
    /// vault. The database lives outside the vault because the vault is synced by
    /// Google Drive and SQLite WAL files do not survive that (D9).
    /// </summary>
    public static string? StateDatabase()
    {
        if (ReadVault() is not { } vault)
            return null;

        // One vault, one state root: the hash is the compile side's, so `compile` and
        // `retrieve` do not end up with two databases for the same vault.
        var directory = LaneCVaultPaths.StateRoot(vault);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "state.db");
    }
}

/// <summary>
/// The one real clock (spec 4.1): every component takes <see cref="IClock"/> and every
/// production wiring resolves to this implementation, so a single run stamps one time
/// everywhere. <c>OOM_FAKE_NOW</c> is the only clock override there is and it is parsed
/// round-trip under the invariant culture, so a fake time reads the same on any locale.
/// </summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared instance; the clock is stateless, so one is enough.</summary>
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

/// <summary>
/// The real file replace. <c>File.Replace</c> is atomic on Windows but needs the
/// destination to exist; the first write of a file falls back to a move.
/// </summary>
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

/// <summary>
/// The real child process boundary: UTF-8 without BOM on every stream, stdin
/// always closed, and the tree killed when the timeout expires.
/// </summary>
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

        process.StandardInput.Write(request.StandardInput);
        process.StandardInput.Flush();
        process.StandardInput.Close();

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
            // Already gone between the timeout and the kill.
        }
    }
}

/// <summary>The real HTTP boundary for the local, OpenAI compatible endpoint.</summary>
public sealed class HttpTransport : IHttp
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(240) };

    public string Send(string method, string url, string body)
    {
        using var message = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = new StringContent(body, new UTF8Encoding(false), "application/json")
        };

        try
        {
            using var response = Client.Send(message);
            using var reader = new StreamReader(response.Content.ReadAsStream(), new UTF8Encoding(false));
            var text = reader.ReadToEnd();
            return response.IsSuccessStatusCode
                ? text
                : throw new IOException($"HTTP {(int)response.StatusCode} {Shorten(text)}");
        }
        catch (HttpRequestException exception)
        {
            throw new IOException($"bağlantı kurulamadı: {exception.Message}", exception);
        }
        catch (TaskCanceledException exception)
        {
            throw new IOException("zaman aşımı", exception);
        }
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80];
}
