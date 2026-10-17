using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Fact]
    public void Kaydet_A_TaraCodexExecSeridiniKaydetmez()
    {
        using var f = new Fixture();
        var path = Path.Combine(SweepRoot(f), "rollout-x-lane.jsonl");
        File.WriteAllText(path, CodexRollout("lane", "exec"), Fixture.Utf8);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        var row = Assert.Single(Rows(f));
        var blocks = row.GetProperty("blok").GetInt32();
        Assert.True(blocks == 0, $"A expected blocks 0; actual {blocks}");
        Assert.Equal(0, row.GetProperty("aday").GetInt32());
        Assert.Equal("bos", row.GetProperty("sonuc").GetString());
        Assert.False(File.Exists(f.DailyPath));
    }

    [Fact]
    public void Kaydet_B_ImlecsizOturumYalnizPencereIcindekiTuruYazar()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, DatedTurn("old", Fixture.Now.AddDays(-20))
            + DatedTurn("recent", Fixture.Now.AddDays(-1)), Fixture.Utf8);
        Assert.Equal(1, f.Write());
        var headings = Regex.Matches(f.Read(), @"(?m)^### \[u ").Count;
        Assert.True(headings == 1, $"B expected owner headings 1; actual {headings}");
        Assert.DoesNotContain("[u old]", f.Read());
        Assert.Contains("[u recent]", f.Read());
    }

    [Fact]
    public void Kaydet_C_BaskaGununTurBasligiTarihTasir()
    {
        using var f = new Fixture();
        var time = Fixture.Now.AddDays(-2);
        File.WriteAllText(f.Transcript, DatedTurn("deferred", time), Fixture.Utf8);
        Assert.Equal(1, f.Write());
        var expected = $"### [u deferred] {time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
        var actual = Assert.Single(File.ReadLines(f.DailyPath), line => line.StartsWith("### "));
        Assert.True(actual == expected, $"C expected {expected}; actual {actual}");
        Assert.Contains($" · {time.ToLocalTime():yyyy-MM-dd HH:mm}–{time.ToLocalTime():yyyy-MM-dd HH:mm} · tur", f.Read());
    }

    [Fact]
    public void Kaydet_D_AcilisSonHatalariModelBaglaminaDaEkler()
    {
        using var f = new Fixture();
        SweepRoot(f);
        SeedLog(f, ErrorRow(Fixture.Now.AddHours(-1)));
        var result = Start(f);
        var message = Message(result);
        using var json = JsonDocument.Parse(result.Out);
        var hasContext = json.RootElement.TryGetProperty("hookSpecificOutput", out var hook)
            && hook.TryGetProperty("additionalContext", out var context)
            && context.GetString()!.StartsWith("[Bildirim] kaydet: son 24 saatte 1 hata", StringComparison.Ordinal);
        Assert.True(hasContext, "D expected additionalContext prefix [Bildirim] kaydet: son 24 saatte 1 hata; actual absent");
        Assert.Equal("SessionStart", hook.GetProperty("hookEventName").GetString());
        Assert.Equal("[Bildirim] " + message, hook.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void Kaydet_E_AcilisCodexCapasiniMetadataKimligiyleOnEler()
    {
        using var f = new Fixture();
        var path = Path.Combine(SweepRoot(f), "rollout-x-interactive.jsonl");
        File.WriteAllText(path, CodexRollout("interactive", "cli"), Fixture.Utf8);
        File.SetLastWriteTimeUtc(path, Fixture.Now.UtcDateTime.AddMinutes(-2));
        f.Daily($"<!-- session:interactive ts:{Fixture.Now:O} turns:0-0 son:codex-u -->\n");
        Assert.Equal((0, "", ""), Start(f));
        var row = Assert.Single(Rows(f));
        var skipped = row.GetProperty("atlanan").GetInt32();
        var candidates = row.GetProperty("aday").GetInt32();
        Assert.True(skipped == 1 && candidates == 0, $"E expected atlanan 1, aday 0; actual atlanan {skipped}, aday {candidates}");
    }

    [Fact]
    public void Kaydet_TaraEtkilesimliCodexCliOturumunuKaydeder()
    {
        using var f = new Fixture();
        var path = Path.Combine(SweepRoot(f), "rollout-x-interactive.jsonl");
        File.WriteAllText(path, CodexRollout("interactive", "cli"), Fixture.Utf8);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Contains("[u codex-u]", f.Read());
        var row = Assert.Single(Rows(f));
        Assert.Equal(1, row.GetProperty("aday").GetInt32());
        Assert.Equal(1, row.GetProperty("blok").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kaydet_PencereIcindeCapaliOturumEskiKuyrugunuDaYazar(bool legacy)
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, DatedTurn("cursor", Fixture.Now.AddDays(-21))
            + DatedTurn("old-tail", Fixture.Now.AddDays(-20)), Fixture.Utf8);
        f.Daily($"<!-- session:fixture ts:{Fixture.Now:O} turns:0-0{(legacy ? "" : " son:cursor")} -->\n", -1);
        Assert.Equal(1, f.Write());
        Assert.DoesNotContain("[u cursor]", f.Read());
        Assert.Contains("[u old-tail]", f.Read());
        Assert.Single(Regex.Matches(f.Read(), @"(?m)^### \[u "));
        Assert.Equal(0, f.Write());
    }

    [Fact]
    public void Kaydet_AyniGunTurBasligiVeAralikSaatOlarakKalir()
    {
        using var f = new Fixture();
        f.Claude(1);
        Assert.Equal(1, f.Write());
        var time = Fixture.Now.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal($"### [u m-0] {time}", Assert.Single(File.ReadLines(f.DailyPath), line => line.StartsWith("### ")));
        Assert.Contains($" · {time}–{time} · tur", f.Read());
    }

    [Fact]
    public void Kaydet_AcilisBildirimYokkenTekSatirBileBasmaz()
    {
        using var f = new Fixture();
        SweepRoot(f);
        Assert.Equal((0, "", ""), Start(f));
        Assert.Equal("bos", Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
    }

    [Theory]
    [InlineData("mesaj")]
    [InlineData("compact")]
    [InlineData("kapanis")]
    public void Kaydet_DigerTetikHatasiYalnizSystemMessageBasar(string trigger)
    {
        using var f = new Fixture();
        var result = f.ProcessRun(["--vault", f.Root, "kaydet", "--tetik", trigger], "{broken");
        var message = Message(result);
        using var json = JsonDocument.Parse(result.Out);
        Assert.Equal("systemMessage", Assert.Single(json.RootElement.EnumerateObject()).Name);
        Assert.Equal(JsonSerializer.Serialize(new { systemMessage = message }), result.Out.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Kaydet_ExecDogrudanTetikteBosSonucDoner()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, CodexRollout("fixture", "exec"), Fixture.Utf8);
        Assert.Equal((0, "", ""), f.Run());
        var row = Assert.Single(Rows(f));
        Assert.Equal("bos", row.GetProperty("sonuc").GetString());
        Assert.Equal(0, row.GetProperty("blok").GetInt32());
        Assert.False(File.Exists(f.DailyPath));
    }

    [Theory]
    [InlineData("tara")]
    [InlineData("acilis")]
    public void Kaydet_ExecMetadataSatirindanSonraParseraGitmedenAtlanir(string trigger)
    {
        using var f = new Fixture();
        var path = Path.Combine(SweepRoot(f), "rollout-x-lane.jsonl");
        // No text turns: parsing this rollout would fail with FormatException.
        File.WriteAllText(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"lane\",\"source\":\"exec\"}}\ninvalid tail", Fixture.Utf8);
        var result = trigger == "acilis" ? Start(f) : f.Run("--tetik", trigger);
        Assert.Equal(0, result.Exit);
        Assert.Equal("", result.Error);
        var row = Assert.Single(Rows(f));
        Assert.Equal(0, row.GetProperty("aday").GetInt32());
        Assert.Equal(0, row.GetProperty("hata_sayisi").GetInt32());
        Assert.Equal("bos", row.GetProperty("sonuc").GetString());
        Assert.False(File.Exists(f.DailyPath));
    }

    [Fact]
    public void Kaydet_CodexCapasiVarsaMetinTuruOlmayanGovdeyiAyristirmaz()
    {
        using var f = new Fixture();
        var path = Path.Combine(SweepRoot(f), "rollout-x-interactive.jsonl");
        File.WriteAllText(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"interactive\",\"source\":\"cli\"}}\ninvalid tail", Fixture.Utf8);
        File.SetLastWriteTimeUtc(path, Fixture.Now.UtcDateTime.AddMinutes(-2));
        f.Daily($"<!-- session:interactive ts:{Fixture.Now:O} turns:0-0 son:codex-u -->\n");
        Assert.Equal((0, "", ""), Start(f));
        var row = Assert.Single(Rows(f));
        Assert.Equal(1, row.GetProperty("atlanan").GetInt32());
        Assert.Equal(0, row.GetProperty("aday").GetInt32());
        Assert.Equal(0, row.GetProperty("hata_sayisi").GetInt32());
    }

    [Theory]
    [InlineData(-60, 1, 0)]
    [InlineData(-61, 0, 1)]
    public void Kaydet_CodexOnElemesiAltmisSaniyeMarjiniKorur(int seconds, int candidates, int skipped)
    {
        using var f = new Fixture();
        var path = Path.Combine(SweepRoot(f), "rollout-x-interactive.jsonl");
        File.WriteAllText(path, CodexRollout("interactive", "cli"), Fixture.Utf8);
        File.SetLastWriteTimeUtc(path, Fixture.Now.UtcDateTime.AddSeconds(seconds));
        f.Daily($"<!-- session:interactive ts:{Fixture.Now:O} turns:0-0 son:codex-u -->\n");
        Assert.Equal((0, "", ""), Start(f));
        var row = Assert.Single(Rows(f));
        Assert.Equal(candidates, row.GetProperty("aday").GetInt32());
        Assert.Equal(skipped, row.GetProperty("atlanan").GetInt32());
    }

    [Fact]
    public void Kaydet_ImlecsizPencereYerelGeceYarisindaBaslar()
    {
        using var f = new Fixture();
        var midnight = Fixture.Now.ToLocalTime().Date.AddDays(-7);
        var floor = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        File.WriteAllText(f.Transcript, DatedTurn("before-floor", floor.AddTicks(-1))
            + DatedTurn("at-floor", floor), Fixture.Utf8);
        // An anchor outside the scanned eight days must not disable the floor.
        f.Daily($"<!-- session:fixture ts:{Fixture.Now.AddDays(-8):O} turns:0-0 son:before-floor -->\n", -8);
        Assert.Equal(1, f.Write());
        Assert.DoesNotContain("[u before-floor]", f.Read());
        Assert.Contains("[u at-floor]", f.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kaydet_BilinmeyenZamanDamgasiPencereIcindeSayilir(bool codex)
    {
        using var f = new Fixture();
        var jsonl = codex
            ? "{\"type\":\"session_meta\",\"payload\":{\"id\":\"fixture\",\"source\":\"cli\"}}\n"
                + "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"id\":\"unknown-0\",\"role\":\"user\",\"content\":\"no timestamp\"}}\n"
                + "{\"type\":\"response_item\",\"index\":7,\"payload\":{\"type\":\"message\",\"id\":\"unknown-7\",\"role\":\"user\",\"content\":\"no timestamp\"}}\n"
            : "{\"sessionId\":\"fixture\",\"uuid\":\"unknown-0\",\"type\":\"user\",\"message\":{\"content\":\"no timestamp\"}}\n"
                + "{\"sessionId\":\"fixture\",\"uuid\":\"unknown-7\",\"type\":\"user\",\"message\":{\"content\":\"no timestamp\"}}\n";
        File.WriteAllText(f.Transcript, jsonl, Fixture.Utf8);
        Assert.Equal(1, f.Write());
        Assert.Equal(2, Regex.Matches(f.Read(), @"(?m)^### \[u ").Count);
        Assert.Contains("[u unknown-7]", f.Read());
    }

    [Theory]
    [InlineData("vscode")]
    [InlineData("other-interactive")]
    public void Kaydet_ExecDisindakiCodexKaynaklariKaydedilir(string source)
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, CodexRollout("fixture", source), Fixture.Utf8);
        Assert.Equal(1, f.Write());
        Assert.Contains("[u codex-u]", f.Read());
    }

    [Fact]
    public void Kaydet_AcilisKendiHatasiniModelBaglaminaEkler()
    {
        using var f = new Fixture();
        var result = f.ProcessRun(["--vault", f.Root, "kaydet", "--tetik", "acilis"], "{broken");
        var message = Message(result);
        Assert.Contains("ArgumentException", message);
        Assert.Contains("son 24 saatte 1 hata", message);
        using var json = JsonDocument.Parse(result.Out);
        Assert.Equal("[Bildirim] " + message, json.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void Kaydet_KarmaGunAraligiHerUcundaKendiYerelTarihiniKullanir()
    {
        using var f = new Fixture();
        var previous = Fixture.Now.AddDays(-1).ToUniversalTime();
        File.WriteAllText(f.Transcript, DatedTurn("previous", previous)
            + DatedTurn("today", Fixture.Now.ToUniversalTime()), Fixture.Utf8);
        Assert.Equal(1, f.Write());
        var oldTime = previous.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var time = Fixture.Now.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Contains($" · {oldTime}–{time} · tur", f.Read());
        Assert.Contains($"### [u previous] {oldTime}", f.Read());
        Assert.Contains($"### [u today] {time}", f.Read());
    }

    private static string DatedTurn(string uuid, DateTimeOffset timestamp) =>
        JsonSerializer.Serialize(new { sessionId = "fixture", uuid, type = "user", timestamp = timestamp.ToString("O"), message = new { content = "synthetic " + uuid } }) + "\n";

    private static string CodexRollout(string id, string source) =>
        JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, source } }) + "\n"
        + JsonSerializer.Serialize(new { type = "response_item", timestamp = Fixture.Now.ToString("O"), payload = new { type = "message", id = "codex-u", role = "user", content = "synthetic" } }) + "\n";
}
