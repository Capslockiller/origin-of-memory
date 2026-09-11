// yazan: claude · opus-5
using System.Text.Json;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// What separates a recall number that is evidence from one that is a claim.
///
/// <para><c>Y-042</c> reads its floor out of <c>bench/results/recall-2026-09-09-r2-gate.json</c>
/// — the file <c>bench/PROVENANCE.md</c> §3 names, by path, as its worked example of a run that
/// recorded a failure and asserted a pass in the same breath. The deeper problem is not the
/// provenance: that test reads numbers a past run left on disk and never measures the ranking
/// that is compiled into the binary beside it. Its one live assertion checks that five concept
/// results come back, which is a shape, not a recall.</para>
///
/// <para>These tests do not re-measure anything — measuring is
/// <c>bench/verify_recall.py</c>'s job, and the binding run happens against the final unified
/// candidate's published executable. What they pin is the contract that run has to satisfy for
/// its output to count, and they pin it against artifacts committed under
/// <c>bench/results/astra-dalga-1/</c>: a gate that issued a verdict, and three that refused
/// to. A contract asserted only against files that satisfy it is not a contract, so each test
/// here checks both sides — what the compliant bundle carries, and what the historical file
/// does not.</para>
/// </summary>
public sealed class RecallEvidenceTests
{
    private const string Bundle = "astra-dalga-1";

    private static string ResultsPath(params string[] parts) =>
        Path.Combine([ScarFixture.RepositoryRoot(), "bench", "results", .. parts]);

    private static JsonDocument Load(params string[] parts)
    {
        var path = ResultsPath(parts);
        Assert.True(File.Exists(path), $"kanıt dosyası yok: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// PROVENANCE.md §2 and §4 as a predicate rather than as prose: who measured it, which
    /// command produced it, which raw artifact holds the output, which commit or binary it ran
    /// against, whether the run was clean, and whether anything stood in for the spec'd
    /// instrument. Returns the field names a file is missing; empty means the file clears the
    /// bar. `source_commit` and `binary_sha256` are one requirement, not two — §2 asks for at
    /// least one of them.
    /// </summary>
    private static string[] MissingProvenanceFields(JsonElement root)
    {
        var missing = new List<string>();
        foreach (var field in new[] { "measured_by", "command", "run_status", "raw_artifact", "substitute" })
            if (!root.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
                missing.Add(field);

        var commit = root.TryGetProperty("source_commit", out var c) && c.ValueKind != JsonValueKind.Null;
        var binary = root.TryGetProperty("binary_sha256", out var b) && b.ValueKind != JsonValueKind.Null;
        if (!commit && !binary)
            missing.Add("source_commit|binary_sha256");

        return [.. missing];
    }

    /// <summary>Every `pass`/`decision` leaf in the document, by dotted path — §3's redaction rule.</summary>
    private static IEnumerable<(string Path, JsonElement Value)> Verdicts(JsonElement node, string path = "")
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                var child = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                if (property.Name is "pass" or "decision")
                    yield return (child, property.Value);
                else
                    foreach (var found in Verdicts(property.Value, child))
                        yield return found;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
                foreach (var found in Verdicts(item, $"{path}[{index++}]"))
                    yield return found;
        }
    }

    private static bool AssertsTrue(JsonElement verdict)
    {
        if (verdict.ValueKind == JsonValueKind.True)
            return true;
        if (verdict.ValueKind != JsonValueKind.Object)
            return false;
        return verdict.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.True);
    }

    // yazan: claude · opus-5
    /// <summary>
    /// The four-part tuple is what makes a results file evidence, and the file Y-042 reads does
    /// not have it. This is not a style complaint: without `raw_artifact` there is nothing to
    /// re-read, and without `source_commit`/`binary_sha256` the numbers do not name what they
    /// measured — so the floor Y-042 enforces is a floor under an unidentified run.
    /// </summary>
    [Fact(DisplayName = "Y-220 · Kanıt dosyası dört parçalı demeti taşır; Y-042'nin okuduğu tarihî dosya taşımaz")]
    public void Y220_EvidenceCarriesTheFourPartTupleAndTheHistoricalFileDoesNot()
    {
        using var fresh = Load(Bundle, "selftest-summary.json");
        Assert.Empty(MissingProvenanceFields(fresh.RootElement));

        using var historical = Load("recall-2026-09-09-r2-gate.json");
        var missing = MissingProvenanceFields(historical.RootElement);
        Assert.Contains("measured_by", missing);
        Assert.Contains("raw_artifact", missing);
        Assert.Contains("substitute", missing);
        Assert.Contains("run_status", missing);
        Assert.Contains("source_commit|binary_sha256", missing);
    }

