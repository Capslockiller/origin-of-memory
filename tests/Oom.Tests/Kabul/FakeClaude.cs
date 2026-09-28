using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Tests.Kabul;

/// <summary>
/// One canned answer for a single invocation of the <see cref="FakeClaude"/> shim:
/// what it writes to stdout/stderr and the process exit code it returns.
/// </summary>
public sealed record FakeClaudeResponse(string Stdout, string Stderr = "", int ExitCode = 0)
{
    /// <summary>
    /// A clean `--output-format stream-json` smoke answer: a "system"/"init" event with
    /// EMPTY tools, mcp_servers and plugins, followed by a "result" event with
    /// is_error=false — SPEC-3.1.0.md B2's definition of a smoke call that passes ("init
    /// tools [] mcp [] plugins []").
    /// </summary>
    public static FakeClaudeResponse StreamJsonSmokeOk(string sessionId = "fake-smoke") => new(
        Stdout: string.Join('\n',
        [
            InitEvent(sessionId, mcpServers: "[]", plugins: "[]"),
            ResultEvent(sessionId, isError: false)
        ]));

    /// <summary>
    /// The same shape as <see cref="StreamJsonSmokeOk"/> but with a NON-empty
    /// mcp_servers list — B2's case that must still fail loud ("runner uyumsuz") even
    /// though the claude process itself exits 0 and reports is_error=false.
    /// </summary>
    public static FakeClaudeResponse StreamJsonSmokeWithMcpServer(string sessionId = "fake-smoke") => new(
        Stdout: string.Join('\n',
        [
            InitEvent(sessionId, mcpServers: "[{\"name\":\"filesystem\",\"status\":\"connected\"}]", plugins: "[]"),
            ResultEvent(sessionId, isError: false)
        ]));

    /// <summary>Same shape again, this time with a non-empty plugins list instead.</summary>
    public static FakeClaudeResponse StreamJsonSmokeWithPlugin(string sessionId = "fake-smoke") => new(
        Stdout: string.Join('\n',
        [
            InitEvent(sessionId, tools: "[]", mcpServers: "[]", plugins: "[{\"name\":\"example-plugin\"}]"),
            ResultEvent(sessionId, isError: false)
        ]));

    /// <summary>Same shape again, this time with a non-empty tools list instead (O22).</summary>
    public static FakeClaudeResponse StreamJsonSmokeWithTool(string sessionId = "fake-smoke") => new(
        Stdout: string.Join('\n',
        [
            InitEvent(sessionId, tools: "[{\"name\":\"Bash\"}]", mcpServers: "[]", plugins: "[]"),
            ResultEvent(sessionId, isError: false)
        ]));

    /// <summary>
    /// An init event as empty as <see cref="StreamJsonSmokeOk"/> (tools/mcp_servers/plugins
    /// all []) but with the FOLLOWING "result" event's is_error=true (O22) — the smoke call
    /// itself exits 0 and its init event looks clean, so only Runner's own is_error check
    /// (not the tools/mcp_servers/plugins loop) can catch this one.
    /// </summary>
    public static FakeClaudeResponse StreamJsonSmokeIsError(string sessionId = "fake-smoke") => new(
        Stdout: string.Join('\n',
        [
            InitEvent(sessionId, tools: "[]", mcpServers: "[]", plugins: "[]"),
            ResultEvent(sessionId, isError: true)
        ]));

    /// <summary>
    /// A CLI-level failure the way a `claude` build older than the B2 flag set answers an
    /// unrecognized flag: empty stdout, an "unknown option" stderr line, exit 1.
    /// </summary>
    public static FakeClaudeResponse UnknownOptionFailure() => new(
        Stdout: string.Empty,
        Stderr: "error: unknown option '--strict-mcp-config'",
        ExitCode: 1);

    /// <summary>
    /// A valid `--output-format json` summarization answer, shaped exactly the way
    /// Flush.ValidateSummary requires: the five headings, in order, each populated.
    /// </summary>
    public static FakeClaudeResponse SummaryOk() => new(
        Stdout: JsonSerializer.Serialize(new { result = ValidSummaryText }));

    public const string ValidSummaryText =
        "## Bağlam\nBağlam.\n## Önemli Konuşmalar\nKonuşma.\n## Alınan Kararlar\nKarar.\n## Öğrenilenler\nDers.\n## Yapılacaklar\nİş.";

