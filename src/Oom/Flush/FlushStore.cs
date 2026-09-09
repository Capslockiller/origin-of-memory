using System.Collections.Concurrent;

namespace Oom.Contracts;

/// <summary>
/// Per-session write-path state (spec 6.3, 8). The durable home is <c>state.db</c>
/// (<c>sessions</c>, <c>retry_queue</c>); the in-memory store below is what a component
/// constructed without a state store — every scar test — sees instead.
/// </summary>
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

/// <summary>Where one session's cursor and retry state live for the duration of a run.</summary>
internal interface IFlushStore
{
    SessionState Get(string sessionId, string? transcriptPath);

    /// <summary>Persists cursor and retry state; the in-memory store already holds the object.</summary>
    void Save(SessionState state);
}

/// <summary>
/// The process-wide store lane B used. It stays because a <c>Flush</c> built without a state
/// store must still see one cursor per session inside one process, which is what the scar
/// tests assert; a configured executable always gets <see cref="DurableFlushStore"/> instead.
/// </summary>
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
        // The dictionary already holds this instance; nothing outlives the process.
    }
}

/// <summary>
/// The real store: <c>sessions</c> carries the turn cursor, <c>retry_queue</c> the attempts.
/// Neither is the only home of anything — the cursor is also provable from the <c>daily/</c>
/// anchors — which is what lets <c>doctor --fix</c> rebuild a lost database (spec 6.8).
/// </summary>
internal sealed class DurableFlushStore(State state, int maxAttempts) : IFlushStore
{
    // One object per session for the life of the run: the parsed session a sweep hands in has
    // to survive until the write function reads it back, and reloading the row would lose it.
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

/// <summary>The lossless raw channel of Y-005; it is a hand-off inside one run, never a file.</summary>
internal static class RawChannel
{
    private static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);

    internal static void Keep(string key, string text) => Texts[key] = text;

    internal static string? Read(string key) => Texts.TryGetValue(key, out var text) ? text : null;
}
