// yazan: claude · opus-5
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// What has to be true before a recall number is allowed to mean anything about the product.
///
/// <para><c>RecallEvidenceTests</c> (wave 1) pinned the shape of an evidence file: the four-part
/// provenance tuple, the redaction rule, refusal before measuring, and the separation of the
/// controls from the recall. Three of its checks were not checks. The identity assertions read
/// <c>identity.*.verified</c> — a boolean the runner writes about itself — and matched
/// <c>binary_sha256</c> against <c>^[0-9a-f]{64}$</c>, which is a check that a hash is
/// hexadecimal, not a check that it is <em>this</em> binary's. The thresholds were read out of
/// the evidence file's own <c>thresholds</c> block, so a file could lower its own bar. And the
/// corpus the runner snapshotted was <c>knowledge/concepts/*.md</c> alone, while the product's
/// retrieval path also reads <c>Duzeltmeler.md</c> and <c>.oom/oom.json</c>.</para>
///
/// <para>These tests close those three and add the three the plan asked for: the executable is
/// bound to an externally issued candidate manifest rather than to the measuring lane's own
/// checkout; every retrieval input is hashed before <em>and after</em> the run; and the indexed
/// path and the full scan are measured separately, with the indexed run required to
/// <em>demonstrate</em> that candidates were narrowed rather than assert it. That last one is not
/// decorative: <c>CandidateNames</c> falls back to a full corpus scan in silence whenever the
/// index is missing, stale or unreadable, <c>CandidateSource</c> is <c>internal</c>, and nothing
/// in the process's stdout, stderr or exit code distinguishes the two. <c>Y-256</c> is the test
/// that makes the harness's witness probe non-circular: it performs the probe against the real
/// ranker and the real index.</para>
///
/// <para>Like wave 1's file, nothing here measures recall. The binding run happens in INT-3
/// against the final unified candidate's published exe. What is pinned is the contract that run
/// must satisfy, asserted against artifacts committed under <c>bench/results/astra-dalga-2/</c>:
/// one run that issued a verdict, nine that refused to, and one forged file that would pass a
/// gate which reads its bar out of the evidence.</para>
/// </summary>
public sealed class RecallCandidateBindingTests
{
    private const string Bundle = "astra-dalga-2";

    // The gate, written down here and in bench/verify_recall.py, and read from nowhere else.
    // A consumer that takes its bar from the file it is grading has no bar.
    private const double RecallAt3 = 0.80;
    private const double RecallAt5 = 0.88;
    private const int ScoredMinimum = 125;
    private static readonly (string Class, int MinimumN, double RecallAt5)[] ClassFloors =
    [
        ("tek-not", 90, 0.85),
        ("cok-not", 20, 0.90),
    ];

    private static string ResultsPath(params string[] parts) =>
        Path.Combine([ScarFixture.RepositoryRoot(), "bench", "results", .. parts]);

