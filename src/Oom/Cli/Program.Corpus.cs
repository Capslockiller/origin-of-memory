using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static IReadOnlyList<string> Concepts(string vault)
    {
        var directory = Path.Combine(vault, "knowledge", "concepts");
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
    }

    private static IReadOnlyList<Note> Corpus(string vault)
    {
        var notes = new Notes();
        var parsed = new List<Note>();
        foreach (var path in Concepts(vault))
        {
            try
            {
                parsed.Add(notes.Parse(path, File.ReadAllText(path, Utf8)));
            }
            catch (FormatException)
            {
            }
        }

        return parsed;
    }
}
