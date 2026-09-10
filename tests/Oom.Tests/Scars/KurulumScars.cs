using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class KurulumScars
{
    [Fact(DisplayName = "Y-065 · Durum, yedek, karantina ve config yalnız kullanıcı ACL'siyle kurulur")]
    public void Y065_SensitiveDirectoriesReceiveUserOnlyAcl()
    {
        var install = new Install();
        foreach (var path in new[] { "state", "backup", "quarantine", "claude-config" })
            Assert.Equal("user-only", install.ApplyUserOnlyAcl(path));
    }

    [Fact(DisplayName = "Y-066 · Kurulu ve yayımlanan ikili digest farkı doctor'da görünür")]
    public void Y066_InstalledBinaryDriftIsReported()
    {
        var item = new Doctor().ValidateInstalledBinary("installed/oom.exe", "release/oom.exe");
        Assert.Equal(HealthLevel.Warning, item.Level);
        Assert.Equal("binary-drift", item.Code);
    }

    [Fact(DisplayName = "Y-067 · Kaynakta gömülü kullanıcı yolu yoktur ve bilinmeyen ayar reddedilir")]
    public void Y067_NoHardCodedUserPathsAndUnknownConfigurationFails()
    {
        var root = ScarFixture.RepositoryRoot();
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories);
        var forbidden = new Regex(@"[A-Za-z]:\\(?:Users\\|OdenaOS)", RegexOptions.IgnoreCase);
        Assert.DoesNotContain(sourceFiles, path => forbidden.IsMatch(File.ReadAllText(path)));
        Assert.Contains("unknown", new Install().ValidateConfiguration("{\"unknown\":true}"));
    }

    [Fact(DisplayName = "Y-068 · Modüller satır bütçesini aşmaz ve frontmatter parser tek kopyadır")]
    public void Y068_ModuleLineBudgetsAndSingleParserAreEnforced()
    {
        var root = ScarFixture.RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Oom"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin")) // authored C# only, not compiler output (owner-approved 2026-09-09)
            .ToArray();
        Assert.True(files.Sum(path => File.ReadLines(path).Count()) <= 9_000 /* D13, owner-approved 2026-09-09 */);
        var parserDefinitions = files.SelectMany(path => File.ReadLines(path)).Count(line => line.Contains(" Note Parse(", StringComparison.Ordinal));
        Assert.Equal(1, parserDefinitions);
    }

    [Fact(DisplayName = "Y-069 · Temiz Windows VM zinciri kurulumdan yeni oturum enjeksiyonuna kadar çalışır")]
    public void Y069_WindowsVmEndToEndChainWorks()
    {
        var installed = new Install().Run("clean-vm-vault");
        Assert.True(installed.Success);
        var ingested = new Ingest().ParseClaude(ScarFixture.TranscriptJsonl(ScarFixture.Session("vm", 3)));
        var sweep = new Sweep().Run([ingested], new SweepOptions());
        var compiled = new Compile().Run("2026-09-09.md", ScarFixture.ValidSummary(), "=== DONE ===");
        var retrieved = new Retrieve().Hook("VM kararı", "next-session");
        Assert.Equal(1, sweep.Covered);
        Assert.True(compiled.IndexCurrent);
        Assert.NotEmpty(retrieved.Hits);
    }

    [Fact(DisplayName = "Y-070 · Beş yüz karakter üstü JSON süreç sınırından bozulmadan geçer")]
    public void Y070_StructuredDataCrossesProcessBoundaryViaStdinOrFile()
    {
        var json = "{\"probe\":\"" + new string('ğ', 550) + "\"}";
        var migrated = new Install().MigrateStructuredData(json);
        Assert.Equal(json, migrated);
        Assert.True(migrated.Length > 500);
    }

    [Fact(DisplayName = "Y-071 · Uzun ortam değeri kırpılmaz ve MSIX MCP yolu bulunur")]
    public void Y071_EnvironmentPersistenceAndMsixDiscoveryAreLossless()
    {
        var install = new Install();
        var value = new string('x', 4_096);
        Assert.Equal(value, install.PersistEnvironment("OOM_LONG_VALUE", value));
        var found = install.FindMcpConfiguration([@"Packages\ClaudeDesktop_msix\LocalState\claude_desktop_config.json"]);
        Assert.Contains("Packages", found);
    }

    [Fact(DisplayName = "Y-072 · Farklı sürücü cwd geçer, olusturuldu false rettir, sahte pid öldürülmez")]
    public void Y072_WindowsSpecificOutcomesAreInterpreted()
    {
        var result = new Install().ValidateWindowsScenario(@"E:\project", @"C:\temp", "{\"olusturuldu\":false}", 999_999);
        Assert.True(result["cwdAccepted"]);
        Assert.True(result["responseRejected"]);
        Assert.False(result["taskkillCalled"]);
    }

    [Fact(DisplayName = "Y-073 · cmd sarmalayıcı gerçek giriş noktasına çözümlenir")]
    public void Y073_CommandWrapperResolvesToRunnableEntryPoint()
    {
        var resolved = new Runner().ResolveExecutable("codex", @"fixture\codex.cmd;fixture\codex.exe");
        Assert.False(resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        Assert.True(resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "Y-074 · Yedek alınamazsa göç hiçbir şey yazmadan durur")]
    public void Y074_MigrationStopsBeforeWritesWhenBackupFails()
    {
        var result = new Install().Run("fixture-backup-fails", fromV0: true);
        Assert.False(result.Success);
        Assert.Empty(result.WrittenPaths);
        Assert.Contains("yedek", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "Y-094 · Repoda PowerShell dosyası yoktur ve metin dosyaları BOM'suz UTF-8'dir")]
    public void Y094_RepositoryHasNoPowerShellAndTextIsUtf8WithoutBom()
    {
        var root = ScarFixture.RepositoryRoot();
        var repositoryFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.nuget{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();
        Assert.DoesNotContain(repositoryFiles, path => Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase));
        var textFiles = repositoryFiles
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Contracts{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}Scars{Path.DirectorySeparatorChar}"))
            .ToArray();
        Assert.DoesNotContain(textFiles, ScarFixture.HasUtf8Bom);
    }

    [Fact(DisplayName = "Y-097 · Altmış dört bit HANDLE kırpılmadan IntPtr olarak korunur")]
    public void Y097_HandlePreservesSixtyFourBitValue()
    {
        const ulong value = 0x00000001_FFFFFFFF;
        var handle = new Runner().PreserveHandle(value);
        Assert.Equal(unchecked((long)value), handle.ToInt64());
    }

    [Fact(DisplayName = "Y-098 · Runner alt süreç stdin'ini kapatır ve zaman aşımında asılı kalmaz")]
    public void Y098_RunnerClosesStdinAndHonorsTimeout()
    {
        var request = new ProcessRequest("fixture-child", [], Path.GetTempPath(), new Dictionary<string, string>(), "payload");
        var result = new Runner().RunProcess(request, TimeSpan.FromMilliseconds(250));
        Assert.True(result.StandardInputClosed);
        Assert.True(result.TimedOut || result.ExitCode == 0);
    }

    [Fact(DisplayName = "Y-100 · Tek dosya yayınında yerel kütüphaneler exe'nin içine girer")]
    public void Y100_SingleFilePublishEmbedsNativeLibraries()
    {
        var root = ScarFixture.RepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "Oom", "Oom.csproj"));
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", project);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>", project);
        // When a published tree is present its exe must stand alone: the installer copies the
        // running file and nothing beside it, so a dll left here never reaches the vault.
        var published = Path.Combine(root, "publish", "win-x64");
        if (File.Exists(Path.Combine(published, "oom.exe")))
            Assert.Empty(Directory.EnumerateFiles(published, "*.dll", SearchOption.TopDirectoryOnly));
    }

    [Fact(DisplayName = "Y-103 · claude çıplak adla değil PATH'ten çözümlenerek başlatılır")]
    public void Y103_ClaudeRequestUsesResolvedExecutable()
    {
        var directory = ScarFixture.TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "claude.cmd"), "@echo off\r\n");
            var runner = new Runner();
            var configuration = Path.Combine(directory, "claude-config");
            var wrapper = runner.BuildClaudeRequest("istem", "claude-haiku-4-5-20251001", directory, configuration, directory);
            Assert.EndsWith("claude.cmd", wrapper.FileName, StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(Path.Combine(directory, "claude.exe"), string.Empty);
            var native = runner.BuildClaudeRequest("istem", "claude-haiku-4-5-20251001", directory, configuration, directory);
            Assert.EndsWith("claude.exe", native.FileName, StringComparison.OrdinalIgnoreCase);
        }
        finally { ScarFixture.Remove(directory); }
    }

    [Fact(DisplayName = "Y-104 · Kurulumun yazdığı oom.json kod varsayılanlarını taşır")]
    public void Y104_InstallerConfigurationComesFromCodeDefaults()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".oom"));
            File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"), Install.DefaultConfiguration, new UTF8Encoding(false));
            var loaded = OomSettings.Load(vault);
            var defaults = OomSettings.Defaults(vault);
            Assert.Null(loaded.LoadError);
            Assert.NotEmpty(loaded.Sweep.Roots);
            Assert.Equal(defaults.Sweep.Roots, loaded.Sweep.Roots);
            Assert.Equal(defaults.Retrieve.MinOverlap, loaded.Retrieve.MinOverlap);
            Assert.Equal(defaults.Retrieve.StrictScore, loaded.Retrieve.StrictScore);
            // The file stays machine-portable: the roots are written unexpanded and expanded on load.
            Assert.Contains("%USERPROFILE%", Install.DefaultConfiguration);
            Assert.DoesNotContain("%USERPROFILE%", loaded.Sweep.Roots[0]);
        }
        finally { ScarFixture.Remove(vault); }
    }

    /// <summary>Never registers anything real: keeps <see cref="Install.Uninstall"/> away from the machine's actual scheduled task.</summary>
    private sealed class FakeScheduler : ITaskScheduler
    {
        public void Register(string name, string xml) { }
    }

    [Fact(DisplayName = "Y-105 · Kurulu kopyadan --uninstall kendi exe'sini silmeye çalışıp çökmüyor, kancalar önce kalkıyor")]
    public void Y105_UninstallFromInstalledCopySurvivesSelfDelete()
    {
        var vault = ScarFixture.TempDirectory();
        var fakeProfile = ScarFixture.TempDirectory();
        try
        {
            var oom = Path.Combine(vault, ".oom");
            Directory.CreateDirectory(oom);
            var exePath = Path.Combine(oom, "oom.exe");
            File.WriteAllText(exePath, "sahte-ikili");
            File.WriteAllText(Path.Combine(oom, "vault.json"), "{}");
            File.WriteAllText(Path.Combine(oom, "oom.json"), "{}");
            File.WriteAllText(Path.Combine(oom, "hub-config.json"), "{}");
            Directory.CreateDirectory(Path.Combine(oom, "claude-config"));

            // Fake user profile: oom's own hooks plus one unrelated tool's hook — RemoveHooks
            // must take only its own entries and leave the other tool's hook in place.
            var settingsPath = Path.Combine(fakeProfile, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var root = JsonNode.Parse(HookTemplates.Render(exePath)) as JsonObject ?? new JsonObject();
            var hooks = (JsonObject)root["hooks"]!;
            hooks["SessionEnd"]!.AsArray().Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "other-tool.exe hook", ["timeout"] = 5 })
            });
            File.WriteAllText(settingsPath, root.ToJsonString(), new UTF8Encoding(false));

            var mcpPath = Path.Combine(fakeProfile, "mcp", "claude_desktop_config.json");

            var install = new Install(
                scheduler: new FakeScheduler(),
                userSettingsPath: () => settingsPath,
                mcpCandidates: () => [mcpPath],
                processPath: () => exePath); // simulates running FROM the installed copy (K7)

            // Hold an exclusive lock on the "running" exe — Windows refuses to delete (or rename)
            // a file another handle has open, the same failure the real self-delete hits.
            using (new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = install.Uninstall(vault);

                Assert.True(result.Success);
                var leftover = Assert.Single(result.Registrations, r => r.StartsWith("exe-elle-sil:", StringComparison.Ordinal));
                var leftoverPath = leftover["exe-elle-sil:".Length..];
                // Locked, so the rename fails too — the file survives under its original name.
                Assert.Equal(exePath, leftoverPath);
                Assert.True(File.Exists(exePath));

                var settingsAfter = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
                var hooksAfter = settingsAfter?["hooks"] as JsonObject;
                Assert.True(hooksAfter is null || (!hooksAfter.ContainsKey("UserPromptSubmit") && !hooksAfter.ContainsKey("PreCompact")));
                if (hooksAfter?["SessionEnd"] is JsonArray sessionEnd)
                    Assert.Contains(sessionEnd, group => group?["hooks"]?.AsArray().Any(entry => entry?["command"]?.ToString() == "other-tool.exe hook") == true);
            }
        }
        finally
        {
            ScarFixture.Remove(vault);
            ScarFixture.Remove(fakeProfile);
        }
    }

    [Fact(DisplayName = "Y-108 · install --uninstall başarıyı ve hatayı kaldırma olarak bildirir")]
    public void Y108_UninstallReportsUninstallOutcome()
    {
        var program = File.ReadAllText(Path.Combine(ScarFixture.RepositoryRoot(), "src", "Oom", "Program.cs"));
        Assert.Contains("Console.WriteLine(result.Success ? \"kaldırma tamam\" : $\"kaldırma başarısız: {result.Error}\");", program, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine($\"kaldırma tamam — çalışan exe elle silinir: {leftoverExe[\"exe-elle-sil:\".Length..]}\");", program, StringComparison.Ordinal);
        Assert.Contains("Console.WriteLine(result.Success ? \"kurulum tamam\" : $\"kurulum başarısız: {result.Error}\");", program, StringComparison.Ordinal);
    }
}
