using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

internal sealed record HubDefinition(string Id, string Name, string Scope, IReadOnlyList<string> Tags, IReadOnlyList<string> TitleKeys);

internal sealed record HubConfiguration(string CatchAll, IReadOnlyList<HubDefinition> Hubs);

public sealed class RootMap
{
    private const int IndexCharacterCap = 4_000;
    private const int SummaryCap = 80;

    private readonly string _vault;
    private readonly Notes _notes;
    private readonly TurkishFold _fold;
    private readonly IFileOperations _files;
    private HubConfiguration? _configuration;

    public RootMap() : this(LaneCVaultPaths.ResolveVault())
    {
    }

    public RootMap(string vaultRoot, Notes? notes = null, TurkishFold? fold = null, IFileOperations? files = null)
    {
        _vault = vaultRoot;
        _notes = notes ?? new Notes();
        _fold = fold ?? new TurkishFold();
        _files = files ?? new VaultFileOperations();
    }

    public string Regenerate()
    {
        var configuration = Configuration;
        var corpus = LoadCorpus();
        var buckets = configuration.Hubs.ToDictionary(hub => hub.Id, _ => new List<Note>(), StringComparer.Ordinal);
        foreach (var note in corpus)
            foreach (var id in HubsFor(configuration, note.Title + "\n" + note.Body, note.Tags))
                buckets[id].Add(note);

        var index = BuildIndex(configuration, buckets);
        LaneCVaultPaths.WriteAtomic(Path.Combine(_vault, "knowledge", "index.md"), index, _files);
        foreach (var hub in configuration.Hubs)
            LaneCVaultPaths.WriteAtomic(Path.Combine(_vault, "knowledge", "hubs", hub.Id + ".md"), BuildHubFile(hub, buckets[hub.Id]), _files);
        WriteFullTable(corpus);
        return index;
    }

    public IReadOnlyList<string> Assign(string dailyText)
        => HubsFor(Configuration, dailyText ?? string.Empty, []);

    internal IReadOnlyList<string> HubsForNote(Note note)
        => HubsFor(Configuration, note.Title + "\n" + note.Body, note.Tags);

    private IReadOnlyList<string> HubsFor(HubConfiguration configuration, string text, IReadOnlyList<string> tags)
    {
        var folded = _fold.Fold(text);
        var foldedTags = tags.Select(tag => _fold.Fold(tag)).ToHashSet(StringComparer.Ordinal);
        var matched = new List<string>();
        foreach (var hub in configuration.Hubs)
        {
            if (string.Equals(hub.Id, configuration.CatchAll, StringComparison.Ordinal))
                continue;
            var hit = hub.Tags.Any(tag => foldedTags.Contains(_fold.Fold(tag)) || folded.Contains(_fold.Fold(tag), StringComparison.Ordinal))
                || hub.TitleKeys.Any(key => folded.Contains(_fold.Fold(key), StringComparison.Ordinal));
            if (hit)
                matched.Add(hub.Id);
        }
        if (matched.Count == 0)
            matched.Add(configuration.CatchAll);
        return matched;
    }

    private static string BuildIndex(HubConfiguration configuration, IReadOnlyDictionary<string, List<Note>> buckets)
    {
        var scopes = configuration.Hubs.ToDictionary(hub => hub.Id, hub => hub.Scope, StringComparer.Ordinal);
        for (var attempt = 0; ; attempt++)
        {
            var builder = new StringBuilder();
            foreach (var hub in configuration.Hubs)
                builder.Append("- **").Append(hub.Name).Append("** (").Append(buckets[hub.Id].Count)
                    .Append(" kavram) — ").Append(scopes[hub.Id]).Append(" → [[hubs/").Append(hub.Id).Append("]]\n");
            var text = builder.ToString();
            if (text.Length <= IndexCharacterCap)
                return text;
            var widest = scopes.OrderByDescending(pair => pair.Value.Length).First();
            if (widest.Value.Length <= 12 || attempt > configuration.Hubs.Count * 4)
                return text[..IndexCharacterCap];
            scopes[widest.Key] = widest.Value[..(widest.Value.Length / 2)].TrimEnd() + "…";
        }
    }

