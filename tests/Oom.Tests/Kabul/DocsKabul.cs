using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance oracle for lane L4-docs-sync (SPEC-3.1.0.md F8-1, F8-2, F8-3). Written
/// independently of README.md/SECURITY.md/CONTRIBUTING.md's own authoring — the assertions
/// below read those files as DATA off disk and cross-check them against the live built exe
/// and this test assembly's own reflection metadata, the same way a reader who does not
/// trust prose would.
///
/// Convention this file enforces (documented at the top of docs/iddialar.md too): every
/// behaviour sentence in the in-scope sections of README.md/SECURITY.md ends with an inline
/// marker "(bkz. docs/iddialar.md#ID)". docs/iddialar.md is a Markdown table mapping each ID
/// to one or more evidence entries, each either "test:Full.Type.Name.Method" (resolved by
/// reflection over THIS test assembly) or "cmd:&lt;args...&gt;" (run against a fresh fixture
/// vault via <see cref="KabulHarness"/>, must exit 0). A behaviour line with no marker, an ID
/// with no row, or a row whose evidence does not resolve, is red.
///
/// MEASURED against this tree before this lane's own docs edits landed (i.e. against the
/// README/SECURITY/docs this lane found on disk): assertion 1 is red because the old README's
/// command table lists 9 commands with no `install`/`kit status`/`kit install` rows and is
/// missing several flags (`flush --transcript`/`--detached`, `doctor --all`) that
/// `oom --help` already prints; assertion 2 is red because docs/iddialar.md did not exist at
/// all. Both are pasted verbatim in this lane's report.
/// </summary>
public sealed class DocsKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Regex MarkerPattern = new(@"\(bkz\. docs/iddialar\.md#([A-Za-z0-9\-]+)\)", RegexOptions.Compiled);

    // ------------------------------------------------------------------
    // Assertion 1 — F8-1: the README command block IS `oom --help`'s own output, byte for
    // byte (line-ending-normalized). Any drift between the two is red by construction.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "F8-1 · README'nin komut bloğu `oom --help` çıktısıyla birebir aynıdır")]
    public void ReadmeCommandBlock_MatchesLiveHelpOutput_ByteForByte()
    {
        using var harness = new KabulHarness();
        var help = harness.Run(Path.Combine(harness.Root, "unused-vault"), ["--help"]);
        Assert.Equal(0, help.ExitCode);

        var readme = ReadRepoFile("README.md");
        var block = FencedBlockAfterHeading(readme, "## Commands")
            ?? throw new InvalidOperationException("README.md: '## Commands' altında ``` bloğu bulunamadı.");

        Assert.Equal(Normalize(help.Stdout).TrimEnd('\n'), Normalize(block).TrimEnd('\n'));
    }

    // ------------------------------------------------------------------
    // Assertion 2 — F8-2: every claim marker in the in-scope sections of README/SECURITY
    // resolves to exactly one docs/iddialar.md row, and every such row's evidence resolves
    // (a real test method by reflection, or a command that exits 0 on the fixture vault). A
    // line inside an in-scope section with NO marker fails the same assertion.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "F8-2 · README/SECURITY'nin her davranış satırı işaretlenmiş, her işaret docs/iddialar.md'de tam bir satıra ve çözülen bir kanıta bağlı")]
    public void EveryBehaviourLine_IsMarked_AndEveryMarkerResolvesToWorkingEvidence()
    {
        var readme = ReadRepoFile("README.md");
        var security = ReadRepoFile("SECURITY.md");
        var iddialar = ReadRepoFile("docs/iddialar.md");

        // Blocking (review, applied): LinesOfSection silently returns nothing for a
        // heading that is not present at all, so a deleted "## Configuration" (say) would
        // make UnmarkedLinesInSections find zero unmarked lines and this gate would stay
        // green while an entire scoped section vanished undetected. Every heading this
        // file scans must actually exist before the scan is allowed to "pass" on emptiness.
        var missingHeadings = ReadmeInScopeHeadings.Where(h => !SectionExists(readme, h)).Select(h => $"README.md: \"{h}\"")
            .Concat(SecurityInScopeHeadings.Where(h => !SectionExists(security, h)).Select(h => $"SECURITY.md: \"{h}\""))
            .ToArray();
        Assert.True(missingHeadings.Length == 0,
            "Taranması beklenen başlık(lar) bulunamadı — silinmiş olabilir:\n" + string.Join('\n', missingHeadings));

        var unmarked = new List<string>();
        unmarked.AddRange(UnmarkedLinesInSections(readme, ReadmeInScopeHeadings, "README.md"));
        unmarked.AddRange(UnmarkedLinesInSections(security, SecurityInScopeHeadings, "SECURITY.md"));
        Assert.True(unmarked.Count == 0,
            "İşaretsiz davranış satırı(ları) — her satır sonunda '(bkz. docs/iddialar.md#ID)' bekleniyor:\n" + string.Join('\n', unmarked));

        var referenced = MarkerPattern.Matches(readme).Select(m => m.Groups[1].Value)
            .Concat(MarkerPattern.Matches(security).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(referenced.Length > 0, "README/SECURITY'de hiç '(bkz. docs/iddialar.md#ID)' işareti bulunamadı.");

        var rows = ParseIddialarRows(iddialar);
        var missingRows = referenced.Where(id => !rows.ContainsKey(id)).ToArray();
        Assert.True(missingRows.Length == 0,
            $"docs/iddialar.md'de satırı olmayan işaret(ler): {string.Join(", ", missingRows)}");

        // Should-fix (review, applied): a row nothing marks any more is orphaned prose —
        // most likely leftover from a deleted or reworded behaviour sentence — and should
        // fail loudly rather than sit in the table unreferenced and unchecked forever.
        var orphanRows = rows.Keys.Where(id => !referenced.Contains(id, StringComparer.Ordinal)).ToArray();
        Assert.True(orphanRows.Length == 0,
            $"docs/iddialar.md'de hiçbir '(bkz. ...#ID)' tarafından kullanılmayan satır(lar): {string.Join(", ", orphanRows)}");

        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        var usageDumpFirstLine = Normalize(harness.Run(Path.Combine(harness.Root, "unused-vault"), ["--help"]).Stdout).Split('\n')[0];
        var failures = new List<string>();
        foreach (var id in referenced)
        {
            foreach (var evidence in rows[id])
            {
                var problem = ResolveEvidence(evidence, harness, vault, usageDumpFirstLine);
                if (problem is not null)
                    failures.Add($"#{id} → {evidence}: {problem}");
            }
        }

        Assert.True(failures.Count == 0, "Çözülemeyen kanıt(lar):\n" + string.Join('\n', failures));
    }

    // ------------------------------------------------------------------
    // Assertion 3 — F8-3: SECURITY.md's two structural claims ('egress gate' and 'nothing
    // else is called') are tied to the SPECIFIC marker tests the lane brief names, not just
    // any evidence — so a docs edit that swaps in a weaker test is still caught.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "F8-3 · SECURITY.md'nin egress/'başka hiçbir şey çağrılmaz' iddiaları isimlendirilen marker testlerine bağlı")]
    public void SecurityStructuralClaims_AreTiedToTheNamedMarkerTests()
    {
        var security = ReadRepoFile("SECURITY.md");

        if (security.Contains("egress", StringComparison.OrdinalIgnoreCase) || security.Contains("Guards", StringComparison.Ordinal))
        {
            Assert.True(MethodExists("Oom.Tests.Kabul.FlushKabul", "GuardedDirectiveInSummary_IsRefused_QuarantinesRawOutput_LeavesDailyByteIdentical"),
                "SECURITY.md bir egress/Guards iddiası taşıyor ama FlushKabul'ün çıkış-noktası reddetme testi yok/adı değişmiş.");
        }

        var claimsNothingElseIsCalled =
            security.Contains("invoke no other program", StringComparison.OrdinalIgnoreCase) ||
            security.Contains("başka hiçbir", StringComparison.OrdinalIgnoreCase) ||
            security.Contains("nothing else is called", StringComparison.OrdinalIgnoreCase);

        if (claimsNothingElseIsCalled)
        {
            Assert.True(MethodExists("Oom.Tests.Kabul.DoctorKabul", "Doctor_NeverInvokesCodebaseMemoryMcpOrAgentReach_EvenWhenBothAreOnPath"),
                "SECURITY.md 'başka hiçbir şey çağrılmaz' diyor ama L3-doctor'ın marker testi yok/adı değişmiş.");
            Assert.True(MethodExists("Oom.Tests.Kabul.ContextKabul", "Assertion9_ExtensionContextLineNeverExecuted_WarnsInstead"),
                "SECURITY.md 'başka hiçbir şey çağrılmaz' diyor ama L1-context'in extensions[].contextLine marker testi yok/adı değişmiş.");
        }
        else
        {
            Assert.Fail("SECURITY.md artık 'oom, claude dışında başka bir şey çalıştırmaz' iddiasını taşımıyor gibi görünüyor — kaldırıldıysa bu testin kendisi de güncellenmeli, sessizce geçmemeli.");
        }
    }

    // ------------------------------------------------------------------
    // Assertion 4 — F6/F8 cross-check: every flag `oom --help` documents is accepted by the
    // parser it documents — none of them falls through to the top-level usage dump (which
    // only happens when Command() sees an unrecognised command word). Parsed straight from
    // the live help text and Oom.Contracts.CommandLine.ValueOptions, so a future flag rename
    // is picked up automatically; nothing here is a fixed, hand-maintained flag list.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "Belgelenen her bayrak ayrıştırıcı tarafından kabul edilir: hiçbiri en üst düzey kullanım dökümüne düşmez")]
    public void EveryDocumentedFlag_IsAcceptedByTheParser_NoneFallsThroughToUsageDump()
    {
        using var harness = new KabulHarness();
        var help = harness.Run(Path.Combine(harness.Root, "unused-vault"), ["--help"]);
        var pairs = ParseCommandFlagPairs(help.Stdout);
        Assert.True(pairs.Count > 10, $"beklenenden az (komut, bayrak) çifti ayrıştırıldı: {pairs.Count}");

        var vault = KabulVaultBuilder.Build(harness.NewScratchDirectory("flag-matrix-vault"));
        var batchFile = Path.Combine(harness.Root, "batch.txt");
        File.WriteAllText(batchFile, "test\n", Utf8);
        var transcriptFile = Path.Combine(harness.Root, "transcript.jsonl");
        File.WriteAllText(transcriptFile, "{}\n", Utf8);
        var sessionJsonFile = Path.Combine(harness.Root, "session.json");
        File.WriteAllText(sessionJsonFile, "{}", Utf8);
        var emptyKitDir = harness.NewScratchDirectory("empty-kit");

        var sampleValues = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--session"] = "s1",
            ["--reason"] = "sessionend",
            ["--transcript"] = transcriptFile,
            ["--query"] = "test",
            ["--top"] = "3",
            ["--batch"] = batchFile,
            ["--session-json"] = sessionJsonFile,
            ["--only"] = "yok-boyle-bir-bilesen",
            ["--kit"] = emptyKitDir,
        };

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        // O21 (driver, applied): `doctor` reads the Claude hook settings
        // (Program.Doctor.UserProfileRoot()) and a plain `doctor`/`kit status`/`kit
        // install` all resolve the kit's home root (Program.Kit.HomeRoot()) — both honour
        // OOM_USERPROFILE, which KabulHarness.Run now sets to its own isolated,
        // per-harness scratch profile on EVERY call by default (never the developer's
        // real ~/.claude or ~/.agents). No special-casing needed here any more: every
        // command in this matrix, "doctor"/"kit" included, goes through the same
        // harness.Run call below.
        var usageDumpFirstLine = Normalize(help.Stdout).Split('\n')[0];
        var failures = new List<string>();
        foreach (var (command, flag) in pairs)
        {
            var args = new List<string>(command) { flag };
            if (CommandLine.ValueOptions.Contains(flag, StringComparer.OrdinalIgnoreCase))
            {
                if (!sampleValues.TryGetValue(flag, out var value))
                {
                    failures.Add($"{string.Join(' ', command)} {flag}: bu bayrak için örnek değer tanımlı değil");
                    continue;
                }

                args.Add(value);
            }

            var result = harness.Run(vault, args, fakeNow: KabulVaultBuilder.DefaultToday, timeout: TimeSpan.FromSeconds(45));
            var firstLine = Normalize(result.Stdout).Split('\n')[0];
            if (string.Equals(firstLine, usageDumpFirstLine, StringComparison.Ordinal))
                failures.Add($"{string.Join(' ', args)}: en üst düzey kullanım dökümüne düştü (exit {result.ExitCode})\nstderr: {result.Stderr}");
        }

        Assert.True(failures.Count == 0, "Kullanım dökümüne düşen bayrak(lar):\n" + string.Join('\n', failures));
    }

    // (Driver, integration: the proposal-file check that stood here was removed — it
    // tested this build process's own proposal files outside the repo, not product
    // behaviour, and pinned a machine path. The proposals are reviewed by Master.)

    // ==================================================================
    // helpers
    // ==================================================================

    // Blocking (review, applied): "## Commands" was excluded on the theory that Assertion
    // 1 already covers it byte-for-byte — but Assertion 1 only diffs the fenced ```
    // block; the --version/MCP-version-equality paragraph directly below the fence is
    // ordinary prose with zero coverage. It is now in scope like every other section;
    // LinesOfSection already skips the fenced block itself (inFence toggling).
    private static readonly string[] ReadmeInScopeHeadings = ["## How it works", "## Install", "## Commands", "## Configuration", "## Vault layout"];
    private static readonly string[] SecurityInScopeHeadings = ["# Security"];

    private static bool SectionExists(string content, string headingPrefix) =>
        Array.FindIndex(Normalize(content).Split('\n'), l => l.StartsWith(headingPrefix, StringComparison.Ordinal)) >= 0;

    private static IEnumerable<string> UnmarkedLinesInSections(string content, string[] headingPrefixes, string fileLabel)
    {
        foreach (var prefix in headingPrefixes)
        {
            foreach (var line in LinesOfSection(content, prefix))
            {
                if (!MarkerPattern.IsMatch(line))
                    yield return $"{fileLabel} [{prefix}]: \"{line.Trim()}\"";
            }
        }
    }

    /// <summary>Yields every in-scope (non-empty, non-table, non-code-fence, non-heading)
    /// line of the section that starts at a line beginning with <paramref name="headingPrefix"/>
    /// and ends at the next line starting with "# " or "## " (or end of file). For
    /// "# Security" this is effectively the file's opening paragraphs, since the next
    /// heading is "## Reporting a vulnerability".</summary>
    private static IEnumerable<string> LinesOfSection(string content, string headingPrefix)
    {
        var lines = Normalize(content).Split('\n');
        var index = Array.FindIndex(lines, l => l.StartsWith(headingPrefix, StringComparison.Ordinal));
        if (index < 0)
            yield break;

        var inFence = false;
        for (var i = index + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
                continue;

            if (trimmed.StartsWith("# ", StringComparison.Ordinal) || trimmed.StartsWith("## ", StringComparison.Ordinal))
                yield break;

            if (trimmed.Length == 0 || trimmed.StartsWith("|", StringComparison.Ordinal) || trimmed.StartsWith("### ", StringComparison.Ordinal))
                continue;

            yield return line;
        }
    }

    private static string? FencedBlockAfterHeading(string content, string heading)
    {
        var lines = Normalize(content).Split('\n');
        var index = Array.FindIndex(lines, l => l.StartsWith(heading, StringComparison.Ordinal));
        if (index < 0)
            return null;

        var start = -1;
        for (var i = index + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
            {
                start = i + 1;
                break;
            }
        }

        if (start < 0)
            return null;

        var end = start;
        while (end < lines.Length && !lines[end].Trim().StartsWith("```", StringComparison.Ordinal))
            end++;

        return string.Join('\n', lines[start..end]);
    }

    /// <summary>Parses docs/iddialar.md's table into id -> evidence-entries (each row's last
    /// non-empty cell, split on ';'). Header/separator rows (containing "---" only) are
    /// skipped; a row before the first "| ID |" header is ignored. Should-fix (review,
    /// applied): a repeated ID used to be silently overwritten by whichever row came last
    /// (plain dictionary-indexer assignment) — <c>TryAdd</c> now makes a repeated ID a hard
    /// parse failure instead.</summary>
    private static IReadOnlyDictionary<string, string[]> ParseIddialarRows(string content)
    {
        var rows = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sawHeader = false;
        foreach (var rawLine in Normalize(content).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("|", StringComparison.Ordinal))
                continue;

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (!sawHeader)
            {
                if (cells.Length > 0 && cells[0].Equals("ID", StringComparison.OrdinalIgnoreCase))
                    sawHeader = true;
                continue;
            }

            if (cells.Length == 0 || cells.All(c => c.Length == 0 || c.All(ch => ch == '-')))
                continue;

            if (cells.Length < 3)
                continue;

            var id = cells[0];
            var evidence = cells[^1];
            if (id.Length == 0 || evidence.Length == 0)
                continue;

            if (!rows.TryAdd(id, evidence.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                throw new InvalidOperationException($"docs/iddialar.md: yinelenen ID '{id}' — her ID tam olarak bir satıra karşılık gelmeli.");
        }

        return rows;
    }

    // Blocking (review, partially applied): a bare reflection existence check let ANY
    // method sharing a name satisfy a "test:" row — a renamed helper, a private no-op, a
    // stub with an empty body — while claiming the referenced behaviour is proven. Full
    // atomic per-clause behavioural probes (running every referenced Kabul test's actual
    // body from inside DocsKabul, and covering every config-table cell individually) is a
    // much larger redesign than one lane-fix round can responsibly carry, and is tracked
    // as follow-up rather than attempted here (see lane report). What ships in this round
    // narrows the gap without that redesign: "test:" now requires the referenced member to
    // be a real [Fact]/[Theory] xUnit test (not just a method with a matching name), and
    // requires it NOT be excluded from the interim gate's own filter
    // (`Kabul!=Regresyon&Kabul!=RetrieveSet&Category!=IntentionalRed`) — since every
    // Kabul-category test DOES run for real, and must pass for real, in that same gate
    // alongside DocsKabul, a "test:" row naming one is only as strong as reflection-proven
    // existence PLUS "this method is not quietly excluded from ever running". "cmd:" now
    // also asserts the command was not silently swallowed by the top-level usage dump
    // (unrecognised-command fallback), which used to exit 0 exactly like a real success.
    private static readonly string[] GateExcludedTraitValues = ["Regresyon", "RetrieveSet", "IntentionalRed"];

    /// <summary>Resolves one evidence entry; returns null when it checks out, otherwise a
    /// human-readable reason it did not.</summary>
    private static string? ResolveEvidence(string evidence, KabulHarness harness, string vault, string usageDumpFirstLine)
    {
        if (evidence.StartsWith("test:", StringComparison.Ordinal))
        {
            var full = evidence["test:".Length..];
            var lastDot = full.LastIndexOf('.');
            if (lastDot < 0)
                return "geçersiz test kimliği (nokta yok)";

            var typeName = full[..lastDot];
            var methodName = full[(lastDot + 1)..];
            return ResolveTestEvidence(typeName, methodName);
        }

        if (evidence.StartsWith("cmd:", StringComparison.Ordinal))
        {
            var commandLine = evidence["cmd:".Length..];
            var args = Tokenize(commandLine);
            var result = harness.Run(vault, args, fakeNow: KabulVaultBuilder.DefaultToday, timeout: TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0)
                return $"'{commandLine}' exit {result.ExitCode} verdi (beklenen 0). stderr: {result.Stderr}";

            var firstLine = Normalize(result.Stdout).Split('\n')[0];
            if (string.Equals(firstLine, usageDumpFirstLine, StringComparison.Ordinal))
                return $"'{commandLine}' exit 0 verdi ama en üst düzey kullanım dökümüne düştü (komut tanınmadı)";

            return null;
        }

        return $"tanınmayan kanıt biçimi: {evidence}";
    }

    private static string? ResolveTestEvidence(string typeFullName, string methodName)
    {
        var type = Assembly.GetExecutingAssembly().GetType(typeFullName);
        if (type is null)
            return $"tür bulunamadı: {typeFullName}";

        var method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == methodName);
        if (method is null)
            return $"metot bulunamadı: {typeFullName}.{methodName}";

        var isFactOrTheory = method.GetCustomAttributes(inherit: true)
            .Any(a => a is FactAttribute or TheoryAttribute);
        if (!isFactOrTheory)
            return $"{typeFullName}.{methodName} bir [Fact]/[Theory] değil — kanıt olarak adlandırılan bir metodun gerçekten koşan bir Kabul testi olması gerekir";

        // Xunit.TraitAttribute carries its (name, value) pair only in the attribute's OWN
        // constructor arguments (measured: it has no public Name/Value property in xunit
        // 2.9.2 — GetProperties() returns nothing but the inherited TypeId; the trait
        // discovery pipeline reads them off IAttributeInfo instead), so CustomAttributeData
        // is the only way to read a trait's value back by plain reflection here.
        var excludedTrait = method.GetCustomAttributesData()
            .Concat(type.GetCustomAttributesData())
            .Where(cad => cad.AttributeType == typeof(TraitAttribute) && cad.ConstructorArguments.Count == 2)
            .Select(cad => (Name: cad.ConstructorArguments[0].Value as string, Value: cad.ConstructorArguments[1].Value as string))
            .FirstOrDefault(t => (t.Name == "Kabul" || t.Name == "Category") && GateExcludedTraitValues.Contains(t.Value, StringComparer.Ordinal));
        if (excludedTrait.Name is not null)
            return $"{typeFullName}.{methodName}, ara kapının kendi filtresinden dışlanmış (Trait {excludedTrait.Name}={excludedTrait.Value}) — bu kanıt hiç koşmuyor";

        return null;
    }

    /// <summary>Used by Assertion 3, which names a SPECIFIC test method rather than
    /// resolving a docs/iddialar.md row — same strengthened check as "test:" evidence
    /// (must be a real, gate-included [Fact]/[Theory]), just returning a bool.</summary>
    private static bool MethodExists(string typeFullName, string methodName) =>
        ResolveTestEvidence(typeFullName, methodName) is null;

    /// <summary>Tokenizes a shell-like command line respecting double-quoted segments (the
    /// only quoting docs/iddialar.md's "cmd:" entries use, e.g. cmd:save "iki kelime").</summary>
    private static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    /// <summary>Parses `oom --help`'s own two-space-indent command headers / four-space-indent
    /// flag lines into (command argv, flag) pairs. "kit status"/"kit install" become
    /// two-word command argvs; the global "  --vault &lt;yol&gt;" line is skipped (it belongs
    /// to no single command).</summary>
    private static IReadOnlyList<(string[] Command, string Flag)> ParseCommandFlagPairs(string helpText)
    {
        var pairs = new List<(string[], string)>();
        string[]? current = null;
        foreach (var raw in Normalize(helpText).Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;

            var fourSpace = raw.StartsWith("    ", StringComparison.Ordinal);
            var twoSpace = !fourSpace && raw.StartsWith("  ", StringComparison.Ordinal);

            if (twoSpace)
            {
                if (trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    current = null;
                    continue;
                }

                var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                current = trimmed.StartsWith("kit ", StringComparison.Ordinal) ? [words[0], words[1]] : [words[0]];
                continue;
            }

            if (fourSpace && current is not null && trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                var flag = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                pairs.Add((current, flag));
            }
        }

        return pairs;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot(), relativePath), Utf8);

    /// <summary>Same walk-up-to-Oom.sln pattern as PublicHygieneKabul.RepoRoot(), kept local
    /// here since that method is private to its own class.</summary>
    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Oom.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException($"no Oom.sln above {AppContext.BaseDirectory}; cannot locate the repo root.");
    }
}
