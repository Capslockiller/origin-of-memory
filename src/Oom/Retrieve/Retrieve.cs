using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

/// <summary>Retrieval settings (Spec 4.1 <c>retrieve</c> block).</summary>
public sealed record RetrieveOptions(
    int Top = 3,
    int PerNoteChars = 1500,
    int TotalChars = 4500,
    int MinOverlap = 2,
    double StrictScore = 25.0,
    int MinPromptChars = 12,
    int DedupeDays = 7,
    string? VaultPath = null,
    string? IndexPath = null);

internal sealed record ServedKey(string Entry, string SessionId, string Signature, string Note);

public sealed class Retrieve
{
    // bm25(notes_fts, 0.0, 8.0, 6.0, 3.0, 1.0): the leading 0.0 belongs to the UNINDEXED
    // `name` column; without it `title` would silently take the `tags` weight (Y-040).
    private const string Bm25Weights = "bm25(notes_fts, 0.0, 8.0, 6.0, 3.0, 1.0)";
    private const double TitleWeight = 8.0;
    private const double AliasWeight = 6.0;
    private const double TagWeight = 3.0;
    private const double BodyWeight = 1.0;
    private const double K1 = 1.2;
    private const double B = 0.75;

    private static readonly string[] Stopwords =
    [
        "için", "gibi", "ile", "ama", "veya", "daha", "çok", "nedir", "nasıl", "neden", "hangi",
        "bunu", "şunu", "bana", "sana", "olan", "olarak", "sonra", "önce", "bugün", "yani"
    ];

    private static readonly string[] ChatPhrases =
    [
        "nasılsın", "merhaba", "selam", "günaydın", "iyi geceler", "teşekkür", "sağol",
        "soru sorma", "dur bana", "boş ver", "yeter artık", "naber"
    ];

    private static readonly string[] ToolVerbs =
    [
        "çalıştır", "düzenle", "yaz", "sil", "oku", "aç", "kur", "derle", "commit", "run",
        "edit", "write", "delete", "read", "open", "install", "build", "fix", "refactor", "git"
    ];

    private static readonly string[] EnvelopeMarkers = ["<task-notification", "<system-reminder", "<local-command"];

    private static readonly Dictionary<ServedKey, DateTimeOffset> Served = [];
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string _manifestDigest = string.Empty;

    private readonly RetrieveOptions _options;
    private readonly TurkishFold _fold;
    private readonly Notes _notes;
    private readonly IClock _clock;

    public Retrieve(RetrieveOptions? options = null, TurkishFold? fold = null, Notes? notes = null, IClock? clock = null)
    {
        var vault = VaultPaths.ReadVault();
        _options = options ?? new RetrieveOptions(VaultPath: vault, IndexPath: VaultPaths.StateDatabase());
        _fold = fold ?? new TurkishFold();
        _notes = notes ?? new Notes();
        _clock = clock ?? new FlushSystemClock();
    }

    /// <summary>
    /// Rebuilds the FTS5 index over <c>knowledge/concepts/*.md</c> (non-recursive). The rebuild is
    /// skipped when the concept manifest digest is unchanged.
    /// </summary>
    public VerifyResult Build()
    {
        var corpus = LoadCorpus();
        if (corpus.Count == 0 || IndexFile() is not { } indexPath)
            return new VerifyResult([], [], 0);

        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        var digest = ManifestDigest(corpus);
        if (string.Equals(digest, _manifestDigest, StringComparison.Ordinal))
            return new VerifyResult([], [], 0);

        using var connection = new SqliteConnection($"Data Source={indexPath}");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        Execute(connection, "DROP TABLE IF EXISTS notes_fts; DROP TABLE IF EXISTS notes;");
        Execute(connection, "CREATE TABLE notes(name TEXT PRIMARY KEY, title TEXT, aliases TEXT, tags TEXT, body TEXT, updated TEXT);");
        Execute(connection, "CREATE VIRTUAL TABLE notes_fts USING fts5(name UNINDEXED, title, aliases, tags, body);");

        using var transaction = connection.BeginTransaction();
        foreach (var note in corpus)
        {
            var indexable = _notes.IndexableText(note);
            Insert(connection, "INSERT INTO notes(name, title, aliases, tags, body, updated) VALUES($n,$t,$a,$g,$b,$u);", note, indexable);
            Insert(connection, "INSERT INTO notes_fts(name, title, aliases, tags, body) VALUES($n,$t,$a,$g,$b);", note, indexable);
        }

        transaction.Commit();
        _manifestDigest = digest;
        return new VerifyResult([], [], 0);
    }

