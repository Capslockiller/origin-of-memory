using System.Text;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kaydet_BozukUtf8VeyaYarimKarakterSonSatirinGercekBaytiniKaydirmaz(bool invalidPrefix)
    {
        using var f = new Fixture();
        var line = Encoding.UTF8.GetBytes(ForkTurn("fixture", "complete", "user"));
        var partial = Encoding.UTF8.GetBytes(ForkTurn("fixture", "partial-unicode", "user", "🙂"));
        // Keep one byte of a multibyte character in the still-incomplete final record.
        partial = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(partial).Replace("\\uD83D\\uDE42", "🙂"));
        var split = partial.AsSpan().IndexOf(Encoding.UTF8.GetBytes("🙂")) + 1;
        Assert.True(split > 0);
        byte[] prefix = invalidPrefix ? [0xff, (byte)'\n'] : [];
        File.WriteAllBytes(f.Transcript, [.. prefix, .. line, .. partial.AsSpan(0, split).ToArray()]);
        Assert.Equal(1, f.Write());
        Assert.Contains($"son:complete bayt:{prefix.Length} -->", f.Read());
        Assert.DoesNotContain("[u partial-unicode]", f.Read());
        using (var stream = new FileStream(f.Transcript, FileMode.Append)) stream.Write(partial.AsSpan(split));
        Assert.Equal(1, f.Write());
        AssertAnchorOffset(f, "partial-unicode", "uuid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Kaydet_YeniCapaninBaytAlaniUtf8SonSatirininIlkBaytiniGosterir(bool tail)
    {
        using var f = new Fixture();
        // BOM, CRLF, malformed lines and multibyte characters all contribute bytes.
        File.WriteAllText(f.Transcript, "çığ🙂\r\n[]\r\n" + ForkTurn("fixture", "initial", "user"), new UTF8Encoding(true));
        Assert.Equal(1, f.Write());
        if (tail)
        {
            File.AppendAllText(f.Transcript, "\r\nbozuk ç🙂\r\n", Fixture.Utf8);
            f.AppendClaude("last", "assistant", "synthetic UTF-8 tail");
            var result = Oom.Contracts.Kaydet.WriteResult(f.Root, "fixture", f.Transcript, "kapanis", Fixture.Now);
            Assert.Equal("bayt", result.ReadPath);
            Assert.Equal(1, result.Blocks);
        }
        AssertAnchorOffset(f, tail ? "last" : "initial", "uuid");
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("rewritten")]
    [InlineData("past-eof")]
    [InlineData("unreadable")]
    [InlineData("negative")]
    public void Kaydet_GecersizBaytTamYolaDonerVeAyniBaytlariYazar(string damage)
    {
        using var fast = new Fixture();
        using var full = new Fixture();
        fast.Claude(2);
        Assert.Equal(1, fast.Write());
        fast.AppendClaude("next", "user", "synthetic new turn");
        var offset = damage switch { "wrong" => "0", "past-eof" => long.MaxValue.ToString(), "unreadable" => "invalid", "negative" => "-1", _ => null };
        if (offset is not null) fast.Daily(Regex.Replace(fast.Read(), @"bayt:\d+", "bayt:" + offset));
        if (damage == "rewritten") File.WriteAllText(fast.Transcript, "rewritten ç🙂\n" + File.ReadAllText(fast.Transcript), Fixture.Utf8);
        File.Copy(fast.Transcript, full.Transcript);
        full.Daily(fast.Read());
        Assert.Equal((0, "", ""), fast.Run());
        Assert.Equal("tam", Assert.Single(Rows(fast)).GetProperty("yol").GetString());
        Assert.Equal(1, Oom.Contracts.Kaydet.WriteResult(full.Root, "fixture", full.Transcript, "kapanis", Fixture.Now, useTail: false).Blocks);
        Assert.Contains("[u next]", fast.Read());
        Assert.Equal(File.ReadAllBytes(full.DailyPath), File.ReadAllBytes(fast.DailyPath));
    }

    [Fact]
    public void Kaydet_BaytsizEskiCapaTamYoluKullanir()
    {
        using var f = new Fixture();
        f.Claude(1);
        f.Daily(Cursor("m-0", 0).Replace(" bayt:0", ""));
        AppendReplay(f);
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal("tam", Assert.Single(Rows(f)).GetProperty("yol").GetString());
        Assert.Contains("[a intervening]", f.Read());
        AssertAnchorOffset(f, "new-8", "uuid");
    }

    [Fact]
    public void Kaydet_EnYeniSonCapasindaBaytYoksaEskiBaytKullanilmaz()
    {
        using var f = new Fixture();
        f.Claude(1);
        Assert.Equal(1, f.Write());
        f.AppendClaude("second", "assistant", "synthetic second turn");
        Assert.Equal(1, f.Write());
        var latest = Regex.Matches(f.Read(), @" bayt:\d+")[^1];
        f.Daily(f.Read().Remove(latest.Index, latest.Length));
        f.AppendClaude("third", "user", "synthetic third turn");
        Assert.Equal((0, "", ""), f.Run());
        Assert.Equal("tam", Assert.Single(Rows(f)).GetProperty("yol").GetString());
        Assert.Contains("turns:2-2", f.Read());
    }

    [Fact]
    public void Kaydet_BaytVeTamOkumaUzakImlecTekrarindaBaytEsittir() => Equivalent(AppendReplay);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Kaydet_BaytVeTamOkumaYazisizSoruReddindeBaytEsittir(bool questionBeforeCursor)
    {
        using var fast = new Fixture();
        using var full = new Fixture();
        fast.Claude(1);
        if (questionBeforeCursor) AppendQuestion(fast);
        fast.AppendClaude("cursor", "assistant", "synthetic cursor");
        Assert.Equal(1, fast.Write());
        if (!questionBeforeCursor) AppendQuestion(fast);
        File.AppendAllText(fast.Transcript, ResultRecord("declined", "The user doesn't want to proceed with this tool use.", "ask", rejected: true));
        fast.AppendClaude("next", "user", "synthetic next turn");
        File.Copy(fast.Transcript, full.Transcript);
        full.Daily(fast.Read());
        var result = Oom.Contracts.Kaydet.WriteResult(fast.Root, "fixture", fast.Transcript, "kapanis", Fixture.Now);
        Assert.Equal("bayt", result.ReadPath);
        Assert.Equal(1, result.Blocks);
        Assert.Equal(1, Oom.Contracts.Kaydet.WriteResult(full.Root, "fixture", full.Transcript, "kapanis", Fixture.Now, useTail: false).Blocks);
        Assert.DoesNotContain("[u declined]", fast.Read());
        Assert.Equal(File.ReadAllBytes(full.DailyPath), File.ReadAllBytes(fast.DailyPath));
    }

    [Fact]
    public void Kaydet_UuidOlmayanClaudeTurununDaSatirBaytiYazilir()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, """{"sessionId":"fixture","type":"user","message":{"content":"synthetic owner"}}""", Fixture.Utf8);
        Assert.Equal(1, f.Write());
        Assert.Contains(" bayt:0 -->", f.Read());
    }

    [Fact]
    public void Kaydet_CodexAcikKimligininBilinenSatirBaytiYazilir()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, """
            {"type":"session_meta","payload":{"id":"fixture"}}
            {"type":"response_item","payload":{"type":"message","id":"codex-owner","role":"user","content":[{"type":"input_text","text":"synthetic owner"}]}}
            """, Fixture.Utf8);
        Assert.Equal(1, f.Write());
        AssertAnchorOffset(f, "codex-owner", "id");
    }

    [Fact]
    public void Kaydet_CodexUretilenKimligindeBilinmeyenBaytAtlanir()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, """
            {"type":"session_meta","payload":{"id":"fixture"}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"synthetic owner"}]}}
            """, Fixture.Utf8);
        Assert.Equal(1, f.Write());
        Assert.DoesNotContain(" bayt:", f.Read());
    }

    private static void AssertAnchorOffset(Fixture f, string uuid, string field)
    {
        var anchor = Regex.Matches(f.Read(), @"son:(\S+) bayt:(\d+) -->")[^1];
        Assert.Equal(uuid, anchor.Groups[1].Value);
        var offset = int.Parse(anchor.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var bytes = File.ReadAllBytes(f.Transcript);
        Assert.InRange(offset, 0, bytes.Length - 1);
        Assert.True(offset == 0 || bytes[offset - 1] == '\n' || offset == 3 && bytes.AsSpan(0, 3).SequenceEqual(Encoding.UTF8.Preamble));
        var end = Array.IndexOf(bytes, (byte)'\n', offset);
        var line = Encoding.UTF8.GetString(bytes, offset, (end < 0 ? bytes.Length : end) - offset);
        Assert.Contains($"\"{field}\":\"{uuid}\"", line);
        Assert.Equal(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(line)), offset);
    }
}
