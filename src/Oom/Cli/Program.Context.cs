using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    // R34: hook stdout is consumed by another process, not a browser, so the escaped
    // \uXXXX form JsonSerializer uses by default (6 bytes per non-ASCII character, e.g.
    // every Turkish ş/ı/ğ/ü/ö/ç) buys nothing and can multiply an already budget-sized
    // context several times over. Raw UTF-8 output keeps hook stdout close to the
    // context's own byte budget.
    private static readonly JsonSerializerOptions HookJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static int Announce(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var hook = HookPayload.Read(ReadStandardInput());
        if (Math.Max(settings.Extensions.Count, ExtensionEntries(vault)) is > 0 and var extensions)
            Console.Error.WriteLine($"context: oom.json extensions[].contextLine artık çalıştırılmıyor — {extensions} uzantı yok sayıldı");

        // S5: only the session-start hook consumes the reflection debt; a manual run reads it.
        State? state = null;
        ContextOptions options;
        try
        {
            state = hook.IsHook ? OpenStateForUpdate() : OpenStateForReading();
            var quarantine = QuarantineNotifications(state);
            var freshness = ReadFreshness(vault, state, now);
            var lastSessionEnd = state?.LastSessionEnd();
            var executable = Executable();
            // R34: the reflection debt is taken LAST, only once every other read that could
            // still throw (freshness, quarantine) has already succeeded. TakeReflectionDebt
            // both reads AND deletes the health row in one call for a hook (ReadWrite)
            // state, so calling it any earlier would delete the row before an exception
            // from one of those later reads could propagate — losing the debt for good on a
            // run whose crashed output never showed it to anyone (B2).
            options = settings.Context with
            {
                LastSessionEnd = lastSessionEnd,
                Executable = executable,
                Freshness = freshness,
                QuarantineNotifications = quarantine.Notices,
                QuarantineTotal = quarantine.Total,
                PendingNotification = state?.TakeReflectionDebt()
            };
        }
        catch (Exception error) when (error is SqliteException or StateSchemaException)
        {
            state?.Dispose();
            state = null;
            const string notice = "state.db okunamadı; vault bağlamı kullanıldı — oom doctor çalıştırın";
            Console.Error.WriteLine($"context: {notice} — {error.Message}");
            options = settings.Context with
            {
                PendingNotification = notice,
                Executable = Executable()
            };
        }

        using (state)
        {
            var result = new Context(options).Build(vault, now);

            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(new { schema_version = 1, sections = result.Sections, chars = result.Text.Length, text = result.Text }));
            else if (hook.IsHook)
                Console.WriteLine(JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = result.Text } }, HookJsonOptions));
            else
                Console.WriteLine(result.Text);
        }

        return 0;
    }

    /// Blocking review finding (schema/degrade): MemoryFreshness.Read walks
    /// CompileQueue.Pending's daily_ingest query, whose columns (e.g. `digest`) a state.db
    /// predating this wave's schema does not have; a manual run opens that database
    /// read-only (OpenStateForReading → StateAccess.ReadOnly, Program.Shared.cs) and cannot
    /// migrate it in place the way the read-write path can, so it would otherwise fail
    /// outright and `oom context` would terminate with a raw SQLite error instead of
    /// emitting context. The real schema-compatibility fix (making CompileQueue's queries
    /// probe for legacy columns) belongs in src/Oom/Compile/CompileQueue.cs and
    /// src/Oom/State/State.cs — outside this lane's owned files — so this catch is a
    /// scoped mitigation: it keeps `oom context` degrading to "freshness unknown" instead
    /// of crashing, for both the manual and hook paths, without touching those files.
    ///
    /// B2/R34: CompileQueue.Pending also reads daily/*.md and knowledge/log.md straight off
    /// disk (IsPending, LoggedDays); a vault file held open elsewhere throws IOException (or,
    /// on a locked-down ACL, UnauthorizedAccessException), not SqliteException — those are
    /// now degraded the same way instead of crashing the whole session-start context.
    private static MemoryFreshness? ReadFreshness(string vault, State? state, DateTimeOffset now)
    {
        try
        {
            return MemoryFreshness.Read(vault, state, now);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"context: hafıza tazeliği okunamadı, bildirim atlanıyor — {error.Message}");
            return null;
        }
    }

    /// NB-3/driver ruling: newest quarantine first (Context.Build itself caps the list to
    /// the top 3 and adds a "… ve N karantina daha" line — see QuarantineLines in
    /// Context.cs); each line names the session and the red/ FILE NAME only, never the
    /// absolute path State.RecordQuarantine wrote into `detail`.
    ///
    /// Should-finding (review, applied): only the newest Context.QuarantineNoticeCap rows'
    /// full text is fetched from state.db — Context.Build never shows more than that many
    /// lines anyway — plus a bounded COUNT(*) for the true total, so a vault with a long
    /// quarantine history does not make every `oom context` load every row's (potentially
    /// long, attacker-influenced — S2/NB-3) `detail` text into memory just to render 3
    /// lines and a count.
    private static (IReadOnlyList<string> Notices, int Total) QuarantineNotifications(State? state)
    {
        // Minor (review): a state.db predating the `health` table (or one where it was
        // never written, e.g. legacy) made this SQL throw SqliteException, which the
        // outer catch turned into "state.db okunamadı; ... oom doctor çalıştırın" — even
        // though nothing about the database was actually unreadable and `oom doctor`
        // itself tolerates the very same absence (R24(b) legacy tolerance). HasColumn
        // already checks table existence first, the same public API State exposes for
        // every other legacy-schema probe.
        if (state is null || !state.HasColumn("health", "component"))
            return ([], 0);

        var rows = state.ReadNamedColumn(
            $"SELECT key, detail FROM health WHERE component = 'flush' AND code = '{State.QuarantineCode}' " +
            $"ORDER BY rowid DESC LIMIT {Context.QuarantineNoticeCap}");
        var total = (int)state.Scalar($"SELECT COUNT(*) FROM health WHERE component = 'flush' AND code = '{State.QuarantineCode}'");
        var notices = rows.Select(row => $"hafıza: karantinaya alınan özet — oturum {row.Name} · {QuarantineFileName(row.Value)}").ToArray();
        return (notices, total);
    }

    /// State.RecordQuarantine writes detail as "özet karantinaya alındı (<reason>): <path>".
    /// <reason> itself can contain a colon (e.g. "yönerge: <findings>"), so the split point
    /// is the literal "): " that always closes the reason — never the first or last colon
    /// in the string — and only the file name of what follows it is kept.
    private static string QuarantineFileName(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return string.Empty;

        var marker = detail.LastIndexOf("): ", StringComparison.Ordinal);
        var path = marker >= 0 ? detail[(marker + 3)..] : detail;
        return Path.GetFileName(path.Trim());
    }

    /// S6: warn whenever oom.json carries a non-empty 'extensions' key, whatever shape its
    /// entries have (settings drop entries without a name, so they cannot tell).
    private static int ExtensionEntries(string vault)
    {
        var path = Path.Combine(vault, ".oom", "oom.json");
        try
        {
            if (!File.Exists(path))
                return 0;

            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty("extensions", out var extensions))
                return 0;

            return extensions.ValueKind switch
            {
                JsonValueKind.Array => extensions.GetArrayLength(),
                JsonValueKind.Null => 0,
                _ => 1
            };
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("context: oom.json okunamadı, extensions denetlenemedi");
            return 0;
        }
    }
}