    private static JsonDocument Load(params string[] parts)
    {
        var path = ResultsPath(parts);
        Assert.True(File.Exists(path), $"kanıt dosyası yok: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement? Maybe(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    /// <summary>
    /// The recall gate as a predicate over an evidence file, with every number it compares
    /// against held as a constant in this class. Returns the reasons the file fails; empty means
    /// it clears the bar.
    ///
    /// <para>The first check is the one wave 1 did not have: a file whose declared thresholds sit
    /// below the constants is rejected outright, whatever its numbers say. Reading the bar from
    /// the file is how a measurement grades its own exam, and a file that carries a lowered bar
    /// is evidence of an attempt, not of a pass.</para>
    /// </summary>
    private static List<string> GateFailures(JsonElement root)
    {
        var failures = new List<string>();

        if (Maybe(root, "thresholds") is { } declared)
        {
            if (declared.GetProperty("recall@3").GetDouble() < RecallAt3)
                failures.Add($"dosya kendi eşiğini düşürmüş: thresholds.recall@3 = {declared.GetProperty("recall@3").GetDouble()}");
            if (declared.GetProperty("recall@5").GetDouble() < RecallAt5)
                failures.Add($"dosya kendi eşiğini düşürmüş: thresholds.recall@5 = {declared.GetProperty("recall@5").GetDouble()}");
            if (declared.GetProperty("scored_minimum").GetInt32() < ScoredMinimum)
                failures.Add($"dosya kendi eşiğini düşürmüş: thresholds.scored_minimum = {declared.GetProperty("scored_minimum").GetInt32()}");
            foreach (var (name, minimum, floor) in ClassFloors)
            {
                var block = declared.GetProperty("per_sinif").GetProperty(name);
                if (block.GetProperty("n").GetInt32() < minimum || block.GetProperty("recall@5").GetDouble() < floor)
                    failures.Add($"dosya kendi sınıf eşiğini düşürmüş: thresholds.per_sinif.{name}");
            }
        }
        else
        {
            failures.Add("thresholds bloğu yok");
        }

        if (root.GetProperty("run_status").GetString() != "ok")
            failures.Add($"run_status = {root.GetProperty("run_status").GetString()}");

        if (Maybe(root, "overall") is not { } overall)
        {
            failures.Add("overall yok");
            return failures;
        }

        if (overall.GetProperty("n").GetInt32() < ScoredMinimum)
            failures.Add($"puanlanan soru {overall.GetProperty("n").GetInt32()} < {ScoredMinimum}");
        if (overall.GetProperty("recall@3").GetDouble() < RecallAt3)
            failures.Add($"recall@3 {overall.GetProperty("recall@3").GetDouble()} < {RecallAt3}");
        if (overall.GetProperty("recall@5").GetDouble() < RecallAt5)
            failures.Add($"recall@5 {overall.GetProperty("recall@5").GetDouble()} < {RecallAt5}");

        var classes = root.GetProperty("per_sinif");
        foreach (var (name, minimum, floor) in ClassFloors)
        {
            if (!classes.TryGetProperty(name, out var measured))
            {
                failures.Add($"sınıf ölçülmemiş: {name}");
                continue;
            }
            if (measured.GetProperty("n").GetInt32() < minimum)
                failures.Add($"{name} n = {measured.GetProperty("n").GetInt32()} < {minimum}");
            if (measured.GetProperty("recall@5").GetDouble() < floor)
                failures.Add($"{name} recall@5 = {measured.GetProperty("recall@5").GetDouble()} < {floor}");
        }

        return failures;
    }

    // yazan: claude · opus-5
    /// <summary>
    /// The executable's provenance has to come from outside the lane that measures it.
    ///
    /// <para>Wave 1's runner recorded <c>source_commit</c> from <c>git rev-parse HEAD</c> in the
    /// directory the script lives in. That is the measuring tree — not the tree the binary was
    /// built from, and not, in this project's own working arrangement, even the same worktree.
    /// The pairing it produced was therefore "some exe" beside "some commit", with nothing
    /// joining them; a reader checking that <c>binary_sha256</c> matches <c>^[0-9a-f]{64}$</c>
    /// learns that a hash is a hash.</para>
    ///
    /// <para>The binding is now an externally issued candidate manifest: a file naming the
    /// accepted <c>source_commit</c> and the published exe's SHA-256, hashed into the evidence so
    /// the statement itself cannot be swapped afterwards. The runner's own checkout is still
    /// recorded — it is useful — but next to <c>checkout_is_not_provenance: true</c>, so it can
    /// never be mistaken for the answer. And the gate this closes is the one the plan names first:
    /// old evidence against a new binary refuses <em>before</em> a query is issued.</para>
    /// </summary>
    [Fact(DisplayName = "Y-250 · Kanıt, koşucunun checkout'una değil dışarıdan verilen aday manifestine bağlanır")]
    public void Y250_EvidenceBindsToAnExternalCandidateManifestNotTheRunnersCheckout()
    {
        using var honest = Load(Bundle, "selftest-1-honest-full-scan.json");
        var root = honest.RootElement;
        var candidate = root.GetProperty("candidate");

        // The manifest exists, was hashed, and names both halves of the binding.
        Assert.NotNull(candidate.GetProperty("manifest_sha256").GetString());
        Assert.Matches("^[0-9a-f]{64}$", candidate.GetProperty("manifest_sha256").GetString()!);
        Assert.NotNull(candidate.GetProperty("source_commit").GetString());
        var published = candidate.GetProperty("publish").GetProperty("sha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", published!);

        // The measured binary IS the published one — compared, not described.
        Assert.Equal(published, root.GetProperty("binary_sha256").GetString());
        // Read as a value kind rather than through GetBoolean: an unpinned run writes null here,
        // and "the comparison did not happen" has to fail as a missing check with a reason, not as
        // a JSON type exception that says nothing about what went wrong.
        Assert.True(candidate.GetProperty("exe_matches_manifest").ValueKind == JsonValueKind.True,
            "exe aday manifestine karşı doğrulanmadı: candidate.exe_matches_manifest doğru değil");

        // The file's own `source_commit` is the manifest's, and the runner's checkout is carried
        // separately and explicitly disclaimed.
        Assert.Equal(candidate.GetProperty("source_commit").GetString(),
            root.GetProperty("source_commit").GetString());
        Assert.True(candidate.GetProperty("checkout_is_not_provenance").GetBoolean());
        Assert.True(candidate.TryGetProperty("runner_checkout_commit", out _));

        // Old evidence, new exe: the manifest names a binary that is not the one on disk, and the
        // run stops before it can produce a number to argue with.
        using var stale = Load(Bundle, "selftest-2-stale-candidate.json");
        var refused = stale.RootElement;
        Assert.True(refused.GetProperty("refused_before_measuring").GetBoolean());
        Assert.Equal("invalid", refused.GetProperty("run_status").GetString());
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("overall").ValueKind);
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("decision").ValueKind);
        Assert.Equal("none-kept", refused.GetProperty("raw_artifact").GetString());
        Assert.True(refused.GetProperty("candidate").GetProperty("exe_matches_manifest").ValueKind == JsonValueKind.False,
            "reddedilen koşumda karşılaştırma yapılmış olmalı ve sonucu yanlış olmalı");
        Assert.NotEqual(refused.GetProperty("candidate").GetProperty("publish").GetProperty("sha256").GetString(),
            refused.GetProperty("binary_sha256").GetString());
    }

