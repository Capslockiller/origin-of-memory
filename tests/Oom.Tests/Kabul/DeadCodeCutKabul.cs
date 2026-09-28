using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Oracle acceptance tests for L4-cut (SPEC-3.1.0.md "Cut" list, R12, build-order step 7),
/// written independently of the implementing lane. Covers the lane's three named oracle
/// assertions:
///   1. Step-7 gate: a word-boundary search of src/ and tests/ finds 0 matches for the
///      lane's named dead identifiers (two settings records' now-unread fields, four
///      contract records with zero callers, and two retired API shapes), and those two
///      retired shapes (the retrieval ranker's mode parameter, the hook-template JSON
///      renderer) are gone from the compiled surface, not merely unreferenced.
///   2. Deleting a member deletes the test that exists only to pin it (the "unknown
///      retrieval mode throws" behaviour becomes structurally impossible once its mode
///      parameter is gone; the hook-template JSON renderer's only caller must be able to
///      reach the same hook-path validation through the builder alone).
///   3. R12: oom.json setting the two dead compile fields, the dead retrieval-mode field
///      and the dead sweep-interval field must each produce a 'bilinmeyen ayar: &lt;key&gt;'
///      stderr line, for every command that loads settings, while the process still
///      exits normally.
///
/// This file deliberately never spells any of the ten dead names/phrases as a single
/// contiguous run of source text (they are assembled from separate literals at compile
/// time — see <see cref="BuildWordBoundaryTokens"/> and <see cref="LiteralTargets"/>):
/// SPEC's own step-7 gate is itself a word-boundary/literal grep of tests/, and this file
/// lives under tests/, so writing any of the ten contiguously here would make this very
/// oracle fail its own gate once the rest of the cut is done.
///
/// Per SPEC R3 this is a NEW file under tests/Oom.Tests/Kabul/ — it never edits
/// KabulHarness, KabulVaultBuilder or FakeClaude, and it owns no existing file.
/// </summary>
public sealed class DeadCodeCutKabul
{
    private static readonly string[] DeadIdentifiers = BuildWordBoundaryTokens();
    private static readonly string[] LiteralTargets = BuildLiteralTargets();

    [Fact(DisplayName = "L4-cut #1 · Ölü üyeler src/ ve tests/ içinde kelime sınırıyla 0 eşleşme verir")]
    public void DeadMembers_AreAbsentFromSourceAndTests()
    {
        var repositoryRoot = ScarFixture.RepositoryRoot();
        var files = SourceFiles(repositoryRoot, ThisFilePath());
        Assert.True(files.Count > 50, $"beklenenden az kaynak dosyası tarandı: {files.Count}");

        var hits = new List<string>();
        foreach (var token in DeadIdentifiers)
        {
            var pattern = new Regex($@"\b{Regex.Escape(token)}\b");
            foreach (var file in files)
            {
                var count = pattern.Matches(File.ReadAllText(file)).Count;
                if (count > 0)
                    hits.Add($"[{token}] {count}x {RelativePath(repositoryRoot, file)}");
            }
        }

        foreach (var target in LiteralTargets)
        {
            foreach (var file in files)
            {
                var count = CountLiteral(File.ReadAllText(file), target);
                if (count > 0)
                    hits.Add($"[{target}] {count}x {RelativePath(repositoryRoot, file)}");
            }
        }

        Assert.True(hits.Count == 0,
            "SPEC-3.1.0.md L4-cut step-7 gate: a word-boundary/literal search of src/ and tests/ must find 0 " +
            "matches for the lane's ten named dead tokens (see this file's own DeadIdentifiers/LiteralTargets, " +
            "built from split fragments so this oracle file itself never trips its own gate). Each is either " +
            "unreferenced dead code, a dead settings field the Cut list/R12 require removed, or a retired API " +
            $"shape, together with the tests that exist only to pin it. Found {hits.Count} live match(es), " +
            "token-in-brackets then count then file:\n" + string.Join('\n', hits));
    }

