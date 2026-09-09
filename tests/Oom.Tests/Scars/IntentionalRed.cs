// yazan: codex · gpt-5
namespace Oom.Tests.Scars;

public sealed class IntentionalRed
{
    [Fact(DisplayName = "IntentionalRed · CI çıkış kodu tohumu")]
    [Trait("Category", "IntentionalRed")]
    public void FailsByDesign() => Assert.Fail("Bu test yalnız --filter IntentionalRed ile çalıştırılır.");
}
