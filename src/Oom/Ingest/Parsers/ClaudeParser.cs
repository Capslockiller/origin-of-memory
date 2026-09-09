// yazan: codex · gpt-5
namespace Oom.Contracts;

/// <summary>
/// The archive backfill entry point for Claude Code transcripts (spec 6.10). The line format
/// itself lives in exactly one file, <see cref="ClaudeTranscript"/>, so when Claude Code moves
/// its shape only that file breaks (spec 1-9); this wrapper adds what <c>ingest</c> needs on
/// top of it: a session id and at least one turn are mandatory, and a line without a timestamp
/// gets a deterministic one so a replay of the same archive produces the same session.
/// </summary>
internal static class ClaudeParser
{
    private static readonly IClock Deterministic = new EpochClock();

    internal static Session Parse(string jsonl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonl);
        var read = ClaudeTranscript.Read(jsonl, Deterministic);
        var turns = read.Turns.Where(turn => turn.Kind == "text").OrderBy(turn => turn.Index).ThenBy(turn => turn.Timestamp).ToArray();
        if (string.IsNullOrWhiteSpace(read.SessionId) || turns.Length == 0)
            throw new FormatException("Claude transkripti oturum kimliği ve en az bir metin turu içermelidir.");

        return new Session(read.SessionId, "claude", turns, turns.Min(turn => turn.Timestamp));
    }

    /// <summary>A stamp-free line must not import differently on two days (Y-077, Y-078).</summary>
    private sealed class EpochClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
    }
}