    [Fact(DisplayName = "L4-cut #1/#2 · Retrieve.Rank artık bir mod parametresi almaz (bilinmeyen mod fırlatma davranışı yapısal olarak imkansızlaşır)")]
    public void RetrieveRank_NoLongerAcceptsAModeParameter()
    {
        // Behavioural, not textual: read the live method signature through reflection so
        // this test compiles whether or not the 3-arg overload still exists. A test for a
        // member's ABSENCE must never itself require that member to exist to compile, so
        // this never calls the retired overload directly.
        var method = typeof(Retrieve).GetMethod("Rank", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, "Retrieve.Rank bulunamadı; Retrieve sınıfının genel arama sözleşmesi değişmiş olabilir.");

        var parameters = method!.GetParameters();
        Assert.True(parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(IReadOnlyList<Note>),
            "SPEC Cut list: the ranking method's mode selector parameter must be removed entirely — a query and " +
            "a note list only, single always-BM25 ranking, no defaulted mode string and no way to request an " +
            "unrecognised ranking mode. A prior test pinned throwing ArgumentException for an unrecognised mode " +
            $"string; that behaviour must become structurally unreachable, not merely untested. Got " +
            $"{parameters.Length} parameter(s): " +
            string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}" + (p.HasDefaultValue ? $" = {p.DefaultValue}" : string.Empty))));
    }

    [Fact(DisplayName = "L4-cut #1/#2 · Kanca şablonlarının JSON üreteci artık bulunmaz; JSON'suz üretici tek başına aynı denetimi besler")]
    public void HookTemplateJsonRenderer_IsGoneAndItsBuilderAloneStillFeedsHookPathValidation()
    {
        // "Render" alone (without the JSON-renderer's owning type name immediately
        // before it, as SPEC's grep requires) is a common enough word that checking for
        // it here would be meaningless; the removal itself is proven precisely, and
        // without ever writing the dotted method reference as contiguous text, by
        // reflecting over the owning type's public static surface.
        var ownerType = typeof(HookTemplates);
        var renderMethod = ownerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Render");
        Assert.True(renderMethod is null,
            "SPEC Cut list: the hook-template JSON-rendering method is dead code (its only caller was a Scars " +
            "test rewritten, per the lane note, onto the plain hook-registration builder). It must be deleted " +
            "outright, not just stop being called.");

        // The replacement path the rewritten test must land on: the builder alone still
        // carries every field Doctor's hook-path validation reads (Event/Command), so
        // rewriting that test onto it loses no coverage. Checked here as a live
        // characterization (already true today, since the builder predates this cut) so
        // a future change to its shape that would silently break that rewrite is caught.
        var registrations = ownerType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, ["C:/oom/oom.exe", "C:/vault"]);
        var list = Assert.IsAssignableFrom<System.Collections.IEnumerable>(registrations).Cast<object>().ToList();
        Assert.Contains(list, r => (string)r.GetType().GetProperty("Event")!.GetValue(r)! == "SessionStart"
            && ((string)r.GetType().GetProperty("Command")!.GetValue(r)!).Contains("context", StringComparison.Ordinal));
        Assert.Contains(list, r => (string)r.GetType().GetProperty("Event")!.GetValue(r)! == "SessionEnd"
            && ((string)r.GetType().GetProperty("Command")!.GetValue(r)!).Contains("sessionend", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "L4-cut #3 (R12) · İki ölü compile ayarı ve önceden ölü iki ayar birlikte her komutta 'bilinmeyen ayar' verir, süreç normal çıkar")]
    public void UnknownDeadConfigKeys_WarnOnStderrForEveryCommandAndExitNormally()
    {
        using var harness = new KabulHarness();
        var vault = KabulVaultBuilder.Build(harness.Root);
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"),
            """
            {
              "compile": { "eveningHour": 5, "minIntervalHours": 10, "maxDailiesPerRun": 3 },
              "retrieveMode": "vektor",
              "sweep": { "everyHours": 4 }
            }
            """);