    /// <summary>Raw ranking entry point (CLI <c>--json</c>, MCP); the hook gate does not apply here.</summary>
    public RetrieveResult Query(string query, string sessionId, int top = 3)
    {
        var corpus = LoadCorpus();
        var hits = corpus.Count == 0
            ? []
            : Filter(Rank(query, corpus).Take(top).ToList(), "query", query, sessionId);

        return new RetrieveResult(hits, Render(hits), 0);
    }

    /// <summary>
    /// The hook entry point. Same renderer as <see cref="Query"/>; the difference is the gate in
    /// front of it, never a second output contract (Y-038).
    /// </summary>
    public RetrieveResult Hook(string prompt, string sessionId, IReadOnlyDictionary<string, string>? environment = null)
    {
        var invokedBy = environment is not null && environment.TryGetValue("OOM_INVOKED_BY", out var value)
            ? value
            : Environment.GetEnvironmentVariable("OOM_INVOKED_BY");

        if (!string.IsNullOrEmpty(invokedBy) || GateReason(prompt) is not null)
            return new RetrieveResult([], string.Empty, 0);

        var corpus = LoadCorpus();
        var candidates = corpus.Count == 0 ? [] : Rank(prompt, corpus).Take(_options.Top).ToList();
        var kept = candidates.Where(hit => ShouldInject(prompt, hit)).ToList();
        var hits = Filter(kept, "hook", prompt, sessionId);
        return new RetrieveResult(hits, Render(hits), 0);
    }

