using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

/// <summary>
/// Who the runner summarizes for. <paramref name="StateDirectory"/> is where the B2 smoke
/// verdict is cached; null means the vault's own state root
/// (<see cref="VaultIdentity.StateRoot"/>, which follows OOM_LOCALAPPDATA). Tests inject
/// their own scratch directory here instead of mutating process-wide environment
/// variables (SPEC-3.1.0.md R16).
/// </summary>
public sealed record RunnerProfile(string Vault, ClaudeSettings Claude, string? StateDirectory = null);

public sealed class Runner
{
    private const string FastModel = "claude-haiku-4-5-20251001";
    private const string SmartModel = "claude-sonnet-5";
    private const string ClaudeBackend = "claude";
    private const string RecursionGuard = "OOM_INVOKED_BY";
    private const int FailureDetailCap = 500;
    private const int SmokeNamePreview = 3;

    /// <summary>Every smoke verdict's <see cref="RunResult.Error"/> starts with this marker.</summary>
    public const string IncompatibleMarker = "runner uyumsuz";
    private const string RunnerIncompatible = IncompatibleMarker;
    private const string SmokeCacheFilePrefix = "runner-smoke-";
    private const string SmokePrompt = "ping";
    private const string SmokeSystemPrompt = "Bu bir uyumluluk kontrolüdür, hiçbir işlem yapma; yalnızca 'ok' yaz.";
    private const string SummarizerSystemPrompt =
        "Sen yalnızca metin özetleyen bir alt süreçsin. Araç çağırmaz, kod veya komut çalıştırmaz, dosya değiştirmezsin. " +
        "Sana verilen transkripti istenen başlıklarla özetle, yürütme, yalnızca özetle.";

