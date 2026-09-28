using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom;

internal static class CompileQueue
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex LoggedDay = new(@"^##.*compile \| (?<name>\S+\.md)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>The exact-content digest State.WriteDailyIngest stores and Pending compares
    /// against (should-finding, review): lower-case hex SHA-256 of the daily file's UTF-8
    /// text, byte for byte — the same bytes File.ReadAllText/WriteAllText round-trip, so a
    /// digest computed at write time and one recomputed later from disk always agree unless
    /// the file's content genuinely changed. R24(a): the value carries the
    /// <see cref="DigestPrefix"/> so it can be told apart from the unprefixed 64-hex digests
    /// an older algorithm left in legacy state.db files.</summary>
    internal static string ContentDigest(string dailyText)
        => DigestPrefix + Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(dailyText))).ToLowerInvariant();

    internal const string DigestPrefix = "v2:";

    // The real time of the newest compile that published notes; null when there is none.
    //
    // Should-finding (review, applied): a malformed daily_ingest.ts used to be silently
    // dropped (TryParse failure -> null -> Max() over IEnumerable<DateTimeOffset?> just
    // treats null as the smallest possible value), so a single corrupt row could make this
    // report an OLDER time than the real last compile — or "hiç" (never) if every row
    // happened to be corrupt — while looking exactly like an honest answer, contrary to
    // R12 and the fail-loud rule. Every malformed timestamp is now ALSO recorded as a
    // health finding (surfaced by `oom doctor`, F5) instead of disappearing without a
    // trace; the best still-parseable timestamp is still returned rather than refusing to
    // answer at all — a doctor-visible warning next to an otherwise-honest reduced answer
    // beats withholding a real last-compile time entirely over one corrupt row. On a
    // read-only command the finding stays in HealthLedger's memory (R24(b)), which the same
    // process's `oom doctor` still shows.
    internal static DateTimeOffset? LastCompile(State? state, DateTimeOffset? observedAt = null)
    {
        // The time of the newest compile that PUBLISHED notes, kept separately in
        // `last_success_ts` so a later retry/rejected/quarantined/fail:rebuild attempt on the
        // same day (which overwrites `ts` with the failed attempt's time) can no longer erase
        // the real last-compile answer and make it read "hiç" (never).
        // `ts` for the 'ingested'/'partial' rows is still folded in, so a state.db written
        // before `last_success_ts` existed (legacy) keeps reporting its real history.
        var stamps = state?.ReadLastCompileStamps() ?? [];
        var parsed = new List<DateTimeOffset>();
        foreach (var stamp in stamps)
        {
            if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            {
                parsed.Add(value);
                continue;
            }
            HealthLedger.Record(new HealthItem("compile", HealthLevel.Error, "bozuk-zaman-damgasi", "daily_ingest.ts",
                $"daily_ingest.ts ayrıştırılamadı, son derleme hesabından düşürüldü: '{stamp}'"), observedAt ?? DateTimeOffset.Now);
        }
        return parsed.Count == 0 ? null : parsed.Max();
    }

    // Pending = daily/*.md with NO daily_ingest row that says the day's CURRENT CONTENT is
    // dealt with, newest first (daily names are ISO dates). A day with no state row at all
    // falls back to the knowledge/log.md convention (B8); a day WITH a row is decided by that
    // row alone. 'low-confidence' counts as done (should-finding): that status is a permanent
    // property of the day's own content (IsPromotable never changes its mind for the same
    // text), so leaving it out of "done" would mean the same day is resent to the model on
    // every single run forever, newest-first, permanently starving older pending days behind
    // maxDailiesPerRun. 'retry'/'rejected'/'quarantined'/'fail:rebuild' are left OUT of "done"
    // on purpose: those are one-off runner/guard/rebuild outcomes that may succeed on a later
    // attempt, so the day must stay pending and be retried.
    //
    // Should-finding (review, now applied): a "done" status used to be permanent no matter
    // what the file on disk said, so a daily a later flush appended to after it was
    // compiled/skipped was never reconsidered. A cruder fix tried earlier — excluding
    // "today"'s daily from Pending outright — was reverted: this file's own Kabul tests
    // write and compile a `daily/<fakeNow's date>.md` in the SAME run, so excluding "today"
    // broke F3-1/F3-5/F6-5 and others outright. The fix below instead compares each "done"
    // day's CURRENT content digest (ContentDigest above) against the one
    // State.WriteDailyIngest recorded for it: unchanged digest stays done, a changed one
    // goes back to pending. A day with no recorded digest (a row written before this fix,
    // or by a caller — e.g. a test — that chose not to supply one) stays done, so this can
    // only make a REAL content change reappear; it never invents a false one.
    //
    // Defect 1: the row's STATUS now decides first. Previously only the DONE-status rows were
    // read, so a day whose row had become 'retry'/'rejected'/'quarantined'/'fail:rebuild'
    // looked like it had no row at all and fell through to log.md — which DID list it, from
    // the first (successful) compile — so an appended-to day that then failed to recompile
    // was dropped from Pending for good. Every row is read now, and any non-done status is
    // pending outright.
    internal static IReadOnlyList<string> Pending(string vault, State? state)
    {
        var directory = Path.Combine(vault, "daily");
        if (!Directory.Exists(directory))
            return [];

        var states = (state?.ReadDailyStates() ?? [])
            .GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var loggedDays = LoggedDays(vault).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .Where(path => IsPending(path, states, loggedDays))
            .OrderDescending(StringComparer.Ordinal)];
    }

    // ANY state row decides; knowledge/log.md is consulted only for a day state has never
    // seen at all. A real `oom compile` writes BOTH a daily_ingest row and a log.md entry for
    // the same day in the same breath (Compile.AppendLog), so log.md could not distinguish
    // "compiled and done" from "compiled, then appended to, then the recompile FAILED":
    // both leave the day in loggedDays. The old shape — which read only the DONE-status rows
    // and treated everything else as absent — dropped a day from Pending for good the moment
    // a retry/rejected/quarantined/fail:rebuild row replaced its 'ingested' row, which is
    // exactly the day a later run must retry.
    //
    // A non-done status therefore means pending outright. A done status stays done unless a
    // comparable digest says the file's CONTENT changed.
    //
    // R24(a): only a digest this version wrote (DigestPrefix) is comparable. A legacy digest
    // from an older algorithm never matches ContentDigest, so comparing it would re-queue
    // every day the old build already compiled; it counts as "no digest" instead.
    private static bool IsPending(string path, IReadOnlyDictionary<string, DailyState> states, IReadOnlySet<string> loggedDays)
    {
        var name = Path.GetFileName(path);
        if (!states.TryGetValue(name, out var row))
            return !loggedDays.Contains(name);

        // R24(a) again: a non-done row written by an older build (legacy digest) for a day
        // log.md records as compiled keeps the old meaning — done. Measured on the real 3.0.x
        // state: three 'rejected' legacy rows would otherwise re-queue days the old build
        // had deliberately given up on (doctor pending 19 against the derived 16).
        if (!Done.Contains(row.Status))
            return !(row.Digest is { Length: > 0 } legacy
                && !legacy.StartsWith(DigestPrefix, StringComparison.Ordinal)
                && loggedDays.Contains(name));

        return row.Digest is { } recorded
            && recorded.StartsWith(DigestPrefix, StringComparison.Ordinal)
            && !string.Equals(recorded, ContentDigest(ReadShared(path)), StringComparison.Ordinal);
    }

    /// <summary>R31/R34: a plain File.ReadAllText opens with the default FileShare.Read,
    /// which is NOT compatible with a real writer's own (Access=Write, Share=Read) handle
    /// — e.g. Save.AppendToDaily's File.AppendAllText, or a flush/compile write elsewhere —
    /// so it throws a sharing-violation IOException the moment a daily/log.md is held open
    /// for append, crashing a read-only command (`oom context`, `compile --dry-run`) that
    /// merely wants to read the CURRENT bytes. Reading with FileShare.ReadWrite|Delete
    /// (the same sharing Context.cs's own companion-file reader already uses) tolerates
    /// that concurrent writer instead.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // The statuses that mean "this day's current content has been dealt with". 'retry',
    // 'rejected', 'quarantined' and 'fail:rebuild' are deliberately absent: those are
    // one-off runner/guard/rebuild outcomes that may succeed on a later attempt, so the day
    // must stay pending and be retried.
    private static readonly HashSet<string> Done = new(StringComparer.Ordinal)
    {
        "ingested", "adopted", "parked", "partial", "low-confidence"
    };

    // Counts only ACTUAL rejections (SPEC F3-2: "3. redde 'parked'"), never retry,
    // quarantined, low-confidence or fail:rebuild attempts — those are not rejections and
    // must not push a day toward parking (should-finding: the old Attempts() read
    // MAX(attempts), which counted every status, so two stalled 'retry' runs plus one
    // real rejection parked a day meant for its third rejection). See
    // State.WriteDailyIngest for where the `rejections` column is maintained.
    internal static int Rejections(State? state, string name)
        => state is null
            // Defect 4: a legacy daily_ingest has no `rejections` column; treat the absent
            // counter as 0 rather than failing the read-only command that asked for it.
            || !state.HasColumn("daily_ingest", "rejections")
            ? 0
            : (int)state.Scalar($"SELECT COALESCE(rejections, 0) FROM daily_ingest WHERE name = '{name.Replace("'", "''", StringComparison.Ordinal)}'");

    private static IEnumerable<string> LoggedDays(string vault)
    {
        var path = Path.Combine(vault, "knowledge", "log.md");
        return File.Exists(path)
            ? LoggedDay.Matches(ReadShared(path)).Select(match => match.Groups["name"].Value)
            : [];
    }
}
