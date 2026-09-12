namespace Oom.Contracts;

public static class CommandLine
{
    public static readonly string[] ValueOptions =
    [
        "--vault", "--session", "--transcript", "--reason", "--query", "--top", "--batch", "--max",
        "--session-json", "--backend", "--transcripts", "--dailies", "--transcript-dir", "--daily-dir", "--out"
    ];

    public static IReadOnlyList<string> Positionals(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var positionals = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(args[index]);
                continue;
            }

            if (ValueOptions.Contains(args[index], StringComparer.OrdinalIgnoreCase))
                index++;
        }

        return positionals;
    }

    public static string Command(IReadOnlyList<string> args) =>
        Positionals(args).FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;

    public static string? Value(IReadOnlyList<string> args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var index = 0; index < args.Count; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                return args[index + 1];

        return null;
    }

    public static string? Argument(IReadOnlyList<string> args, int index)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return Positionals(args).Skip(index + 1).FirstOrDefault();
    }
}
