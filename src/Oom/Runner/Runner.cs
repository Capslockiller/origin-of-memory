using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// Everything a configured executable knows about its one backend (spec 4.1, 6.6): the
/// vault the isolation is anchored at, the <c>CLAUDE_CONFIG_DIR</c> the child is given and the
/// full model ids of the two tiers.
/// </summary>
public sealed record RunnerProfile(string Vault, string ClaudeConfigDirectory, ClaudeSettings Claude);

/// <summary>
/// The only place the product talks to a model (spec 6.6). The one external contract that can
/// drift — the Claude CLI's JSON shape, and Windows executable resolution around it — is isolated
/// here and pinned by the scar tests, so when the outside world changes exactly one file breaks.
/// </summary>
public sealed class Runner
{
    private const string FastModel = "claude-haiku-4-5-20251001";
    private const string SmartModel = "claude-sonnet-5";
    private const string ClaudeBackend = "claude";
    private const string RecursionGuard = "OOM_INVOKED_BY";

    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan SmartTimeout = TimeSpan.FromSeconds(900);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Full model ids only; an alias silently resolved to another tier in v0 (scar Y-018).</summary>
    private static readonly Regex FullModelId = new(@"^claude-[a-z]+-\d+(?:-\d+)*(?:-\d{8})?$", RegexOptions.Compiled);

