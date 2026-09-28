using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// Lane L1-kit-hash acceptance oracle for SPEC-3.1.0.md S6: "bin/kit/manifest.json her
/// dosya için sha256 taşır, `kit status` hash'i karşılaştırır." Three behaviours, each a
/// concrete failing-then-passing assertion:
///
///   1. kit/manifest.json carries a per-file sha256 for every file it references under
///      kit/, and every recorded value matches the real file's bytes.
///   2. A kit SOURCE file with one byte flipped, installed unchanged (same flipped
///      bytes) at the HOME target — so source and target agree with each other, only the
///      manifest's recorded hash disagrees — is reported 'farklı' or 'bozuk' (never
///      'güncel'), naming sha256, with a non-zero exit. Today's Kit.CompareRow only ever
///      compares source to target directly (FilesEqual / DirectoryDiff), so identical
///      corruption on both sides is invisible to it and it reports 'güncel'/exit 0 — the
///      exact defect eski-exe/oom.exe also has (Kit.cs:558-564, unchanged by this lane's
///      starting tree).
///   3. A missing or malformed manifest hash entry makes that component 'bozuk' outright
///      — even when source and target bytes are byte-for-byte identical — and must never
///      silently fall back to the old content comparison to call it 'güncel' instead.
///
/// Test seam and why these are NOT run through KabulHarness (process boundary): `oom kit
/// status` resolves its HOME root via Program.HomeRoot() = the REAL
/// Environment.SpecialFolder.UserProfile with no --home/OOM_HOME override on either the
/// current tree or eski-exe/oom.exe. Driving this scenario through a real child process
/// would therefore mean writing the "same corrupted bytes installed at target" fixture
/// state into this machine's ACTUAL ~/.claude/rules — forbidden outright ("never touch
/// ... ~/.claude/*"), and non-deterministic besides (whatever is already installed
/// there). Oom.Program.RunKit(string[] args, string homeRoot) and Oom.Contracts.Kit's own
/// constructor both already take homeRoot as an explicit parameter for exactly this
/// reason (see the driver's own tests/Oom.Tests/Scars/KitScars.cs, which uses the same
/// seam) — this lane reuses that existing, sanctioned seam rather than inventing a new
/// API. Assertion 1 needs no seam at all: it reads kit/manifest.json and the real kit/
/// tree directly, independent of any command.
/// </summary>
public sealed class KitKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);

    // Since 3.1.0 the public repository ships the kit ENGINE only; kit content stays private
    // (it names a client project). This check runs against the private kit given in
    // OOM_KABUL_KIT (or a kit/ directory in the tree, if one is ever shipped again) and is
    // excluded from CI with the other private-data tests. It never skips silently.
    [Trait("Kabul", "OzelVault")]
    [Fact(DisplayName = "S6-1 · kit/manifest.json: sha256 alan sayısı kit/ altında referans verilen dosya sayısına eşit, her değer dosya baytlarıyla eşleşir")]
    public void ManifestSha256Fields_CountAndMatchRealKitFileBytes()
    {
        var shipped = Path.Combine(ScarFixture.RepositoryRoot(), "kit");
        var kitRoot = Environment.GetEnvironmentVariable("OOM_KABUL_KIT") is { Length: > 0 } privateKit ? privateKit : shipped;
        var manifestPath = Path.Combine(kitRoot, "manifest.json");
        Assert.True(File.Exists(manifestPath), "S6-1: kit manifest yok — OOM_KABUL_KIT özel kit dizinini göstermeli. Bu test hiç atlanmaz.");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Utf8));
        var components = document.RootElement.GetProperty("components");

        var actualFileCount = 0;
        var recordedHashCount = 0;
        var mismatches = new List<string>();

        foreach (var component in components.EnumerateArray())
        {
            var kind = component.GetProperty("kind").GetString()!;
            var relativePath = component.GetProperty("path").GetString()!;
            var componentRoot = Path.Combine(kitRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            var files = kind == "skill"
                ? [.. Directory.EnumerateFiles(componentRoot, "*", SearchOption.AllDirectories)
                    .Select(full => new KitFileEntry(Path.GetRelativePath(componentRoot, full).Replace('\\', '/'), full))]
                : new[] { new KitFileEntry(Path.GetFileName(componentRoot), componentRoot) };

            actualFileCount += files.Length;

            if (!component.TryGetProperty("sha256", out var sha256Element))
                continue;

            if (sha256Element.ValueKind == JsonValueKind.String)
            {
                recordedHashCount += 1;
                CompareOne(relativePath, files[0].Relative, files[0].Full, sha256Element.GetString(), mismatches);
            }
            else if (sha256Element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in sha256Element.EnumerateObject())
                {
                    recordedHashCount += 1;
                    var match = Array.Find(files, f => f.Relative == property.Name);
                    if (match.Full is null)
                    {
                        mismatches.Add($"{relativePath}: manifestte '{property.Name}' anahtarı var ama kit/ altında böyle bir dosya yok");
                        continue;
                    }
                    CompareOne(relativePath, property.Name, match.Full, property.Value.GetString(), mismatches);
                }
            }
        }

        Assert.True(actualFileCount > 0, "kit/ altında referans verilen dosya bulunamadı — fixture/manifest bozuk, bu test hiçbir şeyi kanıtlamaz");
        Assert.Equal(actualFileCount, recordedHashCount);
        Assert.Empty(mismatches);
    }

    private static void CompareOne(string componentPath, string fileRelative, string fileFull, string? recordedHash, List<string> mismatches)
    {
        var expected = Sha256Hex(fileFull);
        if (!string.Equals(expected, recordedHash, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"{componentPath}/{fileRelative}: manifest sha256={recordedHash ?? "(yok)"} gerçek={expected}");
    }

    private readonly record struct KitFileEntry(string Relative, string Full);

    [Fact(DisplayName = "S6-2 · Kaynakta ve hedefte AYNI bozulmuş baytlar: 'kit status' güncel demez, farklı/bozuk der ve sha256 anar, exit ≠ 0")]
    public void CorruptedSourceMatchingCorruptedTarget_IsNeverReportedGuncel()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var rulePath = Path.Combine(kitRoot, "rules", "demo-rule.md");
            var originalBytes = Utf8.GetBytes("# Demo Rule\nİçerik, kabul harness fixture'ı.\n");
            File.WriteAllBytes(rulePath, originalBytes);
            var originalHashHex = Sha256Hex(rulePath);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"], "sha256": "{{originalHashHex}}" }
                  ]
                }
                """, Utf8);

            // Corrupt the KIT SOURCE after the manifest was written against the
            // ORIGINAL bytes: flip the file's last byte.
            var corrupted = (byte[])originalBytes.Clone();
            corrupted[^1] ^= 0xFF;
            File.WriteAllBytes(rulePath, corrupted);

            // Install the SAME corrupted bytes directly at the HOME target: source and
            // target now agree with each other bit-for-bit; only the manifest's
            // recorded (pre-corruption) hash disagrees with both.
            var destinationDirectory = Path.Combine(homeRoot, ".claude", "rules");
            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllBytes(Path.Combine(destinationDirectory, "demo-rule.md"), corrupted);

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

    [Theory(DisplayName = "S6-3 · Manifestte sha256 eksik/bozuk: içerik bayt bayt aynı olsa bile 'bozuk' der, sessizce içerik karşılaştırmasına düşmez")]
    [InlineData(null)]
    [InlineData("bu-gecerli-bir-sha256-degil")]
    public void MissingOrMalformedManifestHash_IsBozukNeverSilentContentFallback(string? sha256Field)
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var rulePath = Path.Combine(kitRoot, "rules", "demo-rule.md");
            var bytes = Utf8.GetBytes("# Demo Rule\nİçerik, kabul harness fixture'ı.\n");
            File.WriteAllBytes(rulePath, bytes);

            var sha256Json = sha256Field is null ? string.Empty : $""", "sha256": "{sha256Field}" """;
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"]{{sha256Json}} }
                  ]
                }
                """, Utf8);

            // The HOME target is BYTE-FOR-BYTE IDENTICAL to the kit source. A content
            // comparison alone would call this row 'güncel' — the manifest hash gap
            // must override that, not be silently skipped.
            var destinationDirectory = Path.Combine(homeRoot, ".claude", "rules");
            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllBytes(Path.Combine(destinationDirectory, "demo-rule.md"), bytes);

            var exitCode = RunKitStatus(kitRoot, homeRoot, out var stdout);

            Assert.True(exitCode != 0, $"exit kodu 0 kaldı; stdout: {stdout}");
            Assert.DoesNotContain("güncel", stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("bozuk", stdout, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    private static string Sha256Hex(string filePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();

    /// <summary>
    /// Drives `oom kit status --kit &lt;kitRoot&gt;` IN-PROCESS against an explicit,
    /// disposable homeRoot via Program.RunKit(args, homeRoot) — the same seam
    /// tests/Oom.Tests/Scars/KitScars.cs already uses for every kit test, chosen here for
    /// the same reason: no CLI flag exists to redirect the real ~/.claude home for a
    /// subprocess run (see the class doc comment).
    /// </summary>
    private static int RunKitStatus(string kitRoot, string homeRoot, out string stdout)
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
            exitCode = Program.RunKit(["kit", "status", "--kit", kitRoot], homeRoot);
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
