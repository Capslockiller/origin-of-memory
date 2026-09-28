using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Oom.Tests.Kabul;

/// <summary>
/// New file added by the L3-privacy lane (SPEC-3.1.0.md R2; wave-2 Codex review blocker:
/// RetrieveKabul embedded a real case id, a real panel name and a private machine path
/// directly in public test source). Reads forbidden_terms and private_path_roots from the
/// private JSON (<see cref="PrivateEval"/>, OOM_KABUL_PRIVATE) and scans every GIT-TRACKED
/// blob under src/, tests/, docs/, kit/, tools/ and the repo root — never untracked files,
/// never bin/obj — for a separator/case-normalized substring match. A hit reports only the
/// 1-based index of the term and the offending file:line; the term text itself is never
/// printed, in this test's output or in an assertion message.
///
/// Codex review (wave 3): the gate reads INDEX blobs (git cat-file -p &lt;sha&gt; against the
/// shas git ls-files -s reports), never the working tree — staged content that hasn't been
/// re-materialized to disk (or that a working-tree edit has since diverged from) is what
/// actually gets published, so that is what must be proven clean. Every in-scope blob is
/// either scanned or counted as a scan failure that fails the gate closed: a missing SHA, a
/// git process failure, or content that does not decode as UTF-8 (a binary blob would hide a
/// leak from a text scan) all fail the run rather than being silently skipped.
///
/// RED on the tree this lane started from: RetrieveKabul.cs carried a real personal case
/// id and a real panel name as literal query text, RedactorKabul.cs carried the file:line
/// of three leaked real secrets, and all three of RetrieveKabul.cs, ContextKabul.cs and
/// RedactorKabul.cs hard-coded the private vault-kopya path as a fallback constant. Those
/// are now read from the private JSON instead (see PrivateEval in OzelKabul.cs) — GREEN
/// once that move is complete and nothing else in the scanned tree repeats the same terms.
/// (This class-level comment is itself scanned by the test below — SPEC-3.1.0.md R2 does
/// not carve out an exception for comments, and this lane's own first build proved why:
/// see the report.)
/// </summary>
[Trait("Kabul", "OzelVault")]
public sealed class PublicHygieneKabul(ITestOutputHelper output)
{
    private static readonly string[] ScannedTopLevelDirectories = ["src", "tests", "docs", "kit", "tools"];

    [Fact(DisplayName = "R2 · İzlenen ağacın git index blob'larında hiçbir yasak terim/özel yol kökü geçmez (yalnız dizin ve dosya:satır basılır, terimin kendisi asla basılmaz; eksik/okunamayan/çözülemeyen blob kapıyı kapalı-başarısız yapar)")]
    public void TrackedTree_ContainsNoForbiddenTerms()
    {
        var eval = PrivateEval.Load();
        Assert.True(eval.ForbiddenTerms.Count > 0, $"{PrivateEval.EnvironmentVariable}: forbidden_terms boş. Bu test hiç atlanmaz.");
        Assert.True(eval.PrivatePathRoots.Count > 0, $"{PrivateEval.EnvironmentVariable}: private_path_roots boş. Bu test hiç atlanmaz.");
        var terms = eval.ForbiddenTerms.Concat(eval.PrivatePathRoots).ToArray();

        var repoRoot = RepoRoot();
        var blobs = TrackedBlobs(repoRoot);
        Assert.True(blobs.Count > 0, $"git ls-files -s boş döndü: {repoRoot}");
        output.WriteLine($"R2: {blobs.Count} izlenen index blob'u, {terms.Length} yasak terim/kök taranıyor.");

        var hits = new List<string>();
        var scanFailures = new List<string>();
        foreach (var (relativePath, sha) in blobs)
        {
            string text;
            try
            {
                text = ReadBlobAsUtf8Text(repoRoot, sha);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DecoderFallbackException)
            {
                // Fail closed: a blob this gate cannot read or decode is a scan failure, not a
                // skip — it must not be able to hide a leak from a text-substring scan.
                scanFailures.Add($"{relativePath} ({ex.GetType().Name})");
                continue;
            }

            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                for (var termIndex = 0; termIndex < terms.Length; termIndex++)
                {
                    if (ContainsNormalized(lines[lineNumber], terms[termIndex]))
                        hits.Add($"terim #{termIndex + 1} · {relativePath}:{lineNumber + 1}");
                }
            }
        }

        foreach (var hit in hits)
            output.WriteLine(hit);
        foreach (var failure in scanFailures)
            output.WriteLine($"R2: TARAMA HATASI (kapalı-başarısız) · {failure}");