    // yazan: claude · opus-5
    /// <summary>
    /// A gate whose thresholds come out of the file it is grading is not a gate.
    ///
    /// <para>Wave 1's proposed Y-042 read <c>thresholds.recall@3</c> and <c>thresholds.recall@5</c>
    /// from the evidence and compared <c>overall</c> against them. Any file that lowered its own
    /// block passed. The numbers are constants here and in <c>bench/verify_recall.py</c>, and the
    /// file's declared block is treated as a claim about what was applied: if it declares anything
    /// below the constants, that is itself a failure, because a compliant runner cannot produce
    /// it.</para>
    ///
    /// <para><c>forged-1-lowered-thresholds.json</c> is the committed negative control. It is the
    /// honest run's file with the thresholds edited down until its (also edited-down) numbers
    /// clear them, every <c>pass</c> reading true and <c>decision.recall_gate: true</c>. It is
    /// well-formed, it satisfies PROVENANCE.md §2 in full, and it is a lie; a gate that reads its
    /// bar from the evidence accepts it without noticing.</para>
    /// </summary>
    [Fact(DisplayName = "Y-251 · Eşikler sabittir; kanıt dosyası kendi eşiğini düşüremez")]
    public void Y251_ThresholdsAreConstantsAndCannotBeLoweredByTheEvidenceFile()
    {
        using var honest = Load(Bundle, "selftest-1-honest-full-scan.json");
        Assert.Empty(GateFailures(honest.RootElement));

        // The file records what was applied, and it matches the constants exactly. Recording it is
        // for the reader; it is not where the bar comes from, which is what `authority` says.
        var declared = honest.RootElement.GetProperty("thresholds");
        Assert.Equal(RecallAt3, declared.GetProperty("recall@3").GetDouble(), 3);
        Assert.Equal(RecallAt5, declared.GetProperty("recall@5").GetDouble(), 3);
        Assert.Equal(ScoredMinimum, declared.GetProperty("scored_minimum").GetInt32());
        Assert.Contains("not this file", declared.GetProperty("authority").GetString()!, StringComparison.Ordinal);

        // The forgery: every verdict true, every number below the gate, thresholds rewritten to fit.
        using var forged = Load(Bundle, "forged-1-lowered-thresholds.json");
        var root = forged.RootElement;
        Assert.True(root.GetProperty("forgery").GetProperty("is_forged").GetBoolean());
        Assert.Equal("ok", root.GetProperty("run_status").GetString());
        Assert.True(root.GetProperty("decision").GetProperty("recall_gate").GetBoolean());

        var failures = GateFailures(root);
        Assert.NotEmpty(failures);
        Assert.Contains(failures, reason => reason.Contains("thresholds.recall@3", StringComparison.Ordinal));
        Assert.Contains(failures, reason => reason.StartsWith("recall@5 ", StringComparison.Ordinal));
        Assert.Contains(failures, reason => reason.StartsWith("tek-not n = ", StringComparison.Ordinal));
        Assert.Contains(failures, reason => reason.Contains($"puanlanan soru 20 < {ScoredMinimum}", StringComparison.Ordinal));
    }

