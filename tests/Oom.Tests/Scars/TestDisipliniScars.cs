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
