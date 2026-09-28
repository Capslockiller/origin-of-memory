using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class CliScars
{
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
