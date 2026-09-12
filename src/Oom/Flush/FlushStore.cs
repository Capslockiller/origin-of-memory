using System.Collections.Concurrent;

namespace Oom.Contracts;

internal sealed class SessionState
{
    internal string Id = string.Empty;
    internal string? TranscriptPath;
    internal Session? Session;
    internal int Cursor = -1;
    internal int Attempts;
    internal bool Parked;
    internal int Notifications;
    internal DateTimeOffset NextAt;
    internal string? LastError;
}

internal interface IFlushStore
{
    SessionState Get(string sessionId, string? transcriptPath);

    void Save(SessionState state);
}

internal sealed class MemoryFlushStore : IFlushStore
{
    internal static readonly MemoryFlushStore Instance = new();

    private static readonly ConcurrentDictionary<string, SessionState> Sessions = new(StringComparer.Ordinal);

    public SessionState Get(string sessionId, string? transcriptPath)
    {
        var state = Sessions.GetOrAdd(sessionId, id => new SessionState { Id = id, TranscriptPath = transcriptPath });
        if (!string.IsNullOrEmpty(transcriptPath))
            state.TranscriptPath = transcriptPath;

        return state;
    }

    public void Save(SessionState state)
    {
    }
}

internal sealed class DurableFlushStore(State state, int maxAttempts) : IFlushStore
{
    private readonly ConcurrentDictionary<string, SessionState> _live = new(StringComparer.Ordinal);

    public SessionState Get(string sessionId, string? transcriptPath)
    {
        var item = _live.GetOrAdd(sessionId, Load);
        if (!string.IsNullOrEmpty(transcriptPath))
            item.TranscriptPath = transcriptPath;

        return item;
    }

    private SessionState Load(string sessionId)
    {
        var row = state.ReadSessionRow(sessionId);
        var attempts = row?.Attempts ?? 0;
        return new SessionState
        {
            Id = sessionId,
            TranscriptPath = row?.TranscriptPath,
            Cursor = row?.Cursor ?? -1,
            Attempts = attempts,
            Parked = attempts >= maxAttempts,
            Notifications = attempts >= maxAttempts ? 1 : 0,
            NextAt = row?.NextAt ?? default,
            LastError = row?.LastError
        };
    }

    public void Save(SessionState item)
    {
        state.WriteSessionRow(item.Id, item.TranscriptPath, item.Cursor);
        if (item.Attempts <= 0)
            state.ClearRetry(item.Id);
        else
            state.WriteRetry(item.Id, item.Attempts, item.NextAt, item.LastError);
    }
}

internal static class RawChannel
{
    private static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);

    internal static void Keep(string key, string text) => Texts[key] = text;

    internal static string? Read(string key) => Texts.TryGetValue(key, out var text) ? text : null;
}
