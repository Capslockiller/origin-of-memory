using System.Text;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

/// <summary>
/// Review finding (should #3, L1-cli-surface repair): the nested unknown-key detection
/// Configuration.cs's Unknown()/AddUnknown() added for 7 sections — backend,
/// backend.claude, compile, context, retrieve, mcp, flush — plus extensions[] was
/// covered by exactly one nested case (sweep.everyHours, via CliKabul's F6-4 oracle).
/// The known-key lists there are hand-copied from the corresponding Read* function, so a
/// key added to a reader without a matching update here would warn falsely on every
/// hook run with nothing catching it. This file closes that gap: one theory case proves
/// every section still flags a genuinely unrecognized nested key, and one fact proves a
/// config using only recognized keys produces zero warnings (no false positives).
/// </summary>
public sealed class ConfigKeyScars
{
    private static readonly UTF8Encoding Utf8 = new(false);

    [Fact(DisplayName = "ConfigKeys-01 · Yalnız bilinen anahtarları kullanan oom.json sıfır uyarı üretir")]
    public void DefaultShapedSettings_ProduceNoUnknownKeys()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), OomSettings.DefaultJson(), Utf8);

            var settings = OomSettings.Load(vault);

            Assert.Empty(settings.UnknownKeys);
            Assert.Null(settings.LoadError);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Theory(DisplayName = "ConfigKeys-02 · Her bölümdeki bilinmeyen iç anahtar yakalanır")]
    [InlineData("backend", """{"backend": {"foo": 1}}""")]
    [InlineData("backend.claude", """{"backend": {"claude": {"foo": 1}}}""")]
    [InlineData("sweep", """{"sweep": {"foo": 1}}""")]
    [InlineData("compile", """{"compile": {"foo": 1}}""")]
    [InlineData("context", """{"context": {"foo": 1}}""")]
    [InlineData("retrieve", """{"retrieve": {"foo": 1}}""")]
    [InlineData("mcp", """{"mcp": {"foo": 1}}""")]
    [InlineData("flush", """{"flush": {"foo": 1}}""")]
    [InlineData("extensions[]", """{"extensions": [{"name": "x", "contextLine": "y", "foo": 1}]}""")]
    public void UnrecognizedNestedKey_IsFlagged(string section, string json)
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), json, Utf8);

            var settings = OomSettings.Load(vault);

            Assert.True(settings.UnknownKeys.Contains("foo", StringComparer.Ordinal),
                $"section '{section}': an unrecognized nested key 'foo' must appear in UnknownKeys " +
                $"(the reader's known-key list for this section has drifted from Unknown()/AddUnknown()). " +
                $"Got: [{string.Join(", ", settings.UnknownKeys)}]");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    // Review finding (should #1, L1-cli-surface repair): JsonElement.TryGetInt32 THROWS
    // InvalidOperationException for a non-Number element instead of returning false, so the
    // old Number() reader crashed every guarded command (including the SessionStart
    // `context` hook) on a wrong-typed number field. Text() and Flag() did not crash, but
    // silently accepted a wrong-typed value with no warning, which is the "sessiz kabul
    // yok" (F6-4) defect. One case per reader type proves neither failure mode remains.
    [Fact(DisplayName = "ConfigKeys-03 · Yanlış türde bir sayı alanı (metin) çökmez, geri düşer ve uyarı üretir")]
    public void WrongTypedNumber_DoesNotThrow_FallsBackWithInvalidValue()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            // sweep.sinceHours was removed (dead setting, L3-sweep: the age gate it fed is
            // gone), so this uses sweep.minTurns to exercise the same Number() failure mode.
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), """{"sweep": {"minTurns": "8"}}""", Utf8);

            var settings = OomSettings.Load(vault);

            Assert.Null(settings.LoadError);
            Assert.Equal(OomSettings.Defaults(vault).Sweep.MinTurns, settings.Sweep.MinTurns);
            Assert.Contains(settings.InvalidValues, item => item.Key == "sweep.minTurns" && item.Value == "8");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    // Review finding (should #4, L3-sweep repair, A2-08): sweep.sinceHours fed the age
    // gate SweepRun.ShouldProcess used to apply before a stamped candidate was even
    // looked at; that gate is gone (R-series: coverage tracking replaces it), so the key
    // must now warn as unrecognized rather than being silently read and ignored.
    [Fact(DisplayName = "ConfigKeys-05 · sweep.sinceHours artık ölü ayar, bilinmeyen anahtar olarak uyarır")]
    public void DeadSinceHoursSetting_IsFlaggedUnknown()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), """{"sweep": {"sinceHours": 8}}""", Utf8);

            var settings = OomSettings.Load(vault);

            Assert.Contains("sinceHours", settings.UnknownKeys);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "ConfigKeys-04 · Yanlış türde bir metin alanı (sayı) sessizce kabul edilmez")]
    public void WrongTypedText_IsNotSilentlyAccepted()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), """{"backend": {"claude": {"fast": 5}}}""", Utf8);

            var settings = OomSettings.Load(vault);

            Assert.Equal(OomSettings.Defaults(vault).Backend.Claude.Fast, settings.Backend.Claude.Fast);
            Assert.Contains(settings.InvalidValues, item => item.Key == "backend.claude.fast" && item.Value == "5");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "ConfigKeys-05 · Yanlış türde bir bool alanı (metin) sessizce kabul edilmez")]
    public void WrongTypedFlag_IsNotSilentlyAccepted()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), """{"mcp": {"enabled": "yes"}}""", Utf8);

            var settings = OomSettings.Load(vault);

            Assert.Equal(OomSettings.Defaults(vault).McpEnabled, settings.McpEnabled);
            Assert.Contains(settings.InvalidValues, item => item.Key == "mcp.enabled" && item.Value == "yes");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
    }
}
