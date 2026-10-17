using Oom.Contracts;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Theory]
    [InlineData("kapanis")]
    [InlineData("mesaj")]
    public void Kaydet_UzaktaTekrarlananImlecAradakiYeniTuruKaybetmez(string trigger)
    {
        using var f = new Fixture();
        f.Claude(1);
        Assert.Equal(1, f.Write());
        AppendReplay(f);
        Assert.Equal((0, "", ""), f.Run("--tetik", trigger));
        Assert.True(f.Read().Contains("[a intervening]"),
            $"ACC-B1 expected [a intervening]; actual absent ({trigger})");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Kaydet_YazisizSoruReddiSoruImlecinIkiYanindaykenDeSahipSozuOlmaz(bool questionBeforeCursor)
    {
        using var f = new Fixture();
        f.Claude(1);
        if (questionBeforeCursor) AppendQuestion(f);
        f.AppendClaude("cursor", "assistant", "synthetic cursor");
        Assert.Equal(1, f.Write());
        if (!questionBeforeCursor) AppendQuestion(f);
        File.AppendAllText(f.Transcript, ResultRecord("declined", "The user doesn't want to proceed with this tool use.", "ask", rejected: true));
        f.AppendClaude("next", "assistant", "synthetic next turn");
        var result = Oom.Contracts.Kaydet.WriteResult(f.Root, "fixture", f.Transcript, "kapanis", Fixture.Now, useTail: false);
        Assert.Equal(1, result.Blocks);
        Assert.True(!f.Read().Contains("[u declined]"),
            $"ACC-m2 expected no [u declined]; actual owner turn (questionBeforeCursor={questionBeforeCursor})");
    }

    private static void AppendQuestion(Fixture f) => f.AppendClaude("question", "assistant",
        new[] { new { type = "tool_use", id = "ask", name = "AskUserQuestion" } });

    private static void AppendReplay(Fixture f)
    {
        f.AppendClaude("intervening", "assistant", "synthetic new turn");
        for (var i = 0; i < 9; i++) f.AppendClaude("new-" + i, "user", "synthetic pending turn");
        File.AppendAllText(f.Transcript, ResultRecord("padding", new string('x', 128 * 1024)));
        f.AppendClaude("m-0", "user", "synthetic repeated UUID");
    }
}
