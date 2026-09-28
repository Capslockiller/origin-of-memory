using System.Text;
using System.Text.Json;

namespace Oom.Tests.Kabul;

/// <summary>
/// O21 acceptance oracle for <see cref="KabulHarness"/> itself (NOT a SPEC-3.1.0.md
/// feature): a doctor-running Kabul child must never read this developer's REAL
/// ~/.claude/settings.json or kit installation. Before this fix, <see cref="KabulHarness.Run"/>
/// left OOM_USERPROFILE unset on every child process, so <c>Program.Doctor.UserProfileRoot()</c>
/// (and <c>Program.Kit.HomeRoot()</c>) fell back to
/// <c>Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)</c> — this machine's
/// real profile — and a green fixture's exit code silently depended on whatever hooks
/// happened to be registered there.
///
/// Assertion 1 below is deliberately independent of what this developer's machine actually
/// has installed: a fresh <see cref="KabulHarness"/>'s default OOM_USERPROFILE is an empty
/// scratch directory with no <c>.claude</c> subdirectory at all, so <c>doctor --json</c> MUST
/// report "no settings.json" / "hook missing" for all four required hooks, on every machine,
/// every run — never "measured on this developer's box, so far so good".
/// </summary>
public sealed class KabulHarnessIsolationKabul
{
    private static readonly string[] RequiredHooks = ["SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact"];
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact(DisplayName = "O21 #1 · Varsayılan koşum: boş senaryo profiliyle 'doctor --json' settings-missing ve dört missing-hook satırı verir — geliştirici makinesinden bağımsız")]
    public void DefaultRun_SeesIsolatedScratchProfile_ReportsMissingSettingsAndHooks_RegardlessOfRealMachine()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);

        // No --userProfile override: this is the plain, everyday call every other Kabul
        // test already makes. If OOM_USERPROFILE were still unset here, this assertion
        // would only pass or fail depending on the real ~/.claude/settings.json this
        // developer's machine happens to have — exactly the defect O21 reports.
        var result = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);

        using var document = JsonDocument.Parse(result.Stdout);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();

        var userSettingsMissing = Assert.Single(items, item =>
            Text(item, "component") == "hooks" && Text(item, "code") == "settings-missing"
            && Text(item, "key").Contains("kullanıcı", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("info", Text(userSettingsMissing, "level"));

        foreach (var hook in RequiredHooks)
        {
            var missing = Assert.Single(items, item =>
                Text(item, "component") == "hooks" && Text(item, "code") == "missing-hook" && Text(item, "key") == hook);
            Assert.NotEqual("info", Text(missing, "level"));
        }
    }

    [Fact(DisplayName = "O21 #2 · userProfile parametresiyle geçersiz kılınabilir: sahte profildeki hook'lar tanınır, varsayılan boş senaryo değil")]
    public void ExplicitUserProfileOverride_IsHonoured_NotTheHarnessDefaultScratch()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);
        var customProfile = harness.NewScratchDirectory("custom-userprofile");
        Directory.CreateDirectory(Path.Combine(customProfile, ".claude"));
        File.WriteAllText(Path.Combine(customProfile, ".claude", "settings.json"), HookJson(harness.ExePath), Utf8);

        var result = harness.Run(vault, ["doctor", "--json"], fakeNow: Today, userProfile: customProfile);

        using var document = JsonDocument.Parse(result.Stdout);
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();

        // Proves the override actually reached the child (not just plumbing that is never
        // read): the default, empty scratch profile would report all four hooks missing;
        // seeing none missing means the CUSTOM profile — with all four registered — was
        // the one actually consulted.
        Assert.DoesNotContain(items, item => Text(item, "component") == "hooks" && Text(item, "code") == "missing-hook");
        Assert.DoesNotContain(items, item => Text(item, "component") == "hooks" && Text(item, "code") == "settings-missing"
            && Text(item, "key").Contains("kullanıcı", StringComparison.OrdinalIgnoreCase));
    }

    private static string HookJson(string executable)
    {
        var hooks = new Dictionary<string, object>();
        foreach (var hook in RequiredHooks)
            hooks[hook] = new[] { new { hooks = new[] { new { type = "command", command = $"\"{executable}\" hook --event {hook}" } } } };
        return JsonSerializer.Serialize(new { hooks });
    }

    private static string Text(JsonElement item, string property) => item.GetProperty(property).GetString() ?? string.Empty;

    private static string BuildMinimalVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }
}
