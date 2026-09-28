using System.Text.Json;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Smoke tests for the Kabul harness mechanism itself (process launch, stdio capture,
/// exit code, fixture shape) — NOT acceptance tests for any SPEC-3.1.0.md feature.
/// These must stay green against whatever exe the default resolution finds (today: this
/// lane's own unmodified build, i.e. the same code as the OLD exe), because they prove
/// the harness works, not that a fix landed.
///
/// The F1 behaviour demonstration (oom context --json against this fixture, run against
/// the OLD exe, asserting chars ≤ 8000) is intentionally NOT a compiled test here — it
/// is run ad hoc and its measured actual value is reported verbatim. A real F1 acceptance
/// test belongs to the lane that implements F1, written against the new budget-cap
/// behaviour; embedding a permanently-failing or Skip-flagged assertion in this file
/// would violate the "no Skip" rule and would not be that lane's own proof.
/// </summary>
public sealed class HarnessSmokeTests
{
    [Fact]
    public void HelpStyleCall_PrintsUsageAndCapturesOutput()
    {
        using var harness = new KabulHarness();

        // "--help" never touches the vault; the path below is a placeholder, never read.
        // 3.1.0 (F6-1): help exits 0. The wave-0 version of this smoke test pinned the old
        // exit 1 and was amended by the driver when L1-cli-surface landed.
        var result = harness.Run(Path.Combine(harness.Root, "unused-vault"), ["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("oom [--vault", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("context [--json]", result.Stdout, StringComparison.Ordinal);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(10), $"help-style call took {result.Elapsed}");
    }

    [Fact]
    public void ContextJson_AgainstRealSizeFixture_RunsEndToEndThroughTheHarness()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);

        var result = harness.Run(vault, ["context", "--json"], fakeNow: KabulVaultBuilder.DefaultToday);

        Assert.Equal(0, result.ExitCode);

        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("chars", out var charsElement), "response has no 'chars' field");
        Assert.True(root.TryGetProperty("text", out var textElement), "response has no 'text' field");
        Assert.True(root.TryGetProperty("sections", out _), "response has no 'sections' field");
        Assert.True(charsElement.GetInt32() > 0);
        Assert.Equal(charsElement.GetInt32(), textElement.GetString()!.Length);
    }

    [Fact]
    public void Fixture_HasTheShapeDescribedInSpec()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var companion = Path.Combine(vault, KabulVaultBuilder.CompanionDir);

        var lastSession = File.ReadAllText(Path.Combine(companion, "Last-Session.md"));
        Assert.Contains("## Session:", lastSession, StringComparison.Ordinal);

        var threads = File.ReadAllText(Path.Combine(companion, "Threads.md"));
        Assert.Contains("## Active Threads", threads, StringComparison.Ordinal);
        Assert.Contains("## Closed Threads", threads, StringComparison.Ordinal);
        Assert.Contains("**Durum:**", threads, StringComparison.Ordinal);
        Assert.Equal(11, CountOccurrences(threads, "**Status:**") + CountOccurrences(threads, "**Durum:**") - 2 /* two closed-thread Status lines are not active */);

        var journal = File.ReadAllText(Path.Combine(companion, "Journal.md"));
        Assert.Contains("## 2026-09-14/15 —", journal, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(journal, "aynı güne ait girdi"));

        Assert.True(File.Exists(Path.Combine(vault, "daily", $"{KabulVaultBuilder.DefaultToday:yyyy-MM-dd}.md")));
        Assert.True(File.Exists(Path.Combine(vault, "knowledge", "index.md")));
        Assert.True(Directory.GetFiles(Path.Combine(vault, "knowledge", "concepts"), "*.md").Length >= 3);

        var settings = File.ReadAllText(Path.Combine(vault, ".oom", "oom.json"));
        Assert.Contains("\"capChars\": 16000", settings, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