        Assert.True(scanFailures.Count == 0,
            $"R2: {scanFailures.Count} izlenen blob okunamadı/UTF-8 olarak çözülemedi — gate hiçbirini atlamaz, kapalı-başarısız olur:\n" +
            string.Join('\n', scanFailures));
        Assert.True(hits.Count == 0,
            $"R2: izlenen ağaçta {hits.Count} yasak terim eşleşmesi var (yalnız dizin ve dosya:satır basılır, terimlerin kendisi hiç basılmaz):\n" +
            string.Join('\n', hits));
    }

    [Fact(DisplayName = "R2 · terim eşleşmesi yol ayırıcılarını normalize eder: forward-slash terim hem forward hem backslash biçimini yakalar (sentetik kök, gerçek özel yol değil)")]
    public void ContainsNormalized_MatchesBothPathSeparatorForms()
    {
        // Codex review (wave 3, R2 gate #3): proves the normalization logic in isolation with
        // a SYNTHETIC drive-letter root — never the real private path, which this test file
        // itself must never embed.
        const string term = "E:/Fake/Ozel/Makine/Yolu";
        Assert.True(ContainsNormalized("kayit: E:/Fake/Ozel/Makine/Yolu/dosya.md", term));
        Assert.True(ContainsNormalized(@"kayit: E:\Fake\Ozel\Makine\Yolu\dosya.md", term));
        Assert.False(ContainsNormalized("kayit: E:/Baska/Yer/dosya.md", term));
    }

    /// <summary>Separator- and case-normalized substring match: a forward-slash term also
    /// catches the same path written with backslashes (Codex review, wave 3, R2 gate #3).</summary>
    private static bool ContainsNormalized(string haystack, string needle)
        => Normalize(haystack).Contains(Normalize(needle), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => value.Replace('\\', '/');

    /// <summary>Walks up from the test assembly's own output directory to find the repo
    /// root (the directory containing Oom.sln) — same pattern RetrieveSetKabul.RepoFile
    /// uses, kept local here since that method is private to its own class.</summary>
    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Oom.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException($"no Oom.sln above {AppContext.BaseDirectory}; cannot locate the repo root.");
    }

    /// <summary>Every git-tracked (never untracked, never bin/obj — those are simply not in
    /// the index) blob's (relative path, index SHA) under src/, tests/, docs/, kit/, tools/,
    /// plus files that sit directly at the repo root (no further subdirectory). '-s -z': the
    /// index SHA per entry (so the caller reads the STAGED blob, not the working-tree file —
    /// Codex review, wave 3) and NUL-separated, unquoted paths (the repo has non-ASCII
    /// directory names, e.g. the Companion dir).</summary>
    private static IReadOnlyList<(string RelativePath, string Sha)> TrackedBlobs(string repoRoot)
    {
        var stdout = RunGit(repoRoot, "git ls-files -s -z", ["ls-files", "-s", "-z"]);

        var entries = new List<(string, string)>();
        foreach (var rawEntry in stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = rawEntry.IndexOf('\t');
            Assert.True(tab > 0, "git ls-files -s -z: beklenmeyen satır biçimi (tab yok).");
            var meta = rawEntry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(meta.Length >= 2, "git ls-files -s -z: beklenmeyen meta alan sayısı.");
            var relativePath = rawEntry[(tab + 1)..];
            if (IsInScope(relativePath))
                entries.Add((relativePath, meta[1]));
        }

        return entries;
    }

    /// <summary>Reads one blob straight from the git object database (the STAGED content,
    /// never the working-tree file — Codex review, wave 3) and decodes it as strict UTF-8:
    /// an invalid byte sequence (e.g. a binary blob) throws rather than silently passing
    /// through, so it becomes a scan failure at the call site instead of a silent skip.</summary>
    private static string ReadBlobAsUtf8Text(string repoRoot, string sha)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("cat-file");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(sha);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"git cat-file başlatılamadı ({sha}).");
        using var content = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(content);
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
            throw new InvalidOperationException($"git cat-file {sha} 30 saniyede bitmedi.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git cat-file {sha} exit {process.ExitCode}: {stderr}");

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        return strictUtf8.GetString(content.ToArray());
    }

    /// <summary>Runs one git subcommand with UTF-8-decoded stdout (the repo has non-ASCII
    /// path components) and fails closed — never returns on a nonzero exit or a timeout.</summary>
    private static string RunGit(string repoRoot, string description, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{description}: git başlatılamadı.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), $"{description}: 30 saniyede bitmedi.");
        Assert.True(process.ExitCode == 0, $"{description}: exit {process.ExitCode}: {stderr}");
        return stdout;
    }

    private static bool IsInScope(string relativePath)
    {
        var firstSlash = relativePath.IndexOf('/', StringComparison.Ordinal);
        if (firstSlash < 0)
            return true; // repo root file, no subdirectory.

        var topLevel = relativePath[..firstSlash];
        return ScannedTopLevelDirectories.Contains(topLevel, StringComparer.Ordinal);
    }
}