    // yazan: claude · opus-5
    /// <summary>
    /// The inventory has to cover everything that can change an answer, and copying it must never
    /// destroy anything.
    ///
    /// <para>The product's retrieval path reads three things out of the vault: the concept corpus,
    /// <c>&lt;companion&gt;/Duzeltmeler.md</c>, and <c>.oom/oom.json</c> — whose <c>retrieve.top</c>,
    /// <c>perNoteChars</c>, <c>totalChars</c>, <c>minOverlap</c> and <c>strictScore</c> reach the
    /// ranker. Wave 1's snapshot copied the first of the three, so its corpus hash described a
    /// third of what was measured and the other two could move without moving a number.</para>
    ///
    /// <para>The second half is about the copy itself. Wave 1 called <c>shutil.rmtree</c> on the
    /// destination before copying. Pointed at the wrong path — and the owner's vault is one
    /// mistyped argument away — that is a silent, unrecoverable deletion of somebody else's data,
    /// performed by a measuring tool that was supposed to be read-only. A destination that
    /// overlaps the source is refused, and a destination that already holds files is refused; the
    /// refusal happens before the destination is even created, so a bad path does not leave a
    /// directory behind inside the vault it was about to be measured from.</para>
    /// </summary>
    [Fact(DisplayName = "Y-252 · Ölçülen girdiler düzeltmeler ve ayarları da kapsar; kopya hedefi silinmez")]
    public void Y252_TheInventoryCoversCorrectionsAndSettingsAndTheSnapshotNeverDeletes()
    {
        using var honest = Load(Bundle, "selftest-1-honest-full-scan.json");
        var inputs = honest.RootElement.GetProperty("inputs");

        Assert.Matches("^[0-9a-f]{64}$", inputs.GetProperty("corpus").GetProperty("sha256").GetString()!);
        Assert.True(inputs.GetProperty("corpus").GetProperty("files").GetInt32() > 0);

        var corrections = inputs.GetProperty("corrections");
        Assert.True(corrections.GetProperty("present").GetBoolean());
        Assert.Matches("^[0-9a-f]{64}$", corrections.GetProperty("sha256").GetString()!);
        Assert.NotEmpty(corrections.GetProperty("ids").EnumerateArray());

        var settings = inputs.GetProperty("settings");
        Assert.True(settings.GetProperty("present").GetBoolean());
        Assert.Matches("^[0-9a-f]{64}$", settings.GetProperty("sha256").GetString()!);
        foreach (var key in new[] { "top", "perNoteChars", "totalChars", "minOverlap", "strictScore" })
            Assert.True(settings.GetProperty("retrieval_keys").TryGetProperty(key, out _),
                $"getirme sonucunu etkileyen ayar envanterde yok: retrieve.{key}");

        // One digest over all five inputs, so "is this the same measurement" is one comparison.
        Assert.Matches("^[0-9a-f]{64}$", inputs.GetProperty("inputs_digest").GetString()!);
        Assert.Equal(inputs.GetProperty("inputs_digest").GetString(),
            honest.RootElement.GetProperty("identity").GetProperty("inputs_digest").GetString());

        var snapshot = inputs.GetProperty("snapshot");
        Assert.True(snapshot.GetProperty("requested").GetBoolean());
        Assert.True(snapshot.GetProperty("disjoint").GetBoolean());
        Assert.NotEqual(snapshot.GetProperty("source").GetString(), snapshot.GetProperty("destination").GetString());

        // A destination inside the source is refused, and refused before it is created.
        using var overlapping = Load(Bundle, "selftest-5-overlapping-snapshot.json");
        Assert.True(overlapping.RootElement.GetProperty("refused_before_measuring").GetBoolean());
        Assert.Equal(JsonValueKind.Null, overlapping.RootElement.GetProperty("decision").ValueKind);
        Assert.Contains(overlapping.RootElement.GetProperty("run_status_reasons").EnumerateArray(),
            reason => reason.GetString()!.Contains("overlaps the source vault", StringComparison.Ordinal));

        // A destination that already holds files is refused rather than emptied.
        using var dirty = Load(Bundle, "selftest-6-dirty-snapshot-destination.json");
        Assert.True(dirty.RootElement.GetProperty("refused_before_measuring").GetBoolean());
        Assert.Contains(dirty.RootElement.GetProperty("run_status_reasons").EnumerateArray(),
            reason => reason.GetString()!.Contains("refusing to delete it", StringComparison.Ordinal));
    }

