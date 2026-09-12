namespace Oom.Contracts;

internal static class SourceClassifier
{
    public static string FromPath(string path) => path.Contains("codex", System.StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";
}
