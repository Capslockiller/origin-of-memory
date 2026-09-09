// yazan: codex · gpt-5
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed class Save
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Guards guards;
    private readonly Flush flush;
    private readonly Func<string, bool> checkpointWriter;

    public Save(Guards? guards = null, Flush? flush = null, Func<string, bool>? checkpointWriter = null)
    {
        this.guards = guards ?? new Guards();
        this.flush = flush ?? new Flush();
        this.checkpointWriter = checkpointWriter ?? VerifiedMemoryWrite;
    }

    public CheckpointResult WriteCheckpoint(string text, IReadOnlyList<string> requiredFields)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(requiredFields);
        var missing = requiredFields.Where(field => string.IsNullOrWhiteSpace(field) ||
            !Regex.IsMatch(text, $@"(?im)^\s*{Regex.Escape(field)}\s*:\s*\S")).ToArray();
        if (missing.Length > 0)
            return new CheckpointResult(false, false, $"Kontrol noktası eksik alan içeriyor: {string.Join(", ", missing)}.");
        var gated = guards.Gate(text, Direction.In, ComponentKind.Flush);
        if (gated.Refused) return new CheckpointResult(false, false, "Kontrol noktası güvenlik kapısında reddedildi.");
        var verified = checkpointWriter(gated.Text);
        return new CheckpointResult(verified, verified, verified ? null : "Kontrol noktası yazıldıktan sonra doğrulanamadı.");
    }

    public string FormatDailyBlock(string text, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var gated = guards.Gate(text, Direction.In, ComponentKind.Flush);
        if (gated.Refused) throw new InvalidOperationException("Kayıt güvenlik kapısında reddedildi.");
        return $"### Kayıt ({now:HH:mm})\n{gated.Text.Trim()}\n";
    }

    public FlushResult SaveSessionJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        Session session;
        try
        {
            session = JsonSerializer.Deserialize<Session>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new FormatException("Dış oturum nesnesi boş.");
        }
        catch (JsonException exception) { throw new FormatException("Dış oturum JSON sözleşmesine uymuyor.", exception); }
        if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.Source) || session.Turns is null)
            throw new FormatException("Dış oturum id, source ve turns alanlarını içermelidir.");

        var safeTurns = new List<Turn>();
        foreach (var turn in session.Turns)
        {
            var gated = guards.Gate(turn.Text, Direction.In, ComponentKind.Flush);
            if (gated.Refused) return new FlushResult(FlushOutcome.Retry, -1, null, null, "Dış oturum güvenlik kapısında reddedildi.");
            safeTurns.Add(turn with { Text = gated.Text });
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"oom-save-{Guid.NewGuid():N}.jsonl");
        var lines = safeTurns.Select(turn => JsonSerializer.Serialize(new
        {
            session_id = session.Id,
            index = turn.Index,
            role = turn.Role,
            kind = turn.Kind,
            text = turn.Text,
            timestamp = turn.Timestamp
        }));
        File.WriteAllText(tempPath, string.Join('\n', lines), Utf8);
        // The external session enters the write path as itself: re-reading the hand-off file
        // would relabel every imported session "claude" and the daily block would claim an
        // import it never made (spec 6.10, gate 11-3).
        try { return flush.FlushSession(session with { Turns = safeTurns }, tempPath, FlushReason.Ingest); }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    private static bool VerifiedMemoryWrite(string text)
    {
        var bytes = Utf8.GetBytes(text);
        var copy = bytes.ToArray();
        return bytes.AsSpan().SequenceEqual(copy) && Utf8.GetString(copy) == text;
    }
}