    // yazan: claude · opus-5
    /// <summary>
    /// A correction hit has to be a correction that exists.
    ///
    /// <para>Wave 1's output check exempted every hit whose name contained a <c>#</c> from the
    /// corpus-existence test, reasoning that a correction is legitimately not a concept file. That
    /// is an unconditional amnesty: against a <c>Duzeltmeler.md</c> with two blocks,
    /// <c>Duzeltmeler.md#99</c> passed, and so would <c>anything#at-all</c>. The runner now parses
    /// the corrections file it copied and computes the exact set of ids it can produce.</para>
    ///
    /// <para>That set depends on a numbering rule it would be easy to guess wrong, so the second
    /// half of this test asks the product instead of assuming. <c>Retrieve.Corrections()</c> splits
    /// on <c>"\n## "</c> — H2 exactly — and names each block <c>Duzeltmeler.md#{notes.Count + 1}</c>,
    /// a counter over <em>accepted</em> blocks: a <c>## </c> whose title line is empty is skipped
    /// and consumes no number, so the id is not the heading's position in the file.
    /// <c>bench/verify_recall.py</c>'s <c>parse_corrections</c> reproduces exactly this, and its
    /// selftest refuses to run if it disagrees with the same fixture asserted here.</para>
    /// </summary>
    [Fact(DisplayName = "Y-253 · Uydurma düzeltme kimliği muaf değildir; numaralama ürünün kuralıdır")]
    public void Y253_FabricatedCorrectionIdsAreNotExemptAndNumberingIsTheProductsRule()
    {
        using var document = Load(Bundle, "selftest-4-fabricated-correction.json");
        var root = document.RootElement;

        Assert.Equal("invalid", root.GetProperty("run_status").GetString());
        Assert.False(root.GetProperty("refused_before_measuring").GetBoolean());
        Assert.False(root.GetProperty("output_integrity").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("overall").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").ValueKind);
        Assert.Contains(root.GetProperty("output_integrity").GetProperty("faults").EnumerateArray(),
            fault => fault.GetString()!.Contains("Duzeltmeler.md#99", StringComparison.Ordinal)
                  && fault.GetString()!.Contains("cannot produce", StringComparison.Ordinal));

        // The numbering rule, asked of the ranker rather than assumed. Three `## ` lines, the
        // middle one with an empty title: the product must produce #1 and #2 and never a #3.
        var vault = ScarFixture.TempDirectory();
        try
        {
            // A concept note has to exist: `LoadCorpus` returns an empty corpus the moment
            // `knowledge/concepts` is missing, and never reaches `Corrections()` at all — so a
            // companion-only vault would prove nothing about the numbering.
            WriteNote(vault, "ilgisiz-kavram.md", "bambaska bir konu hakkinda kisa bir not");
            Directory.CreateDirectory(Path.Combine(vault, ScarFixture.CompanionDir));
            File.WriteAllText(Path.Combine(vault, ScarFixture.CompanionDir, "Duzeltmeler.md"),
                "# Duzeltmeler\n\n" +
                "## Birinci baslik alfa\nAlfa govdesi burada.\n\n" +
                "## \nBasligi bos olan blok atlanir ve numara tuketmez.\n\n" +
                "## Ikinci baslik beta\nBeta govdesi burada.\n",
                new UTF8Encoding(false));

            // The query overlaps both blocks in four terms, comfortably past the default
            // `MinOverlap` of 3: what is under test is the numbering, and a query that fell under
            // the score floor would fail this test for an unrelated reason.
            var hits = new Retrieve(new RetrieveOptions(VaultPath: vault))
                .Query("baslik alfa beta govdesi burada", Guid.NewGuid().ToString(), 10).Hits;
            var corrections = hits.Where(hit => hit.Name.Contains('#', StringComparison.Ordinal)).ToArray();

            Assert.Equal(["Duzeltmeler.md#1", "Duzeltmeler.md#2"],
                corrections.Select(hit => hit.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain("Duzeltmeler.md#3", hits.Select(hit => hit.Name));
            Assert.All(corrections, hit => Assert.Equal("correction", hit.Source));
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    // yazan: claude · opus-5
    /// <summary>
    /// An input that moved during the run splits it across two measurements, and nothing in the
    /// numbers would say so.
    ///
    /// <para>Wave 1 hashed its inputs once, up front, and never looked again. A corpus edited
    /// between query 3 and query 90 therefore produced a well-formed <c>run_status: "ok"</c> file
    /// describing a corpus that no longer existed. The inputs are now re-hashed after the batch,
    /// and the comparison is reported as <c>identity_stability</c> with the moved input named.</para>
    ///
    /// <para>Where that check sits matters as much as that it exists. It runs immediately after the
    /// backend returns and <em>before</em> the output-integrity refusal — because a refusal that
    /// happens earlier than the check it is meant to demonstrate demonstrates nothing about that
    /// check. Wave 1 made exactly that mistake once: its "corrupt output fails the gate" evidence
    /// came from a run that had already been rejected before the integrity checks ran, so the
    /// checks never executed. The scenario here is a pass-through backend that answers honestly
    /// and then edits the corrections file, and the evidence carries both findings — the moved
    /// input, and the newly-numbered correction id the next query then returned.</para>
    /// </summary>
    [Fact(DisplayName = "Y-254 · Ölçüm öncesi ve sonrası kimlik karşılaştırılır; değişen girdi geçerli sonuç üretmez")]
    public void Y254_InputsAreRehashedAfterTheRunAndAMovedInputVoidsTheVerdict()
    {
        using var document = Load(Bundle, "selftest-7-input-changed-midrun.json");
        var root = document.RootElement;

        var stability = root.GetProperty("identity_stability");
        Assert.False(stability.GetProperty("stable").GetBoolean());
        Assert.NotEmpty(stability.GetProperty("changed").EnumerateArray());
        Assert.Contains(stability.GetProperty("changed").EnumerateArray(),
            item => item.GetString()!.StartsWith("corrections:", StringComparison.Ordinal));
        Assert.NotEqual(stability.GetProperty("inputs_digest_before").GetString(),
            stability.GetProperty("inputs_digest_after").GetString());

        Assert.Equal("invalid", root.GetProperty("run_status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pass").ValueKind);

        // The check was reached, which is the point: the run got past identity, past the witness,
        // through the backend, and the stability comparison ran on the way out rather than being
        // skipped by an earlier refusal.
        Assert.False(root.GetProperty("refused_before_measuring").GetBoolean());

        // Every other committed scenario that measured anything reports the stability check as
        // having run — a field that is null wherever it is inconvenient is not a check.
        foreach (var name in new[] { "selftest-1-honest-full-scan.json", "selftest-3-corrupt-output.json",
                                     "selftest-4-fabricated-correction.json", "selftest-8-indexed-narrowing-proven.json",
                                     "selftest-9-unused-index.json" })
        {
            using var other = Load(Bundle, name);
            Assert.NotEqual(JsonValueKind.Null, other.RootElement.GetProperty("identity_stability").ValueKind);
        }
    }

    // yazan: claude · opus-5
    /// <summary>
    /// The indexed path and the full scan are two measurements, and the indexed one has to prove
    /// it was indexed.
    ///
    /// <para><c>CandidateNames</c> narrows through <c>notes_fts</c> when <c>state.db</c> is present
    /// and its stored manifest digest still matches the corpus, and returns <c>null</c> — a full
    /// scan — when the file is missing, when the digest has moved, or when SQLite throws. All three
    /// are silent: <c>CandidateSource</c> is <c>internal</c>, the batch JSON carries
    /// <c>schema_version</c>, <c>query</c> and <c>hits[]{name,score,source,updated}</c>, stderr
    /// carries only the hook's gate reasons, and the exit code is the retrieval's, which is always
    /// zero. So "the index was used" cannot be read; it has to be demonstrated.</para>
    ///
    /// <para>The demonstration is a witness: one note's <c>notes_fts</c> row is deleted while
    /// <c>oom_index_meta</c> is left alone, so the freshness check still passes and the note is
    /// simply not a candidate any more. Ask for that note's own term. Absent means candidates were
    /// narrowed; present means the answer came from a full scan whatever the <c>state.db</c> beside
    /// it suggested, and the indexed verdict is refused. <c>selftest-9-unused-index</c> is that
    /// failure on purpose — a real index file, a run labelled indexed, and a backend that never
    /// opened it.</para>
    /// </summary>
    [Fact(DisplayName = "Y-255 · Tam tarama ve indeksli yol ayrı ölçülür; kullanılmayan indeks kanıt sayılmaz")]
    public void Y255_TheTwoRetrievalPathsAreMeasuredApartAndAnUnusedIndexIsNotEvidence()
    {
        using var scan = Load(Bundle, "selftest-1-honest-full-scan.json");
        Assert.Equal("full-scan", scan.RootElement.GetProperty("index_mode").GetString());
        var scanWitness = scan.RootElement.GetProperty("index_witness");
        Assert.True(scanWitness.GetProperty("proven").GetBoolean());
        Assert.True(scanWitness.GetProperty("witness_returned").GetBoolean());
        Assert.False(scan.RootElement.GetProperty("index").GetProperty("state_db_present").GetBoolean());
        Assert.True(scan.RootElement.GetProperty("pass").GetProperty("index_path_proven").GetBoolean());

        // Indexed, and proven so: the deleted row's note could not come back, the manifest was not
        // touched, and the pristine index was restored and verified afterwards.
        using var indexed = Load(Bundle, "selftest-8-indexed-narrowing-proven.json");
        Assert.Equal("indexed", indexed.RootElement.GetProperty("index_mode").GetString());
        var witness = indexed.RootElement.GetProperty("index_witness");
        Assert.True(witness.GetProperty("proven").GetBoolean());
        Assert.False(witness.GetProperty("witness_returned").GetBoolean());
        Assert.True(witness.GetProperty("rows_deleted").GetInt32() > 0);
        Assert.True(witness.GetProperty("restore_verified").GetBoolean());
        Assert.NotEmpty(witness.GetProperty("hits").EnumerateArray());   // an empty answer proves nothing
        Assert.Equal(witness.GetProperty("manifest_before").GetProperty("manifest_digest").GetString(),
            witness.GetProperty("manifest_after").GetProperty("manifest_digest").GetString());

        // A state.db that is present but never consulted: the witness comes back, so this run is a
        // full scan wearing an index's name and it carries no verdict.
        using var unused = Load(Bundle, "selftest-9-unused-index.json");
        Assert.Equal("indexed", unused.RootElement.GetProperty("index_mode").GetString());
        Assert.True(unused.RootElement.GetProperty("index").GetProperty("state_db_present").GetBoolean());
        Assert.True(unused.RootElement.GetProperty("index_witness").GetProperty("witness_returned").GetBoolean());
        Assert.False(unused.RootElement.GetProperty("index_witness").GetProperty("proven").GetBoolean());
        Assert.Equal("invalid", unused.RootElement.GetProperty("run_status").GetString());
        Assert.Equal(JsonValueKind.Null, unused.RootElement.GetProperty("decision").ValueKind);
        Assert.Contains(unused.RootElement.GetProperty("run_status_reasons").EnumerateArray(),
            reason => reason.GetString()!.Contains("index witness failed", StringComparison.Ordinal));

        // And an indexed run with no index at all is refused before a query is issued, rather than
        // quietly becoming the full-scan measurement that already has its own file.
        using var missing = Load(Bundle, "selftest-10-indexed-without-state-db.json");
        Assert.True(missing.RootElement.GetProperty("refused_before_measuring").GetBoolean());
        Assert.Contains(missing.RootElement.GetProperty("run_status_reasons").EnumerateArray(),
            reason => reason.GetString()!.Contains("no state.db exists", StringComparison.Ordinal));
    }

    // yazan: claude · opus-5
    /// <summary>
    /// The witness probe, performed against the real ranker and a real index.
    ///
    /// <para>Everything <c>Y-255</c> asserts rests on one claim about the product: that a note
    /// missing from <c>notes_fts</c> cannot be returned while the index is in use, and can be
    /// returned when it is not. If that claim were false the probe would be theatre — it would
    /// "prove" the index was used in runs where it was not, or fail runs where it was. The
    /// harness's own selftest cannot settle it, because <c>Retrieve.Build()</c> is not reachable
    /// from the CLI (<c>sweep</c>, <c>compile</c> and <c>doctor --fix</c> each build the index as a
    /// side effect of doing something else, and two of them write into the vault), so the Python
    /// fixtures drive a shim against a stand-in database.</para>
    ///
    /// <para>Here there is no shim. A real index is built over a real fixture vault, the witness
    /// note's row is deleted from <c>notes_fts</c> while <c>oom_index_meta</c> is left untouched —
    /// so the digest comparison in <c>CandidateNames</c> still succeeds and the index is still
    /// considered usable — and the same query is asked twice: once of a ranker with the index and
    /// once of a ranker without one. The unindexed ranker reaches the note; the indexed one cannot.
    /// <c>CandidateSource</c> is asserted on both sides so the difference is attributed to the
    /// candidate set rather than to a ranking accident.</para>
    ///
    /// <para>The five siblings sharing the family term are not decoration. A witness query that
    /// only its own note can answer returns an <em>empty</em> list once that note is excluded, and
    /// an empty answer cannot distinguish "the candidate set excluded it" from "the query reached
    /// nothing" — which is precisely the case the probe exists for. The siblings keep the answer
    /// non-empty so the witness's absence is the entire signal.</para>
    /// </summary>
    [Fact(DisplayName = "Y-256 · Tanık sondası gerçek üründe kanıtlanır: FTS satırı silinen not indeksli yolda erişilemez")]
    public void Y256_TheIndexWitnessHoldsAgainstTheRealRankerAndARealIndex()
    {
        const string Witness = "kume-0.md";
        const string Query = "zeugmatik aileterimi";
        var vault = ScarFixture.TempDirectory();
        var index = Path.Combine(vault, "state.db");
        try
        {
            // Six notes in one family; only the witness also carries the rare term.
            WriteNote(vault, Witness, "zeugmatik terimi aileterimi ailesindendir");
            for (var sibling = 1; sibling < 6; sibling++)
                WriteNote(vault, $"kume-{sibling}.md", $"kardes{sibling} aileterimi ailesindendir");

            new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index)).Build();
            var manifestBefore = ManifestDigest(index);

            // Without an index the ranker scans the corpus and reaches the witness.
            var scanner = new Retrieve(new RetrieveOptions(VaultPath: vault));
            var scanned = scanner.Query(Query, Guid.NewGuid().ToString(), 5).Hits.Select(hit => hit.Name).ToArray();
            Assert.Equal("corpus-scan:no-index", scanner.CandidateSource);
            Assert.Contains(Witness, scanned);

            // Delete only the witness's FTS row. `oom_index_meta` is untouched, so the digest
            // comparison still matches the corpus and the index is still considered usable —
            // a stale index would fall back to a scan and the probe would prove nothing.
            using (var connection = new SqliteConnection($"Data Source={index}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM notes_fts WHERE name = $name";
                command.Parameters.AddWithValue("$name", Witness);
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            Assert.Equal(manifestBefore, ManifestDigest(index));

            // A fresh ranker, as a fresh process would be: the index is consulted, the witness is
            // not a candidate, and it cannot be returned however well it would have ranked.
            var indexed = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: index));
            var narrowed = indexed.Query(Query, Guid.NewGuid().ToString(), 5).Hits.Select(hit => hit.Name).ToArray();
            Assert.Equal("fts", indexed.CandidateSource);
            Assert.NotEmpty(narrowed);
            Assert.DoesNotContain(Witness, narrowed);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    private static string WriteNote(string vault, string name, string body)
    {
        var concepts = Path.Combine(vault, "knowledge", "concepts");
        Directory.CreateDirectory(concepts);
        var path = Path.Combine(concepts, name);
        File.WriteAllText(path,
            $"---\nyazan: claude\nmodel: opus-5\ntitle: {Path.GetFileNameWithoutExtension(name)} basligi\n" +
            "aliases: []\ntags: []\nsources: [2026-09-11.md]\ncreated: 2026-09-11\nupdated: 2026-09-11\n---\n" + body,
            new UTF8Encoding(false));
        return path;
    }

    private static string ManifestDigest(string index)
    {
        using var connection = new SqliteConnection($"Data Source={index}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_digest FROM oom_index_meta ORDER BY generation DESC LIMIT 1";
        return (string)command.ExecuteScalar()!;
    }
}
