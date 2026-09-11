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
    int MinOverlap = 3,
    double StrictScore = 1.0,
    int MinPromptChars = 12,
    int DedupeDays = 7,
    string? VaultPath = null,
    string? IndexPath = null,
    string CompanionDir = "🔮 850-Companion",
    double CorrectionBoost = 4.0,
    string Client = "claude");

/// <summary>
/// The scope one served note is remembered under. Every component is a discriminator that must be
/// able to let memory through again:
/// <list type="bullet">
/// <item><c>Vault</c> — two vaults on one machine share <c>%LOCALAPPDATA%</c>, never a silence.</item>
/// <item><c>Client</c> and <c>Entry</c> — Claude having been shown a note is not Codex having been
/// shown it, and the CLI's <c>--json</c> output is not the hook's injection.</item>
/// <item><c>SessionId</c> — <b>this is what keeps a seven-day silence from spreading.</b> The window
/// is seven days long, so without the session in the key one delivery on Monday would withhold that
/// note from every new conversation until the following Monday. A new session has never been told
/// anything; it starts with the whole vault available to it.</item>
/// <item><c>Signature</c> — a different question in the same session sees the same note again
/// (Y-039). The plan for this table named vault+client+session+note+hash and left this out; Y-039
/// is a live scar over exactly that, so the signature stays in the key.</item>
/// <item><c>ContentHash</c> — the hash of the text that was actually rendered. A note whose body has
/// been rewritten is new memory, not a repeat, and is served again even to the same question.</item>
/// </list>
/// </summary>
internal sealed record ServedKey(string Vault, string Client, string Entry, string SessionId, string Signature, string Note, string ContentHash);

/// <summary>
/// What is known about one served note's fate. The three are not decoration: a hook event is a whole
/// process lifetime, and the process can die between rendering a block and the block reaching the
/// model.
/// </summary>
public enum ServedStatus
{
    /// <summary>The block was built and handed to the caller. Nobody has said it left the process.</summary>
    Prepared,

    /// <summary>The caller acknowledged that the block actually left the process (<see cref="Retrieve.AcknowledgeDelivery"/>).</summary>
    Emitted,