    private static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(240);
    private static readonly TimeSpan SmartTimeout = TimeSpan.FromSeconds(900);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan SmokeCacheLifetime = TimeSpan.FromHours(24);

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
        return Attempt(text, tier, operationId, Guid.NewGuid().ToString("N"), 1);
    }

    public ProcessRequest BuildClaudeRequest(string prompt, string model, string vaultPath, string? pathValue = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultPath);
        if (!FullModelId.IsMatch(model ?? string.Empty))
            throw new ArgumentException($"model kimliği tam olmalı, takma ad kabul edilmez: '{model}'", nameof(model));

        var workingDirectory = OutsideVault(vaultPath);
        var environment = ChildEnvironment();
        var arguments = MeasuredFlagSet(model!, SummarizerSystemPrompt).Append("--output-format").Append("json").ToArray();

        var fileName = ResolveExecutable(ClaudeBackend, pathValue ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        return new ProcessRequest(fileName, arguments, workingDirectory, environment, prompt);
    }

    /// <summary>
    /// The B2-measured flag set (SPEC-3.1.0.md, binding amendment B2): no --bare (it does
    /// not read OAuth credentials, and every real flush would fail with "Not logged in");
    /// --strict-mcp-config --no-session-persistence --setting-sources project --tools ""
    /// --max-turns 1 --model &lt;full id&gt; --system-prompt &lt;prompt&gt;, always prefixed
    /// with -p. Shared by the real summarization request and the startup smoke request so
    /// the two can never drift apart.
    /// </summary>
    private static IEnumerable<string> MeasuredFlagSet(string model, string systemPrompt) =>
    [
        "-p",
        "--strict-mcp-config",
        "--no-session-persistence",
        "--setting-sources", "project",
        "--tools", string.Empty,
        "--max-turns", "1",
        "--model", model,
        "--system-prompt", systemPrompt
    ];

    private static Dictionary<string, string> ChildEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase) { [RecursionGuard] = "oom" };

    private ProcessRequest BuildSmokeRequest(string model, string vaultPath, string pathValue)
    {
        var workingDirectory = OutsideVault(vaultPath);
        var environment = ChildEnvironment();
        var arguments = MeasuredFlagSet(model, SmokeSystemPrompt).Append("--output-format").Append("stream-json").Append("--verbose").ToArray();
        var fileName = ResolveExecutable(ClaudeBackend, pathValue);
        return new ProcessRequest(fileName, arguments, workingDirectory, environment, SmokePrompt);
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

    private RunResult Attempt(string prompt, ModelTier tier, string operationId, string attemptId, int attemptNumber)
    {
        var model = ModelFor(tier);
        if (!_configured)
            return UnknownAttempt(string.Empty, "claude: yapılandırma yok (vault.json bulunamadı)", ClaudeBackend, model, operationId, attemptId, attemptNumber);
        if (Environment.GetEnvironmentVariable(RecursionGuard) is { Length: > 0 })
            return UnknownAttempt(string.Empty, "claude: özyineleme koruması etkin", ClaudeBackend, model, operationId, attemptId, attemptNumber);
        // Checked before the smoke call: an alias ("haiku") in oom.json must not spend a
        // real smoke call and then throw from BuildClaudeRequest.
        if (!FullModelId.IsMatch(model))
            return UnknownAttempt(string.Empty, $"claude: model kimliği tam olmalı, takma ad kabul edilmez: '{model}'", ClaudeBackend, model, operationId, attemptId, attemptNumber);

        var vault = ResolveVault();
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        try
        {
            if (SmokeCheck(model, vault, pathValue) is { } incompatible)
                return UnknownAttempt(string.Empty, incompatible, ClaudeBackend, model, operationId, attemptId, attemptNumber);

            return CallClaude(prompt, tier, model, vault, pathValue, operationId, attemptId, attemptNumber);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return UnknownAttempt(string.Empty, $"claude: {exception.Message}", ClaudeBackend, model, operationId, attemptId, attemptNumber);
        }
    }

    private string ResolveVault() =>
        _profile?.Vault
        ?? Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
        ?? AppContext.BaseDirectory;

    /// <summary>
    /// B2 (binding): validates the measured flag set works against the installed claude
    /// build before every real summarization call, reading tools/mcp_servers/plugins from
    /// the stream-json init event (--output-format json carries none of those fields).
    /// Only a PASSING verdict is cached (SPEC-3.1.0.md R16), per claude exe path + mtime in
    /// the state dir for <see cref="SmokeCacheLifetime"/>, so a healthy runner is validated
    /// once per day, not once per flush. A failure is never cached, not on disk and not in
    /// memory: every later Run probes again, so a fixed login or config takes effect at
    /// once. Returns null when the runner is compatible, or a detail starting with
    /// <see cref="IncompatibleMarker"/> when it is not.
    /// </summary>
    private string? SmokeCheck(string model, string vault, string pathValue)
    {
        var request = BuildSmokeRequest(model, vault, pathValue);
        var key = SmokeCacheKey(request.FileName, request.Arguments);
        var cacheDirectory = SmokeCacheDirectory();

        if (cacheDirectory is not null && HasCachedPass(cacheDirectory, key))
            return null;

        var verdict = ProbeSmoke(request);
        HealthLedger.Record(new HealthItem("runner", verdict is null ? HealthLevel.Info : HealthLevel.Error, "runner-uyumsuz", request.FileName,
            verdict ?? "Özetleyici duman çağrısı geçti; önceki 'runner uyumsuz' bulgusu geçersiz."), _clock.Now);

        if (verdict is null && cacheDirectory is not null)
            WriteSmokePass(cacheDirectory, key);

        return verdict;
    }

    private string? ProbeSmoke(ProcessRequest request)
    {
        var result = RunProcess(request, FastTimeout);
        if (result.TimedOut || result.ExitCode != 0)
            return $"{RunnerIncompatible}: {FailureDetail(result)}";

        if (!TryReadInitEvent(result.StandardOutput, out var init))
            return $"{RunnerIncompatible}: duman yanıtı çözümlenemedi (init olayı yok) — stdout: {Clip(result.StandardOutput.Trim())}";

        foreach (var property in new[] { "tools", "mcp_servers", "plugins" })
        {
            if (!init.TryGetProperty(property, out var list) || list.ValueKind is not JsonValueKind.Array)
                return $"{RunnerIncompatible}: duman init olayında {property} yok";
            if (list.GetArrayLength() > 0)
                return Clip($"{RunnerIncompatible}: duman init olayında {property} dolu: {EntryNames(list)}");
        }

        if (!TryReadResultIsError(result.StandardOutput, out var refused))
            return $"{RunnerIncompatible}: duman yanıtında result olayı yok";
        if (refused)
            return $"{RunnerIncompatible}: duman çağrısı is_error=true döndürdü";

        return null;
    }

    private static string EntryNames(JsonElement list)
    {
        var names = list.EnumerateArray()
            .Select(entry => entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("name", out var name) && name.ValueKind is JsonValueKind.String => name.GetString(),
                _ => entry.GetRawText()
            })
            .Take(SmokeNamePreview)
            .ToList();
        var more = list.GetArrayLength() - names.Count;
        return string.Join(", ", names) + (more > 0 ? $" (+{more})" : string.Empty);
    }

    private static bool TryReadInitEvent(string streamJson, out JsonElement init)
    {
        foreach (var line in SplitJsonLines(streamJson))
        {
            if (!TryParseEvent(line, out var root))
                continue;
            if (!IsEvent(root, "system", "init"))
                continue;

            init = root;
            return true;
        }

        init = default;
        return false;
    }

    private static bool TryReadResultIsError(string streamJson, out bool isError)
    {
        isError = false;
        foreach (var line in SplitJsonLines(streamJson))
        {
            if (!TryParseEvent(line, out var root))
                continue;
            if (!IsEvent(root, "result", null))
                continue;

            isError = root.TryGetProperty("is_error", out var value) && value.ValueKind is JsonValueKind.True;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitJsonLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Where(line => line.Trim().Length > 0);

    private static bool TryParseEvent(string line, out JsonElement root)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
            return root.ValueKind is JsonValueKind.Object;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static bool IsEvent(JsonElement root, string type, string? subtype)
    {
        if (!root.TryGetProperty("type", out var typeValue) || typeValue.ValueKind is not JsonValueKind.String
            || !string.Equals(typeValue.GetString(), type, StringComparison.Ordinal))
            return false;

        if (subtype is null)
            return true;

        return root.TryGetProperty("subtype", out var subtypeValue) && subtypeValue.ValueKind is JsonValueKind.String
            && string.Equals(subtypeValue.GetString(), subtype, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profile's injected state dir (created on demand), else the profiled vault's
    /// state root but only when it already exists: the real state dir always holds
    /// state.db (the CLI opens it before flushing), so the runner never creates a stray
    /// state directory under %LOCALAPPDATA%\oom for a vault that has none (SPEC R16). An
    /// unprofiled Runner (e.g. `new Save()` → `new Flush()` → `new Runner()`) has no real
    /// vault — only the exe's own directory — so it probes without caching.
    /// </summary>
    private string? SmokeCacheDirectory()
    {
        if (_profile is null)
            return null;
        if (_profile.StateDirectory is { Length: > 0 } injected)
            return injected;
        if (string.IsNullOrWhiteSpace(_profile.Vault))
            return null;
        var root = VaultIdentity.StateRoot(_profile.Vault);
        return Directory.Exists(root) ? root : null;
    }

    /// <summary>
    /// One cache file per key, so flush (fast model) and compile (smart model) keep
    /// their own pass instead of overwriting each other's and re-probing on every switch.
    /// </summary>
    private static string SmokeCacheFile(string directory, string key)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(directory, $"{SmokeCacheFilePrefix}{digest[..12]}.json");
    }

    /// <summary>
    /// exe path + mtime (the claude build) + a hash of the exact smoke argv (flag set,
    /// model, system prompt) and this oom build's version, so a new flag set or a model
    /// that was never probed cannot reuse an older pass.
    /// </summary>
    private static string SmokeCacheKey(string exePath, IReadOnlyList<string> arguments)
    {
        string stamp;
        try
        {
            stamp = File.Exists(exePath) ? File.GetLastWriteTimeUtc(exePath).ToString("O", CultureInfo.InvariantCulture) : "missing";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stamp = "unknown";
        }

        var version = typeof(Runner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var material = string.Join('\u0001', arguments.Prepend(version));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"{exePath}|{stamp}|{digest}";
    }

    /// <summary>
    /// True only for a fresh cache entry with the same key that records a pass. An entry
    /// with a non-empty verdict (written by an earlier build that also cached failures) is
    /// ignored, never trusted.
    /// </summary>
    private bool HasCachedPass(string directory, string key)
    {
        try
        {
            var path = SmokeCacheFile(directory, key);
            if (!File.Exists(path))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("key", out var storedKey) || storedKey.ValueKind is not JsonValueKind.String
                || !string.Equals(storedKey.GetString(), key, StringComparison.Ordinal))
                return false;
            if (!root.TryGetProperty("checkedAt", out var checkedAtValue) || checkedAtValue.ValueKind is not JsonValueKind.String
                || !DateTimeOffset.TryParseExact(checkedAtValue.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var checkedAt))
                return false;
            // A checkedAt in the future (clock moved back) is a miss, not an entry that never expires.
            if (checkedAt > _clock.Now || _clock.Now - checkedAt >= SmokeCacheLifetime)
                return false;

            return root.TryGetProperty("verdict", out var verdict) && verdict.ValueKind is JsonValueKind.String
                && verdict.GetString() is { Length: 0 };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Written through a unique temp file and a rename, so concurrent SessionEnd hooks
    /// never leave a torn cache file. A failed write only costs a repeat probe next time;
    /// it is recorded as a warning, not swallowed.
    /// </summary>
    private void WriteSmokePass(string directory, string key)
    {
        var path = SmokeCacheFile(directory, key);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new { key, checkedAt = _clock.Now.ToString("O", CultureInfo.InvariantCulture), verdict = string.Empty });
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            HealthLedger.Record(new HealthItem("runner", HealthLevel.Warning, "runner-duman-onbellegi", path,
                $"Duman önbelleği yazılamadı ({error.Message}); sonraki çağrı yeniden dener."), _clock.Now);
        }
    }

    private RunResult CallClaude(string prompt, ModelTier tier, string model, string vault, string pathValue, string operationId, string attemptId, int attemptNumber)
    {
        var request = BuildClaudeRequest(prompt, model, vault, pathValue);
        var result = RunProcess(request, tier is ModelTier.Fast ? FastTimeout : SmartTimeout);
        if (result.TimedOut || result.ExitCode != 0)
            return UnknownAttempt(string.Empty, FailureDetail(result), ClaudeBackend, model, operationId, attemptId, attemptNumber);

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

    private static string FailureDetail(ProcessResult result)
    {
        var detail = new StringBuilder("claude: çıkış ").Append(result.ExitCode);
        if (result.TimedOut)
            detail.Append(" (zaman aşımı)");
        var error = result.StandardError.Trim();
        detail.Append('\n').Append("stderr: ").Append(error.Length == 0 ? "(boş)" : Clip(error));
        var output = result.StandardOutput.Trim();
        detail.Append('\n').Append("stdout: ").Append(output.Length == 0 ? "(boş)" : Clip(output));
        return detail.ToString();
    }

    private static string Clip(string text) => text.Length <= FailureDetailCap ? text : text[..FailureDetailCap] + "…";

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
}
