using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed record RunnerProfile(string Vault, string ClaudeConfigDirectory, ClaudeSettings Claude);

public sealed class Runner
{
    private const string FastModel = "claude-haiku-4-5-20251001";
    private const string SmartModel = "claude-sonnet-5";
    private const string ClaudeBackend = "claude";
    private const string RecursionGuard = "OOM_INVOKED_BY";

    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan SmartTimeout = TimeSpan.FromSeconds(900);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly Regex FullModelId = new(@"^claude-[a-z]+-\d+(?:-\d+)*(?:-\d{8})?$", RegexOptions.Compiled);

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

    public Runner(RunnerProfile profile, IProcessRunner? processes = null, IClock? clock = null)
        : this(processes, clock, true) => _profile = profile;

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

        var fileName = ResolveExecutable(ClaudeBackend, pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        return new ProcessRequest(fileName, arguments, workingDirectory, environment, prompt);
    }

    public ProcessResult RunProcess(ProcessRequest request, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = _processes.Run(request, timeout);
        var failed = result.TimedOut || result.ExitCode != 0;
        HealthLedger.Record(new HealthItem("hooks", failed ? HealthLevel.Error : HealthLevel.Info, "hook-failed", request.FileName,
            failed ? $"Alt süreç başarısız oldu (çıkış {result.ExitCode}) — oom doctor" : "Alt süreç başarıyla tamamlandı; önceki hata bulgusu geçersiz."), _clock.Now);
        return result;
    }

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

    public IntPtr PreserveHandle(ulong handle) => new(unchecked((long)handle));

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
