using System.Text.Json;
using Xunit.Abstractions;

namespace Oom.Tests.Kabul;

/// <summary>
/// New file added by the L3-privacy lane (SPEC-3.1.0.md R2; wave-2 Codex review blocker:
/// RetrieveKabul embedded a real case id, a real panel name and a private machine path
/// directly in public test source). R3 lets a lane add its own new files under
/// tests/Oom.Tests/Kabul/ without editing the harness; this file also holds
/// <see cref="PrivateEval"/>, the shared loader RetrieveKabul.cs, ContextKabul.cs and
/// RedactorKabul.cs now use instead of hard-coding the private vault path, the private
/// queries/expected paths (F2-2, F2-3, B6, the case-id query) and the three leaked
/// file:line references.
///
/// This class covers acceptance items 2 and 3 for the private JSON contract itself:
///   2. the private JSON is well-formed and carries every field the four Kabul=OzelVault
///      consumers need (structural check, same spirit as RetrieveSetKabul's
///      ValidateFrozenStructure — not a re-check of any threshold, which stays where the
///      owning file already asserts it);
///   3. a missing OOM_KABUL_PRIVATE file fails LOUDLY — an exception, not a skip — naming
///      the environment variable, without mutating the real process-wide OOM_KABUL_PRIVATE
///      (the lane brief forbids that): PrivateEval.Load takes an explicit override path so
///      this is provable without ever setting the env var.
/// </summary>
public sealed class OzelKabul(ITestOutputHelper output)
{
    [Fact(DisplayName = "OzelKabul #1 · OOM_KABUL_PRIVATE eksik dosyayı gösterirse yükleme değişkeni adıyla yüksek sesle başarısız olur, atlanmaz")]
    public void MissingPrivateFile_FailsLoudly_NamingTheEnvironmentVariable()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "oom-kabul-yok-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.False(File.Exists(missingPath));

