using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul.Regresyon;

/// <summary>
/// Process-boundary regression oracles for SPEC-3.1.0.md F7-3 / B10. Every test drives a
/// real oom executable through <see cref="KabulHarness"/> (OOM_KABUL_EXE, a
/// <see cref="KabulVaultBuilder"/> vault, the harness's own OOM_LOCALAPPDATA) and asserts the
/// FIXED behaviour, so it is red against eski-exe/oom.exe and turns green only when the
/// defect is fixed. Nothing here calls a product API: only process launch, file writes into
/// the test's own scratch vault, and file reads.
///
/// Tagged Kabul=Regresyon (SPEC R11): interim lane gates filter these out until W3; the
/// red run against the old exe is recorded in tests/REGRESYON-KANITI.md. A red test here is
/// never fixed by editing the test.
/// </summary>
[Trait("Kabul", "Regresyon")]
public sealed class RegresyonKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;
    private static readonly TimeSpan SlowRun = TimeSpan.FromSeconds(180);

    /// <summary>Claude Code's SessionStart rule (SPEC F1 "Why", measured): a hook string longer
    /// than 10 000 characters is cut and the session receives only a 2 000-character preview.</summary>
    private const int HookStringLimit = 10_000;
    private const int HookPreviewChars = 2_000;

    [Fact(DisplayName = "A1-01 · context --json ≤ 8000 karakter ve ≤ 8500 UTF-8 bayt (dokunulmamış capChars 16000)")]
    public void A1_01_ContextFitsTheSingleBudget()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);

        var text = ContextText(harness, vault);
        var chars = text.Length;
        var bytes = Utf8.GetByteCount(text);

        Assert.True(chars <= 8_000 && bytes <= 8_500,
            $"A1-01: context --json text is {chars} chars / {bytes} UTF-8 bytes on the real-size fixture with the untouched " +
            "capChars 16000; SPEC F1-1/B4/B5 require ≤ 8000 chars and ≤ 8500 bytes.");
    }

    [Fact(DisplayName = "A2-02 · Kurallar.md tam metni oturuma birebir ulaşır")]
    public void A2_02_KurallarReachesTheSessionVerbatim()
    {
        using var harness = new KabulHarness();
        var vault = BuildVaultWithRealShapeRules(harness);
        var kurallar = CompanionText(vault, "Kurallar.md");

        var (context, delivered) = SessionStartDelivery(harness, vault);

        Assert.True(delivered.Contains(kurallar, StringComparison.Ordinal),
            $"A2-02: the full Kurallar.md text ({kurallar.Length} chars) is not verbatim in what the session receives. " +
            $"SessionStart additionalContext is {context.Length} chars (limit {HookStringLimit}, above it only a " +
            $"{HookPreviewChars}-char preview arrives); present anywhere in the raw hook output: " +
            $"{context.Contains(kurallar, StringComparison.Ordinal)}. SPEC B3: Kurallar is never cut.");
    }

    [Fact(DisplayName = "A2-02 · Duzeltmeler.md tam metni oturuma birebir ulaşır")]
    public void A2_02_DuzeltmelerReachesTheSessionVerbatim()
    {
        using var harness = new KabulHarness();
        var vault = BuildVaultWithRealShapeRules(harness);
        var duzeltmeler = CompanionText(vault, "Duzeltmeler.md");

        var (context, delivered) = SessionStartDelivery(harness, vault);

        Assert.True(delivered.Contains(duzeltmeler, StringComparison.Ordinal),
            $"A2-02: the full Duzeltmeler.md text ({duzeltmeler.Length} chars) is not verbatim in what the session receives. " +
            $"SessionStart additionalContext is {context.Length} chars (limit {HookStringLimit}, above it only a " +
            $"{HookPreviewChars}-char preview arrives); present anywhere in the raw hook output: " +
            $"{context.Contains(duzeltmeler, StringComparison.Ordinal)}. SPEC B3: Düzeltmeler is never cut.");
    }

    [Fact(DisplayName = "A5-03 · Journal üstte 09-27, altta 09-17 → Son Journal 09-27'yi seçer")]
    public void A5_03_JournalSelectsTheNewestDateNotTheLastHeading()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        const string newest = "## 2026-09-27 — kabul-journal-en-yeni";
        const string oldest = "## 2026-09-17 — kabul-journal-en-eski";
        WriteFile(Path.Combine(vault, KabulVaultBuilder.CompanionDir, "Journal.md"),
            "# Journal\n\n" +
            newest + "\nEn yeni girdi dosyanın başında durur; sentetik journal gövdesi.\n\n" +
            "## 2026-09-22 — kabul-journal-orta\nOrtadaki girdi; sentetik journal gövdesi.\n\n" +
            oldest + "\nSonradan alta eklenmiş eski girdi; sentetik journal gövdesi.\n");

        var text = ContextText(harness, vault);

        Assert.True(text.Contains(newest, StringComparison.Ordinal) && !text.Contains(oldest, StringComparison.Ordinal),
            $"A5-03: [Son Journal] must pick the newest date (09-27, top of the file), not the last heading (09-17, bottom). " +
            $"09-27 present: {text.Contains(newest, StringComparison.Ordinal)}, 09-17 present: {text.Contains(oldest, StringComparison.Ordinal)}. " +
            $"Journal section as rendered: {Section(text, "[Hafıza — Son Journal]")}");
    }

    [Fact(DisplayName = "A2-03 · 'ok' damgalı 4 turluk oturum 44 tura büyür, mtime −10 saat → skipped=0 ve işlenir")]
    public void A2_03_GrownOldStampedSessionIsProcessed()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var projects = harness.NewScratchDirectory("transcripts");
        WriteSweepSettings(vault, projects, maxSessionsPerRun: 20);
        const string session = "kabul-a203";
        var transcript = Path.Combine(projects, "E--kabul-proje", session + ".jsonl");
        var turnsStart = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(3));
        using var claude = FakeClaude.Answering(harness, ValidSummary);

        WriteTranscript(transcript, session, 4, turnsStart);
        File.SetLastWriteTimeUtc(transcript, (Today - TimeSpan.FromHours(1)).UtcDateTime);
        var first = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);
        var stamped = Anchors(vault, session);
        Assert.True(stamped.Any(range => range.Start == 0 && range.End == 3),
            $"A2-03 fixture precondition: the first sweep must flush the 4-turn session 'ok' (anchor turns:0-3). " +
            $"Anchors: [{string.Join(", ", stamped)}]. exit {first.ExitCode}; stdout: {first.Stdout}; stderr: {first.Stderr}");

        WriteTranscript(transcript, session, 44, turnsStart);
        File.SetLastWriteTimeUtc(transcript, (Today - TimeSpan.FromHours(10)).UtcDateTime);
        var second = harness.Run(vault, ["sweep"], fakeNow: Today, timeout: SlowRun);

        var skipped = SummaryNumber(second.Stdout, "atlandı");
        var sessions = SummaryNumber(second.Stdout, "oturum");
        var newTurns = Anchors(vault, session).Where(range => range.Start > 3).ToArray();
        Assert.True(skipped == 0 && sessions == 1 && newTurns.Length > 0,
            $"A2-03: a 'ok'-stamped session that grew from 4 to 44 turns with mtime −10 h must be processed: want skipped=0, " +
            $"sessions=1 and a daily anchor for turns ≥ 4; got skipped={Show(skipped)}, sessions={Show(sessions)}, " +
            $"new-turn anchors [{string.Join(", ", newTurns)}]. stdout: {second.Stdout}");
    }

    [Fact(DisplayName = "A2-05 · 3 FILE bloğundan birinde END FILE eksik → 2 not yazılır, exit 2")]
    public void A2_05_OneBrokenBlockKeepsTheOtherTwoNotes()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        // Slugs share no token with each other or the fixture's concepts, so no duplicate-name
        // rule (SPEC F3-5, token Jaccard ≥ 0.6) can be the reason a block is not written.
        string[] slugs = ["gunes-paneli-bakim-plani", "kuyu-pompasi-ariza-kaydi", "sera-sulama-takvimi"];
        string[] titles = ["Güneş paneli bakım planı", "Kuyu pompası arıza kaydı", "Sera sulama takvimi"];
        var output = new StringBuilder();
        for (var i = 0; i < slugs.Length; i++)
        {
            output.Append("=== FILE: knowledge/concepts/").Append(slugs[i]).Append(".md ===\n");
            output.Append(ConceptNote(slugs[i], titles[i], slugs[(i + 1) % slugs.Length], slugs[(i + 2) % slugs.Length]));
            if (i != 1)
                output.Append("=== END FILE ===\n");
        }

        output.Append("=== DONE ===\n");
        using var claude = FakeClaude.Answering(harness, output.ToString());

        var result = harness.Run(vault, ["compile"], fakeNow: Today, timeout: SlowRun);

        var concepts = Path.Combine(vault, "knowledge", "concepts");
        var intact = new[] { slugs[0], slugs[2] }.Where(slug => File.Exists(Path.Combine(concepts, slug + ".md"))).ToArray();
        // Block 2 (slugs[1]) is the malformed one: it must not be written — an implementation
        // that writes all three notes and merely returns exit 2 must not turn this oracle
        // green (intact.Length == 2 alone cannot tell "wrote 2 of 3" from "wrote 3 of 3").
        var malformedFile = Path.Combine(concepts, slugs[1] + ".md");
        var malformedWritten = File.Exists(malformedFile);
        // SPEC F3 acceptance (1): the rejected block is "listelenir" as 'reddedildi: <neden>'
        // with a nonempty reason, and must name the rejected block (its slug).
        var rejectedReason = Regex.Match(result.Stdout, @"reddedildi\s*:\s*(?<reason>\S.*)", RegexOptions.IgnoreCase);
        var rejectedNamesBlock = result.Stdout.Contains(slugs[1], StringComparison.Ordinal);
        Assert.True(intact.Length == 2 && result.ExitCode == 2 && !malformedWritten && rejectedReason.Success && rejectedNamesBlock,
            $"A2-05: with block 2 of 3 missing '=== END FILE ===', blocks 1 and 3 must still be written, the malformed " +
            $"block ({slugs[1]}) must not be written, compile must exit 2, and stdout must identify the rejected block " +
            $"with a nonempty 'reddedildi: <neden>' reason; got {intact.Length}/2 intact [{string.Join(", ", intact)}], " +
            $"exit {result.ExitCode}, malformed file written={malformedWritten}, rejected-reason line found={rejectedReason.Success}, " +
            $"stdout names {slugs[1]}={rejectedNamesBlock}. stdout: {result.Stdout}; stderr: {result.Stderr}");
    }

    [Fact(DisplayName = "A2-06 · 7 günden eski 100 retry + 0 yeni → doctor 'Son 7 gün ret: %0,0'")]
    public void A2_06_RejectionRateUsesARealSevenDayWindow()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var projects = harness.NewScratchDirectory("transcripts");
        WriteSweepSettings(vault, projects, maxSessionsPerRun: 100);
        var tenDaysAgo = Today - TimeSpan.FromDays(10);
        for (var i = 0; i < 100; i++)
            WriteTranscript(Path.Combine(projects, "E--kabul-proje", $"kabul-a206-{i:D3}.jsonl"), $"kabul-a206-{i:D3}", 4, tenDaysAgo - TimeSpan.FromHours(2));

        using (FakeClaude.Answering(harness, "özet yok"))
        {
            var sweep = harness.Run(vault, ["sweep"], fakeNow: tenDaysAgo, timeout: SlowRun);
            var retries = Regex.Match(sweep.Stdout, @"retry=(\d+)");
            Assert.True(retries.Success && int.Parse(retries.Groups[1].Value, CultureInfo.InvariantCulture) == 100,
                $"A2-06 fixture precondition: the sweep 10 days ago must record 100 retry outcomes. stdout: {sweep.Stdout}; stderr: {sweep.Stderr}");
        }

        var doctor = DoctorLines(harness, vault);

        var line = Regex.Match(doctor, @"Son 7 gün ret:\s*(?<rate>%?\s*\d+(?:[.,]\d+)?\s*%?)");
        var rate = line.Success ? ParsePercent(line.Groups["rate"].Value) : (double?)null;
        Assert.True(rate == 0.0,
            $"A2-06: 100 retry rows older than 7 days and 0 recent must render 'Son 7 gün ret: %0,0'; got " +
            $"'{(line.Success ? line.Value.Trim() : "(no 'Son 7 gün ret' line)")}'. doctor --json lines: {doctor}");
    }

    [Fact(DisplayName = "V1-02 · Boş state → doctor 'bekleyen' = log.md'de kaydı olmayan gün sayısı")]
    public void V1_02_EmptyStatePendingIsDerivedFromTheLog()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var log = new StringBuilder("# Derleme Günlüğü\n\n");
        var logged = 0;
        for (var day = new DateOnly(2026, 9, 16); day < DateOnly.FromDateTime(Today.Date); day = day.AddDays(1))
        {
            var name = $"{day:yyyy-MM-dd}.md";
            WriteFile(Path.Combine(vault, "daily", name),
                $"---\ntype: daily\ndate: {day:yyyy-MM-dd}\nsource: oom\n---\n# Günlük Log: {day:yyyy-MM-dd}\n\n## Oturumlar\n\n" +
                "### Oturum (10:00)\nSentetik günlük kaydı, V1-02 fixture'ı.\n");
            if (day > new DateOnly(2026, 9, 24))
                continue;

            logged++;
            log.Append($"## [{day:yyyy-MM-dd}T19:00:00.0000000+03:00] compile | {name}\n- kabul-sentetik-kavram-01.md\n\n1 not yayımlandı.\n\n");
        }

        WriteFile(Path.Combine(vault, "knowledge", "log.md"), log.ToString());
        var dailies = Directory.GetFiles(Path.Combine(vault, "daily"), "*.md").Length;
        var expected = dailies - logged;

        var doctor = DoctorLines(harness, vault);

        var shown = Regex.Matches(doctor, @"bekleyen daily:\s*(\d+)", RegexOptions.IgnoreCase)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.True(shown.Length > 0 && shown.All(count => count == expected),
            $"V1-02: with an empty state, 'Bekleyen daily' must equal the days without a knowledge/log.md compile entry " +
            $"({dailies} dailies − {logged} logged = {expected}), not the raw daily count {dailies}; doctor shows " +
            $"[{string.Join(", ", shown)}]. doctor --json lines: {doctor}");
    }

    /// <summary>Every string value of <c>doctor --json</c>, one per line. JSON rather than the table,
    /// because the table may show only yellow/red rows (SPEC accepted amendment) while the JSON
    /// carries every row; walking all strings keeps this independent of the JSON key names.</summary>
    private static string DoctorLines(KabulHarness harness, string vault)
    {
        var result = harness.Run(vault, ["doctor", "--json"], fakeNow: Today, timeout: SlowRun);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Stdout);
        }
        catch (JsonException error)
        {
            Assert.Fail($"doctor --json did not print JSON ({error.Message}); exit {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
            throw;
        }

        using (document)
        {
            var lines = new StringBuilder();
            Collect(document.RootElement, lines);
            return lines.ToString();
        }

        static void Collect(JsonElement element, StringBuilder lines)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    lines.Append(element.GetString()).Append('\n');
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        Collect(property.Value, lines);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Collect(item, lines);
                    break;
            }
        }
    }

    private static string ContextText(KabulHarness harness, string vault)
    {
        var result = harness.Run(vault, ["context", "--json"], fakeNow: Today);
        Assert.True(result.ExitCode == 0, $"context --json exited {result.ExitCode}; stderr: {result.Stderr}");
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }

    /// <summary>Runs context as Claude Code's SessionStart hook does and returns the raw
    /// additionalContext plus what the session actually receives under the host's rule.</summary>
    private static (string Context, string Delivered) SessionStartDelivery(KabulHarness harness, string vault)
    {
        var payload = JsonSerializer.Serialize(new
        {
            session_id = "kabul-a202",
            transcript_path = string.Empty,
            cwd = vault,
            hook_event_name = "SessionStart",
            source = "startup"
        });
        var result = harness.Run(vault, ["context"], standardInput: payload, fakeNow: Today);
        Assert.True(result.ExitCode == 0, $"SessionStart context exited {result.ExitCode}; stderr: {result.Stderr}");

        var context = result.Stdout;
        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.TryGetProperty("hookSpecificOutput", out var output)
                && output.TryGetProperty("additionalContext", out var additional))
                context = additional.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Plain stdout is also accepted by Claude Code as SessionStart context.
        }

        return (context, context.Length <= HookStringLimit ? context : context[..HookPreviewChars]);
    }

    /// <summary>The default fixture plus Kurallar.md / Duzeltmeler.md in the real files' shape
    /// (41 lines ≈ 2.2 KB; ≈ 25 lines ≈ 1.5 KB) with distinctive synthetic lines.</summary>
    private static string BuildVaultWithRealShapeRules(KabulHarness harness)
    {
        var vault = KabulVaultBuilder.Build(harness.Root);
        var kurallar = new StringBuilder("# Kurallar\n\n");
        for (var i = 1; i <= 39; i++)
            kurallar.Append($"- Kural {i:D2}: sentetik kabul kuralı, oturuma birebir ulaşır.\n");
        WriteFile(Path.Combine(vault, KabulVaultBuilder.CompanionDir, "Kurallar.md"), kurallar.ToString());

        var duzeltmeler = new StringBuilder("# Düzeltmeler\n\n");
        for (var i = 1; i <= 8; i++)
            duzeltmeler.Append($"## Sentetik düzeltme {i:D2}\n- Yanlış: kabul örneği {i:D2} eski biçimde, eksik bağlamla yazıldı.\n" +
                $"- Doğru: kabul örneği {i:D2} yeni biçimde, tam bağlamla yazılır ve oturuma birebir ulaşır.\n");
        WriteFile(Path.Combine(vault, KabulVaultBuilder.CompanionDir, "Duzeltmeler.md"), duzeltmeler.ToString());
        return vault;
    }

    private static string CompanionText(string vault, string file) =>
        File.ReadAllText(Path.Combine(vault, KabulVaultBuilder.CompanionDir, file), Utf8).TrimEnd('\n', '\r');

    private static string Section(string text, string label)
    {
        var start = text.IndexOf(label, StringComparison.Ordinal);
        if (start < 0)
            return "(section missing)";

        var next = text.IndexOf("\n[", start + label.Length, StringComparison.Ordinal);
        return next < 0 ? text[start..] : text[start..next];
    }

    private static void WriteSweepSettings(string vault, string root, int maxSessionsPerRun) =>
        WriteFile(Path.Combine(vault, ".oom", "oom.json"), JsonSerializer.Serialize(new
        {
            context = new { capChars = 16_000 },
            sweep = new { roots = new[] { root }, maxSessionsPerRun }
        }));

    /// <summary>Writes a Claude Code-shaped transcript with alternating user/assistant text turns.
    /// Turn i is identical across calls, so a longer write "grows" the same session.</summary>
    private static void WriteTranscript(string path, string sessionId, int turns, DateTimeOffset start)
    {
        var lines = new StringBuilder();
        for (var i = 0; i < turns; i++)
        {
            var user = i % 2 == 0;
            var text = $"Sentetik tur {i:D2}: kabul senaryosu için {(user ? "kullanıcı sorusu" : "asistan yanıtı")}, gerçek içerik değildir.";
            object content = user ? text : new[] { new { type = "text", text } };
            lines.Append(JsonSerializer.Serialize(new
            {
                sessionId,
                type = user ? "user" : "assistant",
                timestamp = (start + TimeSpan.FromMinutes(i)).ToString("O", CultureInfo.InvariantCulture),
                message = new { role = user ? "user" : "assistant", content }
            })).Append('\n');
        }

        WriteFile(path, lines.ToString());
    }

    private static readonly Regex AnchorPattern = new(@"<!-- session:(?<id>\S+) ts:\S+ turns:(?<start>\d+)-(?<end>\d+)", RegexOptions.Compiled);

    private static IReadOnlyList<(int Start, int End)> Anchors(string vault, string sessionId)
    {
        var directory = Path.Combine(vault, "daily");
        return [.. Directory.GetFiles(directory, "*.md")
            .SelectMany(path => AnchorPattern.Matches(File.ReadAllText(path, Utf8)))
            .Where(match => match.Groups["id"].Value == sessionId)
            .Select(match => (int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)))];
    }

    private static int? SummaryNumber(string stdout, string word)
    {
        var match = Regex.Match(stdout, $@"(\d+) {Regex.Escape(word)}\b");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "(not printed)";

    private static double? ParsePercent(string text)
    {
        var digits = text.Replace("%", string.Empty, StringComparison.Ordinal).Trim().Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private const string ValidSummary =
        "## Bağlam\n- Sentetik kabul oturumu.\n## Önemli Konuşmalar\n- Sentetik turlar özetlendi.\n" +
        "## Alınan Kararlar\n- Belirlenmedi.\n## Öğrenilenler\n- Belirlenmedi.\n## Yapılacaklar\n- Belirlenmedi.";

    private static string ConceptNote(string slug, string title, string linkA, string linkB) =>
        "---\n" +
        $"title: {title}\n" +
        "aliases: []\n" +
        "tags: [sentetik]\n" +
        "sources: [daily/2026-09-27.md]\n" +
        "created: 2026-09-27\n" +
        "updated: 2026-09-27\n" +
        "type: concept\n" +
        "hub: genel\n" +
        "---\n" +
        $"# {title}\n\n" +
        $"{title} ({slug}) derleme kabulü için üretilmiş sentetik kavram notudur, gerçek içerik taşımaz.\n\n" +
        "## İlgili Kavramlar\n" +
        $"- [[{linkA}]] — aynı derleme koşumunda üretilen komşu blok\n" +
        $"- [[{linkB}]] — aynı derleme koşumunda üretilen diğer blok\n";

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8);
    }
}