        // R12 says the two compile fields become dead KEYS, so the warning must fire for
        // every command that loads OomSettings, not just one — two independently
        // dispatched, otherwise unrelated commands (a read-only context render and the
        // doctor health check) are run through the same oom.json to demonstrate that.
        foreach (var args in new[] { new[] { "context" }, new[] { "doctor", "--quiet" } })
        {
            var result = harness.Run(vault, args, fakeNow: KabulVaultBuilder.DefaultToday);

            AssertWarnsForDeadCompileHourField(result, "evening", args);
            AssertWarnsForDeadCompileHourField(result, "minInterval", args);
            AssertWarnsForAlreadyDeadKey(result, "retrieveMode", args);
            AssertWarnsForAlreadyDeadKey(result, "everyHours", args);

            Assert.True(result.ExitCode is 0 or 1,
                $"SPEC R12: an unknown/dead settings key must never crash the process — 'oom {string.Join(' ', args)}' " +
                "must exit the same way it would with a clean oom.json (0 for context, 0 or 1 for doctor " +
                $"depending on health), never a caught top-level exception. Got exit {result.ExitCode}. " +
                $"stderr: '{result.Stderr}'; stdout: '{result.Stdout}'");
        }
    }

    private static void AssertWarnsForDeadCompileHourField(KabulResult result, string prefix, string[] args)
    {
        var key = prefix + "Hour" + (prefix == "minInterval" ? "s" : string.Empty);
        // Tolerant of either the bare key (the existing convention every other nested
        // dead key in Configuration.cs already uses, e.g. sweep.everyHours warns as
        // plain 'everyHours') or a 'compile.'-prefixed form: the oracle pins the
        // BEHAVIOUR (the key becomes unknown), not this one unestablished formatting
        // choice.
        var matches = Regex.IsMatch(result.Stderr, $@"bilinmeyen ayar: (compile\.)?{Regex.Escape(key)}\b");
        Assert.True(matches,
            $"SPEC R12: 'oom {string.Join(' ', args)}' stderr must contain 'bilinmeyen ayar: {key}' (optionally " +
            $"'compile.{key}') once the two " +
            "compile settings fields become dead keys (R12: nothing consumes the compile 'decision' any more, so " +
            "Configuration.cs must move them from the read/known list to the unknown-key warning list). This " +
            "tree's current code still structurally reads compile.eveningHour/compile.minIntervalHours into the " +
            $"compile options record, so neither ever reaches the unknown-keys list and this line never appears. " +
            $"Got stderr: '{result.Stderr}'; exit {result.ExitCode}; stdout: '{result.Stdout}'");
    }

    private static void AssertWarnsForAlreadyDeadKey(KabulResult result, string key, string[] args)
    {
        Assert.True(result.Stderr.Contains($"bilinmeyen ayar: {key}", StringComparison.Ordinal),
            $"SPEC R12/F6-4: 'oom {string.Join(' ', args)}' stderr must contain 'bilinmeyen ayar: {key}' — this " +
            "one is already produced by this tree's current code (an earlier lane's fix), kept in this same " +
            "assertion only because SPEC's oracle assertion #3 names all four dead keys together as one " +
            $"behaviour. Got stderr: '{result.Stderr}'; exit {result.ExitCode}; stdout: '{result.Stdout}'");
    }

    private static int CountLiteral(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    private static List<string> SourceFiles(string repositoryRoot, string excludeFile)
    {
        var excludeFull = string.IsNullOrEmpty(excludeFile) ? null : Path.GetFullPath(excludeFile);
        var roots = new[] { Path.Combine(repositoryRoot, "src"), Path.Combine(repositoryRoot, "tests") };
        var files = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                var full = Path.GetFullPath(file);
                if (excludeFull is not null && string.Equals(full, excludeFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                files.Add(full);
            }
        }
        return files;
    }

    private static string RelativePath(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    // [CallerFilePath] only resolves at the CALL SITE's own source location, and xUnit
    // forbids [Fact] methods from taking parameters — so the path is captured through
    // this private, argument-less helper instead of a Fact-method default parameter.
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // Builds the eight word-boundary identifiers from separate fragments so this file's
    // own source text never contains any of them as a contiguous token (see class doc
    // comment: this file lives under tests/, which the gate itself scans).
    private static string[] BuildWordBoundaryTokens() =>
    [
        "Usage" + "Record",
        "Usage" + "Summary",
        "Hook" + "State",
        "Wait" + "Result",
        "Every" + "Hours",
        "Retrieve" + "Mode",
        "Evening" + "Hour",
        "MinInterval" + "Hours",
    ];

    // The remaining two step-7 targets are phrases, not single identifiers (one spans a
    // '.', the other is a whole default-parameter declaration), so they are assembled
    // the same way, from fragments no single one of which contains the target text.
    private static string[] BuildLiteralTargets() =>
    [
        "Hook" + "Templates" + "." + "Render",
        "string mode = " + "\"bm25\"",
    ];
}
