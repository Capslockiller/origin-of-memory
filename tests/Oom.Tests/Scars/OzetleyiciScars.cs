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

    [Fact(DisplayName = "Y-019 · Yerel runner düşünmeyi kapatır ve üretim tavanı gönderir")]
    public void Y019_LocalRequestDisablesThinkingAndCapsGeneration()
    {
        var request = new Runner().BuildLocalRequest("özetle", "qwen3:8b", 2_048);
        Assert.False(request.Think);
        Assert.False(request.Stream);
        Assert.Equal(0, request.Temperature);
        Assert.Equal(2_048, request.MaxTokens);
    }

    [Fact(DisplayName = "Y-020 · Flush backend seçimi compile backend'ine sızmaz")]
    public void Y020_ComponentBackendSelectionIsSealed()
    {
        var runner = new Runner();
        var flush = runner.Run("x", ModelTier.Fast, ComponentKind.Flush, "summary");
        var compile = runner.Run("x", ModelTier.Smart, ComponentKind.Compile, "concepts");
        Assert.Equal("local", flush.Backend);
        Assert.Equal("claude", compile.Backend);
    }

    [Fact(DisplayName = "Y-021 · Yerel çağrı cold/warm ve üç süreyi ayrı kaydeder")]
    public void Y021_LocalTimingSeparatesLoadPromptAndGeneration()
    {
        var runner = new Runner();
        var cold = runner.ReadLocalTiming("{\"load_duration\":900,\"prompt_eval_duration\":100,\"eval_duration\":200}");
        Assert.Equal("cold", cold.Temperature);
        Assert.Equal(900, cold.LoadMs);
        Assert.Equal(100, cold.PromptEvaluationMs);
        Assert.Equal(200, cold.GenerationMs);
        var unknown = runner.ReadLocalTiming("{}");
        Assert.Equal("unknown", unknown.Temperature);
        Assert.Null(unknown.LoadMs);
    }

    [Fact(DisplayName = "Y-115 · Yerel çağrı bağlam penceresini native /api/chat'e taşır; ayar varsayılanı geçersiz kılar")]
    public void Y115_LocalCallCarriesContextWindowAndSettingWinsOverDefault()
    {
        var chain = new Dictionary<ComponentKind, IReadOnlyList<string>> { [ComponentKind.Flush] = ["local"] };
        var defaultHttp = new RecordingHttp("{\"message\":{\"content\":\"ok\"}}");
        // yazan: codex · gpt-5
        new Runner(null, defaultHttp, null, null, "http://127.0.0.1:11434/v1", true, chain).Run("özetle", ModelTier.Fast, ComponentKind.Flush, "summary");
        Assert.Equal("http://127.0.0.1:11434/api/chat", defaultHttp.Url);
        Assert.Contains("\"num_ctx\":8192", defaultHttp.Body);

        var configuredHttp = new RecordingHttp("{\"message\":{\"content\":\"ok\"}}");
        var profile = new RunnerProfile("vault", "vault/claude-config",
            new ClaudeSettings("claude-haiku-4-5-20251001", "claude-sonnet-5", "claude-config"),
            new LocalSettings("http://127.0.0.1:11434/v1", "qwen3:8b", "qwen3:14b", "nomic-embed-text", 24_576), chain);
        var result = new Runner(profile, http: configuredHttp).Run("özetle", ModelTier.Fast, ComponentKind.Flush, "summary");
        Assert.Null(result.Error);
        Assert.Contains("\"num_ctx\":24576", configuredHttp.Body);
    }

    private sealed class RecordingHttp(string response) : IHttp
    {
        public string? Url;
        public string? Body;
        public string Send(string method, string url, string body) { Url = url; Body = body; return response; }
    }
}