/// <summary>
/// A local stand-in for the claude CLI, scoped to one test: a claude.cmd in the test's own
/// scratch dir that prints a fixed Claude Code JSON result. While alive, PATH holds only that
/// dir and the system dir, so the exe under test can never reach a real claude. The harness
/// copies this process's environment into the child. (L1-runner later publishes a shared helper.)
/// </summary>
file sealed class FakeClaude : IDisposable
{
    private readonly string? _previousPath;

    private FakeClaude(string directory)
    {
        _previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + Environment.SystemDirectory);
    }

    public static FakeClaude Answering(KabulHarness harness, string modelText)
    {
        var directory = harness.NewScratchDirectory("fake-claude-" + Guid.NewGuid().ToString("N")[..8]);
        var response = JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = modelText });
        File.WriteAllText(Path.Combine(directory, "response.json"), response, new UTF8Encoding(false));
        // Driver fix (wave 2): the 3.1.0 runner first makes a stream-json smoke call (SPEC B2)
        // and needs a clean init event; answer that call separately from the real one.
        var smoke = "{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\".\",\"session_id\":\"a205-smoke\",\"tools\":[],\"mcp_servers\":[],\"plugins\":[]}\n"
            + "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"}\n";
        File.WriteAllText(Path.Combine(directory, "smoke.jsonl"), smoke, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "claude.cmd"),
            "@echo off\r\necho %* | findstr /C:\"stream-json\" >nul\r\nif %errorlevel%==0 (type \"%~dp0smoke.jsonl\") else (type \"%~dp0response.json\")\r\n",
            Encoding.ASCII);
        return new FakeClaude(directory);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _previousPath);
}
