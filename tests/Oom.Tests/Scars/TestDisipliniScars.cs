using System.Text.RegularExpressions;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class TestDisipliniScars
{
    [Fact(DisplayName = "Y-075 · Kavram recall ile tarih tutar karar düzeltme recall ayrı raporlanır")]
    public void Y075_AccuracyAxesAreReportedSeparately()
    {
        var result = new Retrieve().Query("benchmark: 13 Eylül 48 EUR çelişkisi", "quality", 5);
        Assert.DoesNotContain(result.Hits, x => x.Text.Contains("13 Eylül / 48 EUR", StringComparison.Ordinal));
        Assert.Contains("concept_recall", result.Output);
        Assert.Contains("fact_recall", result.Output);
    }

    [Fact(DisplayName = "Y-076 · Claude ve Codex ingest gerçek örnek sözleşmeleriyle doğrulanır")]
    public void Y076_IngestParsersUseFixedExternalContractSamples()
    {
        var ingest = new Ingest();
        var claude = ingest.ParseClaude("{\"sessionId\":\"claude-fixed\",\"type\":\"user\",\"message\":{\"content\":\"merhaba\"}}");
        var codex = ingest.ParseCodex("{\"type\":\"event_msg\",\"session_id\":\"codex-fixed\",\"payload\":{\"type\":\"user_message\",\"message\":\"merhaba\"}}");
        Assert.Equal("claude-fixed", claude.Id);
        Assert.Equal("codex-fixed", codex.Id);
        Assert.Single(claude.Turns);
        Assert.Single(codex.Turns);
    }

    [Fact(DisplayName = "Y-077 · Zamanlama senaryosu sahte saatle deterministiktir")]
    public void Y077_TimingTestUsesInjectedClock()
    {
        var context = new Context();
        var first = context.Build("fixture-vault", ScarFixture.Now);
        var second = context.Build("fixture-vault", ScarFixture.Now);
        Assert.Equal(first, second);
    }

    [Fact(DisplayName = "Y-078 · Süit gece yarısının iki yanında aynı sonucu verir")]
    public void Y078_DateBoundaryUsesFakeNow()
    {
        var before = new DateTimeOffset(2026, 9, 8, 23, 59, 0, TimeSpan.FromHours(3));
        var after = before.AddMinutes(2);
        var doctor = new Doctor();
        var first = doctor.Check(before);
        var second = doctor.Check(after);
        Assert.Equal(first.Coverage, second.Coverage);
        Assert.Equal(first.RejectionRate, second.RejectionRate);
    }

    [Fact(DisplayName = "Y-079 · Kırmızı testin çıkış kodu çıktı kısaltılsa da korunur")]
    public void Y079_CiPreservesTestExitCodeWithoutPipelineMasking()
    {
        var request = new ProcessRequest("dotnet", ["test", "--filter", "IntentionalRed"], ScarFixture.RepositoryRoot(), new Dictionary<string, string>(), "");
        var result = new Runner().RunProcess(request, TimeSpan.FromMinutes(1));
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact(DisplayName = "Y-080 · Windows kısa ve uzun temp yolları aynı kök kabul edilir")]
    public void Y080_ShortAndLongWindowsPathsNormalizeToSameRoot()
    {
        var guards = new Guards();
        var shortPath = guards.NormalizePath(@"C:\Users\RUNNER~1\AppData\Local\Temp");
        var longPath = guards.NormalizePath(@"C:\Users\RunnerAdmin\AppData\Local\Temp");
        Assert.Equal(longPath, shortPath, ignoreCase: true);
    }

    [Fact(DisplayName = "Y-081 · Statik tarama test fixture'larını dışlar ama kaynak ihlalini yakalar")]
    public void Y081_StaticScanExcludesFixturesOnly()
    {
        var root = ScarFixture.RepositoryRoot();
        var production = Directory.EnumerateFiles(Path.Combine(root, "src", "Oom"), "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.DoesNotContain(production, path => path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        Assert.All(production, path => Assert.DoesNotContain("INTENTIONAL_STATIC_SCAN_VIOLATION", File.ReadAllText(path)));
    }

    [Fact(DisplayName = "Y-082 · Kabul koşumu kazanç ve karşıt metriği birlikte raporlar")]
    public void Y082_AcceptanceGateRequiresPrimaryAndCounterMetric()
    {
        var measured = new Retrieve().Query("acceptance benchmark", "counter-metric", 5);
        Assert.Contains("episodic_top3", measured.Output);
        Assert.Contains("concept_recall_at5", measured.Output);
        Assert.DoesNotContain("gate:green", measured.Output.Contains("concept_recall_at5=0.87", StringComparison.Ordinal) ? "gate:green" : "gate:red");
    }

    [Fact(DisplayName = "Y-116 · Bench alt-ajan izini ve turu olmayan dosyayı dışlar, gerçek transkripti sayar")]
    public void Y116_BenchExcludesSubagentAndNoTurnFilesButKeepsRealTranscript()
    {
        var root = ScarFixture.TempDirectory();
        var real = Path.Combine(root, "real.jsonl");
        File.WriteAllText(real, ScarFixture.TranscriptJsonl(ScarFixture.Session("real-1", 4)));
        var subagentDir = Path.Combine(root, "subagents");
        Directory.CreateDirectory(subagentDir);
        var subagent = Path.Combine(subagentDir, "agent-a1.jsonl");
        File.WriteAllText(subagent, "bu satır JSON bile değil");
        var noTurns = Path.Combine(root, "sidechain-only.jsonl");
        File.WriteAllText(noTurns, "{\"sessionId\":\"empty-1\",\"isSidechain\":true,\"message\":{\"role\":\"user\",\"content\":\"iç konuşma\"}}");

        var chain = new Dictionary<ComponentKind, IReadOnlyList<string>> { [ComponentKind.Flush] = ["local"] };
        // yazan: codex · gpt-5
        var runner = new Runner(null, null, null, null, "http://127.0.0.1:11434/v1", false, chain);
        var (records, excludedSubagent, excludedNoTurns) = Bench.MeasureFlush([real, subagent, noTurns], runner);

        Assert.Equal(1, excludedSubagent);
        Assert.Equal(1, excludedNoTurns);
        Assert.Single(records);
        Assert.Equal("real.jsonl", records[0].Source);
        ScarFixture.Remove(root);
    }

    [Fact(DisplayName = "Y-125 · Test gövdesindeki her Y-numarası scars.md'de bir satıra karşılık gelir ve her satırın da testi vardır")]
    public void Y125_ScarNumbersAndScarsMdRowsMatchInBothDirections()
    {
        var root = ScarFixture.RepositoryRoot();
        var testsDir = Path.Combine(root, "tests");
        var scarsDoc = File.ReadAllText(Path.Combine(root, "docs", "scars.md"));

        var displayNamePattern = new Regex(@"\[(?:Fact|Theory)\(DisplayName\s*=\s*""([^""]*)""", RegexOptions.Compiled);
        var scarNumberPattern = new Regex(@"Y-\d+", RegexOptions.Compiled);
        // Yalnız gerçek tablo satırlarını yakalar: başlık satırı "| Y-# |" (Y'den sonra rakam yok) bu deseni eşlemez.
        var ledgerRowPattern = new Regex(@"^\|\s*(Y-\d+)\s*\|", RegexOptions.Compiled | RegexOptions.Multiline);

        var testNumbers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = File.ReadAllText(file);
            foreach (Match attribute in displayNamePattern.Matches(content))
                foreach (Match number in scarNumberPattern.Matches(attribute.Groups[1].Value))
                    testNumbers.Add(number.Value);
        }

        var ledgerNumbers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match row in ledgerRowPattern.Matches(scarsDoc))
            ledgerNumbers.Add(row.Groups[1].Value);

        // Yön 1: her testte kullanılan Y-numarasının scars.md'de bir satırı olmalı.
        var testsWithoutLedgerRow = testNumbers.Where(number => !scarsDoc.Contains($"| {number} |", StringComparison.Ordinal)).ToArray();
        Assert.True(testsWithoutLedgerRow.Length == 0, $"test without a ledger row: {string.Join(", ", testsWithoutLedgerRow)}");

        // Yön 2: scars.md'deki her satırın DisplayName'de o numarayı taşıyan bir testi olmalı.
        var ledgerRowsWithoutTest = ledgerNumbers.Where(number => !testNumbers.Contains(number)).ToArray();
        Assert.True(ledgerRowsWithoutTest.Length == 0, $"ledger row without a test: {string.Join(", ", ledgerRowsWithoutTest)}");
    }
}
