using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Fact]
    public void Kaydet_AcilisYeniKilitliTranskriptiHataVeBildirimOlmadanAtlar()
    {
        using var f = new Fixture();
        f.Claude(1);
        var path = PrepareBackfill(f);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Start(f);
        var row = Assert.Single(Rows(f));
        var errors = row.GetProperty("hata_sayisi").GetInt32();
        Assert.True(result.Exit == 0 && result.Out.Length == 0 && errors == 0,
            $"ACC-F1 expected exit=0, stdout empty=True, hata_sayisi=0; actual exit={result.Exit}, stdout empty={result.Out.Length == 0}, hata_sayisi={errors}");
        Assert.Equal("", result.Error);
        Assert.Equal(1, row.GetProperty("mesgul").GetInt32());
        Assert.NotEqual("hata", row.GetProperty("sonuc").GetString());
        Assert.False(File.Exists(f.DailyPath));
    }

    [Fact]
    public void Kaydet_TaraImlecOncesindekiCompactTurunuDelikOlarakYazmaz()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, JsonSerializer.Serialize(new
        {
            sessionId = "fixture", uuid = "compact-hole", type = "user", isCompactSummary = true,
            timestamp = Fixture.Now.ToString("O"), message = new { content = "synthetic compaction summary" }
        }) + "\n", Fixture.Utf8);
        f.AppendClaude("cursor", "assistant", "synthetic recorded cursor");
        f.Daily(Cursor("cursor", 1) + "### [a cursor] recorded\n");
        PrepareBackfill(f);

        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        var count = Regex.Matches(f.Read(), @"^### \[h compact-hole\]", RegexOptions.Multiline).Count;
        Assert.True(count == 0, $"ACC-F2 expected compact-hole headings=0; actual {count}");
    }

    [Fact]
    public void Kaydet_AcilisIkiGunlukKilitliTranskriptiHataOlarakBildirir()
    {
        using var f = new Fixture();
        f.Claude(1);
        var path = PrepareBackfill(f, Fixture.Now.AddDays(-2));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Start(f);
        Assert.Equal(0, result.Exit);
        Assert.Contains("systemMessage", result.Out);
        var row = Assert.Single(Rows(f));
        Assert.Equal("hata", row.GetProperty("sonuc").GetString());
        Assert.Equal(1, row.GetProperty("hata_sayisi").GetInt32());
        Assert.Equal(0, row.GetProperty("mesgul").GetInt32());
    }

    [Fact]
    public void Kaydet_AcilisOkumayaIzinVerenYaziciAcikkenTranskriptiKaydeder()
    {
        using var f = new Fixture();
        f.Claude(1);
        var path = PrepareBackfill(f);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        Assert.Equal((0, "", ""), Start(f));
        var row = Assert.Single(Rows(f));
        Assert.Equal(0, row.GetProperty("hata_sayisi").GetInt32());
        Assert.Equal(0, row.GetProperty("mesgul").GetInt32());
        Assert.Equal(1, row.GetProperty("blok").GetInt32());
        Assert.Contains("[u m-0]", f.Read());
    }

    [Fact]
    public void Kaydet_MesajYeniKilitliTranskriptiSessizMesgulKaydederVeSonraYazar()
    {
        using var f = new Fixture();
        f.Claude(10);
        File.SetLastWriteTimeUtc(f.Transcript, Fixture.Now.UtcDateTime);
        using (var locked = new FileStream(f.Transcript, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal((0, "", ""), f.Run("--tetik", "mesaj"));
            Assert.Equal("mesgul", Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
            Assert.False(File.Exists(f.DailyPath));
        }

        Assert.Equal((0, "", ""), f.Run("--tetik", "mesaj"));
        Assert.Equal("yazdi", Rows(f)[1].GetProperty("sonuc").GetString());
        Assert.Equal(10, Regex.Matches(f.Read(), @"^### ", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void Kaydet_BaytKuyruguOkumayaIzinVerenYaziciAcikkenOkunur()
    {
        using var f = new Fixture();
        f.Claude(1);
        Assert.Equal(1, f.Write());
        f.AppendClaude("tail", "assistant", "synthetic tail");
        using var writer = new FileStream(f.Transcript, FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = Oom.Contracts.Kaydet.WriteResult(f.Root, "fixture", f.Transcript, "kapanis", Fixture.Now);
        Assert.Equal("bayt", result.ReadPath);
        Assert.Equal(1, result.Blocks);
        Assert.Contains("[a tail]", f.Read());
    }

    [Theory]
    [InlineData("tara")]
    [InlineData("acilis")]
    public void Kaydet_TaramaMesgulSayarkenDigerOturumuYazarVeSonrakiTetikteTamamlar(string trigger)
    {
        using var f = new Fixture();
        f.Claude(1);
        f.Daily(Cursor("recorded", 0) + "### [u recorded] recorded\n");
        var path = PrepareBackfill(f);
        var other = Path.Combine(Path.GetDirectoryName(path)!, "other.jsonl");
        File.WriteAllText(other, ForkTurn("other", "other-u", "user"), Fixture.Utf8);
        File.SetLastWriteTimeUtc(other, Fixture.Now.UtcDateTime);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = trigger == "acilis" ? Start(f) : f.Run("--tetik", trigger);
            Assert.Equal(0, result.Exit);
            Assert.Equal("", result.Error);
            Assert.DoesNotContain("systemMessage", result.Out);
            var row = Assert.Single(Rows(f));
            Assert.Equal(1, row.GetProperty("mesgul").GetInt32());
            Assert.Equal(0, row.GetProperty("hata_sayisi").GetInt32());
            Assert.Equal(1, row.GetProperty("blok").GetInt32());
            Assert.Contains("[u other-u]", f.Read());
            Assert.DoesNotContain("[u m-0]", f.Read());
        }

        Assert.Equal(0, (trigger == "acilis" ? Start(f) : f.Run("--tetik", trigger)).Exit);
        Assert.Equal(0, Rows(f)[1].GetProperty("mesgul").GetInt32());
        Assert.Single(Regex.Matches(f.Read(), @"^### \[u m-0\]", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(f.Read(), @"^### \[u other-u\]", RegexOptions.Multiline));
    }

    [Theory]
    [InlineData(1439, "mesgul")]
    [InlineData(1440, "hata")]
    public void Kaydet_KilitliTranskriptinMesgulSiniriTamYirmiDortSaattir(int minutes, string outcome)
    {
        using var f = new Fixture();
        f.Claude(10);
        File.SetLastWriteTimeUtc(f.Transcript, Fixture.Now.AddMinutes(-minutes).UtcDateTime);
        using var locked = new FileStream(f.Transcript, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = f.Run("--tetik", "mesaj");
        Assert.Equal(0, result.Exit);
        Assert.Equal(outcome == "mesgul", result.Out.Length == 0);
        Assert.Equal(outcome, Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
    }

    [Fact]
    public void Kaydet_KilitliDailyDosyasiniYeniTranskriptYuzundenMesgulSaymaz()
    {
        using var f = new Fixture();
        f.Claude(10);
        File.SetLastWriteTimeUtc(f.Transcript, Fixture.Now.UtcDateTime);
        f.Daily("---\ntype: daily\n---\n");
        using var locked = new FileStream(f.DailyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = f.Run("--tetik", "mesaj");
        Assert.Equal(0, result.Exit);
        Assert.Contains("systemMessage", result.Out);
        Assert.Equal("hata", Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
    }
}