    /// <summary>A <see cref="Prepared"/> row whose process is gone without ever acknowledging. Not a delivery.</summary>
    Uncertain
}

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

    private static readonly (string Field, double Weight)[] Weights =
        [("title", TitleWeight), ("aliases", AliasWeight), ("tags", TagWeight), ("body", BodyWeight)];

    /// <summary>
    /// What this instance has already handed to its caller. Instance-scoped, not static, because in
    /// production one process is one <see cref="Retrieve"/> and one hook event — a static dictionary
    /// was a per-process cache pretending to be a seven-day ledger, and it died with the executable
    /// after every single hook event, which is why nothing was ever actually de-duplicated.
    /// Suppressing on this layer is honest: the block was returned to the caller, we watched it
    /// happen. Across a process boundary nothing was watched, which is what
    /// <see cref="ServedLedger"/> is careful about.
    /// </summary>
    private readonly Dictionary<ServedKey, DateTimeOffset> _served = [];

    /// <summary>Keys this instance wrote as <see cref="ServedStatus.Prepared"/> and has not acknowledged.</summary>
    private readonly List<ServedKey> _prepared = [];
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly RetrieveOptions _options;
    private readonly TurkishFold _fold;
    private readonly Notes _notes;
    private readonly IClock _clock;
    private IReadOnlyList<Note>? _corpus;
    private Dictionary<string, Dictionary<string, string[]>>? _fields;
    private Dictionary<string, HashSet<string>>? _surfaces;
    private HashSet<string>? _retired;
    private bool? _indexUsable;

    /// <summary>
    /// Where the candidates of the last <see cref="Query"/> or <see cref="Hook"/> came from:
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

        return StateStore.Open(path, StateAccess.ReadWrite).Connection;
    }

    private SqliteConnection? OpenIndexForWrite() => OpenIndexForWriteAt(IndexFile());

    /// <summary>Raw ranking entry point (CLI <c>--json</c>, MCP); the hook gate does not apply here.</summary>
    public RetrieveResult Query(string query, string sessionId, int top = 3)
    {
        var hits = Filter(Search(query, top), "query", query, sessionId);
        return new RetrieveResult(hits, Render(hits), 0);
    }

    /// <summary>
    /// The single search path. <see cref="Query"/> (CLI <c>--json</c> and MCP) and <see cref="Hook"/>
    /// both come through here, so candidate generation cannot differ between the two entry points
    /// the way the output contract once did (Y-038).
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

        var kept = Search(prompt, _options.Top).Where(hit => ShouldInject(prompt, hit)).ToList();
        var hits = Filter(kept, "hook", prompt, sessionId);
        return new RetrieveResult(hits, Render(hits), 0);
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

    /// <summary>
    /// The intent gate decides independently of topic overlap: a chat or stop sentence produces no
    /// injection even when a topic-matching note exists (Y-043).
    /// </summary>
    public bool ShouldInject(string prompt, SearchHit hit)
    {
        if (GateReason(prompt) is not null)
            return false;

        var terms = QueryTerms(prompt);
        if (terms.Length == 0)
            return false;

        // `strictScore` is a per-term mean, not the raw sum. The sum grows with the length of the
        // prompt, so one constant over it binds on short prompts and never on long ones; at 25.0
        // against sums that ran 60-300 it never bound at all and every candidate was injected.
        // yazan: codex · gpt-6
        // Y-110: calibrate the gate at the 542-document gate-5 reference corpus. In a small
        // topic slice, common query terms reach floor IDF and depress the mean even when
        // the first hit identifies the answer. Scale only the gate, capped at the configured
        // threshold; ranking, identity overlap and the caller's strictness remain intact.
        var corpusScale = Math.Min(1.0, Math.Max(1, LoadCorpus().Count) / 542.0);
        if (hit.Score / terms.Length < _options.StrictScore * corpusScale)
            return false;

        var surface = GateSurface(hit.Name);
        return ContentWords(prompt).Count(word => surface.Contains(word)) >= _options.MinOverlap;
    }

    /// <summary>
    /// The note's identity fields — slug, title, aliases, tags — which is where spec 6.4 puts the
    /// overlap test. The old gate ran it over <c>hit.Text</c>, and a 1500 character body shares two
    /// content words with very nearly any prompt, so the test passed 5 of 5 no-answer canaries. The
    /// slug is carried alongside the title because it is the ASCII fold of it, and a prompt typed
    /// without Turkish diacritics only ever matches that form.
    /// </summary>
    private HashSet<string> GateSurface(string name)
    {
        _surfaces ??= LoadCorpus().ToDictionary(note => note.Name, Surface, StringComparer.Ordinal);

        // A hit that is not a corpus note — a synthetic one in a scar test — still gets its name.
        return _surfaces.TryGetValue(name, out var surface) ? surface : Surface(name);
    }

    private HashSet<string> Surface(Note note) =>
        Tokenize($"{Slug(note.Name)} {note.Title} {string.Join(' ', note.Aliases)} {string.Join(' ', note.Tags)}")
            .ToHashSet(StringComparer.Ordinal);

    private HashSet<string> Surface(string name) => Tokenize(Slug(name)).ToHashSet(StringComparer.Ordinal);

    private static string Slug(string name) => Path.GetFileNameWithoutExtension(name).Replace('-', ' ');

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

    /// <summary>
    /// The served-note gate. Two layers, and the difference between them is the whole point:
    /// <list type="bullet">
    /// <item><b>this process</b> — <see cref="_served"/>. The block was returned to the caller in
    /// front of us, so a repeat inside one process is a repeat (Y-039, Y-110).</item>
    /// <item><b>earlier processes</b> — <see cref="ServedLedger"/>, and only rows it can show were
    /// <see cref="ServedStatus.Emitted"/>. A row left <see cref="ServedStatus.Prepared"/> by a
    /// process that is gone becomes <see cref="ServedStatus.Uncertain"/> and suppresses nothing: an
    /// unacknowledged delivery is not a delivery, and treating it as one withholds the owner's
    /// memory on the strength of something nobody observed.</item>
    /// </list>
    /// </summary>
    private IReadOnlyList<SearchHit> Filter(IReadOnlyList<SearchHit> hits, string entry, string query, string sessionId)
    {
        if (hits.Count == 0)
            return hits;

        var signature = Signature(query);
        var now = _clock.Now;
        var cutoff = now.AddDays(-_options.DedupeDays);
        var kept = new List<SearchHit>();
        var total = 0;

        using var ledger = ServedLedger.Open(IndexFile());
        ledger?.Prune(cutoff);
        foreach (var stale in _served.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            _served.Remove(stale);

        foreach (var hit in hits)
        {
            if (hit.Superseded)
                continue;

            var text = Trim(hit.Text, Math.Min(_options.PerNoteChars, Math.Max(0, _options.TotalChars - total)));
            if (text.Length == 0)
                break;

            var key = new ServedKey(VaultScope, ClientScope, entry, sessionId, signature, hit.Name, ContentHash(text));
            if (_served.ContainsKey(key) || ledger?.WasEmitted(key, cutoff) == true)
                continue;

            total += text.Length;
            _served[key] = now;
            if (ledger?.Prepare(key, now) == true)
                _prepared.Add(key);

            kept.Add(hit with { Text = text });
        }

        return kept;
    }

    /// <summary>
    /// Records that everything this instance prepared actually left the process, and returns how many
    /// rows that was. Until this is called the rows say <see cref="ServedStatus.Prepared"/>, and the
    /// next process reads them as <see cref="ServedStatus.Uncertain"/> — so a run that renders a
    /// memory block and then dies, or is killed, or has its stdout discarded, withholds nothing from
    /// the next one.
    /// </summary>
    /// <remarks>
    /// This is the acknowledgement half of the contract and it currently has no caller in
    /// <c>src/</c>: the one place that knows the block reached stdout is <c>Program.cs</c>, which
    /// this lane may not edit. Until that single line exists, every persisted row ages into
    /// <see cref="ServedStatus.Uncertain"/> and the cross-process dedupe deliberately suppresses
    /// nothing — memory is repeated rather than silently withheld, which is the correct direction to
    /// fail in.
    /// </remarks>
    public int AcknowledgeDelivery()
    {
        if (_prepared.Count == 0)
            return 0;

        using var ledger = ServedLedger.Open(IndexFile());
        var acknowledged = ledger?.Acknowledge(_prepared, _clock.Now) ?? 0;
        _prepared.Clear();
        return acknowledged;
    }

    /// <summary>Which vault a served note belongs to; two vaults on one machine never share a silence.</summary>
    private string VaultScope => _options.VaultPath is { Length: > 0 } vault ? VaultIdentity.Hash(VaultIdentity.Canonical(vault)) : string.Empty;

    /// <summary>Which assistant was served. <c>OOM_CLIENT</c> lets a second client say so without a rebuild.</summary>
    private string ClientScope =>
        Environment.GetEnvironmentVariable("OOM_CLIENT") is { Length: > 0 } client
            ? client
            : _options.Client is { Length: > 0 } configured ? configured : "claude";

    /// <summary>The hash of the text that was actually rendered, not of the note it came from.</summary>
    private static string ContentHash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text)))[..16];

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
            _surfaces = null;
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

    private string[] ContentWords(string text) =>
        Tokenize(text).Where(word => word.Length >= 4 && !Stopwords.Contains(word, StringComparer.Ordinal)).Distinct().ToArray();

    private IReadOnlyList<string> Tokenize(string text) => _fold.Tokenize(text ?? string.Empty);

    private string Signature(string query) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(string.Join(' ', Tokenize(query).OrderBy(x => x, StringComparer.Ordinal)))))[..16];

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

    private static bool IsPathToken(string word) =>
        word.Contains('\\') || (word.Contains('/') && word.Contains('.')) || word.Contains(":\\", StringComparison.Ordinal);

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
/// The served-note ledger, on disk, in <c>retrieve_served</c>. It exists because production spawns a
/// fresh <c>oom.exe</c> for every hook event: an in-memory dictionary is emptied between one prompt
/// and the next, so the seven-day dedupe the spec describes had never de-duplicated anything.
///
/// It never creates a file. A vault that has not been installed has no state root, and a retrieval
/// must not be the thing that mints one (Y-161..Y-163); with no ledger the dedupe simply falls back
/// to the process-local layer, which is what the old code had everywhere.
/// </summary>
internal sealed class ServedLedger : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _pid = Environment.ProcessId;

    private ServedLedger(SqliteConnection connection) => _connection = connection;

    internal static ServedLedger? Open(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;

        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection($"Data Source={path}");
            connection.Open();

            // A hook event can land while `oom sweep` holds the write handle. Wait rather than
            // fail: the dedupe degrading to silence is a repeated note, which is recoverable; the
            // dedupe throwing is a hook that returns nothing, which is not.
            using (var busy = connection.CreateCommand())
            {
                busy.CommandText = "PRAGMA busy_timeout=5000;";
                busy.ExecuteNonQuery();
            }

            if (!StateStore.TableExists(connection, "retrieve_served") || !ColumnExists(connection, "status"))
            {
                connection.Dispose();
                return null;
            }

            var ledger = new ServedLedger(connection);
            ledger.Reclassify();
            return ledger;
        }
        catch (SqliteException)
        {
            // A locked or damaged state database must degrade the dedupe, never the answer.
            connection?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Every row another process left <see cref="ServedStatus.Prepared"/> becomes
    /// <see cref="ServedStatus.Uncertain"/> the moment this process opens the ledger. Nobody
    /// acknowledged those blocks and nobody ever will; the table says so out loud rather than
    /// letting them read as deliveries.
    /// </summary>
    private void Reclassify() => Execute(
        "UPDATE retrieve_served SET status = $uncertain WHERE status = $prepared AND pid <> $pid",
        ("$uncertain", Name(ServedStatus.Uncertain)), ("$prepared", Name(ServedStatus.Prepared)), ("$pid", _pid));

    /// <summary>Whether this exact scope was acknowledged as delivered inside the window.</summary>
    internal bool WasEmitted(ServedKey key, DateTimeOffset cutoff)
    {
        using var command = Command(
            "SELECT 1 FROM retrieve_served WHERE status = $emitted AND ts >= $cutoff AND " +
            "vault = $vault AND client = $client AND entry = $entry AND session_id = $session AND " +
            "query_sig = $sig AND note = $note AND content_hash = $hash",
            [("$emitted", Name(ServedStatus.Emitted)), ("$cutoff", Stamp(cutoff)), .. Scope(key)]);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Records the block as built but unacknowledged. Returns whether the row was written.</summary>
    internal bool Prepare(ServedKey key, DateTimeOffset now)
    {
        try
        {
            using var command = Command(
                "INSERT INTO retrieve_served(vault, client, entry, session_id, query_sig, note, content_hash, ts, status, pid, acked_ts) " +
                "VALUES($vault, $client, $entry, $session, $sig, $note, $hash, $ts, $prepared, $pid, NULL) " +
                "ON CONFLICT(vault, client, entry, session_id, query_sig, note, content_hash) DO UPDATE SET " +
                "ts = $ts, status = $prepared, pid = $pid, acked_ts = NULL",
                [("$ts", Stamp(now)), ("$prepared", Name(ServedStatus.Prepared)), ("$pid", _pid), .. Scope(key)]);
            return command.ExecuteNonQuery() > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Turns this process's own prepared rows into acknowledged deliveries.</summary>
    internal int Acknowledge(IReadOnlyList<ServedKey> keys, DateTimeOffset now)
    {
        var acknowledged = 0;
        foreach (var key in keys)
        {
            using var command = Command(
                "UPDATE retrieve_served SET status = $emitted, acked_ts = $ts WHERE pid = $pid AND status = $prepared AND " +
                "vault = $vault AND client = $client AND entry = $entry AND session_id = $session AND " +
                "query_sig = $sig AND note = $note AND content_hash = $hash",
                [("$emitted", Name(ServedStatus.Emitted)), ("$prepared", Name(ServedStatus.Prepared)), ("$ts", Stamp(now)), ("$pid", _pid), .. Scope(key)]);
            acknowledged += command.ExecuteNonQuery();
        }

        return acknowledged;
    }

    /// <summary>The seven-day window, applied where the rows are; <c>State.SweepRetention</c> prunes the same table.</summary>
    internal void Prune(DateTimeOffset cutoff) =>
        Execute("DELETE FROM retrieve_served WHERE ts < $cutoff", ("$cutoff", Stamp(cutoff)));

    public void Dispose() => _connection.Dispose();

    private static (string Name, object Value)[] Scope(ServedKey key) =>
    [
        ("$vault", key.Vault), ("$client", key.Client), ("$entry", key.Entry), ("$session", key.SessionId),
        ("$sig", key.Signature), ("$note", key.Note), ("$hash", key.ContentHash)
    ];

    private static bool ColumnExists(SqliteConnection connection, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info('retrieve_served') WHERE name = $name";
        command.Parameters.AddWithValue("$name", column);
        return command.ExecuteScalar() is not null;
    }

    internal static string Name(ServedStatus status) => status.ToString().ToLowerInvariant();

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, (string Name, object Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
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
