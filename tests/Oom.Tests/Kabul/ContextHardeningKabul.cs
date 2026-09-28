using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// L7a-context acceptance oracle (SPEC-3.1.0.md R15, R27, R31, R34; lane report
/// yamalar/w7/claude-inceleme-eb19f02.md, items B1, B2, M1, M2 and the minors named in the
/// lane brief). Process-boundary tests only, driven through <see cref="KabulHarness"/>
/// against the real exe, following ContextKabul.cs/ContextFinalKabul.cs/ContextNoticeKabul.cs's
/// own precedent for vault fixtures and direct <see cref="State"/> seeding.
///
/// MEASURED against this tree at eb19f02 (pre-fix) before writing these assertions — every
/// test below is RED there for the reason its own comment names (a missing cap, an
/// emptied-out section, or a crashing exit code), never a compile error.
/// </summary>
public sealed class ContextHardeningKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;

    // ------------------------------------------------------------------
    // B1 — R27/R34: Aktif Threadler is no longer exempt from the hard cap. 400 active
    // threads (no per-thread cap existed before this lane) push the raw context well past
    // 8000 chars / 8500 bytes; the fix cuts Aktif Threadler at a line boundary, same as
    // Kurallar/Düzeltmeler, only once the fixed sections still do not fit.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "B1a (R27/R34) · 400 aktif thread ile bağlam yine <= 8000 kar / <= 8500 bayt kalır")]
    public void ManyActiveThreads_StaysWithinHardBudget()
    {
        using var harness = new KabulHarness();
        var vault = WriteCompanionVault(harness.Root, new Dictionary<string, string>
        {
            ["Threads.md"] = ManyThreadsFixture(400),
        });

        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.Equal(0, run.ExitCode);
        var (chars, text) = ContextJson(run);

        Assert.True(chars <= 8_000, $"chars = {chars}, beklenen <= 8000; stderr={run.Stderr}");
        Assert.True(Utf8.GetByteCount(text) <= 8_500, $"UTF-8 bayt = {Utf8.GetByteCount(text)}, beklenen <= 8500");
    }

    // ------------------------------------------------------------------
    // B1 — R34: a single unbounded '### ' heading title (9000 'ş' characters, no status)
    // must itself be capped, the same way a status line already was, so it cannot alone
    // blow the budget; the review measured 9,742 chars / 18,785 bytes without this cap.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "B1b (R34) · Tek thread'in 9000 karakterlik başlığı 200 karaktere kısaltılır, bağlam <= 8000/8500 kalır")]
    public void HugeThreadTitle_IsCappedAndStaysWithinHardBudget()
    {
        using var harness = new KabulHarness();
        var hugeTitle = new string('ş', 9_000);
        var vault = WriteCompanionVault(harness.Root, new Dictionary<string, string>
        {
            ["Threads.md"] = $"# Threads\n\n## Active Threads\n\n### {hugeTitle}\n## Closed Threads\n\n",
        });

        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.Equal(0, run.ExitCode);
        var (chars, text) = ContextJson(run);

        Assert.True(chars <= 8_000, $"chars = {chars}, beklenen <= 8000; stderr={run.Stderr}");
        Assert.True(Utf8.GetByteCount(text) <= 8_500, $"UTF-8 bayt = {Utf8.GetByteCount(text)}, beklenen <= 8500");

        var capped = new string('ş', 200) + "… [kısaltıldı]";
        Assert.Contains(capped, text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('ş', 201), text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // M1 — Codex regression (Context.cs:151-152 at eb19f02): when the fixed sections leave
    // less room than the log's 800-char reserve, the [Bildirim] notice claims "Son Oturum
    // ve Son Journal düşürüldü" but the old formula (`reserved = room.Holds(reserve) ?
    // reserve : default; flexible = room - reserved`) handed Son Oturum/Son Journal the
    // FULL remaining room instead of dropping them, starving Bugünün Logu down to nothing.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M1 (Codex regresyonu) · Rezerv sığmayınca Son Oturum ve Son Journal gerçekten düşer, Bugünün Logu boş kalmaz")]
    public void ReserveDoesNotFit_SonOturumAndSonJournalActuallyDrop_LogGetsFullRoom()
    {
        using var harness = new KabulHarness();
        var vault = harness.NewScratchDirectory("vault-m1");
        var companion = Path.Combine(vault, KabulVaultBuilder.CompanionDir);
        Directory.CreateDirectory(companion);

        // Kurallar alone comfortably fits the 8000-char budget (~6800 chars, the report's
        // own measured size), but combined with a real Last-Session block, a Journal entry
        // and a long daily it leaves less than the 800-char log reserve — the exact
        // regression scenario the review measured.
        File.WriteAllText(Path.Combine(companion, "Kurallar.md"), "# Kurallar\n\n" + Filler(6_800), Utf8);
        File.WriteAllText(Path.Combine(companion, "Last-Session.md"),
            "## Session: 2026-09-27 09:00 — sentetik\n\n" + Filler(1_200), Utf8);
        File.WriteAllText(Path.Combine(companion, "Journal.md"),
            $"# Journal\n\n## {Today:yyyy-MM-dd} — sentetik\n" + Filler(500), Utf8);

        var dailyDir = Path.Combine(vault, "daily");
        Directory.CreateDirectory(dailyDir);
        var dailyBuilder = new StringBuilder($"# {Today:yyyy-MM-dd}\n\n");
        for (var i = 1; i <= 200; i++)
            dailyBuilder.Append($"- log satırı {i:D3}: sentetik günlük kaydı, M1 fixture'ı.\n");
        File.WriteAllText(Path.Combine(dailyDir, $"{Today:yyyy-MM-dd}.md"), dailyBuilder.ToString(), Utf8);

        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.Equal(0, run.ExitCode);
        var (_, text) = ContextJson(run);

        var bildirim = Section(text, "Bildirim");
        // Fixture sanity: the regression branch (room does not hold the reserve) really fired.
        Assert.Contains("Son Oturum ve Son Journal düşürüldü", bildirim, StringComparison.Ordinal);

        var sonOturum = Section(text, "Hafıza — Son Oturum");
        var sonJournal = Section(text, "Hafıza — Son Journal");
        var log = Section(text, "Bugünün Logu");

        // The regression's clearest signature (measured against this tree at eb19f02):
        // sonOturum=0 sonJournal=299 log=125 — Son Journal gets nearly its FULL 300-char
        // cap (the notice's "düşürüldü" is a lie) while Bugünün Logu starves to 125 chars,
        // far under its own 800-char reserve. Fixed (measured against this fix): sonOturum=0
        // sonJournal=0 log=448 — `room` itself is ~450-500 chars here (less than the 800
        // reserve, which is exactly why the notice fires), so the log correctly absorbs
        // all of it instead of the ~125 chars the regression left it.
        Assert.True(sonJournal.Length < 50,
            $"notice 'düşürüldü' diyor ama Son Journal {sonJournal.Length} karakter taşıyor (rezerv sığmayınca düşmeliydi)");
        Assert.True(sonOturum.Length < 50,
            $"notice 'düşürüldü' diyor ama Son Oturum {sonOturum.Length} karakter taşıyor (rezerv sığmayınca düşmeliydi)");
        Assert.True(log.Length >= 400,
            $"Bugünün Logu {log.Length} karakter; rezerv sığmayınca günlük TÜM kalan odayı almalı (regresyonda yalnız 125 karakterdi)");
    }

    // ------------------------------------------------------------------
    // M2 — Context.cs:143-144 at eb19f02: Kurallar is fit first with a plain, unreserved
    // FitLines call and can swallow every byte of the shared room, leaving Düzeltmeler's
    // own FitLines call nothing at all — not even its own truncation marker.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "M2 · Kurallar tüm odayı yutmaz, Düzeltmeler en azından kendi kısaltma imini taşır")]
    public void KurallarDoesNotStarveDuzeltmeler_DuzeltmelerKeepsItsMarker()
    {
        using var harness = new KabulHarness();
        var kurallar = "# Kurallar\n\n" + string.Concat(Enumerable.Range(1, 300)
            .Select(i => $"- Kural {i:D3}: MARKER-KURALLAR-{i:D3} sentetik madde metni, M2 fixture'ı için üretildi.\n"));
        var duzeltmeler = "# Düzeltmeler\n\n" + string.Concat(Enumerable.Range(1, 50)
            .Select(i => $"- Düzeltme {i:D3}: MARKER-DUZELTME-{i:D3} sentetik madde metni, M2 fixture'ı için üretildi.\n"));
        var vault = WriteCompanionVault(harness.Root, new Dictionary<string, string>
        {
            ["Kurallar.md"] = kurallar,
            ["Duzeltmeler.md"] = duzeltmeler,
        });

        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.Equal(0, run.ExitCode);
        var (chars, text) = ContextJson(run);
        Assert.True(chars <= 8_000, $"chars = {chars}, beklenen <= 8000 (fixture çok küçükse M2 hiç tetiklenmez)");

        var duzeltmelerSection = Section(text, "Hafıza — Düzeltmeler");
        Assert.True(duzeltmelerSection.Length > 0, "Düzeltmeler bölümü boş kaldı — Kurallar ortak odanın tamamını yuttu (M2)");
        Assert.Contains("… [kısaltıldı — tamamı:", duzeltmelerSection, StringComparison.Ordinal);
        Assert.Contains("Duzeltmeler.md", duzeltmelerSection, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // B2 (1) — R31/R34: knowledge/log.md held open for append (Access=Write, Share=Read,
    // the same sharing File.AppendAllText itself uses) and no state.db yet. CompileQueue.
    // LoggedDays used to read it with the default (incompatible) FileShare.Read, throwing
    // an IOException that propagated all the way out of Announce uncaught.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "B2-1 (R31/R34) · knowledge/log.md ekleme için açıkken (state.db henüz yok) kanca bağlamı çökmez")]
    public void LogHeldForAppend_FreshVault_HookContextDoesNotCrash()
    {
        using var harness = new KabulHarness();
        var vault = WriteMinimalVault(harness, "vault-b2-log-held");
        var knowledgeDir = Path.Combine(vault, "knowledge");
        Directory.CreateDirectory(knowledgeDir);
        var logPath = Path.Combine(knowledgeDir, "log.md");
        File.WriteAllText(logPath, "# Log\n", Utf8);

        using var writer = new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        var hookPayload = "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"b2-log-held-hook\"}";
        var run = harness.Run(vault, ["context"], standardInput: hookPayload, fakeNow: Today);

        Assert.True(run.ExitCode == 0, $"exit={run.ExitCode}; stdout={run.Stdout.Length} bayt; stderr={run.Stderr}");
        Assert.False(string.IsNullOrWhiteSpace(run.Stdout), "log.md tutulduğunda kanca bağlamı tamamen boş kaldı");
        var additionalContext = JsonDocument.Parse(run.Stdout).RootElement
            .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains("[Bildirim]", additionalContext, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // B2 (2) — R31/R34: today's daily held open for append while an 'ingested' v2-digest
    // row already exists for it — CompileQueue.IsPending must re-read the file to compare
    // digests, and used a plain File.ReadAllText that conflicts with the same held handle.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "B2-2 (R31/R34) · Bugünün daily'si eklemek için açıkken (ingested v2 digest satırı var) elle ve kanca bağlamı çökmez")]
    public void TodaysDailyHeldForAppend_WithIngestedDigestRow_ContextDoesNotCrash()
    {
        using var harness = new KabulHarness();
        var vault = WriteMinimalVault(harness, "vault-b2-daily-held");
        var dailyPath = Path.Combine(vault, "daily", $"{Today:yyyy-MM-dd}.md");
        var dailyContent = $"# {Today:yyyy-MM-dd}\n\n- log satırı 001: sentetik günlük kaydı, B2 fixture'ı.\n";
        File.WriteAllText(dailyPath, dailyContent, Utf8);

        SeedState(harness, vault, state =>
            state.WriteDailyIngest($"{Today:yyyy-MM-dd}.md", "ingested", Today, digest: CompileQueue.ContentDigest(dailyContent)));

        using var writer = new FileStream(dailyPath, FileMode.Open, FileAccess.Write, FileShare.Read);

        var manual = harness.Run(vault, ["context"], fakeNow: Today);
        Assert.True(manual.ExitCode == 0, $"elle: exit={manual.ExitCode}; stdout={manual.Stdout.Length} bayt; stderr={manual.Stderr}");
        Assert.False(string.IsNullOrWhiteSpace(manual.Stdout), "elle koşumda daily tutulduğunda bağlam boş kaldı");

        var hookPayload = "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"b2-daily-held-hook\"}";
        var hook = harness.Run(vault, ["context"], standardInput: hookPayload, fakeNow: Today);
        Assert.True(hook.ExitCode == 0, $"kanca: exit={hook.ExitCode}; stdout={hook.Stdout.Length} bayt; stderr={hook.Stderr}");
        Assert.False(string.IsNullOrWhiteSpace(hook.Stdout), "kanca koşumunda daily tutulduğunda bağlam boş kaldı");
    }

    // ------------------------------------------------------------------
    // B2 (3) — R34: TakeReflectionDebt reads AND deletes the yansıma-borcu health row in
    // one call for a hook (ReadWrite) state. At eb19f02 it ran FIRST, before Freshness —
    // so when the held log.md above made Freshness throw (uncaught: ReadFreshness only
    // caught SqliteException), the row was already gone from state.db even though the
    // crashed run printed nothing and the user never saw the debt notice. This proves the
    // fixed ordering: the same held-file run now succeeds outright and shows the debt in
    // that very run (never losing it to an earlier failed attempt), and a later run does
    // not show it again (consumed exactly once, same life cycle ContextKabul's Assertion10
    // already covers for the non-degraded path).
    // ------------------------------------------------------------------
    [Fact(DisplayName = "B2-3 (R34) · Bağlamı çökertecek koşulda bile yansıma borcu satırı kaybolmaz, o koşumda gösterilir ve bir daha tekrarlanmaz")]
    public void ReflectionDebtSurvivesAFailedContext_AndIsShownExactlyOnce()
    {
        using var harness = new KabulHarness();
        var vault = WriteMinimalVault(harness, "vault-b2-debt-survives");
        SeedReflectionDebt(harness, vault);

        var knowledgeDir = Path.Combine(vault, "knowledge");
        Directory.CreateDirectory(knowledgeDir);
        var logPath = Path.Combine(knowledgeDir, "log.md");
        File.WriteAllText(logPath, "# Log\n", Utf8);

        var hookPayload = "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"b2-debt-survives-hook\"}";
        string additionalContext;
        using (new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var hook = harness.Run(vault, ["context"], standardInput: hookPayload, fakeNow: Today);
            Assert.True(hook.ExitCode == 0, $"exit={hook.ExitCode}; stdout={hook.Stdout.Length} bayt; stderr={hook.Stderr}");
            additionalContext = JsonDocument.Parse(hook.Stdout).RootElement
                .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        }

        Assert.Contains(Nudge.ReflectionDebt, additionalContext, StringComparison.Ordinal);

        // Consumed exactly once by the run above — never silently deleted with nothing
        // ever shown for it, and never re-shown on a later run either.
        var again = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.Equal(0, again.ExitCode);
        Assert.DoesNotContain(Nudge.ReflectionDebt, ContextJson(again).Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Minor — Program.Context.cs:100-103 at eb19f02: QuarantineNotifications queries the
    // `health` table directly with no TableExists probe first (every other legacy-schema
    // read in this codebase checks). A state.db that legitimately has no `health` table
    // (R24(b) legacy tolerance — `oom doctor` already handles this cleanly, reporting 0
    // errors) made this throw SqliteException, caught by the OUTER catch, which then
    // wrongly told the user "state.db okunamadı ... oom doctor çalıştırın".
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Minor · health tablosu olmayan state.db, context'i yanlışlıkla 'state.db okunamadı' dedirtmez")]
    public void HealthTableMissing_ContextDoesNotFalselyReportStateUnreadable()
    {
        using var harness = new KabulHarness();
        var vault = WriteMinimalVault(harness, "vault-health-missing");
        var dbPath = StateDbPath(harness, vault);

        SeedState(harness, vault, _ => { }); // provisions the full current schema, health included
        DropHealthTable(dbPath);

        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.Equal(0, run.ExitCode);
        var (_, text) = ContextJson(run);

        Assert.DoesNotContain("state.db okunamadı", text, StringComparison.Ordinal);
        Assert.Contains("[Bildirim]", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Minor — Context.cs:81 at eb19f02: ReadLines retries three times on IOException then
    // rethrows uncaught, so a companion file held with FileShare.None (nothing else may
    // even read it) crashed the whole session-start context instead of dropping only that
    // one section.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Minor (R34) · Kurallar.md FileShare.None ile tutulduğunda yalnız o bölüm bildirimle düşer, bağlam çökmez")]
    public void CompanionFileHeldExclusively_DropsOnlyThatSectionWithNotice_NeverCrashes()
    {
        using var harness = new KabulHarness();
        var vault = WriteCompanionVault(harness.Root, new Dictionary<string, string>
        {
            ["Kurallar.md"] = "# Kurallar\n\n- CANARY-RULE sentetik madde.\n",
        });
        var kurallarPath = Path.Combine(vault, KabulVaultBuilder.CompanionDir, "Kurallar.md");

        using var exclusive = new FileStream(kurallarPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var run = harness.Run(vault, ["context", "--json"], fakeNow: Today);

        Assert.True(run.ExitCode == 0, $"exit={run.ExitCode}; stdout={run.Stdout.Length} bayt; stderr={run.Stderr}");
        Assert.False(string.IsNullOrWhiteSpace(run.Stdout), "Kurallar.md FileShare.None ile tutulduğunda bağlam tamamen boş kaldı");

        var (_, text) = ContextJson(run);
        Assert.Contains("[Bildirim]", text, StringComparison.Ordinal);
        Assert.Contains("[Hafıza — Kurallar]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY-RULE", text, StringComparison.Ordinal);
        var bildirim = Section(text, "Bildirim");
        Assert.Contains("Kurallar", bildirim, StringComparison.Ordinal);
        Assert.Contains("düşürüldü", bildirim, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Minor — CompileQueue.cs:140 at eb19f02: `compile --dry-run` is documented as
    // read-only (R24(b)) but crashed with an IOException the moment IsPending needed to
    // re-read a held daily to compare its content digest.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Minor (R31) · compile --dry-run, tutulan bir daily üzerinde çökmez")]
    public void CompileDryRun_OnAHeldDaily_DoesNotCrash()
    {
        using var harness = new KabulHarness();
        var vault = WriteMinimalVault(harness, "vault-compile-dryrun-held");
        var dailyPath = Path.Combine(vault, "daily", $"{Today:yyyy-MM-dd}.md");
        var dailyContent = $"# {Today:yyyy-MM-dd}\n\n- log satırı 001: sentetik günlük kaydı, dry-run fixture'ı.\n";
        File.WriteAllText(dailyPath, dailyContent, Utf8);

        SeedState(harness, vault, state =>
            state.WriteDailyIngest($"{Today:yyyy-MM-dd}.md", "ingested", Today, digest: CompileQueue.ContentDigest(dailyContent)));

        using var writer = new FileStream(dailyPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        var run = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Today);

        Assert.True(run.ExitCode == 0, $"exit={run.ExitCode}; stdout={run.Stdout.Length} bayt; stderr={run.Stderr}");
        Assert.Contains("derleme planı (kuru koşum)", run.Stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Minor — Program.Context.cs:52 at eb19f02: the hook JSON path used JsonSerializer's
    // default \uXXXX escaping (6 bytes per non-ASCII character) instead of raw UTF-8, so a
    // Turkish-heavy context (ş/ı/ğ/ü/ö/ç) already within the 8000/8500 text budget still
    // produced a hook stdout many times that size (the review measured 5,172 chars ->
    // 21,945 bytes).
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Minor (R34) · Türkçe ağırlıklı bağlamda kanca stdout'u ham UTF-8 ile 9000 bayta yakın kalır")]
    public void HookStdout_RawUtf8_StaysNearByteBudget_ForTurkishHeavyContext()
    {
        using var harness = new KabulHarness();
        var turkish = new string('ş', 3_000);
        var vault = WriteCompanionVault(harness.Root, new Dictionary<string, string>
        {
            ["Kurallar.md"] = "# Kurallar\n\n" + turkish,
        });

        var hookPayload = "{\"hook_event_name\":\"SessionStart\",\"session_id\":\"hook-escaping\"}";
        var run = harness.Run(vault, ["context"], standardInput: hookPayload, fakeNow: Today);
        Assert.Equal(0, run.ExitCode);

        var root = JsonDocument.Parse(run.Stdout).RootElement;
        var text = root.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.True(text.Length <= 8_000, $"chars = {text.Length}, beklenen <= 8000");
        Assert.True(Utf8.GetByteCount(text) <= 8_500, $"UTF-8 bayt = {Utf8.GetByteCount(text)}, beklenen <= 8500");

        var rawStdoutBytes = Utf8.GetByteCount(run.Stdout);
        Assert.True(rawStdoutBytes <= 9_000,
            $"ham kanca stdout'u {rawStdoutBytes} bayt, beklenen <= 9000 (\\u kaçışı sızmış olabilir)");
    }

    // ==================== helpers ====================

    private static (int Chars, string Text) ContextJson(KabulResult run)
    {
        var root = JsonDocument.Parse(run.Stdout).RootElement;
        return (root.GetProperty("chars").GetInt32(), root.GetProperty("text").GetString()!);
    }

    /// <summary>Same framing every Context.cs section shares (ContextKabul.Section):
    /// everything between a "[label]\n" marker and the next line starting with '[' (or the
    /// closing "Hafıza protokolü zorunludur." sentence, or end of text).
    ///
    /// Fixed here (not present in ContextKabul.cs's copy, which this was ported from):
    /// when a section's body is EMPTY, `bodyStart` lands exactly on the '[' of the very
    /// NEXT label — with no '\n' before it inside the search range starting at bodyStart —
    /// so the original "\n[" search skipped past that immediate boundary and swallowed the
    /// entire next section into this one's "body". An explicit empty-body check closes
    /// that gap; every existing caller of the unfixed version only ever probed a
    /// non-empty section, so it never surfaced there.</summary>
    private static string Section(string text, string label)
    {
        var marker = $"[{label}]\n";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var bodyStart = start + marker.Length;
        if (bodyStart >= text.Length || text[bodyStart] == '[')
            return string.Empty;

        var nextBracket = text.IndexOf("\n[", bodyStart, StringComparison.Ordinal);
        var closing = text.IndexOf("\nHafıza protokolü zorunludur.", bodyStart, StringComparison.Ordinal);
        var candidates = new[] { nextBracket, closing }.Where(index => index >= 0).ToArray();
        var end = candidates.Length == 0 ? text.Length : candidates.Min() + 1;
        return text[bodyStart..end].TrimEnd('\n');
    }

    private static string ManyThreadsFixture(int count)
    {
        var builder = new StringBuilder("# Threads\n\n## Active Threads\n\n");
        for (var i = 1; i <= count; i++)
            builder.Append($"### Konu {i:D3} — sentetik aktif iş kaydı\n");
        builder.Append("## Closed Threads\n\n");
        return builder.ToString();
    }

    private static string Filler(int targetChars)
    {
        if (targetChars <= 0)
            return string.Empty;

        const string seed = "Bu satır sentetik doldurma metnidir, ContextHardeningKabul fixture'ı için üretildi. ";
        var builder = new StringBuilder(targetChars);
        while (builder.Length < targetChars)
            builder.Append(seed);
        builder.Length = targetChars;
        return builder.ToString();
    }

    private static string WriteMinimalVault(KabulHarness harness, string name)
    {
        var vault = harness.NewScratchDirectory(name);
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        return vault;
    }

    private static string WriteCompanionVault(string root, IReadOnlyDictionary<string, string> companionFiles)
    {
        var vault = Path.Combine(root, "vault-companion-" + Guid.NewGuid().ToString("N")[..8]);
        var companion = Path.Combine(vault, KabulVaultBuilder.CompanionDir);
        Directory.CreateDirectory(companion);
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        foreach (var (name, content) in companionFiles)
            File.WriteAllText(Path.Combine(companion, name), content, Utf8);
        return vault;
    }

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

    private static void DropHealthTable(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS health";
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Mirrors ContextKabul.SeedReflectionDebt: seeds a single 'yansima-borcu'
    /// health row directly into the state.db the harness's child process will open.</summary>
    private static void SeedReflectionDebt(KabulHarness harness, string vault)
    {
        var dbPath = StateDbPath(harness, vault);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var state = new State(null, null, dbPath, StateAccess.ReadWrite))
            state.WriteHealthConcurrently([new HealthItem("hafiza", HealthLevel.Warning, "yansima-borcu", "kabul-seed", Nudge.ReflectionDebt)]);
        SqliteConnection.ClearAllPools();
    }
}
