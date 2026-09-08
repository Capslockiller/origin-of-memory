using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class KotaScars
{
    [Fact(DisplayName = "Y-052 · Kota yalnız canlı uçtan okunur, bayat olay yüzdesi basılmaz")]
    public void Y052_LiveQuotaOverridesSilentEventLog()
    {
        var cached = new QuotaWindow("5s", 64, ScarFixture.Now.AddHours(1), ScarFixture.Now.AddHours(-3), "cached");
        var result = new State().ReadQuota(new Dictionary<string, string> { ["codex"] = "{\"used_pct\":100}" }, [cached], ScarFixture.Now);
        Assert.Equal(100, Assert.Single(result.Windows).UsedPercent);
        Assert.DoesNotContain(result.Windows, x => x.Status == "cached");
    }

    [Fact(DisplayName = "Y-053 · Canlı okuma düşerse yüzde yoktur ve bant bilinmiyordur")]
    public void Y053_FailedLiveReadPrintsNoPercentage()
    {
        var result = new State().ReadQuota(new Dictionary<string, string> { ["claude"] = "timeout", ["codex"] = "connection refused" }, [], ScarFixture.Now);
        Assert.All(result.Windows, x => Assert.Null(x.UsedPercent));
        Assert.Equal("bilinmiyor (kaynak yok)", result.Band);
    }

    [Fact(DisplayName = "Y-054 · Donmuş gözlem yeni örnek üretmez ve serbest sayılmaz")]
    public void Y054_FrozenObservationIsUnknownNotFree()
    {
        var frozen = new QuotaWindow("weekly", 99, ScarFixture.Now.AddHours(-1), ScarFixture.Now.AddHours(-36), "cached");
        var result = new State().ReadQuota(new Dictionary<string, string>(), [frozen], ScarFixture.Now);
        Assert.Equal("bilinmiyor", result.Band);
        Assert.DoesNotContain(result.Windows, x => x.Status == "serbest");
    }

    [Fact(DisplayName = "Y-055 · Süresi dolmuş OAuth yüzde değil bilinmiyor ve login çözümü üretir")]
    public void Y055_ExpiredOAuthProvidesLoginResolution()
    {
        var result = new State().ReadQuota(new Dictionary<string, string> { ["claude"] = "401 OAuth access token has expired" }, [], ScarFixture.Now);
        var window = Assert.Single(result.Windows);
        Assert.Null(window.UsedPercent);
        Assert.Equal("bilinmiyor", window.Status);
        Assert.Contains("/login", window.Resolution);
    }

    [Fact(DisplayName = "Y-056 · Kota şema kayması uyarı ve bildirim üretir")]
    public void Y056_QuotaSchemaDriftIsVisible()
    {
        var result = new State().ReadQuota(new Dictionary<string, string> { ["codex"] = "{\"renamed_field\":42}" }, [], ScarFixture.Now);
        Assert.Contains(result.Warnings, x => x.Code == "kota-sozlesme" && x.Level == HealthLevel.Warning);
        var notification = new Notify().Send("kota-sozlesme", "codex", "Kota sözleşmesi değişti — oom doctor", ScarFixture.Now);
        Assert.True(notification.ToastSent || notification.ContextQueued);
    }

    [Fact(DisplayName = "Y-057 · Kota okuyucu durum değiştiren consume çağrısı yapmaz")]
    public void Y057_QuotaReaderNeverConsumesCredit()
    {
        var request = new State().BuildQuotaRequest("codex");
        Assert.DoesNotContain("consume", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mutation", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read", request, StringComparison.OrdinalIgnoreCase);
    }
}