    private static string BuildHubFile(HubDefinition hub, IReadOnlyList<Note> notes)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(hub.Name).Append("\n\n").Append(hub.Scope).Append("\n\n");
        builder.Append("| Kavram | Özet | Güncellendi |\n| --- | --- | --- |\n");
        foreach (var note in notes.OrderBy(note => note.Name, StringComparer.Ordinal))
            builder.Append("| [[concepts/").Append(Path.GetFileNameWithoutExtension(note.Name)).Append('|').Append(Cell(note.Title))
                .Append("]] | ").Append(Cell(Summarize(note))).Append(" | ").Append(note.Updated.ToString("yyyy-MM-dd")).Append(" |\n");
        return builder.ToString();
    }

    private void WriteFullTable(IReadOnlyList<Note> corpus)
    {
        var path = Path.Combine(_vault, "knowledge", "index-full.md");
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        if (File.Exists(path))
            foreach (var line in LaneCVaultPaths.ReadText(path).Split('\n'))
            {
                var cells = line.Split('|');
                if (cells.Length < 6 || line.Contains("---", StringComparison.Ordinal) || line.Contains("Makale", StringComparison.Ordinal))
                    continue;
                var key = cells[1].Trim();
                if (key.Length > 0 && rows.TryAdd(key, line.TrimEnd('\r')))
                    order.Add(key);
            }

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in corpus)
        {
            var key = Cell(note.Title);
            live.Add(key);
            var row = "| " + key + " | " + Cell(Summarize(note)) + " | " + Cell(string.Join(", ", note.Sources)) + " | " + note.Updated.ToString("yyyy-MM-dd") + " |";
            if (!rows.ContainsKey(key))
                order.Add(key);
            rows[key] = row;
        }

        var builder = new StringBuilder("| Makale | Özet | Kaynak | Güncellendi |\n| --- | --- | --- | --- |\n");
        foreach (var key in order.Where(live.Contains))
            builder.Append(rows[key]).Append('\n');
        LaneCVaultPaths.WriteAtomic(path, builder.ToString(), _files);
    }

    private IReadOnlyList<Note> LoadCorpus()
    {
        var directory = Path.Combine(_vault, "knowledge", "concepts");
        if (!Directory.Exists(directory))
            return [];
        var corpus = new List<Note>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                corpus.Add(_notes.Parse("knowledge/concepts/" + Path.GetFileName(file), LaneCVaultPaths.ReadText(file)));
            }
            catch (Exception error) when (error is FormatException or IOException)
            {
            }
        }
        return corpus;
    }

    private HubConfiguration Configuration => _configuration ??= LoadConfiguration();

    private HubConfiguration LoadConfiguration()
    {
        var path = Path.Combine(_vault, ".oom", "hub-config.json");
        if (File.Exists(path))
            try
            {
                using var document = JsonDocument.Parse(LaneCVaultPaths.ReadText(path));
                var root = document.RootElement;
                var catchAll = root.TryGetProperty("catch_all", out var value) ? value.GetString() ?? "genel" : "genel";
                var hubs = new List<HubDefinition>();
                if (root.TryGetProperty("hubs", out var array) && array.ValueKind == JsonValueKind.Array)
                    foreach (var element in array.EnumerateArray())
                        hubs.Add(new HubDefinition(
                            Text(element, "id") ?? catchAll, Text(element, "ad") ?? Text(element, "id") ?? "Genel",
                            Text(element, "kapsam") ?? string.Empty, List(element, "tags"), List(element, "title_keys")));
                if (hubs.Count > 0)
                {
                    if (!hubs.Any(hub => string.Equals(hub.Id, catchAll, StringComparison.Ordinal)))
                        hubs.Add(DefaultCatchAll(catchAll));
                    var tail = hubs.First(hub => string.Equals(hub.Id, catchAll, StringComparison.Ordinal));
                    return new HubConfiguration(catchAll, [.. hubs.Where(hub => !ReferenceEquals(hub, tail)), tail]);
                }
            }
            catch (Exception error) when (error is JsonException or IOException)
            {
            }
        return new HubConfiguration("genel", [DefaultCatchAll("genel")]);
    }

    private static HubDefinition DefaultCatchAll(string id)
        => new(id, "Genel", "Henüz bir hub'a ayrılmamış kavramlar.", [], []);

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> List(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];

    private static string Summarize(Note note)
    {
        var sentence = note.Body.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("---", StringComparison.Ordinal)) ?? string.Empty;
        return sentence.Length <= SummaryCap ? sentence : sentence[..SummaryCap].TrimEnd() + "…";
    }

    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
}