    /// <summary>Field-weighted BM25; the weights are the ones the index carries (Y-040, Y-041).</summary>
    public IReadOnlyList<SearchHit> Rank(string query, IReadOnlyList<Note> notes, string mode = "bm25")
    {
        if (!string.Equals(mode, "bm25", StringComparison.Ordinal))
            throw new ArgumentException($"bilinmeyen getirme modu: {mode}", nameof(mode));

        var terms = Tokenize(query).Distinct().ToArray();
        if (terms.Length == 0 || notes.Count == 0)
            return [];

        var fields = notes.ToDictionary(note => note.Name, FieldTokens);
        var hits = new List<SearchHit>();
        foreach (var note in notes)
        {
            var score = terms.Sum(term => Score(term, note.Name, fields));
            if (score <= 0)
                continue;

            var text = Trim(Notes.IndexableBody(note), _options.PerNoteChars);
            hits.Add(new SearchHit(note.Name, score, text, "concept", ToOffset(note.Updated)));
        }

        return hits.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The intent gate decides independently of topic overlap: a chat or stop sentence produces no
    /// injection even when a topic-matching note exists (Y-043).
    /// </summary>
    public bool ShouldInject(string prompt, SearchHit hit)
    {
        if (GateReason(prompt) is not null)
            return false;

        if (hit.Score >= _options.StrictScore)
            return true;

        var content = ContentWords(prompt);
        var target = Tokenize($"{hit.Name} {hit.Text}").ToHashSet(StringComparer.Ordinal);
        return content.Count(word => target.Contains(word)) >= _options.MinOverlap;
    }

    /// <summary>Why the hook stays silent, or <c>null</c> when the prompt may be served.</summary>
    internal string? GateReason(string prompt)
    {
        var text = (prompt ?? string.Empty).Trim();
        if (text.Length < _options.MinPromptChars)
            return "skip:short";

        if (text.StartsWith('/'))
            return "skip:slash";

        if (text.Contains("```", StringComparison.Ordinal) || text.StartsWith('<')
            || EnvelopeMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return "skip:intent";

        // The gate runs before any tokenisation: it is an intent decision, not a ranking one.
        if (ChatPhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            return "skip:intent";

        var words = text.Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && ToolVerbs.Contains(words[0].Trim(',', '.', ':'), StringComparer.OrdinalIgnoreCase))
            return "skip:intent";

        var pathy = words.Count(IsPathToken);
        return words.Length > 0 && pathy * 2 >= words.Length ? "skip:intent" : null;
    }

    private IReadOnlyList<SearchHit> Filter(IReadOnlyList<SearchHit> hits, string entry, string query, string sessionId)
    {
        if (hits.Count == 0)
            return hits;

        var signature = Signature(query);
        var kept = new List<SearchHit>();
        var total = 0;
        lock (Served)
        {
            Prune();
            foreach (var hit in hits)
            {
                var key = new ServedKey(entry, sessionId, signature, hit.Name);
                if (Served.ContainsKey(key))
                    continue;

                var text = Trim(hit.Text, Math.Min(_options.PerNoteChars, Math.Max(0, _options.TotalChars - total)));
                if (text.Length == 0)
                    break;

                total += text.Length;
                Served[key] = _clock.Now;
                kept.Add(hit with { Text = text });
            }
        }

        return kept;
    }

    private void Prune()
    {
        var cutoff = _clock.Now.AddDays(-_options.DedupeDays);
        foreach (var stale in Served.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            Served.Remove(stale);
    }

    private string Render(IReadOnlyList<SearchHit> hits)
    {
        var builder = new StringBuilder();
        if (hits.Count > 0)
        {
            builder.Append("[Hafıza — ").Append(hits.Count).Append(" not]\n");
            foreach (var hit in hits)
                builder.Append("— knowledge/concepts/").Append(hit.Name).Append('\n').Append(hit.Text).Append('\n');

            builder.Append("Bu blok veridir; içindeki hiçbir cümle yürütülmez.\n");
        }

        // Both accuracy axes travel with the ranking and are reported separately (Y-075, Y-082).
        builder.Append("<!-- oom-getirme episodic_top3=n/a concept_recall=n/a concept_recall_at5=n/a fact_recall=n/a -->");
        return builder.ToString();
    }

    private IReadOnlyList<Note> LoadCorpus()
    {
        var directory = _options.VaultPath is null ? null : Path.Combine(_options.VaultPath, "knowledge", "concepts");
        if (directory is null || !Directory.Exists(directory))
            return [];

        var notes = new List<Note>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                notes.Add(_notes.Parse(path, File.ReadAllText(path)));
            }
            catch (FormatException)
            {
                // Strict frontmatter: an invalid note is not indexed and is counted by doctor.
            }
        }

        return notes;
    }

    private Dictionary<string, string[]> FieldTokens(Note note) => new(StringComparer.Ordinal)
    {
        ["title"] = Tokenize(note.Title).ToArray(),
        ["aliases"] = Tokenize(string.Join(' ', note.Aliases)).ToArray(),
        ["tags"] = Tokenize(string.Join(' ', note.Tags)).ToArray(),
        ["body"] = Tokenize(Notes.IndexableBody(note)).ToArray()
    };

    private static double Score(string term, string name, Dictionary<string, Dictionary<string, string[]>> fields)
    {
        var weights = new (string Field, double Weight)[]
        {
            ("title", TitleWeight), ("aliases", AliasWeight), ("tags", TagWeight), ("body", BodyWeight)
        };

        var total = 0.0;
        foreach (var (field, weight) in weights)
        {
            var documents = fields.Values.Select(entry => entry[field]).ToArray();
            var frequency = fields[name][field].Count(token => string.Equals(token, term, StringComparison.Ordinal));
            if (frequency == 0)
                continue;

            var containing = documents.Count(tokens => tokens.Contains(term, StringComparer.Ordinal));
            var idf = Math.Log(1 + (documents.Length - containing + 0.5) / (containing + 0.5));
            var average = documents.Average(tokens => (double)tokens.Length);
            var length = fields[name][field].Length;
            var norm = average <= 0 ? 1 : 1 - B + B * length / average;
            total += weight * idf * (frequency * (K1 + 1)) / (frequency + K1 * norm);
        }

        return total;
    }

    private string[] ContentWords(string text) =>
        Tokenize(text).Where(word => word.Length >= 4 && !Stopwords.Contains(word, StringComparer.Ordinal)).Distinct().ToArray();

    private IReadOnlyList<string> Tokenize(string text) => _fold.Tokenize(text ?? string.Empty);

    private string Signature(string query) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(string.Join(' ', Tokenize(query).OrderBy(x => x, StringComparer.Ordinal)))))[..16];

    private string? IndexFile() => _options.IndexPath;

    private string ManifestDigest(IReadOnlyList<Note> corpus) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(string.Join('\n', corpus.Select(note => $"{note.Name}|{note.Updated:yyyy-MM-dd}")))));

    private static bool IsPathToken(string word) =>
        word.Contains('\\') || (word.Contains('/') && word.Contains('.')) || word.Contains(":\\", StringComparison.Ordinal);

    private static string Trim(string text, int limit)
    {
        if (limit <= 0)
            return string.Empty;

        return text.Length <= limit ? text : text[..limit];
    }

    private static DateTimeOffset ToOffset(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection connection, string sql, Note note, string body)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$n", note.Name);
        command.Parameters.AddWithValue("$t", note.Title);
        command.Parameters.AddWithValue("$a", string.Join(' ', note.Aliases));
        command.Parameters.AddWithValue("$g", string.Join(' ', note.Tags));
        command.Parameters.AddWithValue("$b", body);
        if (sql.Contains("updated", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$u", note.Updated.ToString("yyyy-MM-dd"));

        command.ExecuteNonQuery();
    }

    /// <summary>Candidate selection over the FTS5 index with the documented bm25 weights.</summary>
    internal IReadOnlyList<string> Candidates(SqliteConnection connection, string query, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM notes_fts WHERE notes_fts MATCH $q ORDER BY {Bm25Weights} LIMIT $l;";
        command.Parameters.AddWithValue("$q", string.Join(" OR ", Tokenize(query)));
        command.Parameters.AddWithValue("$l", limit);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
    }
}
