// yazan: codex · gpt-5
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Oom.Contracts;

public interface IIngestStateStore
{
    bool Contains(string source, string digest);
    void Complete(string source, string digest);
}

public sealed record IngestOutcome(IReadOnlyList<Session> Sessions, int Skipped); // Y-112: imported + digest-dedupe skips.

public sealed class Ingest
{
    private static readonly IIngestStateStore SharedState = new MemoryIngestStateStore();
    private readonly Action<TimeSpan> sleep;
    private readonly IIngestStateStore state;
    private readonly Func<Session, string, FlushResult> flushSession;

    public Ingest(Action<TimeSpan>? sleep = null, IIngestStateStore? state = null, Func<Session, string, FlushResult>? flushSession = null)
    {
        this.sleep = sleep ?? Thread.Sleep;
        this.state = state ?? SharedState;
        this.flushSession = flushSession ?? ((session, path) => new Flush().FlushSession(session.Id, path, FlushReason.Ingest));
    }

    public Session ParseClaude(string jsonl) => ClaudeParser.Parse(jsonl);

    public Session ParseCodex(string jsonl) => CodexParser.Parse(jsonl);

    public IReadOnlyList<Session> Run(string source, IReadOnlyList<string> files, int? max = null) =>
        Run(source, files, max, TimeSpan.Zero, CancellationToken.None);
    public IReadOnlyList<Session> Run(string source, IReadOnlyList<string> files, int? max, TimeSpan sleepBetween, CancellationToken cancellationToken) =>
        RunWithOutcome(source, files, max, sleepBetween, cancellationToken).Sessions;
    public IngestOutcome RunWithOutcome(string source, IReadOnlyList<string> files, int? max = null, TimeSpan sleepBetween = default, CancellationToken cancellationToken = default) // Y-112: Run's walk, also reports skips.
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!source.Equals("claude", StringComparison.OrdinalIgnoreCase) && !source.Equals("codex", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("İçe aktarım kaynağı claude veya codex olmalıdır.", nameof(source));
        if (max is < 0) throw new ArgumentOutOfRangeException(nameof(max), "Azami oturum sayısı negatif olamaz.");
        if (sleepBetween < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sleepBetween));

        var sessions = new List<Session>();
        var skipped = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (max.HasValue && sessions.Count >= max.Value) break;
            var text = File.Exists(file)
                ? File.ReadAllText(file, new UTF8Encoding(false, true))
                : file;
            var digest = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));
            if (state.Contains(source, digest)) { skipped++; continue; }
            var session = source.Equals("claude", StringComparison.OrdinalIgnoreCase) ? ParseClaude(text) : ParseCodex(text);
            sessions.Add(session);
            var transcriptPath = File.Exists(file) ? Path.GetFullPath(file) : WriteTemporaryTranscript(session);
            try
            {
                var result = flushSession(session, transcriptPath);
                if (result.Outcome is FlushOutcome.Ok or FlushOutcome.Empty or FlushOutcome.NoTurns or FlushOutcome.NoNewTurns)
                    state.Complete(source, digest);
            }
            finally
            {
                if (!File.Exists(file) && File.Exists(transcriptPath)) File.Delete(transcriptPath);
            }
            if (sleepBetween > TimeSpan.Zero && (!max.HasValue || sessions.Count < max.Value)) sleep(sleepBetween);
        }
        return new IngestOutcome(sessions, skipped);
    }

    /// <summary>Y-112: walks sweep's own roots filtered to <paramref name="source"/>, oldest <c>*.jsonl</c> first (archive backfill); a missing root is skipped silently.</summary>
    public IReadOnlyList<string> Discover(string source, IReadOnlyList<string> roots, int? max = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (max is < 0) throw new ArgumentOutOfRangeException(nameof(max), "Azami dosya sayısı negatif olamaz.");
        var files = roots.Where(root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) && SourceClassifier.FromPath(root).Equals(source, StringComparison.OrdinalIgnoreCase))
            .SelectMany(EnumerateJsonl).Where(path => !IsSubagentTranscript(path)).OrderBy(File.GetLastWriteTimeUtc);
        return (max.HasValue ? files.Take(max.Value) : files).ToArray();
    }

    private static IEnumerable<string> EnumerateJsonl(string root)
    {
        try { return Directory.EnumerateFiles(root, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }
    // A "subagents" sidecar is all isSidechain lines; ClaudeParser rejects the resulting turn-less session. Bench reuses this rule too (Y-116).
    internal static bool IsSubagentTranscript(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.Equals("subagents", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> ValidateSourceInventory(IReadOnlyList<string> runners, IReadOnlyList<string> exclusions)
    {
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(exclusions);
        var covered = exclusions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return runners.Where(runner => !string.IsNullOrWhiteSpace(runner) && !covered.Contains(runner))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string DailySuffix(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Kaynak boş olamaz.", nameof(source));
        return $", içe aktarım:{source.Trim().ToLowerInvariant()}";
    }

    private static string WriteTemporaryTranscript(Session session)
    {
        var path = Path.Combine(Path.GetTempPath(), $"oom-ingest-{Guid.NewGuid():N}.jsonl");
        var lines = session.Turns.Select(turn => System.Text.Json.JsonSerializer.Serialize(new { session_id = session.Id, turn.Index, turn.Role, turn.Kind, turn.Text, turn.Timestamp }));
        File.WriteAllText(path, string.Join('\n', lines), new UTF8Encoding(false));
        return path;
    }

    private sealed class MemoryIngestStateStore : IIngestStateStore
    {
        private readonly ConcurrentDictionary<string, byte> completed = new(StringComparer.OrdinalIgnoreCase);
        public bool Contains(string source, string digest) => completed.ContainsKey($"{source}:{digest}");
        public void Complete(string source, string digest) => completed.TryAdd($"{source}:{digest}", 0);
    }
}
