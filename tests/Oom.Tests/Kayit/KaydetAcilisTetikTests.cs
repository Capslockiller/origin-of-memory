using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Theory]
    [InlineData("acilis", false)]
    [InlineData("baglam", false)]
    [InlineData("tara", true)]
    public void Kaydet_ImlecOncesindekiSahipCevabiniYalnizTaraGeriDoldurur(string command, bool fillsHole)
    {
        using var f = new Fixture();
        SeedAnswerHole(f);
        PrepareBackfill(f);
        var before = f.Read();
        Assert.Contains("son:cursor", before);
        Assert.DoesNotContain("answer-hole", before);

        var result = command switch
        {
            "acilis" => Start(f),
            "baglam" => f.ProcessRun(["--vault", f.Root, "baglam"],
                JsonSerializer.Serialize(new { session_id = "current", transcript_path = Path.Combine(f.Root, "current.jsonl") })),
            _ => f.Run("--tetik", "tara")
        };

        Assert.Equal(0, result.Exit);
        Assert.Equal("", result.Error);
        var row = Assert.Single(Rows(f));
        Assert.Equal(command == "tara" ? "tara" : "acilis", row.GetProperty("tetik").GetString());
        Assert.Equal(1, row.GetProperty("aday").GetInt32());
        Assert.Equal(0, row.GetProperty("atlanan").GetInt32());
        Assert.Equal(fillsHole ? "yazdi" : "bos", row.GetProperty("sonuc").GetString());
        Assert.Equal(fillsHole ? 1 : 0, row.GetProperty("blok").GetInt32());
        var after = f.Read();
        Assert.Equal(fillsHole ? 1 : 0, Regex.Matches(after, @"^### \[u answer-hole\]", RegexOptions.Multiline).Count);
        if (fillsHole) Assert.Contains("synthetic missing answer", after);
        else Assert.Equal(before, after);
    }
}
