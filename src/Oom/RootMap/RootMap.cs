using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

internal sealed record HubDefinition(string Id, string Name, string Scope, IReadOnlyList<string> Tags, IReadOnlyList<string> TitleKeys);

internal sealed record HubConfiguration(string CatchAll, IReadOnlyList<HubDefinition> Hubs);

public sealed class RootMap
{
    private const int IndexCharacterCap = 4_000;
    private const int SummaryCap = 80;
    private const int VocabularyCap = 40;
    private const string RelatedHeading = "## İlgili";

    private readonly string _vault;
    private readonly Notes _notes;
    private readonly TurkishFold _fold;
    private readonly IFileOperations _files;
    private HubConfiguration? _configuration;
    private IReadOnlyList<string>? _vocabulary;

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

    public string CatchAllHub => Configuration.CatchAll;

    public IReadOnlyList<string> HubIds => [.. Configuration.Hubs.Select(hub => hub.Id)];

    public IReadOnlyList<string> HubLines => [.. Configuration.Hubs.Select(hub => hub.Id + " — " + hub.Scope)];

    public IReadOnlyList<string> TagVocabulary(IReadOnlyList<Note>? corpus = null)
    {
        if (_vocabulary is not null)
            return _vocabulary;

        var allowed = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in Configuration.Hubs.SelectMany(hub => hub.Tags))
            if (tag.Length > 0 && seen.Add(tag))
                allowed.Add(tag);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in (corpus ?? LoadCorpus()).SelectMany(note => note.Tags))
        {
            if (tag.Length == 0)
                continue;
            counts.TryGetValue(tag, out var count);
            counts[tag] = count + 1;
        }

        foreach (var tag in counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                     .Select(pair => pair.Key).Where(tag => seen.Add(tag)).Take(VocabularyCap))
            allowed.Add(tag);

        return _vocabulary = allowed;
    }

    public string Regenerate()
    {
        var configuration = Configuration;
        var corpus = Migrate(configuration, LoadCorpus());
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

    private IReadOnlyList<Note> Migrate(HubConfiguration configuration, IReadOnlyList<Note> corpus)
    {
        var migrated = new List<Note>(corpus.Count);
        foreach (var note in corpus)
        {
            var hub = note.Hub;
            var needsHub = string.IsNullOrWhiteSpace(hub) || !configuration.Hubs.Any(entry => string.Equals(entry.Id, hub, StringComparison.Ordinal));
            var needsType = !string.Equals(note.Type, "concept", StringComparison.Ordinal);
            if (!needsHub && !needsType)
            {
                migrated.Add(note);
                continue;
            }

            hub = needsHub ? HubsFor(configuration, note.Title + "\n" + note.Body, note.Tags)[0] : hub!;
            var path = Path.Combine(_vault, "knowledge", "concepts", Path.GetFileName(note.Name));
            if (!File.Exists(path))
            {
                migrated.Add(note with { Type = "concept", Hub = hub });
                continue;
            }

            try
            {
                LaneCVaultPaths.WriteAtomic(path, Rewrite(LaneCVaultPaths.ReadText(path), hub), _files);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }

            migrated.Add(note with { Type = "concept", Hub = hub, Body = HubLinked(note.Body, hub) });
        }

        return migrated;
    }

    internal static string Rewrite(string text, string hub)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count == 0 || lines[0].Trim() != "---")
            return text;

        var end = lines.FindIndex(1, line => line.Trim() == "---");
        if (end < 0)
            return text;

        var front = lines.GetRange(1, end - 1);
        front.RemoveAll(line => line.StartsWith("type:", StringComparison.Ordinal) || line.StartsWith("hub:", StringComparison.Ordinal));
        front.Add("type: concept");
        front.Add("hub: " + hub);
        var body = HubLinked(string.Join('\n', lines.GetRange(end + 1, lines.Count - end - 1)).Trim('\n'), hub);
        return "---\n" + string.Join('\n', front) + "\n---\n" + body + "\n";
    }

    private static string HubLinked(string body, string hub)
    {
        if (body.Contains("Hub: [[hubs/", StringComparison.Ordinal) || !body.Contains(RelatedHeading, StringComparison.Ordinal))
            return body;
        return body.TrimEnd('\n') + "\n\nHub: [[hubs/" + hub + "]]";
    }

    private static string BuildHubFile(HubDefinition hub, IReadOnlyList<Note> notes)
    {
        var builder = new StringBuilder("---\ntype: hub\n---\n");
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