    private static string InitEvent(string sessionId, string mcpServers, string plugins) =>
        InitEvent(sessionId, tools: "[]", mcpServers, plugins);

    private static string InitEvent(string sessionId, string tools, string mcpServers, string plugins) =>
        "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\".\",\"session_id\":\"" + sessionId + "\","
        + "\"tools\":" + tools + ",\"mcp_servers\":" + mcpServers + ",\"plugins\":" + plugins + ","
        + "\"model\":\"claude-haiku-4-5-20251001\",\"permissionMode\":\"default\",\"apiKeySource\":\"none\"}";

    private static string ResultEvent(string sessionId, bool isError) =>
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":" + (isError ? "true" : "false") + ","
        + "\"result\":\"ok\",\"session_id\":\"" + sessionId + "\"}";
}

/// <summary>
/// A fake `claude` executable for black-box Kabul tests that drive `oom flush` through
/// the real process boundary (<see cref="KabulHarness"/>) without ever calling a real
/// model. Writes a `claude.cmd` shim into its own directory. Every invocation of the
/// shim records its full argv and stdin to files numbered by 1-based CALL INDEX (so a
/// test can tell the smoke call's argv from the summarization call's argv) and answers
/// with the <see cref="FakeClaudeResponse"/> registered for that call index — the LAST
/// registered response repeats for every call index beyond the ones a test explicitly
/// configured, so a test only registers as many responses as it needs to distinguish.
///
/// Consumed read-only by later lanes (SPEC-3.1.0.md L1-runner notes): this file's public
/// surface (<see cref="Create"/>, <see cref="ReplaceProcessPath"/>, <see cref="Argv"/>,
/// <see cref="AllArgv"/>, <see cref="Stdin"/>, <see cref="CallCount"/>) is the shared
/// contract other lanes build their own FakeClaude-driven Kabul tests against.
/// Limitation: the shim records argv with `echo %*` under enabledelayedexpansion, so an
/// argument containing '!' or '%' is not recorded faithfully.
/// </summary>
public sealed class FakeClaude : IDisposable
{
    private static readonly Regex ArgvToken = new("\"([^\"]*)\"|(\\S+)", RegexOptions.Compiled);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string _directory;
    private bool _disposed;

    private FakeClaude(string directory) => _directory = directory;

    /// <summary>The directory holding the shim, its recorded calls and its canned responses.</summary>
    public string HomeDirectory => _directory;

    /// <summary>Absolute path to the fake `claude.cmd` shim itself.</summary>
    public string ShimPath => Path.Combine(_directory, "claude.cmd");

