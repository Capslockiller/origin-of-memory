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
}
