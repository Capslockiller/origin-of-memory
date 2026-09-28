using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class KitScars
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// Creates a directory junction with `cmd /c mklink /J`, which — unlike
    /// `Directory.CreateSymbolicLink` — needs neither admin rights nor Developer Mode on
    /// Windows. A link test using this must never fall back to a silent pass when link
    /// creation fails: any failure here fails the test outright (Codex X-report S4).
    private static void CreateJunction(string link, string target)
    {
        var exitCode = RunCmd($"mklink /J \"{link}\" \"{target}\"", out var stdout, out var stderr);
        Assert.True(exitCode == 0 && Directory.Exists(link),
            $"junction oluşturulamadı ({link} -> {target}): çıkış={exitCode} stdout={stdout} stderr={stderr}");
    }

    /// Denies the current user list/read access to <paramref name="directory"/> via
    /// `icacls`, so `Directory.EnumerateFileSystemEntries` throws `UnauthorizedAccessException`
    /// on it — empirically confirmed on this machine (2026-09-24). Fails the test outright
    /// if the deny could not be applied, rather than silently skipping the scenario.
    private static void DenyAccess(string directory)
    {
        var exitCode = RunCmd($"icacls \"{directory}\" /deny \"{Environment.UserName}:(OI)(CI)RX\"", out var stdout, out var stderr);
        Assert.True(exitCode == 0, $"icacls /deny başarısız ({directory}): çıkış={exitCode} stdout={stdout} stderr={stderr}");
    }

    private static void ResetAccess(string directory) => RunCmd($"icacls \"{directory}\" /reset /T /C", out _, out _);

    private static int RunCmd(string arguments, out string stdout, out string stderr)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        stdout = process!.StandardOutput.ReadToEnd();
        stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private sealed record KitStatusJson(int schema_version, bool kit_yok, KitStatusItemJson[] items);

    private sealed record KitStatusItemJson(string name, string target, string state, string detail);

    private static void BuildKit(string kitRoot)
    {
        Directory.CreateDirectory(Path.Combine(kitRoot, "skills", "demo-skill"));
        Directory.CreateDirectory(Path.Combine(kitRoot, "agents"));
        Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));

        var skillMd = Path.Combine(kitRoot, "skills", "demo-skill", "SKILL.md");
        var skillReferences = Path.Combine(kitRoot, "skills", "demo-skill", "references.md");
        var agentMd = Path.Combine(kitRoot, "agents", "demo-agent.md");
        var ruleMd = Path.Combine(kitRoot, "rules", "demo-rule.md");

        File.WriteAllText(skillMd, "# Demo Skill\nİçerik.\n", Utf8);
        File.WriteAllText(skillReferences, "Referans.\n", Utf8);
        File.WriteAllText(agentMd, "# Demo Agent\n", Utf8);
        File.WriteAllText(ruleMd, "# Demo Rule\n", Utf8);

        var skillSha256 = Sha256ObjectJson(("SKILL.md", skillMd), ("references.md", skillReferences));
        File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
            {
              "schema": 1,
              "components": [
                { "name": "demo-skill", "kind": "skill", "path": "skills/demo-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [], "sha256": { {{skillSha256}} } },
                { "name": "demo-agent", "kind": "agent", "path": "agents/demo-agent.md", "targets": ["claude-agents"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(agentMd)}}" },
                { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(ruleMd)}}" }
              ]
            }
            """, Utf8);
    }

    private static string[] SnapshotFiles(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToBase64String(File.ReadAllBytes(file)))
                .OrderBy(entry => entry, StringComparer.Ordinal)]
            : [];

    /// Lower-case hex, matching both tools/kit/manifest-hash.ps1's ToLowerInvariant()
    /// generator and Kit.IsSha256's lower-case-only acceptance (R18/S6): a fixture
    /// manifest's sha256 is computed here from the bytes the fixture itself just wrote,
    /// never hand-typed, so it can never drift from what the file actually contains.
    private static string Sha256Hex(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// Builds a manifest `"sha256": { ... }` object body (no surrounding braces) for a
    /// skill component from its (relative-path, file) pairs.
    private static string Sha256ObjectJson(params (string Relative, string FullPath)[] files) =>
        string.Join(", ", files.Select(f => $"\"{f.Relative}\": \"{Sha256Hex(f.FullPath)}\""));

    /// Every CLI-level kit test drives this overload with a temp home root — never the
    /// real user profile — so status/install output can't be polluted by whatever is
    /// actually installed under `~/.claude` on the machine running the tests.
    private static int RunKitCaptured(string[] args, string homeRoot, out string stdout, out string stderr)
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
        stderr = errorWriter.ToString();
        return exitCode;
    }

    [Fact(DisplayName = "KIT-1 · status kuru bildirir, kurulumdan sonra güncel olur (skill, agent, rule)")]
    public void Kit1_StatusReportsKuruThenGuncelAfterInstall()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            BuildKit(kitRoot);
            var kit = new Kit(kitRoot, homeRoot);

            var before = kit.Status();
            Assert.Equal(3, before.Count);
            Assert.All(before, row => Assert.Equal(KitState.Kuru, row.State));

            var install = kit.Install(dryRun: false, force: false, only: null);
            Assert.Equal(0, install.ExitCode);
            Assert.Equal(3, install.Outcomes.Count);
            Assert.All(install.Outcomes, outcome => Assert.Equal("kuruldu", outcome.Action));

            var after = kit.Status();
            Assert.Equal(3, after.Count);
            Assert.All(after, row => Assert.Equal(KitState.Guncel, row.State));

            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "skills", "demo-skill", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "skills", "demo-skill", "references.md")));
            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "agents", "demo-agent.md")));
            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "rules", "demo-rule.md")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "KIT-2 · Yerelde değiştirilmiş hedef farklıdır; force olmadan dokunulmaz, force ile eskisi yedeğe taşınıp temiz kopya kurulur")]
    public void Kit2_LocallyEditedDestinationIsFarkliAndForceMovesOldTreeToBackupThenInstallsClean()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            BuildKit(kitRoot);
            var kit = new Kit(kitRoot, homeRoot);
            Assert.Equal(0, kit.Install(false, false, null).ExitCode);

            var rulePath = Path.Combine(homeRoot, ".claude", "rules", "demo-rule.md");
            var agentPath = Path.Combine(homeRoot, ".claude", "agents", "demo-agent.md");
            var skillFile = Path.Combine(homeRoot, ".claude", "skills", "demo-skill", "SKILL.md");

            const string editedRule = "# Demo Rule\nyerel değişiklik\n";
            const string editedAgent = "# Demo Agent\nyerel değişiklik\n";
            const string editedSkillFile = "# Demo Skill\nyerel değişiklik\n";
            File.WriteAllText(rulePath, editedRule, Utf8);
            File.WriteAllText(agentPath, editedAgent, Utf8);
            File.WriteAllText(skillFile, editedSkillFile, Utf8);

            Assert.All(kit.Status(), row => Assert.Equal(KitState.Farkli, row.State));

            var withoutForce = kit.Install(false, false, null);
            Assert.Equal(1, withoutForce.ExitCode);
            Assert.All(withoutForce.Outcomes, outcome => Assert.Equal("atlandı", outcome.Action));
            Assert.Equal(editedRule, File.ReadAllText(rulePath, Utf8));
            Assert.Equal(editedAgent, File.ReadAllText(agentPath, Utf8));
            Assert.Equal(editedSkillFile, File.ReadAllText(skillFile, Utf8));
            Assert.False(Directory.Exists(Path.Combine(homeRoot, ".claude", ".oom-kit-yedek")));

            var withForce = kit.Install(false, true, null);
            Assert.Equal(0, withForce.ExitCode);
            Assert.All(withForce.Outcomes, outcome => Assert.Equal("yedeklenip kuruldu", outcome.Action));

            var rulesBackupRoot = Path.Combine(homeRoot, ".claude", ".oom-kit-yedek", "claude-rules");
            var ruleBackup = Assert.Single(Directory.GetFiles(rulesBackupRoot), f => Path.GetFileName(f).StartsWith("demo-rule.md.bak-", StringComparison.Ordinal));
            Assert.Equal(editedRule, File.ReadAllText(ruleBackup, Utf8));

            var agentsBackupRoot = Path.Combine(homeRoot, ".claude", ".oom-kit-yedek", "claude-agents");
            var agentBackup = Assert.Single(Directory.GetFiles(agentsBackupRoot), f => Path.GetFileName(f).StartsWith("demo-agent.md.bak-", StringComparison.Ordinal));
            Assert.Equal(editedAgent, File.ReadAllText(agentBackup, Utf8));

            var skillsBackupRoot = Path.Combine(homeRoot, ".claude", ".oom-kit-yedek", "claude-skills");
            var skillBackup = Assert.Single(Directory.GetDirectories(skillsBackupRoot), d => Path.GetFileName(d).StartsWith("demo-skill.bak-", StringComparison.Ordinal));
            Assert.Equal(editedSkillFile, File.ReadAllText(Path.Combine(skillBackup, "SKILL.md"), Utf8));

            Assert.Equal("# Demo Rule\n", File.ReadAllText(rulePath, Utf8));
            Assert.Equal("# Demo Agent\n", File.ReadAllText(agentPath, Utf8));
            Assert.Equal("# Demo Skill\nİçerik.\n", File.ReadAllText(skillFile, Utf8));
            Assert.All(kit.Status(), row => Assert.Equal(KitState.Guncel, row.State));

            Assert.DoesNotContain(Directory.GetFiles(Path.Combine(homeRoot, ".claude", "rules")), f => f.Contains(".bak-", StringComparison.Ordinal));
            Assert.DoesNotContain(Directory.GetFiles(Path.Combine(homeRoot, ".claude", "agents")), f => f.Contains(".bak-", StringComparison.Ordinal));
            Assert.DoesNotContain(Directory.GetDirectories(Path.Combine(homeRoot, ".claude", "skills")), d => d.Contains(".bak-", StringComparison.Ordinal));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "KIT-3 · --dry-run hiçbir şey yazmaz; ev dizini dosya dosya aynı kalır")]
    public void Kit3_DryRunWritesNothing()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            BuildKit(kitRoot);
            var kit = new Kit(kitRoot, homeRoot);

            var beforeInstall = SnapshotFiles(homeRoot);
            var dryInstall = kit.Install(dryRun: true, force: false, only: null);
            Assert.All(dryInstall.Outcomes, outcome => Assert.Equal("kurulacak", outcome.Action));
            Assert.Equal(beforeInstall, SnapshotFiles(homeRoot));

            Assert.Equal(0, kit.Install(false, false, null).ExitCode);

            File.WriteAllText(Path.Combine(homeRoot, ".claude", "rules", "demo-rule.md"), "# Demo Rule\nyerel\n", Utf8);
            var afterEdit = SnapshotFiles(homeRoot);

            var dryForce = kit.Install(dryRun: true, force: true, only: "demo-rule");
            var outcome = Assert.Single(dryForce.Outcomes);
            Assert.Equal("yedeklenip kurulacak", outcome.Action);
            Assert.Equal(afterEdit, SnapshotFiles(homeRoot));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "KIT-4 · '..' içeren ve mutlak yol taşıyan manifest girişleri bozuktur; kit kökü dışından hiçbir şey okunmaz")]
    public void Kit4_EscapingAndAbsoluteManifestPathsAreBozukAndNothingOutsideKitRootIsRead()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        var outside = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var okMd = Path.Combine(kitRoot, "rules", "ok.md");
            File.WriteAllText(okMd, "# Ok\n", Utf8);

            var marker = Path.Combine(outside, "gizli.md");
            File.WriteAllText(marker, "dışarıdaki içerik\n", Utf8);
            var escapingPath = "../" + Path.GetFileName(outside) + "/gizli.md";
            var absolutePath = marker.Replace("\\", "/", StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "escaping", "kind": "rule", "path": "{{escapingPath}}", "targets": ["claude-rules"], "license": "MIT", "upstream": [] },
                    { "name": "absolute", "kind": "rule", "path": "{{absolutePath}}", "targets": ["claude-rules"], "license": "MIT", "upstream": [] },
                    { "name": "ok", "kind": "rule", "path": "rules/ok.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(okMd)}}" }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var status = kit.Status();
            Assert.Equal(KitState.Bozuk, status.Single(row => row.Name == "escaping").State);
            Assert.Equal(KitState.Bozuk, status.Single(row => row.Name == "absolute").State);
            Assert.Equal(KitState.Kuru, status.Single(row => row.Name == "ok").State);

            var install = kit.Install(false, false, null);
            Assert.Equal(1, install.ExitCode);
            Assert.Equal("bozuk", install.Outcomes.Single(outcome => outcome.Name == "escaping").Action);
            Assert.Equal("bozuk", install.Outcomes.Single(outcome => outcome.Name == "absolute").Action);
            Assert.Equal("kuruldu", install.Outcomes.Single(outcome => outcome.Name == "ok").Action);

            Assert.False(File.Exists(Path.Combine(homeRoot, ".claude", "rules", "gizli.md")));
            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "rules", "ok.md")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
            ScarFixture.Remove(outside);
        }
    }

    [Fact(DisplayName = "KIT-5 · Doctor gözlemleri kit satırlarını taşır; kit kökü yoksa tek bilgi satırı döner (izole kökler)")]
    public void Kit5_DoctorObservationsCarryKitRowsAndMissingRootYieldsOneInfoRow()
    {
        var kitRootParent = ScarFixture.TempDirectory();
        var kitRoot = Path.Combine(kitRootParent, "kit");
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            var missing = Program.KitObservations(ScarFixture.Now, kitRoot, homeRoot).ToList();
            var missingRow = Assert.Single(missing);
            Assert.Equal("kit", missingRow.Item.Component);
            Assert.Equal(HealthLevel.Info, missingRow.Item.Level);
            Assert.Equal("kit-yok", missingRow.Item.Code);
            Assert.Equal("kit yok", missingRow.Item.Detail);

            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var kit5RuleMd = Path.Combine(kitRoot, "rules", "demo-rule.md");
            File.WriteAllText(kit5RuleMd, "# Demo\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(kit5RuleMd)}}" }
                  ]
                }
                """, Utf8);

            var present = Program.KitObservations(ScarFixture.Now, kitRoot, homeRoot).ToList();
            var presentRow = Assert.Single(present);
            Assert.NotEqual("kit-yok", presentRow.Item.Code);
            Assert.Equal("demo-rule", presentRow.Item.Key);
            Assert.StartsWith("claude-rules → ", presentRow.Item.Detail, StringComparison.Ordinal);
            Assert.Contains(presentRow.Item.Code, new[] { "kuru", "güncel", "farklı", "bozuk" });
            var expectedLevel = presentRow.Item.Code switch
            {
                "güncel" => HealthLevel.Info,
                "bozuk" => HealthLevel.Error,
                _ => HealthLevel.Warning
            };
            Assert.Equal(expectedLevel, presentRow.Item.Level);
        }
        finally
        {
            ScarFixture.Remove(kitRootParent);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-1 · codex-skills hedefi <home>/.agents/skills/<ad>/ dizinine kurulur ve güncel olur")]
    public void K2_1_CodexSkillsTargetInstallsUnderAgentsSkillsAndBecomesGuncel()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "skills", "demo-skill"));
            var k2_1SkillMd = Path.Combine(kitRoot, "skills", "demo-skill", "SKILL.md");
            File.WriteAllText(k2_1SkillMd, "# Demo\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-skill", "kind": "skill", "path": "skills/demo-skill", "targets": ["codex-skills"], "license": "MIT", "upstream": [], "sha256": { "SKILL.md": "{{Sha256Hex(k2_1SkillMd)}}" } }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            Assert.Equal(KitState.Kuru, Assert.Single(kit.Status()).State);
            Assert.Equal(0, kit.Install(false, false, null).ExitCode);

            var destination = Path.Combine(homeRoot, ".agents", "skills", "demo-skill", "SKILL.md");
            Assert.True(File.Exists(destination));
            Assert.Equal(KitState.Guncel, Assert.Single(kit.Status()).State);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-2 · İç içe alt klasörlü beceri (scripts/x.py) alt klasörüyle kopyalanır ve karşılaştırılır")]
    public void K2_2_NestedSubfolderSkillIsCopiedAndComparedIncludingSubfolder()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "skills", "demo-skill", "scripts"));
            var k2_2SkillMd = Path.Combine(kitRoot, "skills", "demo-skill", "SKILL.md");
            var k2_2ScriptPy = Path.Combine(kitRoot, "skills", "demo-skill", "scripts", "x.py");
            File.WriteAllText(k2_2SkillMd, "# Demo\n", Utf8);
            File.WriteAllText(k2_2ScriptPy, "print('x')\n", Utf8);
            var k2_2Sha256 = Sha256ObjectJson(("SKILL.md", k2_2SkillMd), ("scripts/x.py", k2_2ScriptPy));
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-skill", "kind": "skill", "path": "skills/demo-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [], "sha256": { {{k2_2Sha256}} } }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            Assert.Equal(0, kit.Install(false, false, null).ExitCode);
            var copied = Path.Combine(homeRoot, ".claude", "skills", "demo-skill", "scripts", "x.py");
            Assert.True(File.Exists(copied));
            Assert.Equal(KitState.Guncel, Assert.Single(kit.Status()).State);

            File.WriteAllText(copied, "print('değişti')\n", Utf8);
            Assert.Equal(KitState.Farkli, Assert.Single(kit.Status()).State);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-3 · Hedefte fazladan dosya farklı sayılır; force ile eski ağaç (fazladan dosyayla) yedeğe taşınır, hedef güncel olur")]
    public void K2_3_ExtraFileInDestinationSkillIsFarkliAndForceMovesOldTreeWithExtraFileToBackup()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            BuildKit(kitRoot);
            var kit = new Kit(kitRoot, homeRoot);
            Assert.Equal(0, kit.Install(false, false, null).ExitCode);

            var skillDirectory = Path.Combine(homeRoot, ".claude", "skills", "demo-skill");
            var extraFile = Path.Combine(skillDirectory, "extra.md");
            File.WriteAllText(extraFile, "fazladan dosya\n", Utf8);

            Assert.Equal(KitState.Farkli, kit.Status().Single(row => row.Name == "demo-skill").State);

            var withoutForce = kit.Install(false, false, "demo-skill");
            Assert.Equal(1, withoutForce.ExitCode);
            Assert.True(File.Exists(extraFile));

            var withForce = kit.Install(false, true, "demo-skill");
            Assert.Equal(0, withForce.ExitCode);

            Assert.False(File.Exists(extraFile));
            Assert.Equal(KitState.Guncel, kit.Status().Single(row => row.Name == "demo-skill").State);

            var backupRoot = Path.Combine(homeRoot, ".claude", ".oom-kit-yedek", "claude-skills");
            var backupDirectory = Assert.Single(Directory.GetDirectories(backupRoot), d => Path.GetFileName(d).StartsWith("demo-skill.bak-", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(backupDirectory, "extra.md")));
            Assert.True(File.Exists(Path.Combine(backupDirectory, "SKILL.md")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-4 · --json kit status çıktısı ad/hedef/durum taşıyarak çözümlenir")]
    public void K2_4_JsonStatusOutputDeserializesWithNameTargetState()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            BuildKit(kitRoot);
            RunKitCaptured(["kit", "status", "--json", "--kit", kitRoot], homeRoot, out var stdout, out _);

            var parsed = JsonSerializer.Deserialize<KitStatusJson>(stdout, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(parsed);
            Assert.Equal(3, parsed!.items.Length);
            Assert.Contains(parsed.items, item => item.name == "demo-skill" && item.target == "claude-skills");
            Assert.All(parsed.items, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.name));
                Assert.False(string.IsNullOrWhiteSpace(item.target));
                Assert.Contains(item.state, new[] { "kuru", "güncel", "farklı", "bozuk" });
            });
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-5 · Bozuk/okunmaz manifest doctor'ı düşürmez; kit kökü var ama manifest yoksa da hata satırı döner (Finding 1)")]
    public void K2_5_UnreadableManifestNeverCrashesDoctorAndMissingManifestIsErrorRow()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            var missingManifest = Program.KitObservations(ScarFixture.Now, kitRoot, homeRoot).ToList();
            var missingManifestRow = Assert.Single(missingManifest);
            Assert.Equal(HealthLevel.Error, missingManifestRow.Item.Level);
            Assert.Equal("kit-okunamadi", missingManifestRow.Item.Code);
            Assert.StartsWith("manifest okunamadı:", missingManifestRow.Item.Detail, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), "{ bu geçerli json değil", Utf8);
            var malformed = Program.KitObservations(ScarFixture.Now, kitRoot, homeRoot).ToList();
            var malformedRow = Assert.Single(malformed);
            Assert.Equal(HealthLevel.Error, malformedRow.Item.Level);
            Assert.Equal("kit-okunamadi", malformedRow.Item.Code);

            var exitCode = RunKitCaptured(["kit", "status", "--kit", kitRoot], homeRoot, out var stdout, out var stderr);
            Assert.Equal(1, exitCode);
            Assert.Contains("manifest okunamadı", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Oom.Kit", stdout + stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-6 · Bileşen listesi olmayan manifest boş kit değil, bozuk kabul edilir (Finding 2)")]
    public void K2_6_ManifestWithoutComponentsIsMalformedNotEmpty()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), """{"schema":1}""", Utf8);
            var kit = new Kit(kitRoot, homeRoot);
            Assert.False(kit.KitRootMissing);
            var error = Assert.Throws<JsonException>(() => kit.Status());
            Assert.Contains("components", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-7 · --kit sonunda ayraçla verilse de kaynak doğru çözülür (Finding 3)")]
    public void K2_7_TrailingSeparatorInKitRootResolvesCorrectly()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            BuildKit(kitRoot);
            var kit = new Kit(kitRoot + Path.DirectorySeparatorChar, homeRoot);
            var status = kit.Status();
            Assert.Equal(3, status.Count);
            Assert.All(status, row => Assert.Equal(KitState.Kuru, row.State));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-8 · '.', 'skills/.', boş ve kit köküne eşit yol bozuk kabul edilir (Finding 4)")]
    public void K2_8_DotEmptyAndRootEqualPathsAreBozuk()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "skills", "sub"));
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "dot", "kind": "skill", "path": ".", "targets": ["claude-skills"], "license": "MIT", "upstream": [] },
                    { "name": "nested-dot", "kind": "skill", "path": "skills/.", "targets": ["claude-skills"], "license": "MIT", "upstream": [] },
                    { "name": "empty", "kind": "skill", "path": "", "targets": ["claude-skills"], "license": "MIT", "upstream": [] },
                    { "name": "root", "kind": "skill", "path": "{{kitRoot.Replace("\\", "/", StringComparison.Ordinal)}}", "targets": ["claude-skills"], "license": "MIT", "upstream": [] }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var status = kit.Status();
            Assert.Equal(4, status.Count);
            Assert.All(status, row => Assert.Equal(KitState.Bozuk, row.State));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-9 · install kit kökü yoksa 'kit yok' basar ve 1 döner; --only bilinmeyen bileşen adı verir (Finding 6)")]
    public void K2_9_InstallMissingRootPrintsKitYokAndUnknownOnlyNameFails()
    {
        var missingKitRootParent = ScarFixture.TempDirectory();
        var missingKitRoot = Path.Combine(missingKitRootParent, "yok");
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            var exitCode = RunKitCaptured(["kit", "install", "--kit", missingKitRoot], homeRoot, out var stdout, out _);
            Assert.Equal(1, exitCode);
            Assert.Contains("kit yok", stdout, StringComparison.Ordinal);

            BuildKit(kitRoot);
            var exitCode2 = RunKitCaptured(["kit", "install", "--kit", kitRoot, "--only", "yok-boyle-bir-sey"], homeRoot, out var stdout2, out _);
            Assert.Equal(1, exitCode2);
            Assert.Contains("bilinmeyen bileşen: yok-boyle-bir-sey", stdout2, StringComparison.Ordinal);
        }
        finally
        {
            ScarFixture.Remove(missingKitRootParent);
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-10 · Yanlış türde hedef 'kuru' değil 'farklı' sayılır (Finding 8)")]
    public void K2_10_WrongDestinationTypeIsFarkliNotKuru()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            BuildKit(kitRoot);
            var kit = new Kit(kitRoot, homeRoot);

            var skillDestination = Path.Combine(homeRoot, ".claude", "skills", "demo-skill");
            Directory.CreateDirectory(Path.GetDirectoryName(skillDestination)!);
            File.WriteAllText(skillDestination, "bu bir dosya, dizin değil\n", Utf8);

            var ruleDestination = Path.Combine(homeRoot, ".claude", "rules", "demo-rule.md");
            Directory.CreateDirectory(ruleDestination);

            var status = kit.Status();
            var skillRow = status.Single(row => row.Name == "demo-skill");
            Assert.Equal(KitState.Farkli, skillRow.State);
            Assert.Equal("hedef beklenmeyen türde", skillRow.Detail);

            var ruleRow = status.Single(row => row.Name == "demo-rule");
            Assert.Equal(KitState.Farkli, ruleRow.State);
            Assert.Equal("hedef beklenmeyen türde", ruleRow.Detail);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K2-11 · Kaynakta ve hedefte bağlantı (junction) bozuk sayılır")]
    public void K2_11_LinkedSourceAndDestinationAreBozuk()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        var outsideSourceTarget = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "skills", "demo-skill"));
            var k2_11SkillMd = Path.Combine(kitRoot, "skills", "demo-skill", "SKILL.md");
            File.WriteAllText(k2_11SkillMd, "# Demo\n", Utf8);
            var linkedSourcePath = Path.Combine(kitRoot, "skills", "linked-skill");
            CreateJunction(linkedSourcePath, outsideSourceTarget);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-skill", "kind": "skill", "path": "skills/demo-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [], "sha256": { "SKILL.md": "{{Sha256Hex(k2_11SkillMd)}}" } },
                    { "name": "linked-skill", "kind": "skill", "path": "skills/linked-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [] }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var sourceRow = kit.Status().Single(row => row.Name == "linked-skill");
            Assert.Equal(KitState.Bozuk, sourceRow.State);

            Assert.Equal(0, kit.Install(false, false, "demo-skill").ExitCode);
            var skillDestination = Path.Combine(homeRoot, ".claude", "skills", "demo-skill");
            var movedRealSkill = Path.Combine(homeRoot, ".claude", "skills", "demo-skill-gercek");
            Directory.Move(skillDestination, movedRealSkill);
            CreateJunction(skillDestination, movedRealSkill);

            var destinationRow = kit.Status().Single(row => row.Name == "demo-skill");
            Assert.Equal(KitState.Bozuk, destinationRow.State);
            Assert.Equal($"bağlantı: {Path.GetFullPath(skillDestination)}", destinationRow.Detail);

            var install = kit.Install(false, true, "demo-skill");
            Assert.Equal(1, install.ExitCode);
            Assert.True(Directory.Exists(skillDestination));
            Assert.True(File.GetAttributes(skillDestination).HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
            ScarFixture.Remove(outsideSourceTarget);
        }
    }

    [Fact(DisplayName = "K4-1 · Manifest bileşen listesinde null öğe tüm komutu çökertmez, açık hata verir (Codex X-report B3)")]
    public void K4_1_NullComponentInManifestNeverCrashesAndYieldsClearError()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), """{"schema":1,"components":[null]}""", Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var error = Assert.Throws<JsonException>(() => kit.Status());
            Assert.Contains("bileşen", error.Message, StringComparison.OrdinalIgnoreCase);

            var exitCode = RunKitCaptured(["kit", "status", "--kit", kitRoot], homeRoot, out var stdout, out var stderr);
            Assert.Equal(1, exitCode);
            Assert.Contains("manifest okunamadı", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("NullReferenceException", stdout + stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", stdout + stderr, StringComparison.Ordinal);

            var doctorRows = Program.KitObservations(ScarFixture.Now, kitRoot, homeRoot).ToList();
            var doctorRow = Assert.Single(doctorRows);
            Assert.Equal(HealthLevel.Error, doctorRow.Item.Level);
            Assert.Equal("kit-okunamadi", doctorRow.Item.Code);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K4-2 · Yolunda NUL karakteri olan bileşen bozuk sayılır; kardeş bileşenin satırı korunur (Codex X-report B3)")]
    public void K4_2_NulCharacterInPathIsBozukAndSiblingRowSurvives()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var k4_2OkMd = Path.Combine(kitRoot, "rules", "ok.md");
            File.WriteAllText(k4_2OkMd, "# Ok\n", Utf8);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "bad-path", "kind": "rule", "path": "a\u0000b", "targets": ["claude-rules"], "license": "MIT", "upstream": [] },
                    { "name": "ok", "kind": "rule", "path": "rules/ok.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(k4_2OkMd)}}" }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var status = kit.Status();
            Assert.Equal(2, status.Count);
            Assert.Equal(KitState.Bozuk, status.Single(row => row.Name == "bad-path").State);
            Assert.Equal(KitState.Kuru, status.Single(row => row.Name == "ok").State);

            var install = kit.Install(false, false, null);
            Assert.Equal(1, install.ExitCode);
            Assert.Equal("bozuk", install.Outcomes.Single(o => o.Name == "bad-path").Action);
            Assert.Equal("kuruldu", install.Outcomes.Single(o => o.Name == "ok").Action);
            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "rules", "ok.md")));

            var doctorRows = Program.KitObservations(ScarFixture.Now, kitRoot, homeRoot).ToList();
            Assert.Equal(2, doctorRows.Count);
            Assert.Contains(doctorRows, row => row.Item.Key == "ok" && row.Item.Level != HealthLevel.Error);
            Assert.Contains(doctorRows, row => row.Item.Key == "bad-path" && row.Item.Level == HealthLevel.Error);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K4-3 · Beceri kaynağının alt ağacındaki iç içe bağlantı (kök değil) kaynağı bozuk yapar (Codex X-report B1)")]
    public void K4_3_NestedLinkDeepInSkillSourceSubtreeIsBozuk()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        var outsideTarget = ScarFixture.TempDirectory();
        try
        {
            var skillDirectory = Path.Combine(kitRoot, "skills", "nested-link-skill");
            Directory.CreateDirectory(Path.Combine(skillDirectory, "scripts"));
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "# Demo\n", Utf8);
            File.WriteAllText(Path.Combine(outsideTarget, "gizli.py"), "print('dışarı')\n", Utf8);

            var linkedSubdirectory = Path.Combine(skillDirectory, "scripts", "linked");
            CreateJunction(linkedSubdirectory, outsideTarget);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), """
                {
                  "schema": 1,
                  "components": [
                    { "name": "nested-link-skill", "kind": "skill", "path": "skills/nested-link-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [] }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var row = Assert.Single(kit.Status());
            Assert.Equal(KitState.Bozuk, row.State);
            Assert.Contains("bağlantı", row.Detail, StringComparison.Ordinal);

            var install = kit.Install(false, false, null);
            Assert.Equal(1, install.ExitCode);
            Assert.Equal("bozuk", Assert.Single(install.Outcomes).Action);
            Assert.False(Directory.Exists(Path.Combine(homeRoot, ".claude", "skills", "nested-link-skill")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
            ScarFixture.Remove(outsideTarget);
        }
    }

    [Fact(DisplayName = "K4-4 · Hedefin atası (.claude) bağlantıysa satır bozuk sayılır ve bağlantının adı ayrıntıda geçer (Codex X-report B1)")]
    public void K4_4_LinkedAncestorOfDestinationIsBozukAndNamedInDetail()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        var outsideTarget = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var k4_4RuleMd = Path.Combine(kitRoot, "rules", "demo-rule.md");
            File.WriteAllText(k4_4RuleMd, "# Demo\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(k4_4RuleMd)}}" }
                  ]
                }
                """, Utf8);

            var claudeDirectory = Path.Combine(homeRoot, ".claude");
            CreateJunction(claudeDirectory, outsideTarget);

            var kit = new Kit(kitRoot, homeRoot);
            var row = Assert.Single(kit.Status());
            Assert.Equal(KitState.Bozuk, row.State);
            Assert.Equal($"bağlantı: {Path.GetFullPath(claudeDirectory)}", row.Detail);

            var install = kit.Install(false, false, null);
            Assert.Equal(1, install.ExitCode);
            Assert.False(File.Exists(Path.Combine(outsideTarget, "demo-rule.md")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
            ScarFixture.Remove(outsideTarget);
        }
    }

    [Fact(DisplayName = "K4-5 · Aynı normalize hedefe çözülen iki manifest girişi ikisi de bozuk olur; hiçbiri yazılmaz (Codex X-report B2a)")]
    public void K4_5_TwoManifestEntriesResolvingToSameDestinationAreBothBozukAndNothingIsWritten()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "a"));
            Directory.CreateDirectory(Path.Combine(kitRoot, "b"));
            var k4_5AMd = Path.Combine(kitRoot, "a", "x.md");
            var k4_5BMd = Path.Combine(kitRoot, "b", "x.md");
            File.WriteAllText(k4_5AMd, "# A\n", Utf8);
            File.WriteAllText(k4_5BMd, "# B\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "a-rule", "kind": "rule", "path": "a/x.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(k4_5AMd)}}" },
                    { "name": "b-rule", "kind": "rule", "path": "b/x.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(k4_5BMd)}}" }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var status = kit.Status();
            Assert.Equal(2, status.Count);
            Assert.All(status, row => Assert.Equal(KitState.Bozuk, row.State));
            Assert.All(status, row => Assert.Equal("hedef çakışması", row.Detail));

            var install = kit.Install(false, false, null);
            Assert.Equal(1, install.ExitCode);
            Assert.All(install.Outcomes, outcome => Assert.Equal("bozuk", outcome.Action));
            Assert.False(File.Exists(Path.Combine(homeRoot, ".claude", "rules", "x.md")));
            Assert.False(Directory.Exists(Path.Combine(homeRoot, ".claude", "rules")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K4-6 · Beceri kurulumu geçici aşama dizini bırakmaz; hedef tek seferde yerine taşınır (Codex X-report B2b)")]
    public void K4_6_SkillInstallLeavesNoStagingDirectoryBehind()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "skills", "demo-skill"));
            var k4_6SkillMd = Path.Combine(kitRoot, "skills", "demo-skill", "SKILL.md");
            File.WriteAllText(k4_6SkillMd, "# Demo\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-skill", "kind": "skill", "path": "skills/demo-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [], "sha256": { "SKILL.md": "{{Sha256Hex(k4_6SkillMd)}}" } }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            Assert.Equal(0, kit.Install(false, false, null).ExitCode);

            var skillsRoot = Path.Combine(homeRoot, ".claude", "skills");
            Assert.True(Directory.Exists(Path.Combine(skillsRoot, "demo-skill")));
            Assert.DoesNotContain(Directory.GetDirectories(skillsRoot), d => Path.GetFileName(d).StartsWith(".oom-kit-tmp-", StringComparison.Ordinal));
            Assert.Equal(KitState.Guncel, Assert.Single(kit.Status()).State);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K5-1 · Beklenmeyen türde (dizin) hedefin içindeki bağlantı taranır; bozuk sayılır ve force ile bile taşınmaz (Codex X-report B1)")]
    public void K5_1_WrongTypeDestinationDirectoryWithNestedLinkIsBozukAndForceNeverMovesIt()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        var outsideTarget = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            var k5_1RuleMd = Path.Combine(kitRoot, "rules", "demo-rule.md");
            File.WriteAllText(k5_1RuleMd, "# Demo Rule\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "demo-rule", "kind": "rule", "path": "rules/demo-rule.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(k5_1RuleMd)}}" }
                  ]
                }
                """, Utf8);

            // The rule's destination is unexpectedly a directory (wrong type), and a link
            // sits nested inside it, not at its top level. It must be scanned and marked
            // bozuk, not accepted as merely "farklı" (which --force would be allowed to
            // move to backup).
            var ruleDestination = Path.Combine(homeRoot, ".claude", "rules", "demo-rule.md");
            Directory.CreateDirectory(ruleDestination);
            var nestedLink = Path.Combine(ruleDestination, "linked");
            CreateJunction(nestedLink, outsideTarget);

            var kit = new Kit(kitRoot, homeRoot);
            var row = Assert.Single(kit.Status());
            Assert.Equal(KitState.Bozuk, row.State);
            Assert.Contains("bağlantı", row.Detail, StringComparison.Ordinal);

            var install = kit.Install(false, true, null);
            Assert.Equal(1, install.ExitCode);
            Assert.Equal("bozuk", Assert.Single(install.Outcomes).Action);
            Assert.True(Directory.Exists(ruleDestination));
            Assert.True(Directory.Exists(nestedLink));
            Assert.False(Directory.Exists(Path.Combine(homeRoot, ".claude", ".oom-kit-yedek")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
            ScarFixture.Remove(outsideTarget);
        }
    }

    [Fact(DisplayName = "K5-2 · Eksik/boş ad, tür veya yol; bileşen tek bozuk satır olur, sessizce '(adsız)' olarak işlenip kurulmaz")]
    public void K5_2_MissingNameKindOrPathIsSingleBozukRowNotSilentlyInstalled()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            File.WriteAllText(Path.Combine(kitRoot, "rules", "no-name.md"), "# No name\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "rules", "no-kind.md"), "# No kind\n", Utf8);
            var k5_2OkMd = Path.Combine(kitRoot, "rules", "ok.md");
            File.WriteAllText(k5_2OkMd, "# Ok\n", Utf8);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "kind": "rule", "path": "rules/no-name.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [] },
                    { "name": "no-kind", "path": "rules/no-kind.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [] },
                    { "name": "no-path", "kind": "rule", "targets": ["claude-rules"], "license": "MIT", "upstream": [] },
                    { "name": "ok", "kind": "rule", "path": "rules/ok.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(k5_2OkMd)}}" }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var status = kit.Status();
            Assert.Equal(4, status.Count);

            var noName = status.Single(row => row.Name == "(adsız)");
            Assert.Equal(KitState.Bozuk, noName.State);
            Assert.Equal("ad eksik", noName.Detail);
            Assert.Equal("-", noName.Target);

            var noKind = status.Single(row => row.Name == "no-kind");
            Assert.Equal(KitState.Bozuk, noKind.State);
            Assert.Equal("tür eksik", noKind.Detail);

            var noPath = status.Single(row => row.Name == "no-path");
            Assert.Equal(KitState.Bozuk, noPath.State);
            Assert.Equal("yol eksik", noPath.Detail);

            Assert.Equal(KitState.Kuru, status.Single(row => row.Name == "ok").State);

            var install = kit.Install(false, false, null);
            Assert.Equal(1, install.ExitCode);
            Assert.All(install.Outcomes.Where(o => o.Name != "ok"), outcome => Assert.Equal("bozuk", outcome.Action));
            Assert.Equal("kuruldu", install.Outcomes.Single(o => o.Name == "ok").Action);
            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "rules", "ok.md")));
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K5-3 · targets alanı eksik veya boş dizi olan bileşen tek bozuk satır olur (brief'in istediği test)")]
    public void K5_3_MissingOrEmptyTargetsArrayIsSingleBozukRow()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(kitRoot, "rules"));
            File.WriteAllText(Path.Combine(kitRoot, "rules", "no-targets.md"), "# No targets\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "rules", "empty-targets.md"), "# Empty targets\n", Utf8);
            var k5_3OkMd = Path.Combine(kitRoot, "rules", "ok.md");
            File.WriteAllText(k5_3OkMd, "# Ok\n", Utf8);

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "no-targets", "kind": "rule", "path": "rules/no-targets.md", "license": "MIT", "upstream": [] },
                    { "name": "empty-targets", "kind": "rule", "path": "rules/empty-targets.md", "targets": [], "license": "MIT", "upstream": [] },
                    { "name": "ok", "kind": "rule", "path": "rules/ok.md", "targets": ["claude-rules"], "license": "MIT", "upstream": [], "sha256": "{{Sha256Hex(k5_3OkMd)}}" }
                  ]
                }
                """, Utf8);

            var kit = new Kit(kitRoot, homeRoot);
            var status = kit.Status();
            Assert.Equal(3, status.Count);

            var noTargets = status.Single(row => row.Name == "no-targets");
            Assert.Equal(KitState.Bozuk, noTargets.State);
            Assert.Equal("hedef listesi eksik", noTargets.Detail);
            Assert.Equal("-", noTargets.Target);

            var emptyTargets = status.Single(row => row.Name == "empty-targets");
            Assert.Equal(KitState.Bozuk, emptyTargets.State);
            Assert.Equal("hedef listesi eksik", emptyTargets.Detail);

            Assert.Equal(KitState.Kuru, status.Single(row => row.Name == "ok").State);

            var install = kit.Install(false, false, null);
            Assert.Equal(1, install.ExitCode);
            Assert.Equal("bozuk", install.Outcomes.Single(o => o.Name == "no-targets").Action);
            Assert.Equal("bozuk", install.Outcomes.Single(o => o.Name == "empty-targets").Action);
            Assert.Equal("kuruldu", install.Outcomes.Single(o => o.Name == "ok").Action);
        }
        finally
        {
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }

    [Fact(DisplayName = "K5-4 · Kaynak alt ağacında tarama hatası (erişim reddi) bileşeni bozuk yapar; Plan() kesilmez, --only sağlıklı bileşende çalışır")]
    public void K5_4_SourceSubtreeScanErrorIsBozukPlanDoesNotAbortAndOnlyStillWorksForHealthyComponent()
    {
        var kitRoot = ScarFixture.TempDirectory();
        var homeRoot = ScarFixture.TempDirectory();
        var blockedDirectory = Path.Combine(kitRoot, "skills", "blocked-skill", "locked");
        var denied = false;
        try
        {
            Directory.CreateDirectory(blockedDirectory);
            File.WriteAllText(Path.Combine(blockedDirectory, "secret.py"), "print('x')\n", Utf8);
            File.WriteAllText(Path.Combine(kitRoot, "skills", "blocked-skill", "SKILL.md"), "# Blocked\n", Utf8);

            Directory.CreateDirectory(Path.Combine(kitRoot, "skills", "healthy-skill"));
            var k5_4HealthySkillMd = Path.Combine(kitRoot, "skills", "healthy-skill", "SKILL.md");
            File.WriteAllText(k5_4HealthySkillMd, "# Healthy\n", Utf8);

            DenyAccess(blockedDirectory);
            denied = true;

            File.WriteAllText(Path.Combine(kitRoot, "manifest.json"), $$"""
                {
                  "schema": 1,
                  "components": [
                    { "name": "blocked-skill", "kind": "skill", "path": "skills/blocked-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [] },
                    { "name": "healthy-skill", "kind": "skill", "path": "skills/healthy-skill", "targets": ["claude-skills"], "license": "MIT", "upstream": [], "sha256": { "SKILL.md": "{{Sha256Hex(k5_4HealthySkillMd)}}" } }
                  ]
                }
                """, Utf8);

            // The scan error must reject only "blocked-skill"; Plan() must still return
            // "healthy-skill" instead of the whole call throwing out of Status()/Install().
            var kit = new Kit(kitRoot, homeRoot);
            var status = kit.Status();
            Assert.Equal(2, status.Count);
            Assert.Equal(KitState.Bozuk, status.Single(row => row.Name == "blocked-skill").State);
            Assert.Equal(KitState.Kuru, status.Single(row => row.Name == "healthy-skill").State);

            // --only on the healthy component must still succeed even though the other
            // component's source subtree cannot be scanned.
            var onlyHealthy = kit.Install(false, false, "healthy-skill");
            Assert.Equal(0, onlyHealthy.ExitCode);
            Assert.Equal("kuruldu", Assert.Single(onlyHealthy.Outcomes).Action);
            Assert.True(File.Exists(Path.Combine(homeRoot, ".claude", "skills", "healthy-skill", "SKILL.md")));
        }
        finally
        {
            if (denied)
                ResetAccess(blockedDirectory);
            ScarFixture.Remove(kitRoot);
            ScarFixture.Remove(homeRoot);
        }
    }
}
