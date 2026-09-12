using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Oom.Contracts;

public sealed record SweepSettings(int EveryHours, int SinceHours, int MinTurns, int MaxSessionsPerRun, IReadOnlyList<string> Roots);

public sealed record ClaudeSettings(string Fast, string Smart);

public sealed record BackendSettings(ClaudeSettings Claude);

public sealed record CompileOptions(int EveningHour, int MinIntervalHours, int MaxDailiesPerRun);

public sealed record ExtensionSettings(string Name, string ContextLine);

public sealed record OomSettings(
    BackendSettings Backend,
    string RetrieveMode,
    SweepSettings Sweep,
    CompileOptions Compile,
    ContextOptions Context,
    RetrieveOptions Retrieve,
    bool McpEnabled,
    IReadOnlyList<ExtensionSettings> Extensions)
{
    public static readonly string[] KnownKeys =
        ["backend", "retrieveMode", "sweep", "compile", "context", "retrieve", "mcp", "extensions", "nudgeEvery", "reflectionMinPrompts"];

    public int NudgeEvery { get; init; } = 15;

    public int ReflectionMinPrompts { get; init; } = 5;

    private static readonly UTF8Encoding Utf8 = new(false);

    public IReadOnlyList<string> UnknownKeys { get; init; } = [];

    public string? LoadError { get; init; }

    public static readonly string[] DefaultRoots = [@"%USERPROFILE%\.claude\projects", @"%USERPROFILE%\.codex\sessions"];

    public static OomSettings Defaults(string? vault = null) => new(
        new BackendSettings(new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5")),
        "bm25",
        new SweepSettings(8, 8, 3, 20, [.. DefaultRoots.Select(Expand)]),
        new CompileOptions(18, 20, 3),
        new ContextOptions(),
        new RetrieveOptions(VaultPath: vault),
        true,
        []);

    public static string DefaultJson()
    {
        var defaults = Defaults();
        return JsonSerializer.Serialize(new
        {
            backend = new
            {
                claude = new { fast = defaults.Backend.Claude.Fast, smart = defaults.Backend.Claude.Smart }
            },
            retrieveMode = defaults.RetrieveMode,
            sweep = new
            {
                everyHours = defaults.Sweep.EveryHours,
                sinceHours = defaults.Sweep.SinceHours,
                minTurns = defaults.Sweep.MinTurns,
                maxSessionsPerRun = defaults.Sweep.MaxSessionsPerRun,
                roots = DefaultRoots
            },
            compile = new { eveningHour = defaults.Compile.EveningHour, minIntervalHours = defaults.Compile.MinIntervalHours, maxDailiesPerRun = defaults.Compile.MaxDailiesPerRun },
            context = new { companionDir = defaults.Context.CompanionDir, capChars = defaults.Context.CapChars },
            retrieve = new
            {
                top = defaults.Retrieve.Top,
                perNoteChars = defaults.Retrieve.PerNoteChars,
                totalChars = defaults.Retrieve.TotalChars
            },
            mcp = new { enabled = defaults.McpEnabled },
            nudgeEvery = defaults.NudgeEvery,
            reflectionMinPrompts = defaults.ReflectionMinPrompts,
            extensions = Array.Empty<object>()
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static OomSettings Load(string vault)
    {
        var defaults = Defaults(vault);
        var path = Path.Combine(vault, ".oom", "oom.json");
        if (!File.Exists(path))
            return defaults;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8).TrimStart('﻿'));
            var root = document.RootElement;
            return new OomSettings(
                ReadBackend(Section(root, "backend"), defaults.Backend),
                Text(root, "retrieveMode", defaults.RetrieveMode),
                ReadSweep(Section(root, "sweep"), defaults.Sweep),
                ReadCompile(Section(root, "compile"), defaults.Compile),
                ReadContext(Section(root, "context"), defaults.Context),
                ReadRetrieve(Section(root, "retrieve"), defaults.Retrieve),
                Flag(Section(root, "mcp"), "enabled", defaults.McpEnabled),
                ReadExtensions(root))
            {
                UnknownKeys = Unknown(root),
                NudgeEvery = Number(root, "nudgeEvery", defaults.NudgeEvery),
                ReflectionMinPrompts = Number(root, "reflectionMinPrompts", defaults.ReflectionMinPrompts)
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return defaults with { LoadError = $"{path}: {error.Message}" };
        }
    }

    private static BackendSettings ReadBackend(JsonElement element, BackendSettings fallback) =>
        new(ReadClaude(Section(element, "claude"), fallback.Claude));

    private static ClaudeSettings ReadClaude(JsonElement element, ClaudeSettings fallback) => new(
        Text(element, "fast", fallback.Fast),
        Text(element, "smart", fallback.Smart));

    private static SweepSettings ReadSweep(JsonElement element, SweepSettings fallback) => new(
        Number(element, "everyHours", fallback.EveryHours),
        Number(element, "sinceHours", fallback.SinceHours),
        Number(element, "minTurns", fallback.MinTurns),
        Number(element, "maxSessionsPerRun", fallback.MaxSessionsPerRun),
        [.. Strings(element, "roots", fallback.Roots).Select(Expand)]);

    private static CompileOptions ReadCompile(JsonElement element, CompileOptions fallback) => new(
        Number(element, "eveningHour", fallback.EveningHour),
        Number(element, "minIntervalHours", fallback.MinIntervalHours),
        Number(element, "maxDailiesPerRun", fallback.MaxDailiesPerRun));

    private static ContextOptions ReadContext(JsonElement element, ContextOptions fallback) => new(
        Text(element, "companionDir", fallback.CompanionDir),
        Number(element, "capChars", fallback.CapChars));

    private static RetrieveOptions ReadRetrieve(JsonElement element, RetrieveOptions fallback) => fallback with
    {
        Top = Number(element, "top", fallback.Top),
        PerNoteChars = Number(element, "perNoteChars", fallback.PerNoteChars),
        TotalChars = Number(element, "totalChars", fallback.TotalChars)
    };

    private static IReadOnlyList<ExtensionSettings> ReadExtensions(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object || !root.TryGetProperty("extensions", out var value) || value.ValueKind is not JsonValueKind.Array)
            return [];

        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.Object)
            .Select(item => new ExtensionSettings(Text(item, "name", string.Empty), Text(item, "contextLine", string.Empty)))
            .Where(extension => extension.Name.Length > 0 && extension.ContextLine.Length > 0)];
    }

    private static IReadOnlyList<string> Unknown(JsonElement root) =>
        root.ValueKind is JsonValueKind.Object
            ? [.. root.EnumerateObject().Select(property => property.Name).Where(name => !KnownKeys.Contains(name, StringComparer.Ordinal))]
            : [];

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);

    private static JsonElement Section(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;

    private static IReadOnlyList<string> Strings(JsonElement element, string name, IReadOnlyList<string> fallback)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Array)
            return fallback;

        return [.. value.EnumerateArray().Where(item => item.ValueKind is JsonValueKind.String).Select(item => item.GetString()!)];
    }

    private static string Text(JsonElement element, string name, string fallback) =>
        element.ValueKind is JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int Number(JsonElement element, string name, int fallback) =>
        element.ValueKind is JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static bool Flag(JsonElement element, string name, bool fallback) =>
        element.ValueKind is JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
