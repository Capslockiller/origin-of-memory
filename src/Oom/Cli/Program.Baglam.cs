using System.Text.Json;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunBaglam(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var textMode = args.Contains("--metin");
        var text = string.Empty;
        string? systemMessage = null;
        try
        {
            var hook = HookPayload.Read(ReadStandardInput());
            var id = Value(args, "--session") ?? hook.SessionId;
            var transcript = Value(args, "--transcript") ?? hook.TranscriptPath;
            var catchUpError = RunAcilis(vault, settings, now, id, transcript);
            var durum = Durum.Evaluate(vault, settings, now, id, transcript);
            text = Baglam.Build(vault, settings, now, durum);
            systemMessage = durum.IsRed && catchUpError is not null ? $"{durum.Line}; {catchUpError}"
                : durum.IsRed ? durum.Line : catchUpError;
        }
        catch (Exception error) { systemMessage = $"baglam: {KaydetLog.ErrorText(error, 200)}"; }

        if (textMode)
        {
            Console.Write(text.EndsWith('\n') || text.Length == 0 ? text : text + "\n");
            return 0;
        }
        var output = new { hookEventName = "SessionStart", additionalContext = text };
        Console.WriteLine(systemMessage is not null
            ? JsonSerializer.Serialize(new { systemMessage, hookSpecificOutput = output }, HookJsonOptions)
            : JsonSerializer.Serialize(new { hookSpecificOutput = output }, HookJsonOptions));
        return 0;
    }
}
