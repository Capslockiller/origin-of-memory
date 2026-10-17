using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kaydet_KaydedilmemisEskiTurTekrarindaBaytVeTamYolAyniSatiriVeNumarayiYazar(bool fullSecond)
    {
        using var fast = new Fixture();
        using var full = new Fixture();
        var initial = DatedTurn("A", Fixture.Now.AddDays(-20)) + DatedTurn("C", Fixture.Now);
        File.WriteAllText(fast.Transcript, initial, Fixture.Utf8);
        File.WriteAllText(full.Transcript, initial, Fixture.Utf8);
        Assert.Equal(1, fast.Write());
        Assert.Equal(1, full.Write());
        var replayOffset = new FileInfo(fast.Transcript).Length;
        var replay = DatedTurn("A", Fixture.Now.AddDays(-20));
        File.AppendAllText(fast.Transcript, replay, Fixture.Utf8);
        File.AppendAllText(full.Transcript, replay, Fixture.Utf8);
        Assert.Equal(1, Oom.Contracts.Kaydet.WriteResult(fast.Root, "fixture", fast.Transcript, "kapanis", Fixture.Now, useTail: !fullSecond).Blocks);
        Assert.Equal(1, Oom.Contracts.Kaydet.WriteResult(full.Root, "fixture", full.Transcript, "kapanis", Fixture.Now, useTail: false).Blocks);
        var actual = Regex.Matches(full.Read(), @"bayt:(\d+)")[^1].Groups[1].Value;
        Assert.True(actual == replayOffset.ToString(), $"ACC-M1 expected replay bayt:{replayOffset}; actual bayt:{actual} (fullSecond={fullSecond})");
        Assert.Equal(File.ReadAllBytes(full.DailyPath), File.ReadAllBytes(fast.DailyPath));
        File.AppendAllText(fast.Transcript, DatedTurn("D", Fixture.Now), Fixture.Utf8);
        File.AppendAllText(full.Transcript, DatedTurn("D", Fixture.Now), Fixture.Utf8);
        Assert.Equal(1, fast.Write());
        Assert.Equal(1, Oom.Contracts.Kaydet.WriteResult(full.Root, "fixture", full.Transcript, "kapanis", Fixture.Now, useTail: false).Blocks);
        Assert.Equal(File.ReadAllBytes(full.DailyPath), File.ReadAllBytes(fast.DailyPath));
        Assert.Contains("turns:3-3", fast.Read());
        Assert.Single(Regex.Matches(fast.Read(), @"^### \[u A\]", RegexOptions.Multiline));
    }

    [Fact]
    public void Kaydet_TaraImlecOncesindekiSahipCevabiniBirKezYazar()
    {
        using var f = new Fixture();
        SeedAnswerHole(f);
        PrepareBackfill(f);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        var count = Regex.Matches(f.Read(), @"^### \[u answer-hole\]", RegexOptions.Multiline).Count;
        Assert.True(count == 1, $"ACC-G1 expected answer-hole headings 1; actual {count}");
        Assert.Contains(" · cevap", f.Read());
        var first = f.Read();
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Equal(first, f.Read());
    }

    [Fact]
    public void Kaydet_TaraGunDokuzSekizGunlukImlecinKayitsizKuyrugunuYazar()
    {
        using var f = new Fixture();
        var old = Fixture.Now.AddDays(-8);
        File.WriteAllText(f.Transcript, DatedTurn("recorded-old", old) + DatedTurn("tail-old", old.AddMinutes(1)), Fixture.Utf8);
        f.Daily($"<!-- session:fixture ts:{old:O} turns:0-0 source:claude kayit:v1 son:recorded-old bayt:0 -->\n### [u recorded-old] old\n", -8);
        PrepareBackfill(f, old);
        Assert.Equal(0, f.Run("--tetik", "tara", "--gun", "9").Exit);
        var text = File.Exists(f.DailyPath) ? f.Read() : "";
        var count = Regex.Matches(text, @"^### \[u tail-old\]", RegexOptions.Multiline).Count;
        Assert.True(count == 1, $"ACC-G2 expected tail-old headings 1; actual {count}");
        Assert.DoesNotContain("[u recorded-old]", text);
        Assert.Contains($"### [u tail-old] {old.AddMinutes(1).ToLocalTime():yyyy-MM-dd HH:mm}", text);
        Assert.Equal(0, f.Run("--tetik", "tara", "--gun", "9").Exit);
        Assert.Equal(text, f.Read());
    }

    private static void SeedAnswerHole(Fixture f)
    {
        var prefix = DatedTurn("recorded-first", Fixture.Now.AddDays(-3))
            + ResultRecord("answer-hole", "The user answered: synthetic missing answer");
        File.WriteAllText(f.Transcript, prefix + DatedTurn("cursor", Fixture.Now), Fixture.Utf8);
        f.Daily(Cursor("cursor", 2, Fixture.Utf8.GetByteCount(prefix))
            + "### [u recorded-first] old\n### [u cursor] recent\n");
    }

    private static string PrepareBackfill(Fixture f, DateTimeOffset? modified = null)
    {
        var path = Path.Combine(SweepRoot(f), "fixture.jsonl");
        File.Copy(f.Transcript, path);
        File.SetLastWriteTimeUtc(path, (modified ?? Fixture.Now).UtcDateTime);
        return path;
    }
}
