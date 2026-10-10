using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Oom.Contracts;

public sealed record SweepSettings(int MinTurns, int MaxSessionsPerRun, IReadOnlyList<string> Roots);

/// <summary>A settings key whose JSON value had the wrong shape for its reader (e.g. a
/// string where a number was expected, or an unrecognised flush.mode). Kept separate from
/// <see cref="OomSettings.UnknownKeys"/> (genuinely unrecognised key NAMES) so a key that
/// happens to contain ": " can never be mis-split back apart when reported.</summary>
public sealed record InvalidSetting(string Key, string Value, string Fallback);

public sealed record OomSettings(
    SweepSettings Sweep,
    ContextOptions Context,
    RetrieveOptions Retrieve)
{
    public static readonly string[] KnownKeys = ["sweep", "context", "retrieve"];

    private static readonly UTF8Encoding Utf8 = new(false);

    public IReadOnlyList<string> UnknownKeys { get; init; } = [];

    public IReadOnlyList<InvalidSetting> InvalidValues { get; init; } = [];

    public string? LoadError { get; init; }

    public static readonly string[] DefaultRoots = [@"%USERPROFILE%\.claude\projects", @"%USERPROFILE%\.codex\sessions"];

    public static OomSettings Defaults(string? vault = null) => new(
        new SweepSettings(3, 20, [.. DefaultRoots.Select(Expand)]),
        new ContextOptions(),
        new RetrieveOptions(VaultPath: vault));

    public static string DefaultJson()
    {
        var defaults = Defaults();
        return JsonSerializer.Serialize(new
        {
            sweep = new
            {
                minTurns = defaults.Sweep.MinTurns,
                maxSessionsPerRun = defaults.Sweep.MaxSessionsPerRun,
                roots = DefaultRoots
            },
            context = new { companionDir = defaults.Context.CompanionDir, capChars = defaults.Context.CapChars },
            retrieve = new
            {
                top = defaults.Retrieve.Top,
                perNoteChars = defaults.Retrieve.PerNoteChars,
                totalChars = defaults.Retrieve.TotalChars
            }
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
                ReadSweep(Section(root, "sweep"), defaults.Sweep, invalid),
                ReadContext(Section(root, "context"), defaults.Context, invalid),
                ReadRetrieve(Section(root, "retrieve"), defaults.Retrieve, invalid))
            {
                InvalidValues = invalid
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            return defaults with { LoadError = $"{path}: {error.Message}" };
        }
    }

    private static SweepSettings ReadSweep(JsonElement element, SweepSettings fallback, List<InvalidSetting> invalid) => new(
        Number(element, "minTurns", fallback.MinTurns, "sweep", invalid),
        Number(element, "maxSessionsPerRun", fallback.MaxSessionsPerRun, "sweep", invalid),
        [.. Strings(element, "roots", fallback.Roots).Select(Expand)]);

    private static ContextOptions ReadContext(JsonElement element, ContextOptions fallback, List<InvalidSetting> invalid) => new(
        Text(element, "companionDir", fallback.CompanionDir, "context", invalid),
        Number(element, "capChars", fallback.CapChars, "context", invalid));

    private static RetrieveOptions ReadRetrieve(JsonElement element, RetrieveOptions fallback, List<InvalidSetting> invalid) => fallback with
    {
        Top = Number(element, "top", fallback.Top, "retrieve", invalid),
        PerNoteChars = Number(element, "perNoteChars", fallback.PerNoteChars, "retrieve", invalid),
        TotalChars = Number(element, "totalChars", fallback.TotalChars, "retrieve", invalid)
    };

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

    private static string Key(string section, string name) => section.Length == 0 ? name : $"{section}.{name}";

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };
}