    /// <summary>Machine envelopes never become user memory (scar Y-010).</summary>
    private static readonly Regex[] MachineEnvelopes =
    [
        new(@"<task-notification>.*?</task-notification>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase),
        new(@"<system-reminder>.*?</system-reminder>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase),
        new(@"<local-command-(?:stdout|stderr)>.*?</local-command-(?:stdout|stderr)>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase),
        new(@"^\s*(?:tool_use|tool_result|thinking)\s*:.*$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase)
    ];

    private static readonly string[] ExecutablePreference = [".exe", ".com", ".js", ".cmd", ".bat", ".ps1", string.Empty];

    private readonly IProcessRunner _processes;
    private readonly IClock _clock;
    private readonly bool _configured;
    private readonly RunnerProfile? _profile;

    public Runner() : this((IProcessRunner?)null) { }

    public Runner(IProcessRunner? processes, IClock? clock = null, bool? configured = null)
    {
        _processes = processes ?? new WindowsProcessRunner();
        _clock = clock ?? SystemClock.Instance;
        _configured = configured ?? VaultPaths.ReadVault() is not null;
    }

    /// <summary>The configured runner: model ids and isolation directory from <c>oom.json</c>.</summary>
    public Runner(RunnerProfile profile, IProcessRunner? processes = null, IClock? clock = null)
        : this(processes, clock, true) => _profile = profile;

    /// <summary>
    /// One call to the one backend. When it does not answer the call is not lost: the sanitised
    /// transcript comes back with a non-null <c>Error</c> so the caller queues the session instead
    /// of dropping it (spec 6.3-5).
    /// </summary>
    public RunResult Run(string prompt, ModelTier tier, ComponentKind component, string purpose)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _ = component;
        _ = purpose;

        var text = StripMachineEnvelopes(prompt);
        var operationId = Guid.NewGuid().ToString("N");
        var attempt = Attempt(text, tier, operationId, Guid.NewGuid().ToString("N"), 1);
        return attempt.Error is null ? attempt : attempt with { Text = text, Model = ModelFor(tier) };
    }

    /// <summary>
    /// The isolated <c>claude -p</c> invocation. The child gets its own
    /// <c>CLAUDE_CONFIG_DIR</c> and a working directory outside the vault, so it
    /// cannot inherit the user's hooks, skills or plan mode — that inheritance
    /// rejected 21% of v0's flushes (scars Y-011, 10.1 #27). No tools are ever
    /// granted: the model returns text, <c>oom.exe</c> writes the files.
    /// </summary>
    public ProcessRequest BuildClaudeRequest(string prompt, string model, string vaultPath, string isolatedConfigDir, string? pathValue = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolatedConfigDir);
        if (!FullModelId.IsMatch(model ?? string.Empty))
            throw new ArgumentException($"model kimliği tam olmalı, takma ad kabul edilmez: '{model}'", nameof(model));

        var workingDirectory = OutsideVault(vaultPath);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = isolatedConfigDir,
            [RecursionGuard] = "oom"
        };

        string[] arguments =
        [
            "-p",
            "--output-format", "json",
            "--permission-mode", "default",
            "--tools", string.Empty,
            "--max-turns", "1",
            "--model", model!
        ];

        // Y-103: the bare backend name used to become the file name, and where npm ships only
        // claude.cmd Process.Start answered "Sistem belirtilen dosyayı bulamadı" — every session
        // landed in Retry. ResolveExecutable (Y-073) already knew the answer; this path now asks
        // it. The resolver stays pure: the environment is read here, at the call site.
        var fileName = ResolveExecutable(ClaudeBackend, pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        return new ProcessRequest(fileName, arguments, workingDirectory, environment, prompt);
    }

    /// <summary>
    /// Starts a child process, writes its stdin and closes it explicitly — an
    /// unclosed stdin is what left v0's runner hanging forever (scar Y-098) — and
    /// enforces the timeout by killing the whole process tree. A child that can
    /// not be started is reported with a non-zero exit code and a Turkish reason,
    /// never swallowed (scar Y-049); a later success heals that finding (scar Y-113).
    /// </summary>
    public ProcessResult RunProcess(ProcessRequest request, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = _processes.Run(request, timeout);
        var failed = result.TimedOut || result.ExitCode != 0;
        HealthLedger.Record(new HealthItem("hooks", failed ? HealthLevel.Error : HealthLevel.Info, "hook-failed", request.FileName,
            failed ? $"Alt süreç başarısız oldu (çıkış {result.ExitCode}) — oom doctor" : "Alt süreç başarıyla tamamlandı; önceki hata bulgusu geçersiz."), _clock.Now);
        return result;
    }

    /// <summary>
    /// Resolves a bare command against a PATH value. On Windows the npm shipped
    /// <c>codex</c> is a <c>.cmd</c> wrapper and starting it directly fails with
    /// WinError 2/193, so a runnable entry point (<c>.exe</c>, or the <c>.js</c>
    /// the wrapper launches) wins over the wrapper (scar Y-073). Resolution is
    /// pure: it reads the PATH value it is given and does not depend on the
    /// current process environment.
    /// </summary>
    public string ResolveExecutable(string command, string pathValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(pathValue);

        var candidates = new List<string>();
        foreach (var raw in pathValue.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim('"');
            var extension = Path.GetExtension(entry);
            if (extension.Length > 0)
            {
                if (Path.GetFileNameWithoutExtension(entry).Equals(command, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(entry);
                continue;
            }

            foreach (var probe in ExecutablePreference)
            {
                var full = Path.Combine(entry, command + probe);
                if (File.Exists(full))
                    candidates.Add(full);
            }
        }

        if (candidates.Count == 0)
            return command;

        return candidates
            .OrderBy(candidate => Rank(Path.GetExtension(candidate)))
            .First();

        static int Rank(string extension)
        {
            var index = Array.FindIndex(ExecutablePreference, x => x.Equals(extension, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? ExecutablePreference.Length : index;
        }
    }

    /// <summary>
    /// Keeps a 64 bit Windows HANDLE intact. v0's ctypes signature truncated it
    /// to 32 bits (scar Y-097); every interop signature here uses
    /// <see cref="IntPtr"/> or a SafeHandle.
    /// </summary>
    public IntPtr PreserveHandle(ulong handle) => new(unchecked((long)handle));

    /// <summary>
    /// A bounded wait for a detached child's outcome. The loop is finite and
    /// reports the failure: v0's waiter had no ceiling and a missing outcome hung
    /// the caller instead of turning red (scar Y-087).
    /// </summary>
    public WaitResult WaitForOutcome(string target, int maxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var path = Path.Combine(Path.GetTempPath(), "oom", "outcomes", target + ".json");
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (File.Exists(path))
                return new WaitResult(true, attempt, 0, string.Empty);
            if (attempt < maxAttempts)
                Thread.Sleep(PollInterval);
        }

        return new WaitResult(false, maxAttempts, 1, $"'{target}' sonucu {maxAttempts} denemede gelmedi — oom doctor");
    }

    private RunResult Attempt(string prompt, ModelTier tier, string operationId, string attemptId, int attemptNumber)
    {
        var model = ModelFor(tier);
        if (!_configured)
            return UnknownAttempt(string.Empty, "claude: yapılandırma yok (vault.json bulunamadı)", ClaudeBackend, model, operationId, attemptId, attemptNumber);
        if (Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return UnknownAttempt(string.Empty, "claude: özyineleme koruması etkin", ClaudeBackend, model, operationId, attemptId, attemptNumber);

        try
        {
            return CallClaude(prompt, tier, model, operationId, attemptId, attemptNumber);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return UnknownAttempt(string.Empty, $"claude: {exception.Message}", ClaudeBackend, model, operationId, attemptId, attemptNumber);
        }
    }

    private RunResult CallClaude(string prompt, ModelTier tier, string model, string operationId, string attemptId, int attemptNumber)
    {
        var vault = _profile?.Vault
            ?? Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
            ?? AppContext.BaseDirectory;
        var configDirectory = _profile?.ClaudeConfigDirectory ?? Path.Combine(AppContext.BaseDirectory, "claude-config");
        var request = BuildClaudeRequest(prompt, model, vault, configDirectory);
        var result = RunProcess(request, tier is ModelTier.Fast ? FastTimeout : SmartTimeout);
        if (result.TimedOut || result.ExitCode != 0)
            return UnknownAttempt(string.Empty, $"claude: çıkış {result.ExitCode} {result.StandardError}".Trim(), ClaudeBackend, model, operationId, attemptId, attemptNumber);

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var answer) || answer.ValueKind is not JsonValueKind.String)
            {
                // The CLI's output contract changed; this needs the user, so it is
                // a warning that notifies, plus an automatic fallback (spec 6.6).
                return UnknownAttempt(string.Empty, "warn:claude-cli-contract", ClaudeBackend, model, operationId, attemptId, attemptNumber);
            }

            var used = ReadUsedModel(root) ?? model;
            var usage = ReadClaudeUsage(root);
            return new RunResult(answer.GetString() ?? string.Empty, null, ClaudeBackend, used, UsageSource(usage), usage, operationId, attemptId, attemptNumber);
        }
        catch (JsonException)
        {
            return UnknownAttempt(result.StandardOutput, null, ClaudeBackend, model, operationId, attemptId, attemptNumber);
        }
    }

    /// <summary>
    /// <c>modelUsage</c> is keyed by model id and a single call may carry more than one entry
    /// (a mid-call fallback), so every entry is summed rather than the first one taken.
    /// </summary>
    private static TokenUsage? ReadClaudeUsage(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var usage) || usage.ValueKind is not JsonValueKind.Object)
            return null;

        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        var inputSeen = false;
        var outputSeen = false;
        var cacheReadSeen = false;
        var cacheWriteSeen = false;
        foreach (var property in usage.EnumerateObject())
        {
            if (property.Value.ValueKind is not JsonValueKind.Object) continue;
            Add(property.Value, "inputTokens", ref input, ref inputSeen);
            Add(property.Value, "outputTokens", ref output, ref outputSeen);
            Add(property.Value, "cacheReadInputTokens", ref cacheRead, ref cacheReadSeen);
            Add(property.Value, "cacheCreationInputTokens", ref cacheWrite, ref cacheWriteSeen);
        }

        if (!inputSeen && !outputSeen && !cacheReadSeen && !cacheWriteSeen)
            return null;

        return new TokenUsage(inputSeen ? input : null, outputSeen ? output : null, cacheReadSeen ? cacheRead : null, cacheWriteSeen ? cacheWrite : null);

        static void Add(JsonElement element, string name, ref long total, ref bool seen)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Number)
                return;

            total += value.GetInt64();
            seen = true;
        }
    }

    private static string UsageSource(TokenUsage? usage) => usage is null ? "unknown" : "measured";

    private static RunResult UnknownAttempt(string text, string? error, string backend, string model, string operationId, string attemptId, int attemptNumber) =>
        new(text, error, backend, model, "unknown", null, operationId, attemptId, attemptNumber);

    /// <summary>Component → tier mapping: Fast is haiku, Smart is sonnet, and there is no second endpoint.</summary>
    private string ModelFor(ModelTier tier) =>
        tier is ModelTier.Fast ? _profile?.Claude.Fast ?? FastModel : _profile?.Claude.Smart ?? SmartModel;

    private static string? ReadUsedModel(JsonElement root)
    {
        if (!root.TryGetProperty("modelUsage", out var usage) || usage.ValueKind is not JsonValueKind.Object)
            return null;

        foreach (var property in usage.EnumerateObject())
            return property.Name;

        return null;
    }

    private static string StripMachineEnvelopes(string prompt)
    {
        var text = prompt;
        foreach (var envelope in MachineEnvelopes)
            text = envelope.Replace(text, string.Empty);

        return string.Join('\n', text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => line.Trim().Length > 0)).Trim();
    }

    private static string OutsideVault(string vaultPath)
    {
        var directory = Path.Combine(Path.GetTempPath(), "oom", "run");
        Directory.CreateDirectory(directory);
        var full = Path.GetFullPath(directory);
        if (full.StartsWith(Path.GetFullPath(vaultPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("geçici çalışma dizini vault içinde; yalıtım kurulamadı");
        return full;
    }

    private static long? ReadNumber(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.Number ? value.GetInt64() : null;
}
