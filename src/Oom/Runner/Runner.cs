using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// Everything a configured executable knows about its two backends (spec 4.1, 6.6): the
/// vault the isolation is anchored at, the isolated <c>CLAUDE_CONFIG_DIR</c>, the full model
/// ids of both endpoints and the per-component backend chain.
/// </summary>
public sealed record RunnerProfile(
    string Vault,
    string ClaudeConfigDirectory,
    ClaudeSettings Claude,
    LocalSettings Local,
    IReadOnlyDictionary<ComponentKind, IReadOnlyList<string>> Chains);

/// <summary>
/// The only place the product talks to a model (spec 6.6). Every external
/// contract that can drift — the Claude CLI's JSON shape, the OpenAI compatible
/// local endpoint, Windows executable resolution — is isolated here and pinned
/// by the scar tests, so when the outside world changes exactly one file breaks.
/// </summary>
public sealed class Runner
{
    private const string FastModel = "claude-haiku-4-5-20251001";
    private const string SmartModel = "claude-sonnet-5";
    private const string LocalFastModel = "qwen3:8b";
    private const string LocalSmartModel = "qwen3:14b";
    private const string ClaudeBackend = "claude";
    private const string LocalBackend = "local";
    private const string RecursionGuard = "OOM_INVOKED_BY";
    private const int LocalMaxTokens = 2_048;
    private const int LocalDefaultNumCtx = 8_192;

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
    private readonly IHttp _http;
    private readonly IClock _clock;
    private readonly State? _state;
    private readonly string _localUrl;
    private readonly bool _configured;
    private readonly IReadOnlyDictionary<ComponentKind, IReadOnlyList<string>>? _chains;
    private readonly RunnerProfile? _profile;

    public Runner() : this((IProcessRunner?)null, null, null, null) { }

    public Runner(IProcessRunner? processes, IHttp? http = null, IClock? clock = null, State? state = null, string localUrl = "http://127.0.0.1:11434/v1", bool? configured = null, IReadOnlyDictionary<ComponentKind, IReadOnlyList<string>>? chains = null)
    {
        _processes = processes ?? new WindowsProcessRunner();
        _http = http ?? new HttpTransport();
        _clock = clock ?? SystemClock.Instance;
        _state = state;
        _localUrl = OomSettings.ValidateLocalUrl(localUrl);
        _configured = configured ?? VaultPaths.ReadVault() is not null;
        _chains = chains;
    }

    /// <summary>The configured runner: model ids, isolation directory and chains from <c>oom.json</c>.</summary>
    public Runner(RunnerProfile profile, IProcessRunner? processes = null, IHttp? http = null, IClock? clock = null, State? state = null)
        : this(processes, http, clock, state, profile.Local.Url, true, profile.Chains) => _profile = profile;

    /// <summary>
    /// Walks the component's backend list and returns the first answer. The list
    /// is sealed per component (scar Y-020): a flush that fell back to the local
    /// model must not drag compile down with it, so compile always starts — and,
    /// per D5, ends — at Claude. When no backend answers the call is not lost:
    /// the sanitised transcript comes back with a non-null <c>Error</c> so the
    /// caller queues the session instead of dropping it (spec 6.3-5).
    /// </summary>
    public RunResult Run(string prompt, ModelTier tier, ComponentKind component, string purpose)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var text = StripMachineEnvelopes(prompt);
        var requestedModel = ModelFor(ClaudeBackend, tier);
        var chain = BackendChain(component);
        var operationId = Guid.NewGuid().ToString("N");
        if (chain.Count == 0)
            return new RunResult(text, "yapılandırma yok (backend listesi boş)", "none", requestedModel, "unknown", OperationId: operationId);

        RunResult? lastAttempt = null;
        for (var index = 0; index < chain.Count; index++)
        {
            var attemptStarted = _clock.Now;
            var attempt = Attempt(chain[index], text, tier, operationId, Guid.NewGuid().ToString("N"), index + 1);
            var outcome = attempt.Error is null ? "ok" : index == chain.Count - 1 ? "fallback" : "retry";
            Record(attempt, component, tier, text.Length, attempt.Error is null ? attempt.Text.Length : 0, attemptStarted, outcome, purpose);
            if (attempt.Error is null)
                return attempt;

            lastAttempt = attempt;
        }

