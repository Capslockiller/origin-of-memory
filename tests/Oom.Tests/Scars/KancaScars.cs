using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class KancaScars
{
    [Fact(DisplayName = "Y-045 · Açılış bağlamı güncel bölümleri korur ve on bin karakteri aşmaz")]
    public void Y045_ContextKeepsCurrentDataWithinCap()
    {
        var context = new Context().Build("fixture-vault", ScarFixture.Now);
        Assert.InRange(context.Text.Length, 1, 10_000);
        Assert.Contains("[Hafıza — Düzeltmeler]", context.Text);
        Assert.Contains("[Hafıza — Aktif Threadler]", context.Text);
        Assert.True(context.Elapsed <= TimeSpan.FromMilliseconds(500));
        Assert.Equal(context.Sections.Count, context.Sections.Distinct().Count());
    }

    [Fact(DisplayName = "Y-046 · Aynı saniyedeki yardımcı başlangıç asgari bağlam alır")]
    public void Y046_HelpersAreDedupedBySessionAndEventIdentity()
    {
        var first = new HookStart("main", 100, ScarFixture.Now, false, new string('a', 4_000));
        var second = new HookStart("helper", 101, ScarFixture.Now, true, "[Bildirim] yardımcı oturum");
        var result = new Context().Build("fixture-vault", ScarFixture.Now);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.True(second.Context.Length < first.Context.Length);
        Assert.Contains("main", result.Text);
        Assert.Contains("helper", result.Text);
    }

    [Fact(DisplayName = "Y-047 · Yedi gün kancasız çalışmada gecikmiş kapsanmayan oturum kalmaz")]
    public void Y047_SystemWorksWhenAllHooksAreSilent()
    {
        var sessions = Enumerable.Range(0, 21).Select(i => ScarFixture.Session($"day-{i / 3}-session-{i}", 3, lastTurnAt: ScarFixture.Now.AddDays(-(i / 3)))).ToArray();
        var result = new Sweep().Run(sessions, new SweepOptions(SinceHours: 24 * 8));
        Assert.Equal(21, result.Covered);
        Assert.Empty(result.UncoveredIds);
    }

    [Fact(DisplayName = "Y-048 · Eşzamanlı oturumların prompt sayaçları bağımsızdır")]
    public void Y048_HookCountersArePerSession()
    {
        var states = new[] { new HookState("a", 3, ScarFixture.Now), new HookState("b", 7, ScarFixture.Now) };
        var audited = new Context().AuditCompanion("fixture-vault");
        Assert.Equal(3, states.Single(x => x.SessionId == "a").PromptCount);
        Assert.Equal(7, states.Single(x => x.SessionId == "b").PromptCount);
        Assert.DoesNotContain(audited, x => x.Code == "shared-hook-counter");
    }

    [Fact(DisplayName = "Y-049 · Kanca hata yolu kalıcı doctor bulgusu bırakır")]
    public void Y049_HookFailureIsPersisted()
    {
        var request = new ProcessRequest("missing-interpreter", [], Path.GetTempPath(), new Dictionary<string, string>(), "{}");
        var process = new Runner().RunProcess(request, TimeSpan.FromSeconds(1));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(new Doctor().Check(ScarFixture.Now).Items, x => x.Code == "hook-failed");
    }

    [Fact(DisplayName = "Y-050 · El katmanı denetimi mtime yerine içeriğe bakar")]
    public void Y050_CompanionAuditUsesContentNotMtime()
    {
        var items = new Context().AuditCompanion("fixture-touched-but-stale");
        Assert.Contains(items, x => x.Code == "companion-content-stale");
    }

    [Fact(DisplayName = "Y-051 · Açılışta enjekte edilen bölüm listesi belgeyle birebir eşleşir")]
    public void Y051_ContextSectionsMatchDocumentedList()
    {
        var expected = new[] { "Bildirim", "Son Oturum", "Aktif Threadler", "Kurallar", "Düzeltmeler", "Son Journal", "Durum", "Bilgi Tabanı — İndeks", "Bugünün Logu" };
        var result = new Context().Build("fixture-vault", ScarFixture.Now);
        Assert.Equal(expected, result.Sections);
    }

    [Fact(DisplayName = "Y-090 · Çift hook kaydı kırmızıdır ve ikinci flush locked olur")]
    public void Y090_DuplicateHookRegistrationAndFlushAreRejected()
    {
        var findings = new Doctor().ValidateHooks("{same-hook}", "{same-hook}");
        Assert.Contains(findings, x => x.Level == HealthLevel.Error && x.Code == "duplicate-hook");
        var result = new Flush().FlushSession("duplicate", "duplicate.jsonl", FlushReason.SessionEnd);
        Assert.Equal(FlushOutcome.Locked, result.Outcome);
    }
}
