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
