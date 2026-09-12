using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class SurecIsletmeScars
{
    [Fact(DisplayName = "Y-085 · Kontrol noktası doğrulanmadan kaydedildi raporlanamaz")]
    public void Y085_CheckpointMustBeCompleteAndVerified()
    {
        var save = new Save();
        var incomplete = save.WriteCheckpoint("Karar var ama devir yok", ["karar", "düzeltme", "devir"]);
        Assert.False(incomplete.Written);
        Assert.False(incomplete.Verified);
        var complete = save.WriteCheckpoint("karar: x\ndüzeltme: y\ndevir: z", ["karar", "düzeltme", "devir"]);
        Assert.True(complete.Written && complete.Verified);
    }

    [Fact(DisplayName = "Y-086 · Sürümdeki her yetenek iddiası test veya ölçüm kanıtı taşır")]
    public void Y086_ReleaseClaimsRequireEvidence()
    {
        var claims = new Dictionary<string, string> { ["tek exe"] = "publish-single-file", ["kurulum çalışıyor"] = "" };
        var findings = new Doctor().ValidateReleaseClaims(claims);
        Assert.Contains(findings, x => x.Code == "claim-without-evidence" && x.Key == "kurulum çalışıyor");
    }

    [Fact(DisplayName = "Y-087 · Bekleyici döngü N denemede kırmızı çıkar ve sonsuza dek beklemez")]
    public void Y087_WaitLoopIsBoundedAndSurfacesFailure()
    {
        var result = new Runner().WaitForOutcome("never-arrives", maxAttempts: 5);
        Assert.False(result.Completed);
        Assert.Equal(5, result.Attempts);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Error);
    }

    [Fact(DisplayName = "Y-088 · Doctor yeşil olmak için kapsama ve ret oranını ölçer")]
    public void Y088_DoctorHealthRequiresCoverageAndRejectionTargets()
    {
        var result = new Doctor().Check(ScarFixture.Now);
        Assert.True(result.Coverage >= 0.95);
        Assert.True(result.RejectionRate <= 0.03);
        Assert.DoesNotContain(result.Items, x => x.Code == "uncovered-session");
    }

    // yazan: codex · gpt-5
    [Fact(DisplayName = "Y-109 · Main son çare olarak istisnayı rc 1'e çevirir ve WER kutusunu kapatır")]
    public void Y109_MainCatchesUnhandledExceptionAndDisablesWerDialog()
    {
        var program = typeof(Save).Assembly.GetType("Oom.Program", throwOnError: true)!;
        var main = program.GetMethod("Main", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            binder: null, [typeof(string[]), typeof(Func<string[], int>)], modifiers: null);
        Assert.NotNull(main);

        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var result = (int)main.Invoke(null,
                [Array.Empty<string>(), new Func<string[], int>(_ => throw new InvalidOperationException("dispatch kırıldı"))])!;

            Assert.Equal(1, result);
            Assert.StartsWith("hata: InvalidOperationException: dispatch kırıldı", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var source = File.ReadAllText(Path.Combine(ScarFixture.RepositoryRoot(), "src", "Oom", "Program.cs"));
        Assert.Contains("SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);", source, StringComparison.Ordinal);
    }

}
