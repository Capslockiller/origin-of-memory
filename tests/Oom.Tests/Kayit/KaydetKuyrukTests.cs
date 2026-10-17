using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Fact]
    public void Kaydet_YuzMbTranskriptteMesajOrtancasiDortYuzMsAltinda()
    {
        using var f = new Fixture();
        using (var writer = new StreamWriter(f.Transcript, false, Fixture.Utf8))
        {
            var filler = ResultRecord("bulk", new string('x', 1024 * 1024));
            for (var i = 0; i < 100; i++) writer.Write(filler);
        }
        var cursorOffset = new FileInfo(f.Transcript).Length;
        f.Claude(11);
        var times = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            f.Daily(Cursor("m-0", 0, cursorOffset));
            var timer = Stopwatch.StartNew();
            Assert.Equal((0, "", ""), f.Run("--tetik", "mesaj"));
            times.Add(timer.ElapsedMilliseconds);
        }
        var median = times.Order().ElementAt(1);
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("TMPDIR")!, "adim12-timing.json"),
            JsonSerializer.Serialize(new { runs_ms = times, median_ms = median, bytes = new FileInfo(f.Transcript).Length }));
        Assert.True(median < 400, $"ACC-1 expected median < 400 ms; actual {median} ms (runs: {string.Join(", ", times)})");
        Assert.Contains("turns:1-10", f.Read());
    }

    [Fact]
    public void Kaydet_SoruCevabiniSahibinSozuOlarakYazar()
    {
        using var f = new Fixture();
        f.Claude(1);
        f.AppendClaude("question", "assistant", new[] { new { type = "tool_use", id = "ask", name = "AskUserQuestion" } });
        File.AppendAllText(f.Transcript, ResultRecord("answer", "The user answered: synthetic choice", "ask"));
        Assert.Equal((0, "", ""), f.Run("--tetik", "kapanis"));
        Assert.True(Regex.IsMatch(f.Read(), @"### \[u answer\] [^\r\n]+ · cevap\r?\nThe user answered: synthetic choice"),
            "ACC-2a expected [u answer] heading with · cevap and answer text; actual absent");
    }

    [Fact]
    public void Kaydet_AracReddindekiYaziliGerekceyiSahibinSozuOlarakYazar()
    {
        using var f = new Fixture();
        f.Claude(1);
        var feedback = "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). To tell you how to proceed, the user said:\nsynthetic feedback";
        File.AppendAllText(f.Transcript, ResultRecord("rejection", feedback, "exit-plan", rejected: true));
        Assert.Equal((0, "", ""), f.Run("--tetik", "kapanis"));
        Assert.True(Regex.IsMatch(f.Read(), @"### \[u rejection\] [^\r\n]+ · cevap\r?\n") && f.Read().Contains(feedback),
            "ACC-2b expected [u rejection] heading with · cevap and typed feedback; actual absent");
    }

    [Fact]
    public void Kaydet_KuyrukVeTamOkumaDuzOturumdaBaytEsittir() => Equivalent(f =>
    {
        f.AppendClaude("plain-user", "user", "synthetic next prompt");
        f.AppendClaude("plain-agent", "assistant", "synthetic next response");
    });

    [Fact]
    public void Kaydet_KuyrukVeTamOkumaCatalKopyalarindaBaytEsittir() => Equivalent(f =>
    {
        File.AppendAllText(f.Transcript, ForkTurn("parent", "copied", "user"));
        f.AppendClaude("fork-new", "user", "synthetic fork tail");
    }, prefix: ForkTurn("parent", "parent-old", "assistant"));

    [Fact]
    public void Kaydet_KuyrukVeTamOkumaAracBirlesimiVeHarnessIleBaytEsittir() => Equivalent(f =>
    {
        f.AppendClaude("cursor-tool", "assistant", new[] { new { type = "tool_use", name = "Read" } });
        f.AppendClaude("agent-new", "assistant", "synthetic agent text");
        f.AppendClaude("agent-tool", "assistant", new[] { new { type = "tool_use", name = "Bash" } });
        f.AppendClaude("harness", "user", "<system-reminder>synthetic harness</system-reminder>");
        f.AppendClaude("owner-new", "user", "synthetic owner text");
    });

    [Fact]
    public void Kaydet_KuyrukVeTamOkumaImlecSonrasiCevaptaBaytEsittir() => Equivalent(f =>
        File.AppendAllText(f.Transcript, ResultRecord("answer-new", "The user answered: synthetic answer")));

    [Fact]
    public void Kaydet_KuyrukVeTamOkumaYalnizHarnessKuyrugundaBaytEsittir() => Equivalent(f =>
        f.AppendClaude("harness-only", "user", "<system-reminder>synthetic harness</system-reminder>"));

    [Fact]
    public void Kaydet_KuyrukVeTamOkumaProjeDiziniDegisseDeBaytEsittir() => Equivalent(f =>
        File.AppendAllText(f.Transcript, ForkTurn("fixture", "moved-cwd", "user")
            .Replace("\"message\":", "\"cwd\":\"different-project\",\"message\":")));

    [Fact]
    public void Kaydet_ImlecVarkenKuyrukDosyaninYuzdeBesindenAziniOkur()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, ResultRecord("bulk", new string('x', 4 * 1024 * 1024)), Fixture.Utf8);
        var cursorOffset = new FileInfo(f.Transcript).Length;
        f.Claude(2);
        f.Daily(Cursor("m-0", 0, cursorOffset));
        Assert.Equal((0, "", ""), f.Run());
        var row = Assert.Single(Rows(f));
        Assert.Equal("bayt", row.GetProperty("yol").GetString());
        Assert.InRange(row.GetProperty("okunan_kb").GetInt32(), 1, (int)(new FileInfo(f.Transcript).Length / 1024 * 0.05));
        Assert.Contains("turns:1-1", f.Read());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("legacy")]
    [InlineData("missing")]
    [InlineData("overflow")]
    public void Kaydet_GecerliSonImleciYoksaTamOkur(string anchor)
    {
        using var f = new Fixture();
        f.Claude(2);
        if (anchor == "legacy") f.Daily($"<!-- session:fixture ts:{Fixture.Now:O} turns:0-0 -->\n");
        if (anchor == "missing") f.Daily(Cursor("not-in-file", 0));
        if (anchor == "overflow") f.Daily(Cursor("m-0", 0).Replace("turns:0-0", "turns:0-999999999999999"));
        Assert.Equal((0, "", ""), f.Run());
        var row = Assert.Single(Rows(f));
        Assert.Equal("tam", row.GetProperty("yol").GetString());
        Assert.Equal((new FileInfo(f.Transcript).Length + 1023) / 1024, row.GetProperty("okunan_kb").GetInt32());
        Assert.Contains("[a m-1]", f.Read());
    }

    [Fact]
    public void Kaydet_SoruImlecOncesindeyseCevapOnekiYeterlidir()
    {
        using var f = new Fixture();
        f.AppendClaude("question", "assistant", new[] { new { type = "tool_use", id = "ask", name = "AskUserQuestion" } });
        f.AppendClaude("cursor", "assistant", "synthetic cursor");
        Assert.Equal(1, f.Write());
        File.AppendAllText(f.Transcript, ResultRecord("after-cursor-answer", "The user answered: synthetic answer", "ask"));
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal("bayt", Assert.Single(Rows(f)).GetProperty("yol").GetString());
        Assert.Matches(@"### \[u after-cursor-answer\] [^\r\n]+ · cevap", f.Read());
        Assert.Contains("turns:2-2", f.Read());
    }

    [Fact]
    public void Kaydet_CevapOnekiMetinBlokluCevabiKaydeder()
    {
        using var f = new Fixture();
        f.AppendClaude("question", "assistant", new[] { new { type = "tool_use", id = "ask", name = "AskUserQuestion" } });
        File.AppendAllText(f.Transcript, ResultRecord("blocks-answer", new[]
        {
            new { type = "text", text = "The user answered: synthetic first" }, new { type = "text", text = "synthetic second" }
        }, "ask"));
        Assert.Equal((0, "", ""), f.Run());
        Assert.Matches(@"### \[u blocks-answer\] [^\r\n]+ · cevap", f.Read());
        Assert.Contains("The user answered: synthetic first\nsynthetic second", f.Read());
    }

    [Fact]
    public void Kaydet_PlanOnayiSahipSozuOlarakYazilmaz()
    {
        using var f = new Fixture();
        f.Claude(1);
        File.AppendAllText(f.Transcript, ResultRecord("approval", "User has approved your plan. You can now start coding.\nsynthetic plan"));
        Assert.Equal((0, "", ""), f.Run());
        Assert.DoesNotContain("approval", f.Read());
        Assert.DoesNotContain("synthetic plan", f.Read());
    }

    [Fact]
    public void Kaydet_SiradanAracSonucuYazilmaz()
    {
        using var f = new Fixture();
        f.Claude(1);
        File.AppendAllText(f.Transcript, ResultRecord("ordinary", "synthetic tool output"));
        Assert.Equal((0, "", ""), f.Run());
        Assert.DoesNotContain("ordinary", f.Read());
        Assert.DoesNotContain("synthetic tool output", f.Read());
    }

    [Fact]
    public void Kaydet_CevaptakiSirNormalSahipKuraliylaMaskelenir()
    {
        using var f = new Fixture();
        var token = "ghp_" + new string('A', 40);
        File.WriteAllText(f.Transcript, ResultRecord("masked-answer", "The user answered: " + token), Fixture.Utf8);
        Assert.Equal((0, "", ""), f.Run());
        Assert.DoesNotContain(token, f.Read());
        Assert.Contains("ghp_****(maskelendi)", f.Read());
        Assert.Contains("[maske:tur]", f.Read());
        Assert.Matches(@"### \[u masked-answer\] [^\r\n]+ · cevap", f.Read());
    }

    [Fact]
    public void Kaydet_CevapMetniYirmiBinKarakterdeKesilir()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, ResultRecord("long-answer", "The user answered: " + new string('x', 20_005)), Fixture.Utf8);
        Assert.Equal((0, "", ""), f.Run());
        Assert.Contains("The user answered: " + new string('x', 19_981) + " (+24 kr)", f.Read());
        Assert.DoesNotContain(new string('x', 19_982), f.Read());
    }

    [Fact]
    public void Kaydet_YarimSonSatiriTamamlanincaSonrakiCagriYazar()
    {
        using var f = new Fixture();
        f.Claude(1);
        Assert.Equal(1, f.Write());
        var line = ForkTurn("fixture", "partial", "user", "synthetic unicode: çığ ö🙂");
        var split = line.IndexOf("synthetic", StringComparison.Ordinal) + 4;
        File.AppendAllText(f.Transcript, line[..split], Fixture.Utf8);
        Assert.Equal(0, f.Write());
        Assert.DoesNotContain("partial", f.Read());
        Assert.Contains("son:m-0", f.Read());
        File.AppendAllText(f.Transcript, line[split..], Fixture.Utf8);
        Assert.Equal(1, f.Write());
        Assert.Contains("[u partial]", f.Read());
        Assert.Contains("synthetic unicode: çığ ö🙂", f.Read());
        Assert.Equal(0, f.Write());
    }

    [Fact]
    public void Kaydet_ParentUuidImlecEslesmesiSayilmaz()
    {
        using var f = new Fixture();
        f.Claude(1);
        f.Daily(Cursor("m-0", 0));
        File.AppendAllText(f.Transcript, ForkTurn("fixture", "child", "user").Replace("{\"sessionId\"", "{\"parentUuid\":\"m-0\",\"sessionId\""));
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal("bayt", Assert.Single(Rows(f)).GetProperty("yol").GetString());
        Assert.Contains("[u child]", f.Read());
    }

    [Fact]
    public void Kaydet_YalnizParentUuidVarsaTamOkumayaDoner()
    {
        using var f = new Fixture();
        f.Daily(Cursor("removed", 0));
        File.WriteAllText(f.Transcript, ForkTurn("fixture", "child", "user")
            .Replace("{\"sessionId\"", "{\"parentUuid\":\"removed\",\"sessionId\""), Fixture.Utf8);
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal("tam", Assert.Single(Rows(f)).GetProperty("yol").GetString());
        Assert.Contains("[u child]", f.Read());
    }

    [Fact]
    public void Kaydet_CodexSonImleciOlsaDaTamOkur()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, CodexRollout("fixture", "cli"), Fixture.Utf8);
        f.Daily(Cursor("codex-u", 0));
        Assert.Equal((0, "", ""), f.Run());
        var row = Assert.Single(Rows(f));
        Assert.Equal("tam", row.GetProperty("yol").GetString());
        Assert.Equal((new FileInfo(f.Transcript).Length + 1023) / 1024, row.GetProperty("okunan_kb").GetInt32());
    }

    [Fact]
    public void Kaydet_KuyrukCokParcaliUtf8IcerikteTamOkumaylaBaytEsittir() => Equivalent(f =>
        f.AppendClaude("unicode-tail", "user", new string('ç', 130_000) + "🙂"));

    [Fact]
    public void Kaydet_ImlecIgnesiParcaSinirindaBolunseDeBulunur()
    {
        using var f = new Fixture();
        var cursor = ForkTurn("fixture", "needle-boundary", "user");
        var needleStart = cursor.IndexOf("\"uuid\"", StringComparison.Ordinal);
        var padding = 64 * 1024 - (cursor.Length - needleStart) + 7;
        File.WriteAllText(f.Transcript, new string(' ', 80_000) + "\n" + cursor + new string(' ', padding), Fixture.Utf8);
        f.Daily(Cursor("needle-boundary", 0, 80_001));
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal("bayt", Assert.Single(Rows(f)).GetProperty("yol").GetString());
        Assert.DoesNotContain("###", f.Read());
    }

    [Fact]
    public void Kaydet_TaraOkunanKbTumTranskriptOkumalarininToplamidir()
    {
        using var f = new Fixture();
        var root = SweepRoot(f);
        var first = ForkTurn("first", "u-first", "user", new string('x', 900));
        var second = ForkTurn("second", "u-second", "user", new string('x', 900));
        File.WriteAllText(Path.Combine(root, "first.jsonl"), first, Fixture.Utf8);
        File.WriteAllText(Path.Combine(root, "second.jsonl"), second, Fixture.Utf8);
        Assert.Equal(0, f.Run("--tetik", "tara").Exit);
        var row = Assert.Single(Rows(f));
        Assert.Equal((2 * (first.Length + second.Length) + 1023) / 1024, row.GetProperty("okunan_kb").GetInt32());
        Assert.False(row.TryGetProperty("yol", out _));
    }

    [Fact]
    public void Kaydet_HatadaDaOkunanKbLogaGirer()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, ForkTurn("fixture", "invalid id", "user", new string('x', 2000)), Fixture.Utf8);
        Assert.Contains("InvalidDataException", Message(f.Run()));
        var row = Assert.Single(Rows(f));
        Assert.Equal((new FileInfo(f.Transcript).Length + 1023) / 1024, row.GetProperty("okunan_kb").GetInt32());
    }

    private static void Equivalent(Action<Fixture> append, string prefix = "")
    {
        using var fast = new Fixture();
        using var full = new Fixture();
        File.WriteAllText(fast.Transcript, prefix, Fixture.Utf8);
        fast.Claude(2);
        Assert.Equal(1, fast.Write());
        append(fast);
        File.Copy(fast.Transcript, full.Transcript);
        full.Daily(fast.Read());
        var tailResult = Oom.Contracts.Kaydet.WriteResult(fast.Root, "fixture", fast.Transcript, "kapanis", Fixture.Now);
        var fullResult = Oom.Contracts.Kaydet.WriteResult(full.Root, "fixture", full.Transcript, "kapanis", Fixture.Now, useTail: false);
        Assert.Equal("bayt", tailResult.ReadPath);
        Assert.Equal("tam", fullResult.ReadPath);
        Assert.Equal(1, tailResult.Blocks);
        Assert.Equal(1, fullResult.Blocks);
        Assert.Equal(File.ReadAllBytes(full.DailyPath), File.ReadAllBytes(fast.DailyPath));
    }

    private static string Cursor(string uuid, int end, long byteOffset = 0) => $"<!-- session:fixture ts:{Fixture.Now:O} turns:0-{end} source:claude kayit:v1 son:{uuid} bayt:{byteOffset} -->\n";

    private static string ResultRecord(string uuid, object content, string toolId = "ordinary", bool rejected = false) =>
        JsonSerializer.Serialize(new
        {
            sessionId = "fixture", uuid, type = "user", timestamp = Fixture.Now.ToString("O"), cwd = "sentetik-proje",
            toolUseResult = rejected ? (object)"rejected" : new { },
            message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = toolId, content, is_error = rejected } } }
        }) + "\n";
}
