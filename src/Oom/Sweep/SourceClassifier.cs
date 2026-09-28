namespace Oom.Contracts;

internal static class SourceClassifier
{
    public static bool IsSubagentTranscript(string path)
    {
        if (!path.EndsWith(".jsonl", System.StringComparison.OrdinalIgnoreCase))
            return false;

        if (Path.GetFileName(path).StartsWith("agent-", System.StringComparison.OrdinalIgnoreCase))
            return true;

        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory))
        {
            if (string.Equals(Path.GetFileName(directory), "subagents", System.StringComparison.OrdinalIgnoreCase))
                return true;

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    public static string FromPath(string path)
    {
        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Contains(".codex", StringComparer.OrdinalIgnoreCase) ? "codex" : "claude";
    }
}
