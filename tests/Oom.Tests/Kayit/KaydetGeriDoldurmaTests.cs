using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Fact]
    public void Kaydet_TaraImlecinOncesindekiEskiBloktaKayitliUuidyiTekrarYazmaz()
    {
        using var f = new Fixture();
        SeedAnswerHole(f);
        f.Daily(Cursor("recorded-first", 0) + "### [u recorded-first] old\n", -3);
        f.Daily(f.Read().Replace("### [u recorded-first] old\n", ""));
        PrepareBackfill(f);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.DoesNotContain("[u recorded-first]", f.Read());
        Assert.Single(Regex.Matches(f.Read(), @"^### \[u cursor\]", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(f.Read(), @"^### \[u answer-hole\]", RegexOptions.Multiline));
        var all = string.Join("\n", Directory.GetFiles(Path.Combine(f.Root, "daily"), "*.md").Select(File.ReadAllText));
        Assert.Single(Regex.Matches(all, @"^### \[u recorded-first\]", RegexOptions.Multiline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kaydet_YalnizEskiDelikBlogundanSonraMesajEnIleriImlectenDevamEder(bool full)
    {
        using var f = new Fixture();
        SeedAnswerHole(f);
        var before = Imlec.TailCursor(f.Root, "fixture", Fixture.Now)!;
        PrepareBackfill(f);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Contains($"turns:1-1 source:claude kayit:v1 son:cursor bayt:{before.ByteOffset}", f.Read());
        var after = Imlec.TailCursor(f.Root, "fixture", Fixture.Now)!;
        Assert.Equal(before.End, after.End);
        Assert.Equal(before.ByteOffset, after.ByteOffset);
        Assert.Equal(before.Last, after.Last);
        for (var i = 0; i < 10; i++) f.AppendClaude("next-" + i, i % 2 == 0 ? "user" : "assistant", "synthetic new turn");
        var result = Oom.Contracts.Kaydet.WriteResult(f.Root, "fixture", f.Transcript, "mesaj", Fixture.Now, useTail: !full);
        Assert.Equal(1, result.Blocks);
        Assert.Equal(full ? "tam" : "bayt", result.ReadPath);
        Assert.Contains("turns:3-12", f.Read());
        Assert.Equal(13, Regex.Matches(f.Read(), @"^### \[[uah] ", RegexOptions.Multiline).Count);
        for (var i = 0; i < 10; i++) Assert.Contains(" next-" + i + "]", f.Read());
        Assert.Equal(0, f.Write("mesaj"));
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("three")]
    public void Kaydet_SatirOrtasindakiBaytTamYolaDonerVeAyniBaytlariYazar(string location)
    {
        using var fast = new Fixture();
        using var full = new Fixture();
        fast.Claude(2);
        var bytes = File.ReadAllBytes(fast.Transcript);
        var offset = location == "three" ? 3 : bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("\"uuid\""));
        Assert.True(offset > 0 && bytes[offset - 1] != '\n');
        fast.Daily(Cursor("m-0", 0, offset));
        full.Daily(fast.Read());
        File.Copy(fast.Transcript, full.Transcript);
        var result = Oom.Contracts.Kaydet.WriteResult(fast.Root, "fixture", fast.Transcript, "kapanis", Fixture.Now);
        Assert.Equal("tam", result.ReadPath);
        Assert.Equal(1, result.Blocks);
        Assert.Equal(1, Oom.Contracts.Kaydet.WriteResult(full.Root, "fixture", full.Transcript, "kapanis", Fixture.Now, useTail: false).Blocks);
        Assert.Equal(File.ReadAllBytes(full.DailyPath), File.ReadAllBytes(fast.DailyPath));
    }

    [Fact]
    public void Kaydet_Utf8BomSonrasindakiUcBaytiGecerliSatirBasiSayar()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, DatedTurn("cursor", Fixture.Now) + DatedTurn("next", Fixture.Now), new UTF8Encoding(true));
        f.Daily(Cursor("cursor", 0, 3));
        var result = Oom.Contracts.Kaydet.WriteResult(f.Root, "fixture", f.Transcript, "kapanis", Fixture.Now);
        Assert.Equal("bayt", result.ReadPath);
        Assert.Equal(1, result.Blocks);
        Assert.Contains("turns:1-1", f.Read());
    }

    [Fact]
    public void Kaydet_MesajImlecOncesindekiDeligiDoldurmaz()
    {
        using var f = new Fixture();
        SeedAnswerHole(f);
        for (var i = 0; i < 10; i++) f.AppendClaude("next-" + i, "user", "synthetic new turn");
        Assert.Equal(1, f.Write("mesaj"));
        Assert.DoesNotContain("[u answer-hole]", f.Read());
        Assert.Contains("turns:3-12", f.Read());
    }

    [Fact]
    public void Kaydet_MesajSekizGunlukCapayiGormezVeYediGunlukTabaniKorur()
    {
        using var f = new Fixture();
        var old = Fixture.Now.AddDays(-8);
        File.WriteAllText(f.Transcript, DatedTurn("old-cursor", old), Fixture.Utf8);
        for (var i = 0; i < 10; i++) File.AppendAllText(f.Transcript, DatedTurn("old-" + i, old), Fixture.Utf8);
        f.Daily(Cursor("old-cursor", 0), -8);
        Assert.Equal((0, "", ""), f.Run("--tetik", "mesaj"));
        Assert.False(File.Exists(f.DailyPath));
    }

    [Fact]
    public void Kaydet_AcilisGunDokuzBileDelikDoldurmazVeYediGunlukBakisiKorur()
    {
        using var f = new Fixture();
        SeedAnswerHole(f);
        var root = Path.GetDirectoryName(PrepareBackfill(f))!;
        var old = Fixture.Now.AddDays(-8);
        File.WriteAllText(Path.Combine(root, "old.jsonl"), DatedTurn("old-cursor", old).Replace("fixture", "old")
            + DatedTurn("old-tail", old).Replace("fixture", "old"), Fixture.Utf8);
        f.Daily($"<!-- session:old ts:{old:O} turns:0-0 source:claude kayit:v1 son:old-cursor bayt:0 -->\n### [u old-cursor] old\n", -8);
        var result = f.ProcessRun(["--vault", f.Root, "kaydet", "--tetik", "acilis", "--gun", "9"],
            JsonSerializer.Serialize(new { session_id = "current", transcript_path = Path.Combine(f.Root, "current.jsonl") }));
        Assert.Equal((0, "", ""), result);
        Assert.DoesNotContain("[u answer-hole]", f.Read());
        Assert.DoesNotContain("[u old-tail]", f.Read());
    }

    [Fact]
    public void Kaydet_TaraYediGunTabaniniDahilEderOncesiniVeGelecegiDislar()
    {
        using var f = new Fixture();
        var midnight = Fixture.Now.ToLocalTime().Date.AddDays(-7);
        var floor = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        File.WriteAllText(f.Transcript, DatedTurn("too-old", floor.AddTicks(-1)) + DatedTurn("at-floor", floor)
            + DatedTurn("future", Fixture.Now.AddDays(1)) + DatedTurn("cursor", Fixture.Now), Fixture.Utf8);
        f.Daily(Cursor("cursor", 3) + "### [u cursor] recent\n");
        PrepareBackfill(f);
        Assert.Equal(0, f.Run("--tetik", "tara", "--gun", "2").Exit);
        Assert.Contains($"### [u at-floor] {floor.ToLocalTime():yyyy-MM-dd HH:mm}", f.Read());
        Assert.DoesNotContain("[u too-old]", f.Read());
        Assert.DoesNotContain("[u future]", f.Read());
    }

    [Fact]
    public void Kaydet_TaraHerIkiYandakiUahDelikleriniTranskriptSirasiylaTekYazar()
    {
        using var f = new Fixture();
        f.AppendClaude("owner-hole", "user", "synthetic owner");
        f.AppendClaude("agent-hole", "assistant", "synthetic agent");
        f.AppendClaude("harness-hole", "user", "<system-reminder>synthetic harness</system-reminder>");
        f.AppendClaude("cursor", "assistant", "synthetic cursor");
        f.AppendClaude("owner-hole", "user", "synthetic replay");
        f.AppendClaude("after-hole", "assistant", "synthetic tail");
        f.Daily(Cursor("cursor", 3) + "### [a cursor] recent\n");
        PrepareBackfill(f);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        var ids = Regex.Matches(f.Read(), @"^### \[[uah] ([^\]]+)\]", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "cursor", "owner-hole", "agent-hole", "harness-hole", "after-hole" }, ids);
        Assert.Contains("turns:0-5 source:claude kayit:v1 son:after-hole", f.Read());
        Assert.DoesNotContain("synthetic harness", f.Read());
        var first = f.Read();
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Equal(first, f.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kaydet_TaraBilinmeyenZamaniImlecinOncesindeDeKaydeder(bool codex)
    {
        using var f = new Fixture();
        var text = codex
            ? "{\"type\":\"session_meta\",\"payload\":{\"id\":\"fixture\",\"source\":\"cli\"}}\n"
                + "{\"type\":\"response_item\",\"index\":7,\"payload\":{\"type\":\"message\",\"id\":\"unknown-hole\",\"role\":\"user\",\"content\":\"synthetic unknown\"}}\n"
                + "{\"type\":\"response_item\",\"index\":8,\"payload\":{\"type\":\"message\",\"id\":\"cursor\",\"role\":\"user\",\"content\":\"synthetic cursor\"}}\n"
            : "{\"sessionId\":\"fixture\",\"uuid\":\"unknown-hole\",\"type\":\"user\",\"message\":{\"content\":\"synthetic unknown\"}}\n"
                + DatedTurn("cursor", Fixture.Now);
        File.WriteAllText(f.Transcript, text, Fixture.Utf8);
        f.Daily(Cursor("cursor", codex ? 8 : 1).Replace(" bayt:0", "") + "### [u cursor] recent\n");
        PrepareBackfill(f);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Single(Regex.Matches(f.Read(), @"^### \[u unknown-hole\]", RegexOptions.Multiline));
        var first = f.Read();
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Equal(first, f.Read());
    }

    [Fact]
    public void Kaydet_TaraYalnizHarnessIcerenOturumunKayitsizTurunuDaYazar()
    {
        using var f = new Fixture();
        f.AppendClaude("harness-hole", "user", "<system-reminder>synthetic harness</system-reminder>");
        PrepareBackfill(f);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Contains("### [h harness-hole]", f.Read());
        Assert.DoesNotContain("synthetic harness", f.Read());
        var first = f.Read();
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Equal(first, f.Read());
    }

    [Fact]
    public void Kaydet_DelikBlogununAsilImleciYediGunDisindaKalincaTamOkumaNumarayiKorur()
    {
        using var f = new Fixture();
        var old = Fixture.Now.AddDays(-8);
        File.WriteAllText(f.Transcript, DatedTurn("hole", old) + DatedTurn("already", old) + DatedTurn("cursor", old), Fixture.Utf8);
        var offset = Fixture.Utf8.GetByteCount(DatedTurn("hole", old) + DatedTurn("already", old));
        f.Daily($"<!-- session:fixture ts:{old:O} turns:1-2 source:claude kayit:v1 son:cursor bayt:{offset} -->\n"
            + "### [u already] old\n### [u cursor] old\n", -8);
        PrepareBackfill(f, old);
        Assert.Equal(0, f.Run("--tetik", "tara", "--gun", "9").Exit);
        Assert.Contains($"turns:0-0 source:claude kayit:v1 son:cursor bayt:{offset}", f.Read());
        for (var i = 0; i < 10; i++) File.AppendAllText(f.Transcript, DatedTurn("next-" + i, Fixture.Now), Fixture.Utf8);
        var result = Oom.Contracts.Kaydet.WriteResult(f.Root, "fixture", f.Transcript, "mesaj", Fixture.Now);
        Assert.Equal("tam", result.ReadPath);
        Assert.Equal(1, result.Blocks);
        Assert.Contains("turns:3-12", f.Read());
        Assert.DoesNotContain("[u already]", f.Read());
        Assert.DoesNotContain("[u cursor]", f.Read());
        Assert.Equal(0, f.Write("mesaj"));
    }
}
