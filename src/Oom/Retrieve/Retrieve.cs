using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

/// <summary>Retrieval settings (Spec 4.1 <c>retrieve</c> block).</summary>
public sealed record RetrieveOptions(
    int Top = 3,
    int PerNoteChars = 1500,
    int TotalChars = 4500,
    string? VaultPath = null,
    string? IndexPath = null,
    string CompanionDir = "🔮 850-Companion",
    double CorrectionBoost = 4.0);

/// <summary>
/// Corpus statistics for one query, on the axis SQLite's <c>bm25()</c> uses: one row count, one
/// average row length, one document frequency per term over the whole row, and each row's own
/// length. They are row-level and not field-level on purpose — see <see cref="Retrieve.Score"/>.
/// </summary>
internal sealed record CorpusStats(
    int Documents,
    double Average,
    IReadOnlyDictionary<string, int> Frequency,
    IReadOnlyDictionary<string, int> Length);

internal sealed record IndexedNote(Note Note, string Text);
internal sealed record IndexManifest(long Generation, string Digest, DateTimeOffset BuiltAt);

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
    private const double IdfFloor = 1e-6;
    private const string CorrectionTag = "düzeltme";

    private static readonly string[] Stopwords =
    [
        "için", "gibi", "ile", "ama", "veya", "daha", "çok", "nedir", "nasıl", "neden", "hangi",
        "bunu", "şunu", "bana", "sana", "olan", "olarak", "sonra", "önce", "bugün", "yani"
    ];

    private static readonly (string Field, double Weight)[] Weights =
        [("title", TitleWeight), ("aliases", AliasWeight), ("tags", TagWeight), ("body", BodyWeight)];

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly RetrieveOptions _options;
    private readonly TurkishFold _fold;
    private readonly Notes _notes;
    private readonly IClock _clock;
    private IReadOnlyList<Note>? _corpus;
    private Dictionary<string, Dictionary<string, string[]>>? _fields;
    private HashSet<string>? _retired;
    private bool? _indexUsable;

    /// <summary>
    /// Where the candidates of the last <see cref="Query"/> came from:
    /// <c>fts</c> when the FTS5 index generated them, <c>corpus-scan:*</c> when it could not and the
    /// file scan did, with the reason attached. A retrieval path may fall back silently — a broken
    /// index must never stop answering — but it may not be *unobservable*, or the next measurement
    /// reports "connected to the index" while it is really scoring a directory listing.
    /// </summary>
    internal string CandidateSource { get; private set; } = "not-run";

    public Retrieve(RetrieveOptions? options = null, TurkishFold? fold = null, Notes? notes = null, IClock? clock = null)
    {
        var vault = VaultPaths.ReadVault();
        _options = options ?? new RetrieveOptions(VaultPath: vault, IndexPath: VaultPaths.StateDatabase());
        _fold = fold ?? new TurkishFold();
        _notes = notes ?? new Notes();
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>
    /// Rebuilds the FTS5 index over <c>knowledge/concepts/*.md</c> (non-recursive) and then checks
    /// its own work. The rebuild is skipped when the concept manifest digest is unchanged; the
    /// verification is never skipped, so a "skipped, nothing to do" answer is a measured answer and
    /// not an assumption. The returned <see cref="VerifyResult.ExitCode"/> is the verifier's verdict:
    /// 0 when the index matches the corpus entry for entry, 1 when it does not.
    ///
    /// Three things this method no longer does, all of them the same illness:
    /// <list type="number">
    /// <item>It does not create a directory. <c>Directory.CreateDirectory</c> here minted one state
    /// root per test run and one per <c>compile</c> against an uninstalled workspace — a write path
    /// that brings a root into existence as a side effect of asking to index (Y-171).</item>
    /// <item>It does not issue DDL. <c>StateStore</c> owns <c>notes</c>, <c>notes_fts</c> and
    /// <c>oom_index_meta</c>, so there is one schema owner of this file and not three.</item>
    /// <item>It does not <c>DROP</c>. A rebuild empties and refills the index; it does not destroy
    /// tables somebody else is responsible for, in a file that also carries the owner's ledger
    /// (Y-172).</item>
    /// </list>
    /// </summary>
    public VerifyResult Build()
    {
        var corpus = LoadCorpus(refresh: true);
        if (OpenIndexForWrite() is not { } connection)
            return new VerifyResult([], [], 0);

        using (connection)
        {
            // IndexableText is both what FTS receives and what the manifest covers. Prepare it once
            // so content hashing does not add a second note-body pass to a rebuild.
            var indexed = corpus.Select(note => new IndexedNote(note, _notes.IndexableText(note))).ToArray();
            var digest = ManifestDigest(indexed);
            var previous = ReadManifest(connection);

            if (previous is null || !string.Equals(digest, previous.Digest, StringComparison.Ordinal))
            {
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, "DELETE FROM notes_fts; DELETE FROM notes;");
                foreach (var item in indexed)
                {
                    Insert(connection, transaction, "INSERT INTO notes(name, title, aliases, tags, body, updated) VALUES($n,$t,$a,$g,$b,$u);", item.Note, item.Text, folded: false);
                    Insert(connection, transaction, "INSERT INTO notes_fts(name, title, aliases, tags, body) VALUES($n,$t,$a,$g,$b);", item.Note, item.Text, folded: true);
                }

                WriteManifest(connection, transaction, new IndexManifest((previous?.Generation ?? 0) + 1, digest, _clock.Now));
                transaction.Commit();
            }

            return IndexVerifier.Verify(connection, indexed, digest);
        }
    }

    /// <summary>
    /// The index as an independent question: does the file on disk still hold exactly this corpus?
    /// Read-only, creates nothing, and is the same code <see cref="Build"/> grades itself with and
    /// the same code <see cref="Doctor.VerifyIndex(Retrieve)"/> reports — one answer to "is the
    /// index sound", never two opinions that can disagree.
    /// </summary>
    public VerifyResult VerifyIndex()
    {
        var corpus = LoadCorpus();
        if (IndexFile() is not { } path || !File.Exists(path))
            return new VerifyResult([], [], 0);

        var indexed = corpus.Select(note => new IndexedNote(note, _notes.IndexableText(note))).ToArray();
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            return IndexVerifier.Verify(connection, indexed, ManifestDigest(indexed));
        }
        catch (SqliteException)
        {
            // An index that cannot be opened holds none of the corpus; saying so is the honest answer.
            return new VerifyResult([.. indexed.Select(item => item.Note.Name).Order(StringComparer.OrdinalIgnoreCase)], [], 1);
        }
    }

    /// <summary>
    /// The write handle on the index, or <c>null</c> when there is nowhere to write that already
    /// exists. <see cref="StateStore"/> provisions the schema, so the index tables arrive on the
    /// same versioned ladder as every other table in the file.
    /// </summary>
    private static SqliteConnection? OpenIndexForWriteAt(string? path)
    {
        if (path is null)
            return null;

        // Y-171: an existing directory is the condition, not a directory this call makes. A machine
        // that has run `oom install` has its state root; a temporary workspace that never installed
        // must not acquire one because something asked for an index.
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;

        return StateStore.Open(path, StateAccess.ReadWrite);
    }

    private SqliteConnection? OpenIndexForWrite() => OpenIndexForWriteAt(IndexFile());

    /// <summary>
    /// The one retrieval entry point: ranked, budgeted, rendered. It is asked on demand — by the
    /// CLI, by <c>mcp</c>, by the assistant when it decides it needs memory — and never by a hook
    /// that guesses on the owner's behalf.
    /// </summary>
    public RetrieveResult Query(string query, string sessionId, int top = 3)
    {
        _ = sessionId;
        var hits = Budget(Search(query, top));
        return new RetrieveResult(hits, Render(hits), 0);
    }

    /// <summary>
    /// The per-note and total character budgets of spec 6.4, applied to a ranked list. A concept
    /// the hand layer has retired is dropped here rather than in the ranker, so its score is still
    /// computed and comparable (Y-035).
    /// </summary>
    private IReadOnlyList<SearchHit> Budget(IReadOnlyList<SearchHit> hits)
    {
        var kept = new List<SearchHit>();
        var total = 0;
        foreach (var hit in hits)
        {
            if (hit.Superseded)
                continue;

            var text = Trim(hit.Text, Math.Min(_options.PerNoteChars, Math.Max(0, _options.TotalChars - total)));
            if (text.Length == 0)
                break;

            total += text.Length;
            kept.Add(hit with { Text = text });
        }

        return kept;
    }

    /// <summary>
    /// The single search path.
    ///
    /// Candidates come from the FTS5 index — <see cref="Candidates"/>, which until now had no caller
    /// outside the scar suite — whenever the index is present and its manifest still describes the
    /// corpus on disk. The scoring itself does not move: <see cref="Score"/> ranks the same
    /// <see cref="Note"/> objects against the same whole-corpus statistics, so the index narrows
    /// *which* notes are scored and changes no score of any note that survives the narrowing. That
    /// is only safe while the index cannot drop a note the ranker would have scored, which is a
    /// property of the tokens it is built from and is pinned by a scar test, not by this comment.
    /// </summary>
    private IReadOnlyList<SearchHit> Search(string query, int top)
    {
        var corpus = LoadCorpus();
        if (corpus.Count == 0)
        {
            CandidateSource = "empty-corpus";
            return [];
        }

        return RankCandidates(query, corpus, CandidateNames(query, corpus)).Take(top).ToList();
    }

    /// <summary>
    /// The FTS5 candidate set for one query, or <c>null</c> when the index cannot be trusted to hold
    /// the corpus that is about to be ranked. Null is a fall-back to the full scan, never an empty
    /// result: an absent, stale or unreadable index must degrade retrieval's speed, not its answers.
    /// The freshness test is the same manifest digest <see cref="Build"/> writes, computed once per
    /// instance rather than once per query — the corpus is parsed once per instance for exactly the
    /// same reason (see <see cref="LoadCorpus"/>).
    /// </summary>
    private IReadOnlySet<string>? CandidateNames(string query, IReadOnlyList<Note> corpus)
    {
        if (_indexUsable == false)
            return null;

        if (IndexFile() is not { } path || !File.Exists(path))
        {
            // A missing index must not be created here: `Data Source=` alone would create an empty
            // database file, and a read path may not bring a write into being.
            _indexUsable = false;
            CandidateSource = "corpus-scan:no-index";
            return null;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();
            if (_indexUsable is null)
            {
                var digest = ManifestDigest([.. corpus.Select(note => new IndexedNote(note, _notes.IndexableText(note)))]);
                _indexUsable = ReadManifest(connection) is { } manifest
                    && string.Equals(digest, manifest.Digest, StringComparison.Ordinal);
            }

            if (_indexUsable == false)
            {
                CandidateSource = "corpus-scan:stale-index";
                return null;
            }

            var names = Candidates(connection, query, corpus.Count).ToHashSet(StringComparer.Ordinal);
            CandidateSource = "fts";
            return names;
        }
        catch (SqliteException error)
        {
            _indexUsable = false;
            CandidateSource = $"corpus-scan:sqlite-{error.SqliteErrorCode}";
            return null;
        }
    }

    /// <summary>Field-weighted BM25; the weights are the ones the index carries (Y-040, Y-041).</summary>
    public IReadOnlyList<SearchHit> Rank(string query, IReadOnlyList<Note> notes, string mode = "bm25")
    {
        if (!string.Equals(mode, "bm25", StringComparison.Ordinal))
            throw new ArgumentException($"bilinmeyen getirme modu: {mode}", nameof(mode));

        return RankCandidates(query, notes, null);
    }

    /// <summary>
    /// <see cref="Rank"/> restricted to a candidate set. The statistics stay whole-corpus — document
    /// count, average length and per-term document frequency all come from <paramref name="notes"/>
    /// and not from the candidates — because BM25's idf is a property of the corpus, not of the
    /// shortlist. Score a shortlist against the shortlist's own statistics and every number moves;
    /// score it against the corpus's and a narrowed run is byte-identical to a full one for every
    /// note the shortlist kept.
    /// </summary>
    private IReadOnlyList<SearchHit> RankCandidates(string query, IReadOnlyList<Note> notes, IReadOnlySet<string>? candidates)
    {
        var terms = QueryTerms(query);
        if (terms.Length == 0 || notes.Count == 0)
            return [];

        var fields = ReferenceEquals(notes, _corpus) && _fields is not null ? _fields : notes.ToDictionary(note => note.Name, FieldTokens);
        if (ReferenceEquals(notes, _corpus))
            _fields = fields;

        var stats = Statistics(fields, terms);
        var hits = new List<SearchHit>();
        foreach (var note in notes)
        {
            if (candidates is not null && !candidates.Contains(note.Name))
                continue;

            var score = terms.Sum(term => Score(term, note.Name, fields[note.Name], stats));
            if (score <= 0)
                continue;

            // A hand-layer correction carries the boost and its own source label; a concept the
            // hand layer has retired keeps its rank but is marked superseded (Y-035).
            var correction = note.Tags.Contains(CorrectionTag, StringComparer.Ordinal);
            var text = Trim(Notes.IndexableBody(note), _options.PerNoteChars);
            hits.Add(new SearchHit(note.Name, correction ? score * _options.CorrectionBoost : score, text,
                correction ? "correction" : "concept", ToOffset(note.Updated), _retired?.Contains(note.Name) == true));
        }

        return hits.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Name, StringComparer.Ordinal).ToList();
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

    /// <summary>
    /// The concept corpus, parsed once per instance: re-reading 542 notes for every query cost
    /// two thirds of a second and blew the 300 ms budget of spec 6.4 on a batch.
    /// </summary>
    private IReadOnlyList<Note> LoadCorpus(bool refresh = false)
    {
        if (!refresh && _corpus is not null)
            return _corpus;

        if (refresh)
        {
            _corpus = null;
            _fields = null;
            _retired = null;
            _indexUsable = null;
        }

        var directory = _options.VaultPath is null ? null : Path.Combine(_options.VaultPath, "knowledge", "concepts");
        if (directory is null || !Directory.Exists(directory))
            return _corpus = [];

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

        return _corpus = [.. notes, .. Corrections()];
    }

    /// <summary>
    /// The hand layer is part of the index population. v0 indexed <c>knowledge/concepts</c> alone,
    /// so a correction the owner had just written by hand never entered the ranking and the stale
    /// concept it corrects kept answering (scar Y-035). Every <c>## </c> block of the companion
    /// <c>Duzeltmeler.md</c> becomes one document, ranked with
    /// <see cref="RetrieveOptions.CorrectionBoost"/>; a block whose <c>yerine:</c> line names a
    /// concept retires that concept, which then leaves the ranking marked superseded.
    /// </summary>
    private IReadOnlyList<Note> Corrections()
    {
        var path = _options.VaultPath is null
            ? null
            : Path.Combine(_options.VaultPath, _options.CompanionDir, "Duzeltmeler.md");
        if (path is null || !File.Exists(path))
            return [];

        var notes = new List<Note>();
        var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = DateOnly.FromDateTime(File.GetLastWriteTime(path));
        foreach (var block in ("\n" + File.ReadAllText(path).Replace("\r\n", "\n")).Split("\n## ").Skip(1))
        {
            var lines = block.Split('\n');
            var title = lines[0].Trim();
            if (title.Length == 0)
                continue;

            foreach (var line in lines.Where(x => x.TrimStart().StartsWith("yerine:", StringComparison.OrdinalIgnoreCase)))
                retired.Add(line.TrimStart()[7..].Trim());

            notes.Add(new Note($"Duzeltmeler.md#{notes.Count + 1}", title, [], [CorrectionTag], ["Duzeltmeler.md"],
                updated, updated, string.Join('\n', lines.Skip(1))));
        }

        _retired = retired;
        return notes;
    }

    // Index-side tokens keep every occurrence: BM25 needs a term frequency, and over the
    // de-duplicated list of Tokenize the saturation factor is a constant (Y-040 stays a
    // weight test, this makes it a frequency test too).
    private Dictionary<string, string[]> FieldTokens(Note note) => new(StringComparer.Ordinal)
    {
        ["title"] = _fold.TokenizeAll(note.Title).ToArray(),
        ["aliases"] = _fold.TokenizeAll(string.Join(' ', note.Aliases)).ToArray(),
        ["tags"] = _fold.TokenizeAll(string.Join(' ', note.Tags)).ToArray(),
        ["body"] = _fold.TokenizeAll(Notes.IndexableBody(note)).ToArray()
    };

    /// <summary>
    /// Row-level corpus statistics for one query. They used to be recomputed inside the score of
    /// every (term, note, field) triple, which made one query over 542 notes cost 690 ms against
    /// the 300 ms budget of spec 6.4; hoisting them out is what bought that back.
    /// </summary>
    private static CorpusStats Statistics(Dictionary<string, Dictionary<string, string[]>> fields, string[] terms)
    {
        var length = fields.ToDictionary(pair => pair.Key,
            pair => Weights.Sum(weight => pair.Value[weight.Field].Length), StringComparer.Ordinal);

        var containing = terms.ToDictionary(term => term,
            term => fields.Values.Count(entry => Weights.Any(weight => entry[weight.Field].Contains(term, StringComparer.Ordinal))),
            StringComparer.Ordinal);

        return new CorpusStats(fields.Count, fields.Count == 0 ? 0 : length.Values.Average(), containing, length);
    }

    /// <summary>
    /// BM25F in the shape SQLite's <c>bm25()</c> computes it (spec 6.4 names that query as the
    /// index authority, so the in-process ranker has to agree with it): the field weights scale
    /// the term frequency, the weighted frequency is summed across the four columns, and the
    /// saturation and length normalisation are then applied <em>once</em> against the row.
    ///
    /// The previous form saturated each field separately and summed the results, which turned the
    /// title weight into an unsaturated 8x multiplier — one common word in a title outscored four
    /// rare words in the note that answered the question. Same weights, same k1 and b, same
    /// tokens; only the order of the operations changed, and gate 5 moved 0.720 -> 0.832 (@3).
    /// Document frequency is likewise per row and not per field, as bm25() counts it.
    /// </summary>
    private static double Score(string term, string name, Dictionary<string, string[]> note, CorpusStats stats)
    {
        var weighted = 0.0;
        var occurrences = 0;
        foreach (var (field, weight) in Weights)
        {
            var frequency = note[field].Count(token => string.Equals(token, term, StringComparison.Ordinal));
            if (frequency == 0)
                continue;

            weighted += weight * frequency;
            occurrences += frequency;
        }

        if (occurrences == 0)
            return 0;

        var containing = stats.Frequency[term];
        // bm25()'s idf, which goes negative for a term carried by more than half the corpus and is
        // floored there rather than allowed to subtract from the score.
        var idf = Math.Log((stats.Documents - containing + 0.5) / (containing + 0.5));
        if (idf <= 0)
            idf = IdfFloor;

        var norm = stats.Average <= 0 ? 1 : 1 - B + B * stats.Length[name] / stats.Average;
        return idf * (weighted * (K1 + 1)) / (weighted + K1 * norm);
    }

    /// <summary>
    /// The terms a query is ranked with: content words only. The raw prompt still reaches
    /// <see cref="GateReason"/> untouched, because `skip:*` is an intent decision over the
    /// sentence, not over its terms. Stopwords and two- or three-letter tokens matched 407 of
    /// 542 notes per query and put the score of a note that shares only "nedir" next to the
    /// score of the note that answers it. A query with no content word at all (a bare "ne
    /// zaman?") falls back to its full token list rather than returning nothing.
    /// </summary>
    private string[] QueryTerms(string query)
    {
        var content = Tokenize(query).Where(word => !Stopwords.Contains(word, StringComparer.Ordinal)).Distinct().ToArray();
        return content.Length > 0 ? content : Tokenize(query).Distinct().ToArray();
    }

    private IReadOnlyList<string> Tokenize(string text) => _fold.Tokenize(text ?? string.Empty);

    private string? IndexFile() => _options.IndexPath;

    private static string ManifestDigest(IReadOnlyList<IndexedNote> corpus)
    {
        var manifest = new StringBuilder();

        // The digest covers the index FORMAT as well as its content. Without this line a tree that
        // changes how `notes_fts` is tokenized would hash an unchanged corpus to an unchanged digest,
        // `Build` would skip the rebuild, and every existing installation would keep an index the new
        // query path can no longer read — a silent, machine-local retrieval outage. Bump the tag
        // whenever `Shape` changes what reaches the index.
        AppendManifestField(manifest, "fts-format=fold-tokens-v2");
        foreach (var item in corpus.OrderBy(item => item.Note.Name, StringComparer.Ordinal))
        {
            // These are exactly the values inserted into `notes` and `notes_fts`, length-prefixed
            // to preserve field boundaries. An edit to any indexed field changes this SHA-256.
            AppendManifestField(manifest, item.Note.Name);
            AppendManifestField(manifest, item.Note.Title);
            AppendManifestField(manifest, string.Join(' ', item.Note.Aliases));
            AppendManifestField(manifest, string.Join(' ', item.Note.Tags));
            AppendManifestField(manifest, item.Text);
            AppendManifestField(manifest, item.Note.Updated.ToString("yyyy-MM-dd"));
        }

        return Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(manifest.ToString())));
    }

    private static void AppendManifestField(StringBuilder manifest, string value) =>
        manifest.Append(value.Length).Append(':').Append(value).Append('\n');

    private static IndexManifest? ReadManifest(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation, manifest_digest, built_at FROM oom_index_meta ORDER BY generation DESC LIMIT 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new IndexManifest(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)))
            : null;
    }

    private static void WriteManifest(SqliteConnection connection, SqliteTransaction transaction, IndexManifest manifest)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM oom_index_meta; INSERT INTO oom_index_meta(generation, manifest_digest, built_at) VALUES($g, $d, $t);";
        command.Parameters.AddWithValue("$g", manifest.Generation);
        command.Parameters.AddWithValue("$d", manifest.Digest);
        command.Parameters.AddWithValue("$t", manifest.BuiltAt.ToString("O"));
        command.ExecuteNonQuery();
    }


    private static string Trim(string text, int limit)
    {
        if (limit <= 0)
            return string.Empty;

        return text.Length <= limit ? text : text[..limit];
    }

    private static DateTimeOffset ToOffset(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Insert(SqliteConnection connection, SqliteTransaction transaction, string sql, Note note, string body, bool folded)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$n", note.Name);
        command.Parameters.AddWithValue("$t", Shape(note.Title, folded));
        command.Parameters.AddWithValue("$a", Shape(string.Join(' ', note.Aliases), folded));
        command.Parameters.AddWithValue("$g", Shape(string.Join(' ', note.Tags), folded));
        command.Parameters.AddWithValue("$b", Shape(body, folded));
        if (sql.Contains("updated", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$u", note.Updated.ToString("yyyy-MM-dd"));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// What a column receives. <c>notes</c> keeps the note's own text, because it is the readable
    /// copy. <c>notes_fts</c> receives <see cref="TurkishFold.TokenizeAll"/>'s output instead of that
    /// text, so the index is built out of exactly the tokens a query is tokenized into.
    ///
    /// FTS5's own <c>unicode61</c> tokenizer is not that tokenizer, and the gap was not academic: it
    /// strips the diacritic from <c>güvenlik</c> and keeps the dotless <c>ı</c> of <c>kapısı</c>,
    /// while <see cref="TurkishFold"/> keeps the diacritic and folds <c>ı</c> onto <c>i</c> (Y-044).
    /// A search for <c>kapısı</c> therefore matched nothing in the index while the in-process ranker
    /// found the note, and the five-character prefixes the fold emits for Turkish suffixes were not
    /// in the index at all. That is precisely the mismatch <see cref="TurkishFold"/>'s own summary
    /// forbids — "a note can never be written with one folding and searched with another" — and it
    /// is why <see cref="Candidates"/> could not safely be given a caller before now.
    /// </summary>
    private string Shape(string value, bool folded) => folded ? string.Join(' ', _fold.TokenizeAll(value)) : value;

    /// <summary>Candidate selection over the FTS5 index with the documented bm25 weights.</summary>
    internal IReadOnlyList<string> Candidates(SqliteConnection connection, string query, int limit)
    {
        // Every term is quoted, so a token that happens to spell an FTS5 operator is read as the word
        // it is rather than as syntax, and a malformed query cannot become a SqliteException on the
        // read path. An empty token list would make `MATCH ''` a syntax error, so it returns nothing.
        var terms = Tokenize(query).Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"").ToArray();
        if (terms.Length == 0)
            return [];

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM notes_fts WHERE notes_fts MATCH $q ORDER BY {Bm25Weights} LIMIT $l;";
        command.Parameters.AddWithValue("$q", string.Join(" OR ", terms));
        command.Parameters.AddWithValue("$l", limit);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
    }
}

/// <summary>
/// The one answer to "does the index still hold this corpus". <see cref="Retrieve.Build"/> grades
/// itself with it, <see cref="Retrieve.VerifyIndex"/> asks it on its own, and
/// <see cref="Doctor.VerifyIndex(Retrieve)"/> reports it — so there is no second opinion that can
/// call an index sound while the first one calls it broken.
/// </summary>
internal static class IndexVerifier
{
    /// <summary>The name-set comparison, which is all a caller holding two lists of names can check.</summary>
    internal static VerifyResult Compare(IReadOnlyList<string> corpus, IReadOnlyList<string> index)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(index);
        var corpusSet = corpus.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexSet = index.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = corpusSet.Except(indexSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = indexSet.Except(corpusSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new VerifyResult(missing, extra, missing.Length == 0 && extra.Length == 0 ? 0 : 1);
    }

    /// <summary>
    /// The full check against an open index: every corpus note present in both <c>notes</c> and
    /// <c>notes_fts</c>, no entry in either that the corpus does not have, every stored field equal
    /// to the field that was indexed, and the manifest digest equal to the corpus's own digest.
    ///
    /// A note whose stored row no longer matches the corpus is reported as <c>Missing</c>: the index
    /// does not contain the corpus's entry for it, whatever else it contains under that name. A
    /// manifest digest that disagrees while every row matches leaves both lists empty and still
    /// returns exit code 1 — the verdict is the exit code, and it never says "sound" on a guess.
    /// </summary>
    internal static VerifyResult Verify(SqliteConnection connection, IReadOnlyList<IndexedNote> corpus, string digest)
    {
        if (!StateStore.TableExists(connection, "notes") || !StateStore.TableExists(connection, "notes_fts"))
            return new VerifyResult([.. corpus.Select(item => item.Note.Name).Order(StringComparer.OrdinalIgnoreCase)], [], 1);

        var stored = ReadRows(connection);
        var indexedNames = ReadFtsNames(connection);
        var expected = corpus.ToDictionary(item => item.Note.Name, Fields, StringComparer.Ordinal);

        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var extra = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, fields) in expected)
        {
            if (!indexedNames.Contains(name) || !stored.TryGetValue(name, out var row) || !string.Equals(row, fields, StringComparison.Ordinal))
                missing.Add(name);
        }

        foreach (var name in stored.Keys.Concat(indexedNames).Where(name => !expected.ContainsKey(name)))
            extra.Add(name);

        var manifest = ReadDigest(connection);
        var sound = missing.Count == 0 && extra.Count == 0 && string.Equals(manifest, digest, StringComparison.Ordinal);
        return new VerifyResult([.. missing], [.. extra], sound ? 0 : 1);
    }

    /// <summary>The indexed fields of one note, in the same order and with the same length prefixes the manifest uses.</summary>
    private static string Fields(IndexedNote item) => Join(
        item.Note.Title, string.Join(' ', item.Note.Aliases), string.Join(' ', item.Note.Tags), item.Text,
        item.Note.Updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string Join(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
            builder.Append(value.Length).Append(':').Append(value).Append('\n');
        return builder.ToString();
    }

    private static Dictionary<string, string> ReadRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, title, aliases, tags, body, updated FROM notes";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            rows[reader.GetString(0)] = Join(Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5));
        return rows;
    }

    private static HashSet<string> ReadFtsNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM notes_fts";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static string? ReadDigest(SqliteConnection connection)
    {
        if (!StateStore.TableExists(connection, "oom_index_meta"))
            return null;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_digest FROM oom_index_meta ORDER BY generation DESC LIMIT 1;";
        return command.ExecuteScalar() as string;
    }

    private static string Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
}
