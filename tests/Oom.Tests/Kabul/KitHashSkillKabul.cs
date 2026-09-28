using System.Security.Cryptography;
using System.Text;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Lane L1-kit-hash acceptance oracle, added per review finding (KitKabul only exercises
/// a single-file "rule" component; nothing here fails on the tree before this lane's
/// fix). New file under tests/Oom.Tests/Kabul/, per SPEC-3.1.0.md R3 — KitKabul.cs itself
/// is left untouched.
///
/// Covers the object-shaped sha256 branch of Kit.TryReadExpectedHashes (skill
/// components, nested keys), plus the `kit install` side, which also changed even though
/// no oracle exercised it:
///
///   1. A skill with a nested file (references/SOURCE.md) whose bytes are corrupted
///      identically at kit source and HOME target — same defect class as KitKabul's
///      S6-2, but through the multi-key skill branch, for a nested path (exercises the
///      '/' key normalisation Kit.EnumerateFilesNoLinks/TryReadExpectedHashes relies on).
///   2. A skill manifest whose sha256 object is missing a key for a file that exists on
///      disk ("sha256 eksik: &lt;file&gt;").
///   3. A skill manifest whose sha256 object has an extra key for a file that does not
///      exist on disk ("sha256 bilinmeyen dosya: &lt;file&gt;").
///   4. A rule component whose manifest sha256 is upper-case hex — rejected outright
///      ("sha256 eksik veya geçersiz"), never case-normalised.
///   5. `kit install` on a component whose kit-tree source no longer matches the
///      manifest hash: outcome is "bozuk", process exit is non-zero, and no file is
///      written at the destination — the install path is not merely "unchanged" from a
///      contract standpoint, it now also refuses on a bad source instead of copying it.
///
/// Same seam as KitKabul.cs and Scars/KitScars.cs: Program.RunKit(args, homeRoot) with an
/// explicit, disposable homeRoot, never the real ~/.claude.
/// </summary>
public sealed class KitHashSkillKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);

    [Fact(DisplayName = "S6-4 · Skill'de iç içe dosya (references/SOURCE.md), kaynakta ve hedefte AYNI bozulmuş baytlar: 'kit status' güncel demez, sha256 anar, exit ≠ 0")]
    public void SkillNestedFile_CorruptedSourceMatchingCorruptedTarget_IsNeverReportedGuncel()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            var skillSource = Path.Combine(kitRoot, "skills", "demo-skill");
            Directory.CreateDirectory(Path.Combine(skillSource, "references"));
            var skillMainBytes = Utf8.GetBytes("# Demo Skill\n");
            File.WriteAllBytes(Path.Combine(skillSource, "SKILL.md"), skillMainBytes);
            var sourceBytes = Utf8.GetBytes("# Kaynak\nİçerik, kabul harness fixture'ı.\n");
            var sourceRelPath = Path.Combine(skillSource, "references", "SOURCE.md");
            File.WriteAllBytes(sourceRelPath, sourceBytes);

            var mainHash = Sha256Hex(Path.Combine(skillSource, "SKILL.md"));
            var originalRefHash = Sha256Hex(sourceRelPath);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    {
                      "name": "demo-skill",
                      "kind": "skill",
                      "path": "skills/demo-skill",
                      "targets": ["claude-skills"],
                      "sha256": { "SKILL.md": "{{mainHash}}", "references/SOURCE.md": "{{originalRefHash}}" }
                    }
                  ]
                }
                """, Utf8);

            // Corrupt the nested SOURCE file at the kit source, after the manifest was
            // written against the original bytes.
            var corrupted = (byte[])sourceBytes.Clone();
            corrupted[^1] ^= 0xFF;
            File.WriteAllBytes(sourceRelPath, corrupted);

            // Install the SAME corrupted bytes directly at the HOME target: source and
            // target agree with each other; only the manifest's recorded hash disagrees.
            var destinationSkillDir = Path.Combine(homeRoot, ".claude", "skills", "demo-skill", "references");
            Directory.CreateDirectory(destinationSkillDir);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(destinationSkillDir)!, "SKILL.md"), skillMainBytes);
            File.WriteAllBytes(Path.Combine(destinationSkillDir, "SOURCE.md"), corrupted);

            var exitCode = RunKitStatus(kitRoot, homeRoot, out var stdout);

            Assert.True(exitCode != 0, $"exit kodu 0 kaldı; stdout: {stdout}");
            Assert.DoesNotContain("güncel", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                stdout.Contains("farklı", StringComparison.OrdinalIgnoreCase) || stdout.Contains("bozuk", StringComparison.OrdinalIgnoreCase),
                $"çıktı 'farklı' ya da 'bozuk' demiyor: {stdout}");
            Assert.Contains("sha256", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "S6-5 · Skill manifestinde bir dosya için sha256 anahtarı eksik: bozuk der, 'sha256 eksik' anar")]
    public void SkillManifest_MissingHashKeyForExistingFile_IsBozuk()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            var skillSource = Path.Combine(kitRoot, "skills", "demo-skill");
            Directory.CreateDirectory(skillSource);
            var mainBytes = Utf8.GetBytes("# Demo Skill\n");
            File.WriteAllBytes(Path.Combine(skillSource, "SKILL.md"), mainBytes);
            File.WriteAllBytes(Path.Combine(skillSource, "extra.md"), Utf8.GetBytes("fazladan dosya\n"));

            var mainHash = Sha256Hex(Path.Combine(skillSource, "SKILL.md"));

            // "extra.md" exists on disk but has no entry in sha256.
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    {
                      "name": "demo-skill",
                      "kind": "skill",
                      "path": "skills/demo-skill",
                      "targets": ["claude-skills"],
                      "sha256": { "SKILL.md": "{{mainHash}}" }
                    }
                  ]
                }
                """, Utf8);

            var exitCode = RunKitStatus(kitRoot, homeRoot, out var stdout);

            Assert.True(exitCode != 0, $"exit kodu 0 kaldı; stdout: {stdout}");
            Assert.Contains("bozuk", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sha256 eksik", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "S6-6 · Skill manifestinde var olmayan bir dosya için fazladan sha256 anahtarı: bozuk der, 'sha256 bilinmeyen dosya' anar")]
    public void SkillManifest_ExtraHashKeyForMissingFile_IsBozuk()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            var skillSource = Path.Combine(kitRoot, "skills", "demo-skill");
            Directory.CreateDirectory(skillSource);
            var mainBytes = Utf8.GetBytes("# Demo Skill\n");
            File.WriteAllBytes(Path.Combine(skillSource, "SKILL.md"), mainBytes);
            var mainHash = Sha256Hex(Path.Combine(skillSource, "SKILL.md"));

            // "ghost.md" has a sha256 entry but does not exist on disk.
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    {
                      "name": "demo-skill",
                      "kind": "skill",
                      "path": "skills/demo-skill",
                      "targets": ["claude-skills"],
                      "sha256": { "SKILL.md": "{{mainHash}}", "ghost.md": "{{new string('a', 64)}}" }
                    }
                  ]
                }
                """, Utf8);

            var exitCode = RunKitStatus(kitRoot, homeRoot, out var stdout);

            Assert.True(exitCode != 0, $"exit kodu 0 kaldı; stdout: {stdout}");
            Assert.Contains("bozuk", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sha256 bilinmeyen dosya", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "S6-7 · Rule manifestinde büyük harf hex sha256: kabul edilmez, büyük/küçük harfe normalize edilmez, bozuk der")]
    public void RuleManifest_UpperCaseHexSha256_IsRejectedNotNormalised()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var rulePath = Path.Combine(kitRoot, "rules", "demo-rule.md");
            var bytes = Utf8.GetBytes("# Demo Rule\nİçerik.\n");
            File.WriteAllBytes(rulePath, bytes);
            var upperHash = Sha256Hex(rulePath).ToUpperInvariant();

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"], "sha256": "{{upperHash}}" }
                  ]
                }
                """, Utf8);

            var exitCode = RunKitStatus(kitRoot, homeRoot, out var stdout);

            Assert.True(exitCode != 0, $"exit kodu 0 kaldı; stdout: {stdout}");
            Assert.Contains("bozuk", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "S6-8 · 'kit install': kaynağı manifest hash'iyle uyuşmayan bileşen bozuk döner, exit ≠ 0, hedefe hiçbir dosya yazılmaz")]
    public void KitInstall_OnCorruptedSource_RefusesAndWritesNothing()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var rulePath = Path.Combine(kitRoot, "rules", "demo-rule.md");
            var originalBytes = Utf8.GetBytes("# Demo Rule\nİçerik.\n");
            File.WriteAllBytes(rulePath, originalBytes);
            var originalHash = Sha256Hex(rulePath);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"], "sha256": "{{originalHash}}" }
                  ]
                }
                """, Utf8);

            // Corrupt the kit source after the manifest was written against the
            // original bytes — the source no longer matches its own manifest entry.
            var corrupted = (byte[])originalBytes.Clone();
            corrupted[^1] ^= 0xFF;
            File.WriteAllBytes(rulePath, corrupted);

            var destinationPath = Path.Combine(homeRoot, ".claude", "rules", "demo-rule.md");

            var exitCode = RunKitInstall(kitRoot, homeRoot, out var stdout);

            Assert.True(exitCode != 0, $"exit kodu 0 kaldı; stdout: {stdout}");
            Assert.Contains("bozuk", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(destinationPath), $"kurulum reddedilmesi gerekirken hedefe dosya yazılmış: {destinationPath}");
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    private static string Sha256Hex(string filePath) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(filePath)));

    private static int RunKitStatus(string kitRoot, string homeRoot, out string stdout) =>
        RunKit(["kit", "status", "--kit", kitRoot], homeRoot, out stdout);

    private static int RunKitInstall(string kitRoot, string homeRoot, out string stdout) =>
        RunKit(["kit", "install", "--kit", kitRoot], homeRoot, out stdout);

    /// <summary>
    /// Drives `oom kit &lt;args&gt;` IN-PROCESS against an explicit, disposable homeRoot
    /// via Program.RunKit(args, homeRoot) — the same seam KitKabul.cs and
    /// Scars/KitScars.cs already use, for the same reason (no CLI flag redirects the real
    /// ~/.claude home for a subprocess run).
    /// </summary>
    private static int RunKit(string[] args, string homeRoot, out string stdout)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var outWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errorWriter);
            exitCode = Program.RunKit(args, homeRoot);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        stdout = outWriter.ToString();
        return exitCode;
    }
}
