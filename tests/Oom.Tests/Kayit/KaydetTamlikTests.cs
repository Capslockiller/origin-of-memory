using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Fact]
    public void Kaydet_CatalOturumKendiTurlariniYazar()
    {
        using var f = new Fixture();
        f.Claude(2);
        File.AppendAllText(f.Transcript, ForkTurn("F", "fork-u", "user") + ForkTurn("F", "fork-a", "assistant"));
        var count = 0;
        var error = Record.Exception(() => count = Oom.Contracts.Kaydet.Write(f.Root, "F", f.Transcript, "kapanis", Fixture.Now));
        Assert.True(error is null, $"Expected fork block without exception; actual {error?.GetType().Name}: {error?.Message}");
        Assert.Equal(1, count);
        Assert.Contains("session:F", f.Read());
        Assert.Contains("[u fork-u]", f.Read());
        Assert.Contains("[a fork-a]", f.Read());
        Assert.DoesNotContain("m-0", f.Read());
        Assert.DoesNotContain("m-1", f.Read());
    }

    [Fact]
    public void Kaydet_OlmayanTranskriptSifirDoner()
    {
        using var f = new Fixture();
        var count = -1;
        var error = Record.Exception(() => count = f.Write());
        Assert.True(error is null, $"Expected missing transcript to return 0; actual {error?.GetType().Name}");
        Assert.Equal(0, count);
    }

    [Fact]
    public void Kaydet_AcilisAcikKisaOturumunKuyrugunuYazar()
    {
        using var f = new Fixture();
        f.Claude(4);
        Assert.Equal(0, f.Write("mesaj"));
        f.Config(JsonSerializer.Serialize(new { sweep = new { roots = new[] { f.Root } } }));
        var result = f.ProcessRun(["--vault", f.Root, "kaydet", "--tetik", "acilis"],
            JsonSerializer.Serialize(new { session_id = "other", transcript_path = Path.Combine(f.Root, "other.jsonl") }));
        Assert.True(result.Exit == 0, $"Expected acilis exit 0 and short-session block; actual exit {result.Exit}: {result.Error.Trim()}");
        Assert.Contains("session:fixture", f.Read());
        Assert.Contains("[u m-2]", f.Read());
    }

    private static string ForkTurn(string sessionId, string uuid, string role, string text = "synthetic") =>
        JsonSerializer.Serialize(new { sessionId, uuid, type = role, timestamp = Fixture.Now.ToString("O"), message = new { content = text } }) + "\n";

    [Fact]
    public void Kaydet_AcilisGunceliDislarVeEskiCapaliDosyayiOkumaz()
    {
        using var f = new Fixture();
        var root = SweepRoot(f);
        File.WriteAllText(Path.Combine(root, "current.jsonl"), ForkTurn("current", "current-u", "user"));
        var currentPath = Path.Combine(root, "different-name.jsonl");
        File.WriteAllText(currentPath, ForkTurn("current", "current-other", "user"));
        var old = Path.Combine(root, "old.jsonl");
        File.WriteAllText(old, "this would be unreadable if parsed");
        File.SetLastWriteTimeUtc(old, Fixture.Now.UtcDateTime.AddMinutes(-2));
        f.Daily($"<!-- session:old ts:{Fixture.Now:O} turns:0-0 son:old-u -->\n");
        using var locked = new FileStream(old, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Start(f, "current", currentPath);
        Assert.Equal((0, "", ""), result);
        var row = Assert.Single(Rows(f));
        Assert.Equal(0, row.GetProperty("aday").GetInt32());
        Assert.Equal(1, row.GetProperty("atlanan").GetInt32());
        Assert.Equal(0, row.GetProperty("blok").GetInt32());
        Assert.DoesNotContain("current-u", f.Read());
    }

    [Fact]
    public void Kaydet_AcilisHataYokkenStdoutBosKalir()
    {
        using var f = new Fixture();
        SweepRoot(f);
        Assert.Equal((0, "", ""), Start(f));
        Assert.Equal("bos", Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
    }

    [Fact]
    public void Kaydet_AcilisSonYirmiDortSaatHatalariniTekJsonlaBildirir()
    {
        using var f = new Fixture();
        SweepRoot(f);
        SeedLog(f, ErrorRow(Fixture.Now.AddHours(-23)) + ErrorRow(Fixture.Now.AddHours(-25)));
        var result = Start(f);
        var message = Message(result);
        Assert.Equal($"kaydet: son 24 saatte 1 hata — {LogPath(f)}", message);
        Assert.Equal(3, Rows(f).Length);
    }

    [Theory]
    [InlineData("mesaj")]
    [InlineData("compact")]
    [InlineData("kapanis")]
    [InlineData("acilis")]
    public void Kaydet_BozukPayloadKancayiEngellemezVeTekHataKaydeder(string trigger)
    {
        using var f = new Fixture();
        var result = f.ProcessRun(["--vault", f.Root, "kaydet", "--tetik", trigger], "{broken");
        var message = Message(result);
        Assert.Contains("ArgumentException", message);
        Assert.Contains("(oturum -)", message);
        if (trigger == "acilis") Assert.Contains("son 24 saatte 1 hata", message);
        var row = Assert.Single(Rows(f));
        Assert.Equal("hata", row.GetProperty("sonuc").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("oturum").ValueKind);
        Assert.DoesNotContain("broken", File.ReadAllText(LogPath(f)));
    }

    [Theory]
    [InlineData("mesaj")]
    [InlineData("compact")]
    [InlineData("kapanis")]
    [InlineData("acilis")]
    public void Kaydet_AyarYuklemeHatasiKancayiEngellemez(string trigger)
    {
        using var f = new Fixture();
        f.Config("{broken");
        var result = trigger == "acilis" ? Start(f) : f.Run("--tetik", trigger);
        Assert.Contains("InvalidDataException", Message(result));
        Assert.Equal("hata", Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
    }

    [Theory]
    [InlineData("mesaj", 10, "yazdi")]
    [InlineData("mesaj", 4, "bekliyor")]
    [InlineData("compact", 2, "yazdi")]
    [InlineData("kapanis", 2, "yazdi")]
    [InlineData("kapanis", 0, "dosya-yok")]
    public void Kaydet_HerCagriTekSureliSonucSatiriEkler(string trigger, int turns, string outcome)
    {
        using var f = new Fixture();
        f.Claude(turns);
        Assert.Equal((0, "", ""), f.Run("--tetik", trigger));
        var row = Assert.Single(Rows(f));
        Assert.Equal(trigger, row.GetProperty("tetik").GetString());
        Assert.Equal("fixture", row.GetProperty("oturum").GetString());
        Assert.True(row.GetProperty("ms").GetInt32() >= 0);
        Assert.Equal(outcome, row.GetProperty("sonuc").GetString());
        Assert.Equal(outcome == "yazdi" ? 1 : 0, row.GetProperty("blok").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("hata").ValueKind);
        Assert.True(DateTimeOffset.TryParse(row.GetProperty("ts").GetString(), out _));
        Assert.Equal((0, "", ""), f.Run("--tetik", "kapanis"));
        Assert.Equal(2, Rows(f).Length);
        Assert.Equal(turns == 0 ? "dosya-yok" : outcome == "bekliyor" ? "yazdi" : "bos", Rows(f)[1].GetProperty("sonuc").GetString());
    }

    [Theory]
    [InlineData("other", "user", "kimlik-yok")]
    [InlineData("fixture", "system", "bos")]
    public void Kaydet_KimlikVeyaMetinOlmayincaHatasizSonucKaydeder(string id, string role, string outcome)
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, ForkTurn(id, "empty-u", role));
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal(outcome, Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
        Assert.False(File.Exists(f.DailyPath));
    }

    [Fact]
    public void Kaydet_TaraGunSekizBesGunlukTranskriptiKapsar()
    {
        using var f = new Fixture();
        var path = Path.Combine(SweepRoot(f), "old.jsonl");
        File.WriteAllText(path, ForkTurn("old", "old-u", "user"));
        File.SetLastWriteTimeUtc(path, Fixture.Now.UtcDateTime.AddDays(-5));
        var first = f.Run("--tetik", "tara");
        Assert.Equal(0, first.Exit);
        Assert.Equal("tara: 0 oturum, 0 blok, 0 hata", first.Out.Trim());
        Assert.False(File.Exists(f.DailyPath));
        var second = f.Run("--tetik", "tara", "--gun", "8");
        Assert.Equal(0, second.Exit);
        Assert.Equal("tara: 1 oturum, 1 blok, 0 hata", second.Out.Trim());
        Assert.Contains("session:old", f.Read());
        Assert.Equal(2, Rows(f).Length);
        var row = Rows(f)[1];
        Assert.Equal(JsonValueKind.Null, row.GetProperty("oturum").ValueKind);
        Assert.Equal(1, row.GetProperty("aday").GetInt32());
        Assert.Equal(0, row.GetProperty("atlanan").GetInt32());
        Assert.Equal(0, row.GetProperty("hata_sayisi").GetInt32());
        Assert.Equal("yazdi", row.GetProperty("sonuc").GetString());
        Assert.True(row.GetProperty("ms").GetInt32() >= 0);
    }

    [Fact]
    public void Kaydet_CatalVeAnaOturumUuidTekrarlamaz()
    {
        using var f = new Fixture();
        f.Claude(2);
        var fork = Path.Combine(f.Root, "F.jsonl");
        File.WriteAllText(fork, File.ReadAllText(f.Transcript) + ForkTurn("F", "fork-u", "user") + ForkTurn("F", "fork-a", "assistant"));
        Assert.Equal(1, Oom.Contracts.Kaydet.Write(f.Root, "F", fork, "kapanis", Fixture.Now));
        Assert.Equal(0, Oom.Contracts.Kaydet.Write(f.Root, "F", fork, "kapanis", Fixture.Now));
        Assert.Equal(1, f.Write());
        Assert.Equal(0, f.Write());
        var ids = Regex.Matches(f.Read(), @"(?m)^### \[[ua] ([^\]]+)\]").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(4, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Kaydet_TaraCatalKimliginiDosyaAdindanAlir()
    {
        using var f = new Fixture();
        var root = SweepRoot(f);
        File.WriteAllText(Path.Combine(root, "F.jsonl"), ForkTurn("P", "parent-u", "user") + ForkTurn("F", "fork-u", "user"));
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        Assert.Contains("session:F", f.Read());
        Assert.DoesNotContain("parent-u", f.Read());
    }

    [Fact]
    public void Kaydet_TekKimlikliTranskriptinTurIndeksleriDegismez()
    {
        using var f = new Fixture();
        f.Claude(4);
        var jsonl = File.ReadAllText(f.Transcript);
        var before = ClaudeTranscript.Read(jsonl, SystemClock.Instance).Turns;
        var after = ClaudeTranscript.Read(jsonl, SystemClock.Instance, "fixture").Turns;
        Assert.Equal(before.Select(t => (t.Index, t.Uuid)), after.Select(t => (t.Index, t.Uuid)));
    }

    [Fact]
    public void Kaydet_CodexKimlikUyusmazligiHalaHataVerir()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, """
            {"type":"session_meta","payload":{"id":"other"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":"synthetic"}}
            """);
        Assert.Throws<InvalidDataException>(() => f.Write());
        Assert.Contains("InvalidDataException", Message(f.Run()));
    }

    [Fact]
    public void Kaydet_LogIkiMegabaytiAsincaOncekiDosyayiDegistirerekDondurur()
    {
        using var f = new Fixture();
        var previous = Path.Combine(Path.GetDirectoryName(LogPath(f))!, "kaydet.1.jsonl");
        var oversized = new string(' ', 2 * 1024 * 1024 + 1) + "\n";
        SeedLog(f, oversized);
        File.WriteAllText(previous, "older rotation");
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal(oversized, File.ReadAllText(previous));
        Assert.Single(Rows(f));
    }

    [Fact]
    public void Kaydet_LogYazilamazsaKancaCikisiVeStdoutDegismez()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(LogPath(f));
        var result = f.Run();
        Assert.Equal(0, result.Exit);
        Assert.Equal("", result.Out);
        Assert.Single(result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("kaydet log:", result.Error);
    }

    [Fact]
    public async Task Kaydet_ParalelKancalarIkiAyriLogSatiriEkler()
    {
        using var f = new Fixture();
        f.Claude(10);
        var results = await Task.WhenAll(Task.Run(() => f.Run()), Task.Run(() => f.Run()));
        Assert.All(results, r => Assert.Equal((0, "", ""), r));
        Assert.Equal(2, Rows(f).Length);
        Assert.Equal(new[] { "bos", "yazdi" }, Rows(f).Select(r => r.GetProperty("sonuc").GetString()).Order().ToArray());
    }

    [Fact]
    public async Task Kaydet_LogPaylasimKilitindeKisaSureYenidenDener()
    {
        using var f = new Fixture();
        SeedLog(f, "");
        var held = new FileStream(LogPath(f), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var append = Task.Run(() => KaydetLog.Append(LogPath(f), Fixture.Now, "mesaj", "fixture", Stopwatch.StartNew(), new Oom.Contracts.Kaydet.Result()));
            await Task.Delay(60);
            held.Dispose();
            await append;
        }
        finally { held.Dispose(); }
        Assert.Equal("bos", Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
    }

    [Fact]
    public void Kaydet_AcilisKirkBuyukTranskriptteUcSaniyedenKisaSurer()
    {
        using var f = new Fixture();
        var root = SweepRoot(f);
        var filler = new string('x', 1536 * 1024);
        for (var i = 0; i < 40; i++)
        {
            var path = Path.Combine(root, $"s-{i}.jsonl");
            File.WriteAllText(path, ForkTurn($"s-{i}", $"u-{i}", "user", filler), Fixture.Utf8);
            File.SetLastWriteTimeUtc(path, Fixture.Now.UtcDateTime.AddMinutes(i < 38 ? -2 : 0));
        }
        for (var day = -7; day <= 0; day++)
        {
            var anchors = string.Concat(Enumerable.Range(0, 38).Select(i => $"<!-- session:s-{i} ts:{Fixture.Now.AddDays(day):O} turns:0-0 son:u-{i} -->\n"));
            f.Daily(anchors + new string('x', 200 * 1024 - anchors.Length) + "\n", day);
        }
        var timer = Stopwatch.StartNew();
        var result = Start(f);
        timer.Stop();
        Assert.Equal((0, "", ""), result);
        var row = Assert.Single(Rows(f));
        Assert.Equal(2, row.GetProperty("aday").GetInt32());
        Assert.Equal(38, row.GetProperty("atlanan").GetInt32());
        Assert.Equal(2, row.GetProperty("blok").GetInt32());
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "acilis-timing.json"),
            JsonSerializer.Serialize(new { wall_ms = timer.ElapsedMilliseconds, logged_ms = row.GetProperty("ms").GetInt32(), transcript_bytes = Directory.GetFiles(root).Sum(p => new FileInfo(p).Length), daily_files = 8, daily_bytes_before = 8 * (200 * 1024 + 1) }));
        Assert.True(timer.ElapsedMilliseconds < 3000, $"acilis took {timer.ElapsedMilliseconds} ms");
    }

    private static string SweepRoot(Fixture f)
    {
        var root = Path.Combine(f.Root, "transcripts");
        Directory.CreateDirectory(root);
        f.Config(JsonSerializer.Serialize(new { sweep = new { roots = new[] { root } } }));
        return root;
    }

    private static (int Exit, string Out, string Error) Start(Fixture f, string id = "current", string? path = null) =>
        f.ProcessRun(["--vault", f.Root, "kaydet", "--tetik", "acilis"],
            JsonSerializer.Serialize(new { session_id = id, transcript_path = path ?? Path.Combine(f.Root, "current.jsonl") }));

    private static string LogPath(Fixture f) => Path.Combine(f.Root, "local", "oom", VaultFiles.Hash(f.Root), "kaydet.jsonl");

    private static JsonElement[] Rows(Fixture f) => File.ReadLines(LogPath(f)).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();

    private static void SeedLog(Fixture f, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath(f))!);
        File.WriteAllText(LogPath(f), text, Fixture.Utf8);
    }

    private static string ErrorRow(DateTimeOffset ts) => JsonSerializer.Serialize(new { ts = ts.ToString("O"), sonuc = "hata" }) + "\n";

    private static string Message((int Exit, string Out, string Error) result)
    {
        Assert.Equal(0, result.Exit);
        Assert.Equal("", result.Error);
        var line = Assert.Single(result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var json = JsonDocument.Parse(line);
        var message = json.RootElement.GetProperty("systemMessage").GetString()!;
        if (json.RootElement.TryGetProperty("hookSpecificOutput", out var hook))
        {
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal(2, hook.EnumerateObject().Count());
            Assert.Equal("SessionStart", hook.GetProperty("hookEventName").GetString());
            Assert.Equal("[Bildirim] " + message, hook.GetProperty("additionalContext").GetString());
        }
        else Assert.Single(json.RootElement.EnumerateObject());
        return message;
    }
}
