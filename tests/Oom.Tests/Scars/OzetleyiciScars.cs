using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class OzetleyiciScars
{
    [Fact(DisplayName = "Y-016 · Ölçülmemiş ve damgasız fallback normal güvenle derlenmez")]
    public void Y016_UnmeasuredFallbackCannotEnterNormalConfidence()
    {
        var metrics = new BenchmarkResult(0.7, 0.8, 0.88, 0.145, new Dictionary<string, double> { ["windows"] = 6 });
        var result = new Compile().Run("2026-09-08.md", "fallback_backend: local\nconfidence: normal", ScarFixture.ValidSummary());
        Assert.Equal(6, metrics.Metrics["windows"]);
        Assert.DoesNotContain(result.WrittenPaths, path => path.StartsWith("knowledge/concepts/", StringComparison.Ordinal));
        Assert.Equal("low-confidence", result.Status);
    }

    [Fact(DisplayName = "Y-017 · Biçim gürültüsü normalleşir, eksik bölüm ham çıktıyla reddedilir")]
    public void Y017_SummaryValidatorNormalizesNoiseAndPersistsRejection()
    {
        var flush = new Flush();
        var noisy = "Özet aşağıdadır.\n" + ScarFixture.ValidSummary().Replace("## ", "### **").Replace("\n", "**\n");
        var accepted = flush.ValidateSummary(noisy, "noise");
        Assert.True(accepted.Accepted);
        Assert.Equal(5, accepted.Normalized.Split("## ", StringSplitOptions.None).Length - 1);
        var rejected = flush.ValidateSummary("## Bağlam\nEksik", "missing");
        Assert.False(rejected.Accepted);
        Assert.Contains("red", rejected.RejectionPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Y-018 · Model takma adı reddedilir, gerçek model modelUsage'dan kaydedilir")]
    public void Y018_ModelIdMustBeExactAndRecordedFromResponse()
    {
        var runner = new Runner();
        Assert.Throws<ArgumentException>(() => runner.BuildClaudeRequest("x", "haiku", "vault", "isolated"));
        var result = runner.Run("x", ModelTier.Fast, ComponentKind.Flush, "summary");
        Assert.Equal("claude-haiku-4-5-20251001", result.Model);
    }

}
