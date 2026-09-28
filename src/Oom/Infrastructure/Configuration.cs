using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Oom.Contracts;

public sealed record SweepSettings(int MinTurns, int MaxSessionsPerRun, IReadOnlyList<string> Roots);

public sealed record FlushSettings(string Mode, int SliceTurns);

public sealed record ClaudeSettings(string Fast, string Smart);

public sealed record BackendSettings(ClaudeSettings Claude);

public sealed record CompileOptions(int MaxDailiesPerRun);

public sealed record ExtensionSettings(string Name, string ContextLine);

/// <summary>A settings key whose JSON value had the wrong shape for its reader (e.g. a
/// string where a number was expected, or an unrecognised flush.mode). Kept separate from
/// <see cref="OomSettings.UnknownKeys"/> (genuinely unrecognised key NAMES) so a key that
/// happens to contain ": " can never be mis-split back apart when reported.</summary>
public sealed record InvalidSetting(string Key, string Value, string Fallback);

public sealed record OomSettings(
    BackendSettings Backend,
    SweepSettings Sweep,
    CompileOptions Compile,
    ContextOptions Context,
    RetrieveOptions Retrieve,
    bool McpEnabled,
    IReadOnlyList<ExtensionSettings> Extensions)
{
    public static readonly string[] KnownKeys =
        ["backend", "sweep", "compile", "context", "retrieve", "mcp", "extensions", "nudgeEvery", "reflectionMinPrompts", "flush"];

    public int NudgeEvery { get; init; } = 15;

    public FlushSettings Flush { get; init; } = new(DefaultFlushMode, DefaultSliceTurns);

    public int ReflectionMinPrompts { get; init; } = 5;

    public const string DefaultFlushMode = "dilim";

    public const int DefaultSliceTurns = 30;

    private static readonly string[] FlushModes = ["dilim", "tam"];

    private static readonly UTF8Encoding Utf8 = new(false);

    public IReadOnlyList<string> UnknownKeys { get; init; } = [];

    public IReadOnlyList<InvalidSetting> InvalidValues { get; init; } = [];

    public string? LoadError { get; init; }

    public static readonly string[] DefaultRoots = [@"%USERPROFILE%\.claude\projects", @"%USERPROFILE%\.codex\sessions"];

    public static OomSettings Defaults(string? vault = null) => new(
        new BackendSettings(new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5")),
        new SweepSettings(3, 20, [.. DefaultRoots.Select(Expand)]),
        new CompileOptions(3),
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
            sweep = new
            {
                minTurns = defaults.Sweep.MinTurns,
                maxSessionsPerRun = defaults.Sweep.MaxSessionsPerRun,
                roots = DefaultRoots
            },
            compile = new { maxDailiesPerRun = defaults.Compile.MaxDailiesPerRun },
            context = new { companionDir = defaults.Context.CompanionDir, capChars = defaults.Context.CapChars },
            retrieve = new
            {
                top = defaults.Retrieve.Top,
                perNoteChars = defaults.Retrieve.PerNoteChars,
                totalChars = defaults.Retrieve.TotalChars
            },
            mcp = new { enabled = defaults.McpEnabled },
            flush = new { mode = defaults.Flush.Mode, sliceTurns = defaults.Flush.SliceTurns },
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
            var invalid = new List<InvalidSetting>();
            using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8).TrimStart('﻿'));
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
                return defaults with { LoadError = $"{path}: kök bir JSON nesnesi değil ({root.ValueKind})" };

            return new OomSettings(
                ReadBackend(Section(root, "backend"), defaults.Backend, invalid),
                ReadSweep(Section(root, "sweep"), defaults.Sweep, invalid),
                ReadCompile(Section(root, "compile"), defaults.Compile, invalid),
                ReadContext(Section(root, "context"), defaults.Context, invalid),
                ReadRetrieve(Section(root, "retrieve"), defaults.Retrieve, invalid),
                Flag(Section(root, "mcp"), "enabled", defaults.McpEnabled, "mcp", invalid),
                ReadExtensions(root))
            {
                Flush = ReadFlush(Section(root, "flush"), defaults.Flush, invalid),
                UnknownKeys = Unknown(root),
                InvalidValues = invalid,
                NudgeEvery = Number(root, "nudgeEvery", defaults.NudgeEvery, string.Empty, invalid),
                ReflectionMinPrompts = Number(root, "reflectionMinPrompts", defaults.ReflectionMinPrompts, string.Empty, invalid)
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return defaults with { LoadError = $"{path}: {error.Message}" };
        }
    }

    private static BackendSettings ReadBackend(JsonElement element, BackendSettings fallback, List<InvalidSetting> invalid) =>
        new(ReadClaude(Section(element, "claude"), fallback.Claude, invalid));

    private static ClaudeSettings ReadClaude(JsonElement element, ClaudeSettings fallback, List<InvalidSetting> invalid) => new(
        Text(element, "fast", fallback.Fast, "backend.claude", invalid),
        Text(element, "smart", fallback.Smart, "backend.claude", invalid));

    private static SweepSettings ReadSweep(JsonElement element, SweepSettings fallback, List<InvalidSetting> invalid) => new(
        Number(element, "minTurns", fallback.MinTurns, "sweep", invalid),
        Number(element, "maxSessionsPerRun", fallback.MaxSessionsPerRun, "sweep", invalid),
        [.. Strings(element, "roots", fallback.Roots).Select(Expand)]);

    private static FlushSettings ReadFlush(JsonElement element, FlushSettings fallback, List<InvalidSetting> invalid)
    {
        var mode = Text(element, "mode", fallback.Mode, "flush", invalid);
        if (!FlushModes.Contains(mode, StringComparer.Ordinal))
        {
            invalid.Add(new InvalidSetting("flush.mode", mode, DefaultFlushMode));
            mode = DefaultFlushMode;
        }

        return new FlushSettings(mode, Number(element, "sliceTurns", fallback.SliceTurns, "flush", invalid));
    }

    private static CompileOptions ReadCompile(JsonElement element, CompileOptions fallback, List<InvalidSetting> invalid) => new(
        Number(element, "maxDailiesPerRun", fallback.MaxDailiesPerRun, "compile", invalid));

    private static ContextOptions ReadContext(JsonElement element, ContextOptions fallback, List<InvalidSetting> invalid) => new(
        Text(element, "companionDir", fallback.CompanionDir, "context", invalid),
        Number(element, "capChars", fallback.CapChars, "context", invalid));

    private static RetrieveOptions ReadRetrieve(JsonElement element, RetrieveOptions fallback, List<InvalidSetting> invalid) => fallback with
    {
        Top = Number(element, "top", fallback.Top, "retrieve", invalid),
        PerNoteChars = Number(element, "perNoteChars", fallback.PerNoteChars, "retrieve", invalid),
        TotalChars = Number(element, "totalChars", fallback.TotalChars, "retrieve", invalid)
    };

    private static IReadOnlyList<ExtensionSettings> ReadExtensions(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object || !root.TryGetProperty("extensions", out var value) || value.ValueKind is not JsonValueKind.Array)
            return [];

        return [.. value.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.Object)
            .Select(item => new ExtensionSettings(Text(item, "name", string.Empty, string.Empty, []), Text(item, "contextLine", string.Empty, string.Empty, [])))
            .Where(extension => extension.Name.Length > 0 && extension.ContextLine.Length > 0)];
    }

    private static IReadOnlyList<string> Unknown(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
            return [];

        var unknown = root.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !KnownKeys.Contains(name, StringComparer.Ordinal))
            .ToList();

        AddUnknown(Section(root, "backend"), ["claude"], unknown);
        AddUnknown(Section(Section(root, "backend"), "claude"), ["fast", "smart"], unknown);
        AddUnknown(Section(root, "sweep"), ["minTurns", "maxSessionsPerRun", "roots"], unknown);
        AddUnknown(Section(root, "compile"), ["maxDailiesPerRun"], unknown);
        AddUnknown(Section(root, "context"), ["companionDir", "capChars"], unknown);
        AddUnknown(Section(root, "retrieve"), ["top", "perNoteChars", "totalChars"], unknown);
        AddUnknown(Section(root, "mcp"), ["enabled"], unknown);
        AddUnknown(Section(root, "flush"), ["mode", "sliceTurns"], unknown);

        if (Section(root, "extensions") is { ValueKind: JsonValueKind.Array } extensions)
            foreach (var extension in extensions.EnumerateArray())
                AddUnknown(extension, ["name", "contextLine"], unknown);

        return [.. unknown.Distinct(StringComparer.Ordinal)];
    }

    private static void AddUnknown(JsonElement element, IReadOnlyList<string> known, List<string> unknown)
    {
        if (element.ValueKind is not JsonValueKind.Object)
            return;

        unknown.AddRange(element.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !known.Contains(name, StringComparer.Ordinal)));
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);

    private static JsonElement Section(JsonElement root, string name) =>
        root.ValueKind is JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;

    private static IReadOnlyList<string> Strings(JsonElement element, string name, IReadOnlyList<string> fallback)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Array)
            return fallback;

        return [.. value.EnumerateArray().Where(item => item.ValueKind is JsonValueKind.String).Select(item => item.GetString()!)];
    }

    private static string Text(JsonElement element, string name, string fallback, string section, List<InvalidSetting> invalid)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind is JsonValueKind.String)
            return value.GetString() ?? fallback;

        invalid.Add(new InvalidSetting(Key(section, name), Describe(value), fallback));
        return fallback;
    }

    private static int Number(JsonElement element, string name, int fallback, string section, List<InvalidSetting> invalid)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        // JsonElement.TryGetInt32 THROWS InvalidOperationException for a non-Number
        // element (it does not just return false) — the ValueKind check must come first,
        // or e.g. {"sweep":{"minTurns":"8"}} crashes every guarded command.
        if (value.ValueKind is JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;

        invalid.Add(new InvalidSetting(Key(section, name), Describe(value), fallback.ToString()));
        return fallback;
    }

    private static bool Flag(JsonElement element, string name, bool fallback, string section, List<InvalidSetting> invalid)
    {
        if (element.ValueKind is not JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.GetBoolean();

        invalid.Add(new InvalidSetting(Key(section, name), Describe(value), fallback ? "true" : "false"));
        return fallback;
    }

    private static string Key(string section, string name) => section.Length == 0 ? name : $"{section}.{name}";

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };
}
