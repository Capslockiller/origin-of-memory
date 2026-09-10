namespace Oom.Contracts;

internal static class SourceClassifier // The claude/codex path rule (spec 6.3), shared by Sweep, SweepRun and Ingest.Discover (Y-112).
{
    public static string FromPath(string path) => path.Contains("codex", System.StringComparison.OrdinalIgnoreCase) ? "codex" : "claude";
}