        // Codex review (wave 3): the exception must name the environment variable and the
        // failure category, but never echo the configured path's value — a routine
        // misconfiguration (OOM_KABUL_PRIVATE pointed somewhere that doesn't exist) must not
        // publish a private machine path into test logs.
        var exception = Assert.Throws<InvalidOperationException>(() => PrivateEval.Load(missingPath));
        Assert.Contains(PrivateEval.EnvironmentVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains("not found", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(missingPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "OzelKabul #2 · Yapısal olmayan bozuk JSON da atlanmaz, ayrıştırma hatasıyla başarısız olur")]
    public void MalformedPrivateFile_FailsLoudly_NotSkipped()
    {
        var brokenPath = Path.Combine(Path.GetTempPath(), "oom-kabul-bozuk-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(brokenPath, "{ bu gecerli json degil");
        try
        {
            Assert.ThrowsAny<JsonException>(() => PrivateEval.Load(brokenPath));
        }
        finally
        {
            File.Delete(brokenPath);
        }
    }

    [Trait("Kabul", "OzelVault")]
    [Fact(DisplayName = "OzelKabul #3 · Özel JSON her alanı taşır: vault_path var, yasak terim ve özel yol kökü listeleri dolu, dört sorgu ve üç dosya:satır referansı eksiksiz")]
    public void PrivateFile_CarriesEveryFieldTheFourOzelVaultConsumersNeed()
    {
        var eval = PrivateEval.Load();

        Assert.True(Directory.Exists(eval.VaultPath), "OzelKabul #3: eval.VaultPath yok. Bu test hiç atlanmaz.");
        Assert.True(eval.ForbiddenTerms.Count > 0, "OzelKabul #3: forbidden_terms boş.");
        // Codex review (wave 3, R2 gate #3): private_path_roots is a required, separately
        // validated field — PublicHygieneKabul must be able to prove a private machine path
        // root is actually present in what it scans for, not just an arbitrary nonempty list.
        Assert.True(eval.PrivatePathRoots.Count > 0, "OzelKabul #3: private_path_roots boş.");
        Assert.True(eval.F22.Query.Length > 0 && eval.F22.Expected.Length > 0, "OzelKabul #3: retrieve.f2_2 eksik.");
        Assert.True(eval.F23.Query.Length > 0 && eval.F23.Expected.Length > 0, "OzelKabul #3: retrieve.f2_3 eksik.");
        Assert.True(eval.B6.Query.Length > 0 && eval.B6.Expected.Length > 0, "OzelKabul #3: retrieve.b6 eksik.");
        Assert.True(eval.CaseIdQuery.Length > 0, "OzelKabul #3: retrieve.case_id eksik.");
        Assert.Equal(3, eval.RedactorTargetLines.Count);
        Assert.All(eval.RedactorTargetLines, line => Assert.True(line.File.Length > 0 && line.Line > 0));

        output.WriteLine($"OzelKabul #3: forbidden_terms={eval.ForbiddenTerms.Count}, private_path_roots={eval.PrivatePathRoots.Count}, redactor.target_lines={eval.RedactorTargetLines.Count}");
    }
}

/// <summary>
/// Shared loader for the lane's private acceptance data. Resolution order: an explicit
/// override path (tests use this to probe a MISSING file without mutating the real,
/// process-wide OOM_KABUL_PRIVATE — the lane brief forbids that mutation), then
/// OOM_KABUL_PRIVATE, then the default path beside the private retrieval eval set
/// (SPEC-3.1.0.md R2's own fixed location, outside this repo).
///
/// A missing file or malformed JSON throws — never returns null, never lets a caller
/// silently skip. Every existing Kabul=OzelVault consumer (RetrieveKabul, ContextKabul,
/// RedactorKabul) calls this before touching the private vault copy, so a broken
/// OOM_KABUL_PRIVATE surfaces as a loud, named failure on every one of them, not just here.
/// </summary>
internal static class PrivateEval
{
    public const string EnvironmentVariable = "OOM_KABUL_PRIVATE";

    public static PrivateEvalData Load(string? overridePath = null)
    {
        var path = overridePath
            ?? Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? DefaultPath();

        if (!File.Exists(path))
            // Codex review (wave 3): never echo the configured path's value here — only the
            // environment variable name and the failure category. A routine misconfiguration
            // must not publish a private machine path into test output or CI logs.
            throw new InvalidOperationException(
                $"{EnvironmentVariable}: private acceptance data not found. " +
                $"Set {EnvironmentVariable} to the private JSON's path. This is never skipped.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var vaultPath = RequireString(root, "vault_path", path);

        var forbiddenTerms = root.TryGetProperty("forbidden_terms", out var terms)
            ? terms.EnumerateArray().Select(t => t.GetString() ?? string.Empty).Where(t => t.Length > 0).ToArray()
            : [];

        // Codex review (wave 3, R2 gate #3): a separate, required field for private path
        // ROOTS (as opposed to the free-form forbidden_terms list) — PublicHygieneKabul
        // validates this is actually populated, not just any nonempty forbidden_terms array,
        // and scans for it with separator-normalized matching.
        var privatePathRoots = RequireProperty(root, "private_path_roots", path)
            .EnumerateArray().Select(t => t.GetString() ?? string.Empty).Where(t => t.Length > 0).ToArray();

        var retrieve = RequireProperty(root, "retrieve", path);
        var f22 = RequireProperty(retrieve, "f2_2", path);
        var f23 = RequireProperty(retrieve, "f2_3", path);
        var b6 = RequireProperty(retrieve, "b6", path);
        var caseId = RequireProperty(retrieve, "case_id", path);

        var redactor = RequireProperty(root, "redactor", path);
        var targetLines = RequireProperty(redactor, "target_lines", path)
            .EnumerateArray()
            .Select(t => new RedactorTargetLine(RequireString(t, "file", path), t.GetProperty("line").GetInt32()))
            .ToArray();

        return new PrivateEvalData(
            vaultPath,
            forbiddenTerms,
            privatePathRoots,
            new RetrieveQuery(RequireString(f22, "query", path), RequireString(f22, "expected_daily", path)),
            new RetrieveQuery(RequireString(f23, "query", path), RequireString(f23, "expected_slug_prefix", path)),
            new RetrieveQuery(RequireString(b6, "query", path), RequireString(b6, "target_daily", path)),
            RequireString(caseId, "query", path),
            targetLines);
    }

    /// <summary>Codex review (wave 3, R2): no absolute private machine path is embedded in
    /// public source. The default location sits one level above the repo root (both the main
    /// repo and every worktree live under 10-Aktif) — never a literal drive/root here.</summary>
    private static string DefaultPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Oom.sln")))
                return Path.Combine(directory.FullName, "..", "oom-surum-3.1", "degerlendirme", "ozel-kabul.json");
        }

        throw new InvalidOperationException($"no Oom.sln above {AppContext.BaseDirectory}; cannot derive the default {EnvironmentVariable} path.");
    }

    // Codex review (wave 3): '{path}' is never interpolated into these messages — a missing or
    // malformed field must not publish the configured private path's value either.
    private static JsonElement RequireProperty(JsonElement element, string name, string path)
        => element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidOperationException($"{EnvironmentVariable}: the private JSON is missing required field '{name}'.");

    private static string RequireString(JsonElement element, string name, string path)
        => RequireProperty(element, name, path).GetString()
            ?? throw new InvalidOperationException($"{EnvironmentVariable}: the private JSON's '{name}' field is empty.");
}

internal sealed record PrivateEvalData(
    string VaultPath,
    IReadOnlyList<string> ForbiddenTerms,
    IReadOnlyList<string> PrivatePathRoots,
    RetrieveQuery F22,
    RetrieveQuery F23,
    RetrieveQuery B6,
    string CaseIdQuery,
    IReadOnlyList<RedactorTargetLine> RedactorTargetLines);

internal sealed record RetrieveQuery(string Query, string Expected);

internal sealed record RedactorTargetLine(string File, int Line);
