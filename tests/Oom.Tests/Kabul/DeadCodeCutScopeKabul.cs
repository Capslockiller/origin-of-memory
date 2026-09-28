using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Step-7 gate ("ölü üyeler grep'te 0") for the REST of SPEC-3.1.0.md's Cut list — the
/// members DeadCodeCutKabul does not pin because L4-cut's own ten named tokens are a
/// subset of the full list. Covers the two groups the lane's diff review found were
/// enforced only by "I grepped once", not by a test:
///   - Types removed in this lane: ToolHealth, and (already gone at HEAD) FreshnessResult,
///     IngressRecord, ReconciliationResult, LaneCVaultPaths.
///   - Members removed in this lane: ProcessRunnerFactory, ToolHealthRows, LastCoverage,
///     and (already gone at HEAD) EvaluateFreshness, ContentDigests, ReadNumber,
///     VerifiedMemoryWrite, ParseTranscript.
///
/// Scans src/ ONLY, never tests/: several of these names are legitimately still written
/// in prose inside other lanes' Kabul oracle comments (e.g. DoctorKabul.cs's own doc
/// comment references "ToolHealth" while describing what was cut), and this file must
/// not fail on another lane's file it does not own. src/ carries no such comments today,
/// so the src/-only scope loses no real coverage.
///
/// Declarations and call/member-access sites use separate patterns (not one flat
/// word-boundary list) precisely so a future declaration comment such as
/// "// replaces the old ToolHealth" cannot itself trip this gate the way a naive
/// \bToolHealth\b scan of tests/ already does for DoctorKabul.cs.
/// </summary>
public sealed class DeadCodeCutScopeKabul
{
    private static readonly Regex DeclarationPattern = new(
        @"\b(class|record|struct|interface)\s+(ToolHealth|FreshnessResult|IngressRecord|ReconciliationResult|LaneCVaultPaths)\b");

    private static readonly Regex CallOrMemberAccessPattern = new(
        @"\b(ProcessRunnerFactory|ToolHealthRows|LastCoverage|EvaluateFreshness|ContentDigests|ReadNumber|VerifiedMemoryWrite|ParseTranscript)\s*\(");

    [Fact(DisplayName = "L4-cut scope · Cut listesinin geri kalanı (ToolHealth ailesi ve daha önce silinenler) src/ içinde yeniden görünmez")]
    public void RestOfCutList_NeverReappearsInSource()
    {
        var repositoryRoot = ScarFixture.RepositoryRoot();
        var files = SourceFiles(repositoryRoot, ThisFilePath());
        Assert.True(files.Count > 30, $"beklenenden az kaynak dosyası tarandı (yalnız src/): {files.Count}");

        var hits = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var relative = RelativePath(repositoryRoot, file);

            foreach (Match match in DeclarationPattern.Matches(text))
                hits.Add($"[declaration: {match.Value}] {relative}");

            foreach (Match match in CallOrMemberAccessPattern.Matches(text))
                hits.Add($"[call/member-access: {match.Value}] {relative}");
        }

        Assert.True(hits.Count == 0,
            "SPEC-3.1.0.md L4-cut step-7 gate, extended to the rest of the Cut list: src/ must contain 0 " +
            "declarations of ToolHealth/FreshnessResult/IngressRecord/ReconciliationResult/LaneCVaultPaths and " +
            "0 call/member-access sites for ProcessRunnerFactory/ToolHealthRows/LastCoverage/EvaluateFreshness/" +
            "ContentDigests/ReadNumber/VerifiedMemoryWrite/ParseTranscript. " +
            $"Found {hits.Count} live match(es), pattern-kind and match then file:\n" + string.Join('\n', hits));
    }

    private static List<string> SourceFiles(string repositoryRoot, string excludeFile)
    {
        var excludeFull = string.IsNullOrEmpty(excludeFile) ? null : Path.GetFullPath(excludeFile);
        var root = Path.Combine(repositoryRoot, "src");
        var files = new List<string>();
        if (!Directory.Exists(root))
            return files;

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
        return files;
    }

    private static string RelativePath(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string ThisFilePath([CallerFilePath] string path = "") => path;
}