    // yazan: claude · opus-5
    /// <summary>
    /// PROVENANCE.md §3 is binding: a run that is not `ok` may report raw numbers but may not
    /// assert that a gate passed. The historical file breaks it in the direction that matters —
    /// `index.error` is a recorded failure and `pass.recall@5` still reads `true`. Every refusal
    /// this lane's runner emits obeys it instead, which is the property under test: refusing is
    /// something the code does, not something a report says it does.
    /// </summary>
    [Fact(DisplayName = "Y-221 · run_status 'ok' değilken hiçbir dosya kapı geçti diyemez")]
    public void Y221_ANonOkRunMayNotAssertAPass()
    {
        foreach (var name in new[] { "selftest-2-wrong-exe-hash.json", "selftest-3-corrupt-output.json", "selftest-4-misaligned-output.json" })
        {
            using var document = Load(Bundle, name);
            Assert.NotEqual("ok", document.RootElement.GetProperty("run_status").GetString());
            foreach (var (path, verdict) in Verdicts(document.RootElement))
                Assert.False(AssertsTrue(verdict), $"{name}: run_status 'ok' değil ama {path} doğru diyor");
        }

        // The shape §3 forbids, still on disk, still read by Y-042.
        using var historical = Load("recall-2026-09-09-r2-gate.json");
        Assert.Equal(JsonValueKind.String, historical.RootElement.GetProperty("index").GetProperty("error").ValueKind);
        Assert.True(historical.RootElement.GetProperty("pass").GetProperty("recall@5").GetBoolean(),
            "tarihî dosya artık pass.recall@5 doğru demiyor — bu testin anlattığı bulgu değişmiş demektir");
    }

    // yazan: claude · opus-5
    /// <summary>
    /// The identity check has to happen before the measurement, not after it. `kos20.py` records
    /// `binary_sha256` from whatever executable it happened to run; that is a description. Here a
    /// mismatched expectation stops the run — `refused_before_measuring` is true, there is no
    /// `overall` block at all, and `raw_artifact` is the literal `none-kept` §2 requires rather
    /// than a silent omission. A wrong binary cannot produce a number to argue with.
    /// </summary>
    [Fact(DisplayName = "Y-222 · Yanlış exe hash'i ölçümden önce reddedilir, sayı üretilmez")]
    public void Y222_AWrongExecutableHashRefusesBeforeMeasuring()
    {
        using var document = Load(Bundle, "selftest-2-wrong-exe-hash.json");
        var root = document.RootElement;

        Assert.True(root.GetProperty("refused_before_measuring").GetBoolean());
        Assert.Equal("invalid", root.GetProperty("run_status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("overall").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").ValueKind);
        Assert.Equal("none-kept", root.GetProperty("raw_artifact").GetString());

        var executable = root.GetProperty("identity").GetProperty("executable");
        Assert.False(executable.GetProperty("verified").GetBoolean());
        Assert.NotEqual(executable.GetProperty("expected").GetString(), executable.GetProperty("sha256").GetString());

        // The honest run is the other side of the same check: pinned, and verified true.
        using var honest = Load(Bundle, "selftest-1-honest.json");
        Assert.True(honest.RootElement.GetProperty("identity").GetProperty("executable").GetProperty("verified").GetBoolean());
        Assert.True(honest.RootElement.GetProperty("identity").GetProperty("corpus").GetProperty("verified").GetBoolean());
        Assert.True(honest.RootElement.GetProperty("identity").GetProperty("gold").GetProperty("verified").GetBoolean());
    }

    // yazan: claude · opus-5
    /// <summary>
    /// A backend that answers with notes which are not in the corpus is not a ranker scoring
    /// badly; it is not a ranker. Averaged blindly it yields `recall@5: 0.0` and `run_status:
    /// "ok"` — a number, publishable, and false. The runner checks each returned note against the
    /// corpus it actually measured, so corrupt output produces no recall figure at all.
    /// </summary>
    [Fact(DisplayName = "Y-223 · Bozuk getirme çıktısı düşük skora değil, skorsuzluğa düşer")]
    public void Y223_CorruptRetrievalOutputYieldsNoScoreAtAll()
    {
        using var document = Load(Bundle, "selftest-3-corrupt-output.json");
        var root = document.RootElement;

        Assert.Equal("invalid", root.GetProperty("run_status").GetString());
        Assert.False(root.GetProperty("output_integrity").GetProperty("ok").GetBoolean());
        Assert.NotEmpty(root.GetProperty("output_integrity").GetProperty("faults").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("overall").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("per_sinif").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("decision").ValueKind);

        // It was refused for what it returned, not for failing to return: the shim spoke the
        // protocol correctly and still could not be scored.
        Assert.False(root.GetProperty("refused_before_measuring").GetBoolean());
        Assert.Contains(root.GetProperty("output_integrity").GetProperty("faults").EnumerateArray(),
            fault => fault.GetString()!.Contains("is not in the measured corpus", StringComparison.Ordinal));

        // And the misalignment control catches the failure a line count cannot see.
        using var misaligned = Load(Bundle, "selftest-4-misaligned-output.json");
        Assert.Contains(misaligned.RootElement.GetProperty("output_integrity").GetProperty("faults").EnumerateArray(),
            fault => fault.GetString()!.Contains("echoed query does not match", StringComparison.Ordinal));
    }

