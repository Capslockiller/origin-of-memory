using System.Text.Json;
using Oom.Contracts;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Fact]
    public void Durum_DortDakikalikCapaVeTemizKayittaYesilVeSifirDoner()
    {
        using var f = new Fixture();
        SweepRoot(f);
        f.Daily($"<!-- session:fixture ts:{Fixture.Now.AddMinutes(-4):O} turns:0-1 source:claude kayit:v1 son:m-1 -->\n");
        var result = f.ProcessRun(["--vault", f.Root, "durum"]);
        Assert.Equal(0, result.Exit);
        Assert.StartsWith("[Durum] yeşil", result.Out.Trim());
        Assert.Equal("[Durum] yeşil · son kayıt 4 dk · kayıtsız 0 · kaydet hatası 0", result.Out.Trim());
    }

    [Fact]
    public void Durum_IkiSaatOncekiKayitsizSahipOturumuKirmiziUretirGuncelOturumsaYesilKalir()
    {
        using var f = new Fixture();
        var root = SweepRoot(f);
        f.Daily($"<!-- session:recorded ts:{Fixture.Now.AddMinutes(-4):O} turns:0-1 source:claude kayit:v1 son:r-1 -->\n");
        const string unrecordedId = "2ee86a05-1111-2222-3333-444444444444";
        var path = Path.Combine(root, unrecordedId + ".jsonl");
        File.WriteAllText(path, ForkTurn(unrecordedId, "u-1", "user", "sahip mesajı"), Fixture.Utf8);
        File.SetLastWriteTimeUtc(path, Fixture.Now.UtcDateTime.AddHours(-2));

        var red = f.ProcessRun(["--vault", f.Root, "durum"]);
        Assert.Equal(1, red.Exit);
        Assert.StartsWith("[Durum] KIRMIZI", red.Out.Trim());
        Assert.Contains("kayıtsız 1: 2ee86a05", red.Out.Trim());

        var greenWithPayload = f.ProcessRun(["--vault", f.Root, "durum"],
            JsonSerializer.Serialize(new { session_id = unrecordedId, transcript_path = path }));
        Assert.Equal(0, greenWithPayload.Exit);
        Assert.StartsWith("[Durum] yeşil", greenWithPayload.Out.Trim());
    }

    [Fact]
    public void Durum_EnYeniCapaOtuzSaatlikseKirmiziVeOtuzSaatGosterir()
    {
        using var f = new Fixture();
        SweepRoot(f);
        f.Daily($"<!-- session:fixture ts:{Fixture.Now.AddHours(-30):O} turns:0-1 source:claude kayit:v1 son:m-1 -->\n", -1);
        var result = f.ProcessRun(["--vault", f.Root, "durum"]);
        Assert.Equal(1, result.Exit);
        Assert.StartsWith("[Durum] KIRMIZI", result.Out.Trim());
        Assert.Contains("son kayıt 30 sa", result.Out.Trim());
    }

    [Fact]
    public void Durum_SonYirmiDortSaatteKaydetHatasiVarsaKirmiziVeSayiyiGosterir()
    {
        using var f = new Fixture();
        SweepRoot(f);
        f.Daily($"<!-- session:fixture ts:{Fixture.Now.AddMinutes(-4):O} turns:0-1 source:claude kayit:v1 son:m-1 -->\n");
        SeedLog(f, ErrorRow(Fixture.Now.AddHours(-2)) + ErrorRow(Fixture.Now.AddHours(-26)));
        var result = f.ProcessRun(["--vault", f.Root, "durum"]);
        Assert.Equal(1, result.Exit);
        Assert.Equal("[Durum] KIRMIZI · kaydet: son 24 saatte 1 hata", result.Out.Trim());
    }

    [Fact]
    public void Durum_JsonSayaclariVeUzunEkSatirlariDondurur()
    {
        using var f = new Fixture();
        SweepRoot(f);
        f.Daily($"<!-- session:fixture ts:{Fixture.Now.AddMinutes(-4):O} turns:0-1 source:claude kayit:v1 son:m-1 -->\n### [u m-0] 11:56 · [maske:tur]\ngizli\n");
        Directory.CreateDirectory(Path.Combine(f.Root, "daily"));
        File.WriteAllText(Path.Combine(f.Root, "daily", "2024-01-01.md"), "eski\n", Fixture.Utf8);
        SeedLog(f,
            JsonSerializer.Serialize(new { ts = Fixture.Now.AddHours(-1).ToString("O"), tetik = "mesaj", ms = 12, sonuc = "yazdi" }) + "\n" +
            JsonSerializer.Serialize(new { ts = Fixture.Now.AddHours(-2).ToString("O"), tetik = "mesaj", ms = 45, sonuc = "hata" }) + "\n");

        var jsonResult = f.ProcessRun(["--vault", f.Root, "durum", "--json"]);
        Assert.Equal(1, jsonResult.Exit);
        using var doc = JsonDocument.Parse(jsonResult.Out.Trim());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("yesil").GetBoolean());
        Assert.Equal(4, root.GetProperty("son_kayit_dk").GetInt32());
        Assert.Equal(0, root.GetProperty("kayitsiz").GetInt32());
        Assert.Equal(1, root.GetProperty("kaydet_hatasi").GetInt32());
        Assert.Equal(1, root.GetProperty("maske_tur").GetInt32());
        Assert.Equal(2, root.GetProperty("daily_dosya").GetInt32());
        Assert.Equal(1, root.GetProperty("eski_daily").GetInt32());

        var longResult = f.ProcessRun(["--vault", f.Root, "durum", "--uzun"]);
        Assert.Equal(1, longResult.Exit);
        Assert.Contains("maske: 1 tur", longResult.Out);
        Assert.Contains("daily: 2 dosya,", longResult.Out);
        Assert.Contains("12 aydan eski 1", longResult.Out);
        Assert.Contains("kaydet mesaj: n=2 p50=12 ms p95=45 ms max=45 ms hata=1", longResult.Out);
        Assert.Contains("kaydet hata: 1", longResult.Out);
    }

    [Fact]
    public void Durum_YavasHesapVeCokluKayitsizKimlikSiniriKorunur()
    {
        using var f = new Fixture();
        var root = SweepRoot(f);
        for (var i = 0; i < 5; i++)
        {
            var id = $"session{i}-aaaa-bbbb-cccc";
            var p = Path.Combine(root, id + ".jsonl");
            File.WriteAllText(p, ForkTurn(id, $"u-{i}", "user", "sahip"), Fixture.Utf8);
            File.SetLastWriteTimeUtc(p, Fixture.Now.UtcDateTime.AddHours(-2));
        }
        var settings = OomSettings.Load(f.Root);
        var evaluated = Durum.Evaluate(f.Root, settings, Fixture.Now, elapsedOverrideMs: 1800);
        Assert.True(evaluated.IsRed);
        Assert.True(evaluated.Line.Length <= 160);
        Assert.EndsWith(" (yavaş: 1,8 sn)", evaluated.Line);
        Assert.Contains("+2", evaluated.Line);
    }
}
