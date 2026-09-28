using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Process-boundary acceptance oracles for lane L1-cli-surface (SPEC-3.1.0.md F6-1, F6-4,
/// F6-5, F6-6, F9-1, NB-10, B5). Every test drives a real oom executable through
/// <see cref="KabulHarness"/> (OOM_KABUL_EXE selects old vs. new) and asserts the FIXED,
/// honest command surface, so each is red against both this tree's current build and
/// eski-exe/oom.exe, and turns green only once L1's fix lands. Nothing here calls a
/// product API directly: only process launch, stdio and file reads/writes into the test's
/// own scratch vault.
///
/// Written by the independent oracle author (kahin), not the implementer. Per SPEC R3 this
/// is a NEW file under tests/Oom.Tests/Kabul/ — it never edits KabulHarness or
/// KabulVaultBuilder, which are driver-owned.
/// </summary>
public sealed class CliKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;

    // SPEC F6-6 / driver ruling R5: no usage line may use one of these English verbs —
    // the final help is Turkish prose, only machine identifiers (flags, enum values) stay
    // English. Regex copied verbatim from the lane brief's oracle assertion 7.
    private static readonly Regex BannedEnglishVerb = new(@"\b(print|search|use this|emit|report|select|serve)\b", RegexOptions.IgnoreCase);

    // SPEC F9-1: 'oom --version' must print exactly '3.1.0+<commit>', where <commit> is
    // either a short git hash or the literal 'unknown' (lane note: when git is unavailable
    // in the build sandbox the build emits '3.1.0+unknown' and the L5 release script
    // refuses that value — refusing it is L5's job, not this lane's).
    private static readonly Regex VersionPattern = new(@"^3\.1\.0\+(?<commit>[0-9a-f]{7,40}|unknown)\s*$", RegexOptions.IgnoreCase);

    [Fact(DisplayName = "F6-6 (1) · oom --help exit 0 ve kullanım metnini basar")]
    public void Help_ExitsZeroAndPrintsUsage()
    {
        using var harness = new KabulHarness();
        var placeholderVault = Path.Combine(harness.Root, "unused-vault");

        var result = harness.Run(placeholderVault, ["--help"]);

        Assert.True(result.ExitCode == 0 && result.Stdout.Contains("oom ", StringComparison.Ordinal),
            $"F6-6: 'oom --help' must exit 0 and print the usage text (old code falls to the default branch and " +
            $"exits 1, per Program.cs's empty-command path). Got exit {result.ExitCode}; stdout: {result.Stdout}; " +
            $"stderr: {result.Stderr}");
    }

    [Fact(DisplayName = "F9-1 · oom --version exit 0 ve tam olarak '3.1.0+<commit>' basar")]
    public void Version_ExitsZeroAndPrintsSemverPlusCommit()
    {
        using var harness = new KabulHarness();
        var placeholderVault = Path.Combine(harness.Root, "unused-vault");

        var result = harness.Run(placeholderVault, ["--version"]);
        var match = VersionPattern.Match(result.Stdout.TrimEnd('\r', '\n'));

        Assert.True(result.ExitCode == 0 && match.Success,
            $"F9-1: 'oom --version' must exit 0 and print exactly '3.1.0+<commit>' (a short git hash, or 'unknown' " +
            "when git was unavailable at build time). Old code has no --version flag at all (falls to the default " +
            $"branch: usage + exit 1). Got exit {result.ExitCode}; stdout: '{result.Stdout}'; stderr: {result.Stderr}");
    }

    [Fact(DisplayName = "F9-1 · MCP initialize serverInfo.version, oom --version çıktısıyla aynı")]
    public void Mcp_ServerInfoVersion_MatchesCliVersionString()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);

        var versionResult = harness.Run(vault, ["--version"]);
        var cliVersion = versionResult.Stdout.Trim();

        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { }
        });
        var mcpResult = harness.Run(vault, ["mcp"], standardInput: request + "\n");

        string? mcpVersion = null;
        JsonException? parseError = null;
        try
        {
            using var document = JsonDocument.Parse(FirstNonEmptyLine(mcpResult.Stdout));
            mcpVersion = document.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString();
        }
        catch (JsonException error)
        {
            parseError = error;
        }

        Assert.True(cliVersion.Length > 0 && mcpVersion == cliVersion,
            $"F9-1: MCP's initialize response serverInfo.version must equal the 'oom --version' string exactly. " +
            $"Old code hardcodes serverInfo.version to '2.0' (Mcp.cs). Got cliVersion='{cliVersion}' (exit " +
            $"{versionResult.ExitCode}), mcpVersion='{mcpVersion ?? "(null)"}'{(parseError is null ? string.Empty : $", JSON parse error: {parseError.Message}")}. " +
            $"mcp stdout: {mcpResult.Stdout}; mcp stderr: {mcpResult.Stderr}");
    }

    [Fact(DisplayName = "F6-4 · retrieveMode, sweep.everyHours ve bilinmeyen 'foo' → context stderr'de üç 'bilinmeyen ayar' satırı")]
    public void Context_WarnsOnStderrForDeadAndUnknownConfigKeys()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        WriteFile(Path.Combine(vault, ".oom", "oom.json"),
            """
            {
              "context": { "capChars": 16000 },
              "retrieveMode": "vektor",
              "sweep": { "everyHours": 4 },
              "foo": "bar"
            }
            """);

        var result = harness.Run(vault, ["context"], fakeNow: Today);

        var hasRetrieveMode = result.Stderr.Contains("bilinmeyen ayar: retrieveMode", StringComparison.Ordinal);
        var hasEveryHours = result.Stderr.Contains("bilinmeyen ayar: everyHours", StringComparison.Ordinal);
        var hasFoo = result.Stderr.Contains("bilinmeyen ayar: foo", StringComparison.Ordinal);
        Assert.True(hasRetrieveMode && hasEveryHours && hasFoo,
            "F6-4: 'oom context' stderr must contain 'bilinmeyen ayar: retrieveMode', 'bilinmeyen ayar: everyHours' " +
            "and 'bilinmeyen ayar: foo' when oom.json sets a dead key (retrieveMode), a dead nested key " +
            "(sweep.everyHours) and a genuinely unknown top-level key (foo). Old code never warns on stderr for " +
            "any command (unknown top-level keys surface only as `doctor` rows, and retrieveMode/sweep.everyHours " +
            "are structurally recognized keys today, so they are never even flagged as unknown there either). " +
            $"Got retrieveMode={hasRetrieveMode}, everyHours={hasEveryHours}, foo={hasFoo}. stderr: '{result.Stderr}'; " +
            $"exit {result.ExitCode}; stdout: {result.Stdout}");
    }

    [Fact(DisplayName = "F6-5 · retrieve --query x --session s → non-zero exit ve stderr'de hata")]
    public void Retrieve_RejectsRemovedSessionFlag()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);

        var result = harness.Run(vault, ["retrieve", "--query", "x", "--session", "s"], fakeNow: Today);

        Assert.True(result.ExitCode != 0 && result.Stderr.Trim().Length > 0,
            "F6-5: 'retrieve --session' was cut (SPEC Cut / F6-5) — passing it must exit non-zero with an error on " +
            "stderr, not be silently accepted as a session label. Old code accepts --session as a free-form search " +
            $"label and exits 0. Got exit {result.ExitCode}; stderr: '{result.Stderr}'; stdout: {result.Stdout}");
    }

    [Fact(DisplayName = "B5 · context/doctor/sweep --dry-run koşumları oom.json'un hash'ini değiştirmez")]
    public void Loading_NeverRewritesOomJson()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var settingsPath = Path.Combine(vault, ".oom", "oom.json");
        // Keep the untouched, live capChars value (SPEC B5's own example) and point sweep
        // at an empty, fast-to-scan root so --dry-run stays quick and deterministic.
        var emptyRoot = harness.NewScratchDirectory("no-sweep-roots");
        WriteFile(settingsPath,
            $$"""
            {
              "context": { "capChars": 16000 },
              "sweep": { "roots": [{{JsonSerializer.Serialize(emptyRoot)}}] }
            }
            """);
        var before = Sha256(settingsPath);

        var context = harness.Run(vault, ["context"], fakeNow: Today);
        var doctor = harness.Run(vault, ["doctor"], fakeNow: Today);
        var sweep = harness.Run(vault, ["sweep", "--dry-run"], fakeNow: Today);

        var after = Sha256(settingsPath);
        Assert.True(before == after,
            $"B5: loading oom.json (via context, doctor and sweep --dry-run) must never rewrite it — the migration " +
            "path that would have rewritten capChars into a new schema on install is explicitly forbidden by SPEC " +
            $"amendment B5. sha256 before={before}, after={after}. context exit {context.ExitCode}; " +
            $"doctor exit {doctor.ExitCode}; sweep exit {sweep.ExitCode}; oom.json now: {File.ReadAllText(settingsPath, Utf8)}");
    }

    [Fact(DisplayName = "F6-6/NB-10 · Yardım metni: tek dil (Türkçe), İngilizce fiil yok, sessionend/--all/serbest metin belgeli")]
    public void Help_IsAnHonestSingleLanguageTurkishSurface()
    {
        using var harness = new KabulHarness();
        var placeholderVault = Path.Combine(harness.Root, "unused-vault");

        var result = harness.Run(placeholderVault, ["--help"]);
        var help = result.Stdout;

        var bannedMatches = BannedEnglishVerb.Matches(help).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var reasonLine = OptionDetailLine(help, "--reason");
        var reasonListsSessionEnd = reasonLine.Contains("sessionend", StringComparison.OrdinalIgnoreCase);
        var detachedLine = OptionDetailLine(help, "--detached");
        var detachedDescribesInProcess = detachedLine.Contains("aynı süreç", StringComparison.OrdinalIgnoreCase);
        var doctorAllLine = OptionDetailLine(help, "--all");
        var doctorDocumentsAll = doctorAllLine.Length > 0;
        var saveDocumentsFreeText = help.Contains("serbest metin", StringComparison.OrdinalIgnoreCase);

        Assert.True(bannedMatches.Length == 0 && reasonListsSessionEnd && detachedDescribesInProcess && doctorDocumentsAll && saveDocumentsFreeText,
            "F6-6/NB-10: the final help text must (a) use no English surface verb from " +
            $"/\\b(print|search|use this|emit|report|select|serve)\\b/i — found [{string.Join(", ", bannedMatches)}]; " +
            $"(b) document 'sessionend' on the --reason line — line: '{reasonLine}'; " +
            $"(c) describe --detached as in-process ('aynı süreç') handling, not a separate/background process — line: '{detachedLine}'; " +
            $"(d) document 'doctor --all' — line: '{doctorAllLine}'; " +
            "(e) document that 'save' accepts free text ('serbest metin'). Old help (and this tree's current help) " +
            $"is English-flavored and documents none of these. Full help:\n{help}");
    }

    private static string FirstNonEmptyLine(string text) =>
        text.Split('\n').Select(line => line.Trim('\r')).FirstOrDefault(line => line.Length > 0) ?? string.Empty;

    private static string LineContaining(string text, string needle) =>
        text.Split('\n').Select(line => line.Trim('\r')).FirstOrDefault(line => line.Contains(needle, StringComparison.Ordinal)) ?? string.Empty;

    /// <summary>The option's own detail line (e.g. "    --reason &lt;r&gt;   set the flush reason..."),
    /// found by its trimmed text starting with <paramref name="flag"/> — not a command summary line
    /// that merely mentions the flag in brackets (e.g. "flush [--reason &lt;r&gt;] ...").</summary>
    private static string OptionDetailLine(string text, string flag) =>
        text.Split('\n').Select(line => line.Trim('\r').TrimStart())
            .FirstOrDefault(line => line.StartsWith(flag, StringComparison.Ordinal)) ?? string.Empty;

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8);
    }
}
