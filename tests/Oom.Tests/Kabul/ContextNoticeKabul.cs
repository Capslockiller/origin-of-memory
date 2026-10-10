using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// L4-context-notice acceptance oracle (SPEC-3.1.0.md F5-5, NB-3, R5, R15, S5). Written
/// independently of the implementation by the lane's oracle author — the implementer never
/// sees this file's reasoning, only the assertions below. Drives a real oom.exe through
/// <see cref="KabulHarness"/> against small, synthetic, purpose-built vaults, following
/// DoctorKabul's precedent of seeding flush_log/coverage/daily_ingest rows directly through
/// <see cref="State"/> (the same public API the product code itself writes through), and
/// FlushKabul's precedent of driving a guard-caught 'refused' flush through a
/// <see cref="FakeClaude"/> shim on PATH.
///
/// MEASURED against this tree (current, pre-fix code) before writing these assertions: none
/// of Program.Context.cs/Context.cs read MemoryFreshness or a quarantine list at all today —
/// `oom context`'s [Bildirim] only ever carries the yansıma-borcu line (S5) and the
/// context.capChars-ignored notice (B5). Every assertion below is therefore RED here for
/// that reason (an absent notice), not from a compile error. Also measured RED against
/// eski-exe/oom.exe (OOM_KABUL_EXE): eski-exe has no numeric `coverage` table, no
/// `daily_ingest` digest bookkeeping and no red/ quarantine directory at all, so its
/// [Bildirim] never carries anything resembling these lines either.
///
/// Numbering below follows the lane brief's own 1-4 oracle list.
/// </summary>
public sealed class ContextNoticeKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;

    // Same reasoning as FlushKabul: pin turn timestamps to noon-ish LOCAL time on Today's
    // calendar date using the running machine's own UTC offset for that date, so a guard-
    // caught flush's own EventTime conversion can never roll the date across midnight
    // regardless of the test machine's timezone.
    private static readonly TimeSpan LocalOffsetOnToday =
        TimeZoneInfo.Local.GetUtcOffset(DateTime.SpecifyKind(Today.Date, DateTimeKind.Unspecified));

    // ------------------------------------------------------------------
    // #1 — F5-5: a state whose last (real) sweep coverage row is 14 days
    // old, with 20 pending dailies, makes `oom context`'s [Bildirim]
    // carry the exact line 'hafıza: son tarama 14 gün önce, 20 daily
    // derlenmedi'. eski-exe: the line is absent entirely (no such
    // mechanism exists there).
    // ------------------------------------------------------------------


    // ------------------------------------------------------------------
    // #2 — F5-5: a healthy state (last sweep 1 hour ago, 0 pending
    // dailies) never shows the freshness line at all.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "F5-5 #2 · sağlıklı durum (tarama 1 saat önce, 0 bekleyen) → [Bildirim]'de 'son tarama' satırı yok")]
    public void HealthyState_FreshnessNoticeIsAbsent()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root); // empty daily/ → 0 pending

        SeedState(harness, vault, state => state.RecordCoverage(Today.AddHours(-1), covered: 10, total: 10, windowDays: 7));

        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.Equal(0, run.ExitCode);

        var (_, text) = ContextJson(run);
        var bildirim = Section(text, "Bildirim");
        Assert.DoesNotContain("son tarama", bildirim, StringComparison.Ordinal);
        Assert.DoesNotContain("daily derlenmedi", bildirim, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // #3 — NB-3: after a flush is quarantined as 'refused' (a guard-
    // caught directive in the model's summary, same fixture shape as
    // FlushKabul's own S2 #1), [Bildirim] names both the session and the
    // red/ file that was actually written. eski-exe: nothing (no red/
    // directory, no such notice).
    // ------------------------------------------------------------------


    // ------------------------------------------------------------------
    // #4 — R15/driver ruling: with BOTH the freshness notice and a
    // quarantine notice present at once, the real-size fixture still
    // gives chars <= 8000 / UTF-8 bytes <= 8500, and Kurallar.md's full
    // text still appears in the context output verbatim (the notices
    // must come out of the same budget those never-cut sections already
    // live in, never break it).
    // ------------------------------------------------------------------


    // ==================== helpers ====================

    private static (int Chars, string Text) ContextJson(KabulResult run)
    {
        var root = JsonDocument.Parse(run.Stdout).RootElement;
        return (root.GetProperty("chars").GetInt32(), root.GetProperty("text").GetString()!);
    }

    /// <summary>Same framing every Context.cs section shares (ContextKabul.Section):
    /// everything between a "[label]\n" marker and the next line starting with '[' (or
    /// the closing "Hafıza protokolü zorunludur." sentence, or end of text).</summary>
    private static string Section(string text, string label)
    {
        var marker = $"[{label}]\n";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var bodyStart = start + marker.Length;
        var nextBracket = text.IndexOf("\n[", bodyStart, StringComparison.Ordinal);
        var closing = text.IndexOf("\nHafıza protokolü zorunludur.", bodyStart, StringComparison.Ordinal);
        var candidates = new[] { nextBracket, closing }.Where(index => index >= 0).ToArray();
        var end = candidates.Length == 0 ? text.Length : candidates.Min() + 1;
        return text[bodyStart..end].TrimEnd('\n');
    }

    private static string BuildMinimalVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static void WriteDaily(string vault, string name) =>
        File.WriteAllText(Path.Combine(vault, "daily", name),
            $"# {Path.GetFileNameWithoutExtension(name)}\n\n- sentetik günlük satırı, kabul harness fixture'ı, gerçek içerik değil.\n", Utf8);

    private static void SeedState(KabulHarness harness, string vault, Action<State> seed)
    {
        var dbPath = StateDbPath(harness, vault);
        using (var state = new State(null, null, dbPath))
            seed(state);
        SqliteConnection.ClearAllPools();
    }

    private static string StateDbPath(KabulHarness harness, string vault)
    {
        var canonical = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);
        return Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical), VaultIdentity.DatabaseName);
    }

    private static string RedDirectory(KabulHarness harness, string vault) =>
        Path.Combine(Path.GetDirectoryName(StateDbPath(harness, vault))!, "red");

    private static IReadOnlyList<string> ReadFlushOutcomes(KabulHarness harness, string vault, string sessionId)
    {
        using var state = new State(null, null, StateDbPath(harness, vault), StateAccess.ReadOnly);
        try
        {
            return state.ReadColumn($"SELECT outcome FROM flush_log WHERE session_id = '{sessionId}' ORDER BY rowid");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string WriteTranscript(KabulHarness harness, string sessionId, int turnCount)
    {
        var directory = harness.NewScratchDirectory("transcripts-" + sessionId);
        var path = Path.Combine(directory, "session.jsonl");

        var lines = new List<string>();
        for (var i = 0; i < turnCount; i++)
        {
            var role = i % 2 == 0 ? "user" : "assistant";
            var timestamp = new DateTimeOffset(Today.Year, Today.Month, Today.Day, 9, i, 0, LocalOffsetOnToday);
            var text = $"Sentetik tur {i}: kabul harness fixture metni, gerçek içerik değildir (oturum {sessionId}).";
            lines.Add(JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                index = i,
                role,
                kind = "text",
                text,
                timestamp = timestamp.ToString("O")
            }));
        }

        File.WriteAllText(path, string.Join('\n', lines), Utf8);
        return path;
    }
}
