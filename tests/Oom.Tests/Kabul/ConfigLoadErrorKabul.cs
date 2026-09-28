using System.Text;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Process-boundary oracle for lane L1-cli-surface repair review finding "should #1"
/// (Program.cs ReportConfigurationWarnings never surfaced OomSettings.LoadError): a
/// malformed oom.json must not be a silent acceptance (SPEC F6-4: "sessiz kabul yok").
/// Every guarded command loads settings through ReportConfigurationWarnings, so
/// `context` stands in for the whole family here.
///
/// Written as a NEW file, per SPEC R3 / the lane brief: CliKabul.cs (the driver's own
/// oracle) is not edited.
/// </summary>
public sealed class ConfigLoadErrorKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;

    [Fact(DisplayName = "F6-4 (review) · bozuk oom.json → stderr'de 'ayar dosyası okunamadı' uyarısı, komut yine varsayılanlarla çalışır")]
    public void Context_WarnsOnStderrWhenOomJsonIsMalformed()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var settingsPath = Path.Combine(vault, ".oom", "oom.json");
        File.WriteAllText(settingsPath, "{ bad json", Utf8);

        var result = harness.Run(vault, ["context"], fakeNow: Today);

        Assert.True(result.ExitCode == 0 && result.Stderr.Contains("ayar dosyası okunamadı", StringComparison.Ordinal),
            "Review finding (should #1): a malformed oom.json falls back to defaults everywhere " +
            "(OomSettings.Load catches JsonException), but ReportConfigurationWarnings only ever printed " +
            "settings.UnknownKeys and never settings.LoadError, so every command but `doctor` swallowed the " +
            $"failure with nothing on stderr. Got exit {result.ExitCode}; stderr: '{result.Stderr}'; stdout: {result.Stdout}");
    }

    [Theory(DisplayName = "F6-4 (review) · kök nesne olmayan oom.json ('[1]', '\"x\"') → stderr'de 'ayar dosyası okunamadı', sessiz kabul yok")]
    [InlineData("[1]")]
    [InlineData("\"x\"")]
    public void Context_WarnsOnStderrWhenOomJsonRootIsNotAnObject(string json)
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var settingsPath = Path.Combine(vault, ".oom", "oom.json");
        File.WriteAllText(settingsPath, json, Utf8);

        var result = harness.Run(vault, ["context"], fakeNow: Today);

        Assert.True(result.ExitCode == 0 && result.Stderr.Contains("ayar dosyası okunamadı", StringComparison.Ordinal),
            "Review finding (should #3): an oom.json whose root parses as valid JSON but is not an object " +
            "(e.g. a bare array or string) used to fall back to every default with NO warning and no LoadError — " +
            "a malformed file that happens to be syntactically valid JSON was accepted completely silently, which " +
            $"is the F6-4 'sessiz kabul yok' defect. oom.json content: '{json}'. Got exit {result.ExitCode}; " +
            $"stderr: '{result.Stderr}'; stdout: {result.Stdout}");
    }
}
