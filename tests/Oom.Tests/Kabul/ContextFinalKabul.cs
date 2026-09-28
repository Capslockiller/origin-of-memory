using System.Text;
using System.Text.Json;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

public sealed class ContextFinalKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact(DisplayName = "L6d O9 · Bozuk state.db hem SessionStart hem elle context için vault bağlamını engellemez")]
    public void CorruptStateDatabaseFallsBackToVaultContext()
    {
        using var harness = new KabulHarness();
        var vault = WriteVault(harness, "vault-corrupt-state");
        CorruptStateDatabase(harness, vault);

        var hookPayload = JsonSerializer.Serialize(new
        {
            session_id = "l6d-o9",
            transcript_path = string.Empty,
            cwd = vault,
            hook_event_name = "SessionStart",
            source = "startup"
        });
        var hook = harness.Run(vault, ["context"], standardInput: hookPayload, fakeNow: Today);

        Assert.True(hook.ExitCode == 0,
            $"SessionStart exit={hook.ExitCode}; stdout={hook.Stdout.Length} bayt; stderr={hook.Stderr}");
        var additionalContext = JsonDocument.Parse(hook.Stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        AssertVaultFallback(additionalContext);

        var manual = harness.Run(vault, ["context"], fakeNow: Today);
        Assert.True(manual.ExitCode == 0,
            $"manual exit={manual.ExitCode}; stdout={manual.Stdout.Length} bayt; stderr={manual.Stderr}");
        AssertVaultFallback(manual.Stdout);
    }

    [Fact(DisplayName = "L6d O16 · Yazılmakta olan daily SessionStart bağlamının diğer bölümlerini düşürmez")]
    public void SharedDailyDoesNotAbortContext()
    {
        using var harness = new KabulHarness();
        var vault = WriteVault(harness, "vault-shared-daily");
        var daily = WriteDaily(vault, new string('L', 1_200));

        using var writer = new FileStream(daily, FileMode.Open, FileAccess.Write, FileShare.Read);
        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);

        Assert.True(run.ExitCode == 0,
            $"exit={run.ExitCode}; stdout={run.Stdout.Length} bayt; stderr={run.Stderr}");
        Assert.False(string.IsNullOrWhiteSpace(run.Stdout), "context stdout boş kaldı");
        var text = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("[Bildirim]", text, StringComparison.Ordinal);
        Assert.Contains("[Hafıza — Aktif Threadler]", text, StringComparison.Ordinal);
        Assert.Contains("CANARY-THREAD", text, StringComparison.Ordinal);
        Assert.Contains("[Hafıza — Kurallar]", text, StringComparison.Ordinal);
        Assert.Contains("CANARY-RULE", text, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "L6d O19/R27 · Her koşulda bağlam ≤ 8000 karakter ve ≤ 8500 UTF-8 bayt")]
    [InlineData(7_300, 1_200)]
    [InlineData(10_001, 0)]
    public void HardBudgetHoldsWhenFixedSectionsConsumeTheRoom(int ruleChars, int dailyChars)
    {
        using var harness = new KabulHarness();
        var vault = WriteVault(harness, "vault-hard-budget-" + ruleChars, new string('K', ruleChars));
        if (dailyChars > 0)
            WriteDaily(vault, new string('G', dailyChars));

        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);

        Assert.Equal(0, run.ExitCode);
        var root = JsonDocument.Parse(run.Stdout).RootElement;
        var text = root.GetProperty("text").GetString()!;
        Assert.True(text.Length <= 8_000,
            $"context {text.Length} karakter; sınır 8000; stderr={run.Stderr}");
        Assert.True(Utf8.GetByteCount(text) <= 8_500,
            $"context {Utf8.GetByteCount(text)} UTF-8 bayt; sınır 8500; stderr={run.Stderr}");

        if (ruleChars > 8_000)
        {
            Assert.Contains("… [kısaltıldı — tamamı: 🔮 850-Companion/Kurallar.md]", text, StringComparison.Ordinal);
            Assert.Contains("[Bildirim]", text, StringComparison.Ordinal);
            Assert.Contains("Kurallar.md", text.Split("\n[Zaman]", StringSplitOptions.None)[0], StringComparison.Ordinal);
        }
    }

    private static string WriteVault(KabulHarness harness, string name, string? rules = null)
    {
        var vault = harness.NewScratchDirectory(name);
        var companion = Path.Combine(vault, new ContextOptions().CompanionDir);
        Directory.CreateDirectory(companion);
        File.WriteAllText(Path.Combine(companion, "Threads.md"),
            "# Threads\n\n## Active Threads\n\n### Active\n**Status:** CANARY-THREAD\n\n## Closed\n", Utf8);
        File.WriteAllText(Path.Combine(companion, "Kurallar.md"),
            rules ?? "# Kurallar\n\n- CANARY-RULE\n", Utf8);
        return vault;
    }

    private static string WriteDaily(string vault, string body)
    {
        var directory = Path.Combine(vault, "daily");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Today:yyyy-MM-dd}.md");
        File.WriteAllText(path, $"# Günlük Log: {Today:yyyy-MM-dd}\n\n### Oturum (09:00)\n{body}\n", Utf8);
        return path;
    }

    private static void CorruptStateDatabase(KabulHarness harness, string vault)
    {
        var canonical = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);
        var stateRoot = Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical));
        Directory.CreateDirectory(stateRoot);
        File.WriteAllBytes(Path.Combine(stateRoot, VaultIdentity.DatabaseName), Enumerable.Repeat((byte)0xA5, 4_096).ToArray());
    }

    private static void AssertVaultFallback(string text)
    {
        Assert.Contains("CANARY-THREAD", text, StringComparison.Ordinal);
        Assert.Contains("[Hafıza — Kurallar]", text, StringComparison.Ordinal);
        Assert.Contains("CANARY-RULE", text, StringComparison.Ordinal);
        Assert.Contains("[Bildirim]", text, StringComparison.Ordinal);
        Assert.Contains("state.db", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oom doctor", text, StringComparison.Ordinal);
    }
}
