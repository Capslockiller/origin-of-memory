// yazan: codex · gpt-5
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Oom.Contracts;

/// <summary>The <c>sweep</c> block of <c>oom.json</c> (spec 4.1).</summary>
public sealed record SweepSettings(int EveryHours, int SinceHours, int MinTurns, int MaxSessionsPerRun, IReadOnlyList<string> Roots);

/// <summary>The <c>backend.claude</c> block: the two full model ids (spec 6.6).</summary>
public sealed record ClaudeSettings(string Fast, string Smart);

/// <summary>The <c>backend</c> block. There is one backend — the Claude CLI — and one place it is configured.</summary>
public sealed record BackendSettings(ClaudeSettings Claude);

/// <summary>The <c>compile</c> block of <c>oom.json</c> (spec 4.1, 6.5).</summary>
public sealed record CompileOptions(int EveningHour, int MinIntervalHours, int MaxDailiesPerRun);

/// <summary>One <c>extensions</c> entry — the single extension point of spec 2.2-4.</summary>
public sealed record ExtensionSettings(string Name, string ContextLine);

/// <summary>
/// The whole of <c>oom.json</c> (spec 4.1). Every command is configured from this one file;
/// nothing reads an environment variable but <c>OOM_INVOKED_BY</c> and <c>OOM_FAKE_NOW</c>,
/// and no path is compiled in — the roots come from here and are expanded against the user's
/// own environment at read time.
/// </summary>
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
    /// <summary>Keys spec 4.1 defines; anything else is a warning for doctor, never an error.</summary>
    public static readonly string[] KnownKeys =
        ["backend", "retrieveMode", "sweep", "compile", "context", "retrieve", "mcp", "extensions", "nudgeEvery", "reflectionMinPrompts"];

    /// <summary>How many prompts apart <c>oom nudge</c> reminds the owner to write the companion layer.</summary>
    public int NudgeEvery { get; init; } = 15;

    /// <summary>Below this many prompts a session is too short to owe a reflection at all.</summary>
    public int ReflectionMinPrompts { get; init; } = 5;

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Keys the file carried that spec 4.1 does not define (spec 4.1: warning, not error).</summary>
    public IReadOnlyList<string> UnknownKeys { get; init; } = [];

    /// <summary>
    /// Why <c>oom.json</c> could not be read, when it exists but is unusable — invalid JSON,
    /// an unreadable file. Falling back to the defaults is right, doing it silently is not:
    /// the live acceptance run served a whole session from defaults while a broken file sat in
    /// the vault. <c>doctor</c> turns this into the <c>config hata json</c> row.
    /// </summary>
    public string? LoadError { get; init; }

    /// <summary>
    /// The sweep roots as they are written to disk: <c>%USERPROFILE%</c> is left unexpanded so the
    /// file stays portable between machines, and <see cref="Expand"/> resolves it on load.
    /// </summary>
    public static readonly string[] DefaultRoots = [@"%USERPROFILE%\.claude\projects", @"%USERPROFILE%\.codex\sessions"];

    /// <summary>The spec 4.1 defaults, used verbatim when <c>oom.json</c> is absent or unreadable.</summary>
    public static OomSettings Defaults(string? vault = null) => new(
        new BackendSettings(new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5")),
        "bm25",
        new SweepSettings(8, 8, 3, 20, [.. DefaultRoots.Select(Expand)]),
        new CompileOptions(18, 20, 3),
        new ContextOptions(),
        new RetrieveOptions(VaultPath: vault),
        true,
        []);

    /// <summary>
    /// The defaults as <c>oom.json</c> text — the file a fresh install writes (Y-104). The
    /// installer used to carry a second, hand-written copy of this document, and the two drifted:
    /// its <c>sweep.roots</c> was <c>[]</c>, so a clean install swept nothing, and its
    /// <c>retrieve.strictScore</c> 25,0 / <c>minOverlap</c> 2 were the pre-R2 values from lane D
    /// (`9f1c36a`), overriding the tuned ones. There is one source of truth now, and it is here.
    /// </summary>
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

    /// <summary>Reads <c>&lt;vault&gt;\.oom\oom.json</c>; an unreadable file falls back to the defaults, never to a guess.</summary>
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

    /// <summary><c>%VAR%</c> is expanded here so no user path is ever compiled in (gate 11-4).</summary>
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