    // yazan: claude · opus-5
    /// <summary>
    /// The binding gate is 125 scored questions at recall@3 ≥ 0.80 and recall@5 ≥ 0.88, and it is
    /// computed from those questions alone. The hook path and the negative controls are measured
    /// in the same pass and reported in their own block, because they answer a different question
    /// — `retrieve --hook` applies an intent gate and a score floor that `Query` never applies, so
    /// no injection rate is a recall. This test pins the separation: the controls block exists,
    /// carries its own verdict, and the recall verdict is not read from it.
    /// </summary>
    [Fact(DisplayName = "Y-224 · Kapı 125 puanlanan soruyu sayar; kanca ve negatif kontroller ayrı raporlanır ve yerine geçmez")]
    public void Y224_TheGateCountsScoredQuestionsAndControlsAreReportedApart()
    {
        using var document = Load(Bundle, "selftest-1-honest.json");
        var root = document.RootElement;

        var thresholds = root.GetProperty("thresholds");
        Assert.Equal(0.80, thresholds.GetProperty("recall@3").GetDouble(), 3);
        Assert.Equal(0.88, thresholds.GetProperty("recall@5").GetDouble(), 3);
        Assert.Equal(125, thresholds.GetProperty("scored_minimum").GetInt32());

        var overall = root.GetProperty("overall");
        Assert.Equal(125, overall.GetProperty("n").GetInt32());
        Assert.True(overall.GetProperty("recall@3").GetDouble() >= thresholds.GetProperty("recall@3").GetDouble());
        Assert.True(overall.GetProperty("recall@5").GetDouble() >= thresholds.GetProperty("recall@5").GetDouble());

        // recall@3 and recall@5 must be able to disagree, or the fixture cannot tell the two
        // thresholds apart and passing both of them means having cleared only one.
        Assert.True(overall.GetProperty("recall@5").GetDouble() > overall.GetProperty("recall@3").GetDouble(),
            "fikstür iki eşiği ayırt etmiyor: recall@5 ile recall@3 aynı");

        var controls = root.GetProperty("controls");
        Assert.Equal("kanarya", controls.GetProperty("negative_controls").GetProperty("label").GetString());
        Assert.True(controls.GetProperty("negative_controls").GetProperty("n").GetInt32() > 0);
        Assert.Contains("never a substitute", controls.GetProperty("measures").GetString()!, StringComparison.Ordinal);

        // The recall verdict names what it was computed from, and the controls carry a verdict of
        // their own rather than feeding that one.
        var decision = root.GetProperty("decision");
        Assert.True(decision.GetProperty("recall_gate").GetBoolean());
        Assert.Contains("scored questions only", decision.GetProperty("computed_from").GetString()!, StringComparison.Ordinal);
        Assert.True(decision.TryGetProperty("controls_gate", out _));

        // A hook that injects nothing at all has a zero false-positive rate and has distinguished
        // nothing; that has to read as no verdict, not as a pass.
        if (controls.GetProperty("vacuous").ValueKind == JsonValueKind.True)
            Assert.Equal(JsonValueKind.Null, controls.GetProperty("pass").ValueKind);
    }

    // yazan: claude · opus-5
    /// <summary>
    /// `tek-not` and `cok-not` are answer-cardinality labels the owner wrote into the gold set:
    /// one note answers, or several do. They are not an episodic/concept split, nothing in this
    /// repository maps them to one, and Y-042's own comment concedes that the episodic axis its
    /// wording implies "has no measurement anywhere in this repository". Renaming them would
    /// manufacture that axis out of two labels that do not mean it — so the labels are carried
    /// through verbatim and the missing axis is recorded as missing.
    /// </summary>
    [Fact(DisplayName = "Y-225 · Sınıf etiketleri yeniden adlandırılmaz; episodik eksen ölçülmedi diye açık kalır")]
    public void Y225_ClassLabelsAreNotRenamedAndTheEpisodicAxisStaysOpen()
    {
        using var document = Load(Bundle, "selftest-1-honest.json");
        var root = document.RootElement;

        var classes = root.GetProperty("per_sinif").EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Contains("tek-not", classes);
        Assert.Contains("cok-not", classes);
        foreach (var name in classes)
            Assert.DoesNotContain(name, new[] { "episodic", "episodik", "concept", "kavram" }, StringComparer.OrdinalIgnoreCase);

        var episodic = root.GetProperty("episodic_axis");
        Assert.False(episodic.GetProperty("measured").GetBoolean());
        Assert.Contains("not renamed", episodic.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

        // The fixture bundle says so of itself too: a synthetic corpus is declared a substitute
        // at the top level, where §4 requires it, so no reader can mistake it for the gold run.
        using var summary = Load(Bundle, "selftest-summary.json");
        var substitute = summary.RootElement.GetProperty("substitute");
        Assert.True(substitute.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("decision").GetProperty("recall_gate").ValueKind);
    }
}