        return lastAttempt! with { Text = text, Model = requestedModel };
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
    /// The OpenAI compatible local request. Thinking is off and the generation
    /// cap is explicit: Ollama's default thinking mode burned the whole token
    /// budget before a single line of the answer arrived (scar Y-019).
    /// </summary>
    public LocalRequest BuildLocalRequest(string prompt, string model, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokens, 1);
        return new LocalRequest(false, 0, false, maxTokens, model, prompt);
    }

    /// <summary>
    /// Splits a local response's timings into load, prompt evaluation and
    /// generation, and says whether the model was cold or warm. A single
    /// <c>duration_ms</c> could not tell a 60 second model load from a slow
    /// answer (scar Y-021); a response without the fields is <c>unknown</c>,
    /// never a guess.
    /// </summary>
    public CallTiming ReadLocalTiming(string responseJson)
    {
        ArgumentNullException.ThrowIfNull(responseJson);
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var load = ReadNumber(root, "load_duration");
            var prompt = ReadNumber(root, "prompt_eval_duration");
            var generation = ReadNumber(root, "eval_duration");

            var temperature = (load, prompt, generation) switch
            {
                (null, null, null) => "unknown",
                (> 0, _, _) => "cold",
                _ => "warm"
            };

            return new CallTiming(temperature, load, prompt, generation);
        }
        catch (JsonException)
        {
            return new CallTiming("unknown", null, null, null);
        }
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

    private RunResult Attempt(string backend, string prompt, ModelTier tier, string operationId, string attemptId, int attemptNumber)
    {
        var model = ModelFor(backend, tier);
        if (!_configured)
            return UnknownAttempt(string.Empty, $"{backend}: yapılandırma yok (vault.json bulunamadı)", backend, model, operationId, attemptId, attemptNumber);
        if (Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return UnknownAttempt(string.Empty, $"{backend}: özyineleme koruması etkin", backend, model, operationId, attemptId, attemptNumber);

        try
        {
            return backend == ClaudeBackend
                ? CallClaude(prompt, tier, model, operationId, attemptId, attemptNumber)
                : CallLocal(prompt, tier, model, operationId, attemptId, attemptNumber);
        }
        catch (IOException exception)
        {
            return UnknownAttempt(string.Empty, $"{backend}: {exception.Message}", backend, model, operationId, attemptId, attemptNumber);
        }
        catch (InvalidOperationException exception)
        {
            return UnknownAttempt(string.Empty, $"{backend}: {exception.Message}", backend, model, operationId, attemptId, attemptNumber);
        }
    }

    private RunResult CallClaude(string prompt, ModelTier tier, string model, string operationId, string attemptId, int attemptNumber)
    {
        var vault = _profile?.Vault
            ?? Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
            ?? AppContext.BaseDirectory;
        var configDirectory = _profile?.ClaudeConfigDirectory ?? Path.Combine(AppContext.BaseDirectory, "claude-config");

        // First use creates the isolated directory and links this machine's session credential
        // into it; without that the isolated `claude -p` is unauthenticated (spec 6.6).
        IsolationState = ClaudeIsolation.Prepare(configDirectory);
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

    // Y-115: /v1/chat/completions drops num_ctx silently (verified live, Ollama 0.33.3); only the
    // native /api/chat route honours it, so local calls go there, capped to that same window.
    private RunResult CallLocal(string prompt, ModelTier tier, string model, string operationId, string attemptId, int attemptNumber)
    {
        var numCtx = _profile?.Local.NumCtx ?? LocalDefaultNumCtx;
        var capped = prompt.Length > numCtx * 3 ? prompt[..(numCtx * 3)] : prompt;
        var request = BuildLocalRequest(capped, model, LocalMaxTokens);
        var body = JsonSerializer.Serialize(new
        {
            model = request.Model,
            stream = request.Stream,
            think = request.Think,
            messages = new[] { new { role = "user", content = request.Prompt } },
            options = new { temperature = request.Temperature, num_predict = request.MaxTokens, num_ctx = numCtx }
        });

        _ = tier;
        var response = _http.Send("POST", $"{LocalRoot(_localUrl)}/api/chat", body);
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var usage = ReadLocalUsage(root);
            var content = root.GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content)
                ? new RunResult(string.Empty, "local: boş yanıt", LocalBackend, model, UsageSource(usage), usage, operationId, attemptId, attemptNumber)
                : new RunResult(content, null, LocalBackend, model, UsageSource(usage), usage, operationId, attemptId, attemptNumber);
        }
        catch (JsonException) { return UnknownAttempt(string.Empty, "local: yanıt biçimi tanınmadı", LocalBackend, model, operationId, attemptId, attemptNumber); }
        catch (KeyNotFoundException) { return UnknownAttempt(string.Empty, "local: yanıt biçimi tanınmadı", LocalBackend, model, operationId, attemptId, attemptNumber); }
    }

    private static string LocalRoot(string url) => url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;

    private void Record(RunResult attempt, ComponentKind component, ModelTier tier, int inputChars, int outputChars, DateTimeOffset started, string outcome, string purpose) =>
        _state?.RecordCall(attempt.Backend, component, tier, attempt.Model, inputChars, outputChars,
            (long)(_clock.Now - started).TotalMilliseconds, outcome, attempt.UsageQuality, purpose,
            attempt.OperationId!, attempt.AttemptId!, attempt.AttemptNumber!.Value, attempt.Usage);

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

    private static TokenUsage? ReadLocalUsage(JsonElement root)
    {
        var input = ReadNumber(root, "prompt_eval_count");
        var output = ReadNumber(root, "eval_count");
        return input is null && output is null ? null : new TokenUsage(input, output, null, null);
    }

    private static string UsageSource(TokenUsage? usage) => usage is null ? "unknown" : "measured";

    private static RunResult UnknownAttempt(string text, string? error, string backend, string model, string operationId, string attemptId, int attemptNumber) =>
        new(text, error, backend, model, "unknown", null, operationId, attemptId, attemptNumber);

    /// <summary>The component's chain from <c>oom.json</c> (spec 6.6); the spec 4.1 defaults when unconfigured.</summary>
    private IReadOnlyList<string> BackendChain(ComponentKind component) =>
        _chains is not null && _chains.TryGetValue(component, out var configured)
            ? configured
            : component is ComponentKind.Compile ? [ClaudeBackend] : [ClaudeBackend, LocalBackend];

    /// <summary>What the isolated config directory did on this run; doctor and the report read it.</summary>
    public string IsolationState { get; private set; } = "kurulmadı";

    private string ModelFor(string backend, ModelTier tier) => backend switch
    {
        ClaudeBackend => tier is ModelTier.Fast ? _profile?.Claude.Fast ?? FastModel : _profile?.Claude.Smart ?? SmartModel,
        _ => tier is ModelTier.Fast ? _profile?.Local.Fast ?? LocalFastModel : _profile?.Local.Smart ?? LocalSmartModel
    };

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
