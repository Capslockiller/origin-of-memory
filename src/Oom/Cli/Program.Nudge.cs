using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunNudge(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        _ = vault;
        var hook = HookPayload.Read(ReadStandardInput());
        var session = Value(args, "--session") ?? hook.SessionId ?? "cli";
        using var state = OpenState();
        Console.WriteLine(Nudge.Envelope(Nudge.Lines(state, session, now, settings.NudgeEvery)));

        return 0;
    }
}