    /// <summary>
    /// Creates a fresh fake `claude` under a new subdirectory of <paramref name="root"/>,
    /// pre-loaded with <paramref name="responses"/> in call-index order (response 1 for
    /// the shim's first invocation, response 2 for its second, and so on; the last one
    /// given repeats for every call beyond that).
    /// </summary>
    public static FakeClaude Create(string root, params FakeClaudeResponse[] responses)
    {
        ArgumentNullException.ThrowIfNull(root);
        var directory = Path.Combine(root, "fake-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        for (var i = 0; i < responses.Length; i++)
        {
            var n = i + 1;
            File.WriteAllText(Path.Combine(directory, $"stdout-{n}.txt"), responses[i].Stdout, Utf8);
            File.WriteAllText(Path.Combine(directory, $"stderr-{n}.txt"), responses[i].Stderr, Utf8);
            File.WriteAllText(Path.Combine(directory, $"exit-{n}.txt"), responses[i].ExitCode.ToString(CultureInfo.InvariantCulture), Utf8);
        }

        File.WriteAllText(Path.Combine(directory, "response-count.txt"), responses.Length.ToString(CultureInfo.InvariantCulture), Utf8);
        File.WriteAllText(Path.Combine(directory, "claude.cmd"), ShimScript, Utf8);
        return new FakeClaude(directory);
    }

    /// <summary>Total number of times the shim has been invoked so far.</summary>
    public int CallCount
    {
        get
        {
            var path = Path.Combine(_directory, "call-count.txt");
            return File.Exists(path) && int.TryParse(File.ReadAllText(path, Utf8).Trim(), out var n) ? n : 0;
        }
    }

    /// <summary>
    /// The exact argument list the shim received on the given 1-based call: tokenized
    /// the same way CMD's `%*` re-quotes its own command line (a bare token per element;
    /// "" for an empty argument; the raw text for a token with no embedded space).
    /// Empty if that call never happened.
    /// </summary>
    public IReadOnlyList<string> Argv(int callIndex)
    {
        var path = Path.Combine(_directory, $"argv-{callIndex}.txt");
        if (!File.Exists(path))
            return [];

        var line = File.ReadAllText(path, Utf8).TrimEnd('\r', '\n');
        var tokens = new List<string>();
        foreach (Match match in ArgvToken.Matches(line))
            tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        return tokens;
    }

    /// <summary>Argv for every call made so far, in call order (index 0 = call 1).</summary>
    public IReadOnlyList<IReadOnlyList<string>> AllArgv() =>
        Enumerable.Range(1, CallCount).Select(Argv).ToArray();

    /// <summary>The raw stdin text the shim received on the given 1-based call (empty if none/never called).</summary>
    public string Stdin(int callIndex)
    {
        var path = Path.Combine(_directory, $"stdin-{callIndex}.txt");
        return File.Exists(path) ? File.ReadAllText(path, Utf8) : string.Empty;
    }

    /// <summary>
    /// Replaces the CURRENT PROCESS's PATH with JUST this fake's directory (not a
    /// prepend) and returns a scope that restores the previous value on Dispose.
    /// KabulHarness spawns the oom child with UseShellExecute=false and never sets PATH
    /// itself, so the child's PATH is whatever ProcessStartInfo lazily copies from THIS
    /// process's environment at the moment KabulHarness.Run builds its ProcessStartInfo —
    /// exactly the window this scope covers.
    ///
    /// A prepend is not enough: Runner.ResolveExecutable ranks every PATH-entry candidate
    /// by extension preference (.exe before .cmd) ACROSS THE WHOLE PATH, not by which
    /// directory comes first — measured on this machine, a real claude.exe elsewhere on
    /// PATH always outranks a claude.cmd shim in an earlier directory. A full replacement
    /// is the only way to guarantee this fake is the ONLY "claude" the child can resolve.
    /// Verified separately (see the lane's proof notes) that oom.exe itself, and a `.cmd`
    /// child Process.Start launches, both still run correctly with PATH restricted to a
    /// single directory — .NET launches `.cmd` files via a fixed System32\cmd.exe path,
    /// not a PATH search, and oom.exe needs nothing else from PATH in this flow.
    ///
    /// Every test in this suite runs with class-level parallelism disabled
    /// (tests/Oom.Tests/xunit.runner.json, peak concurrency 1), which is what keeps a
    /// process-wide PATH mutation safe here; do not add class-level parallelism back
    /// without re-checking this.
    /// </summary>
    public IDisposable ReplaceProcessPath()
    {
        var previous = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", _directory);
        return new RestorePath(previous);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RestorePath(string previous) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", previous);
    }

    // Records argv (%*, one call's full command line, CMD-quoted) and stdin to numbered
    // files, then answers from stdout-<idx>/stderr-<idx>/exit-<idx> where idx is this
    // call's 1-based index CLAMPED to response-count.txt (so the last registered
    // response repeats for every call beyond the ones a test configured). Findstr "^"
    // drains stdin to a file without hanging on an empty/immediately-closed stream —
    // verified empirically (see the lane's proof notes) before relying on it here.
    private const string ShimScript = @"@echo off
setlocal enabledelayedexpansion
set ""DIR=%~dp0""
set ""N=0""
if exist ""%DIR%call-count.txt"" set /p N=<""%DIR%call-count.txt""
set /a N+=1
>""%DIR%call-count.txt"" echo %N%
>""%DIR%argv-%N%.txt"" echo %*
findstr ""^"" >""%DIR%stdin-%N%.txt"" 2>nul
set ""COUNT=0""
if exist ""%DIR%response-count.txt"" set /p COUNT=<""%DIR%response-count.txt""
set /a IDX=%N%
if %COUNT% GTR 0 if %IDX% GTR %COUNT% set /a IDX=%COUNT%
if exist ""%DIR%stdout-%IDX%.txt"" type ""%DIR%stdout-%IDX%.txt""
if exist ""%DIR%stderr-%IDX%.txt"" type ""%DIR%stderr-%IDX%.txt"" 1>&2
set ""EC=0""
if exist ""%DIR%exit-%IDX%.txt"" set /p EC=<""%DIR%exit-%IDX%.txt""
exit /b %EC%
";
}
