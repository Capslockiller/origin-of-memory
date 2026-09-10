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
        // The old shape of this Fact only compared two records it had built itself and never
        // called the hook path, so it asserted nothing about the duplicate start. It now drives
        // Context.Start, which is where the (session id, event, identity) key lives.
        var vault = ScarFixture.CompanionVault("# Düzeltmeler\n" + new string('x', 4_000) + "\n");
        try
        {
            var context = new Context();
            var main = new HookStart("main-" + Guid.NewGuid().ToString("N")[..8], 100, ScarFixture.Now, false, string.Empty);
            var helper = new HookStart("helper-" + Guid.NewGuid().ToString("N")[..8], 101, ScarFixture.Now, true, string.Empty);
            var full = context.Start(main, vault);
            var repeated = context.Start(main, vault);           // same session, same event, same second
            var minimal = context.Start(helper, vault);          // Desktop's helper start
            Assert.NotEqual(main.SessionId, helper.SessionId);
            Assert.Contains("[Hafıza — Düzeltmeler]", full.Text);
            Assert.True(repeated.Text.Length < full.Text.Length);
            Assert.True(minimal.Text.Length < full.Text.Length);
            Assert.DoesNotContain("[Hafıza — Düzeltmeler]", repeated.Text);
            // A different second is a different identity and gets the full block again.
            var later = new HookStart(main.SessionId, 100, ScarFixture.Now.AddSeconds(1), false, string.Empty);
            Assert.Equal(full.Text.Length, context.Start(later, vault).Text.Length);
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
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
        var vault = ScarFixture.CompanionVault("# Düzeltmeler\n- ilk hâli\n");
        try
        {
            var context = new Context();
            var path = Path.Combine(vault, ScarFixture.CompanionDir, "Duzeltmeler.md");
            Assert.DoesNotContain(context.AuditCompanion(vault), x => x.Code == "companion-content-stale");

            // Touched — a fresh mtime, byte-identical content. mtime says "current", content does not.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            Assert.Contains(context.AuditCompanion(vault), x => x.Code == "companion-content-stale" && x.Key == "Duzeltmeler.md");

            // Really edited: same fresh mtime, different bytes, and the finding goes away.
            File.WriteAllText(path, "# Düzeltmeler\n- ikinci hâli\n");
            Assert.DoesNotContain(context.AuditCompanion(vault), x => x.Code == "companion-content-stale");
        }
        finally
        {
            ScarFixture.Remove(vault);
        }
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

    [Fact(DisplayName = "K6 · Companion dosyası var ama başlığı yoksa context tek satır uyarır")]
    public void ContextWarnsWhenCompanionFileCarriesNoMarker()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            var companion = Path.Combine(vault, new ContextOptions().CompanionDir);
            Directory.CreateDirectory(companion);
            File.WriteAllText(Path.Combine(companion, "Last-Session.md"), "başlıksız gövde\nikinci satır\n");
            var warnings = new Context().CompanionWarnings(vault);
            var warning = Assert.Single(warnings);
            Assert.Contains("Son Oturum", warning);
            Assert.Contains("## Session:", warning);
        }
        finally { ScarFixture.Remove(vault); }
    }
}
