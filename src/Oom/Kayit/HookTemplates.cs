namespace Oom.Contracts;

public sealed record HookRegistration(string Event, string Command, int TimeoutSeconds);

public static class HookTemplates
{
    // Double quotes make & ' ^ | < > ( ) ; and spaces literal under both bash -c and cmd /c.
    // These four stay live inside them: '"' ends the quotes, bash expands '$' and '`',
    // cmd expands '%'. No single form is safe for both shells, so such a path is refused.
    private static readonly char[] Unquotable = ['"', '$', '`', '%'];

    public static IReadOnlyList<HookRegistration> Build(string executablePath, string vault)
    {
        var prefix = Quote(executablePath) + " --vault " + Quote(vault);
        return
        [
            new("SessionStart", prefix + " baglam", 15),
            new("UserPromptSubmit", prefix + " kaydet --tetik mesaj", 5),
            new("SessionEnd", prefix + " kaydet --tetik kapanis", 15),
            new("PreCompact", prefix + " kaydet --tetik compact", 15)
        ];
    }

    public static char? UnquotableCharacter(string path) =>
        path.IndexOfAny(Unquotable) is var index and >= 0 ? path[index] : null;

    private static string Quote(string path) => $"\"{path.Replace('\\', '/')}\"";
}
