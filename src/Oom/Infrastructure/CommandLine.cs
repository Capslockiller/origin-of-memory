namespace Oom.Contracts;

/// <summary>
/// The argument shape every command shares (spec 4, 5): the global <c>--vault &lt;path&gt;</c>
/// option and the other value-taking options may stand before the command token, so a command's
/// own positional arguments cannot be read by index.
/// </summary>
public static class CommandLine
{
    /// <summary>Options that consume the argument after them.</summary>
    public static readonly string[] ValueOptions =
    [
        "--vault", "--session", "--transcript", "--reason", "--query", "--top", "--batch", "--max",
        "--session-json", "--backend", "--transcripts", "--dailies", "--transcript-dir", "--daily-dir", "--out"
    ];

    /// <summary>Arguments that are neither an option nor an option's value, in order.</summary>
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

    /// <summary>The command token: the first positional argument, folded to lower case.</summary>
    public static string Command(IReadOnlyList<string> args) =>
        Positionals(args).FirstOrDefault()?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>The value of a named option, or <c>null</c> when it was not given.</summary>
    public static string? Value(IReadOnlyList<string> args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var index = 0; index < args.Count; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
                return args[index + 1];

        return null;
    }

    /// <summary>The <paramref name="index"/>th positional argument after the command token.</summary>
    public static string? Argument(IReadOnlyList<string> args, int index)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        // Y-102: `save` used to read args[1], which is the vault path the moment the global
        // option is written first — `--vault X save "..."` reported "eksik alan" on every run.
        return Positionals(args).Skip(index + 1).FirstOrDefault();
    }
}
