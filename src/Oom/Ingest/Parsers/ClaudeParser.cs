namespace Oom.Contracts;

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

    private sealed class EpochClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
    }
}
