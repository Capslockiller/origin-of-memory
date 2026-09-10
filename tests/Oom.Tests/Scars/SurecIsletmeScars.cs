using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class SurecIsletmeScars
{
    [Fact(DisplayName = "Y-083 · Worktree temizliği yalnız tek tek adlandırılmış hedefleri kaldırır")]
    public void Y083_CleanupNeverRemovesUnnamedWorktree()
    {
        var existing = new[] { "origin-of-memory-lane-a", "origin-of-memory-panel", "origin-of-memory-lane-b" };
        var planned = new Install().PlanWorktreeCleanup(["origin-of-memory-lane-a", "origin-of-memory-lane-b"], existing);
        Assert.Equal(2, planned.Count);
        Assert.DoesNotContain("origin-of-memory-panel", planned);
    }

    [Fact(DisplayName = "Y-084 · Her yeni runner giriş kaynağı bir dışlama testi ister")]
    public void Y084_NewRunnerRequiresTranscriptExclusion()
    {
        var missing = new Ingest().ValidateSourceInventory(["claude", "local", "new-runner"], ["claude", "local"]);
        Assert.Equal(["new-runner"], missing);
    }

    [Fact(DisplayName = "Y-085 · Kontrol noktası doğrulanmadan kaydedildi raporlanamaz")]
    public void Y085_CheckpointMustBeCompleteAndVerified()
    {
        var save = new Save();
        var incomplete = save.WriteCheckpoint("Karar var ama devir yok", ["karar", "düzeltme", "devir"]);
        Assert.False(incomplete.Written);
        Assert.False(incomplete.Verified);
        var complete = save.WriteCheckpoint("karar: x\ndüzeltme: y\ndevir: z", ["karar", "düzeltme", "devir"]);
        Assert.True(complete.Written && complete.Verified);
    }

    [Fact(DisplayName = "Y-086 · Sürümdeki her yetenek iddiası test veya ölçüm kanıtı taşır")]
    public void Y086_ReleaseClaimsRequireEvidence()
    {
        var claims = new Dictionary<string, string> { ["tek exe"] = "publish-single-file", ["kurulum çalışıyor"] = "" };
        var findings = new Doctor().ValidateReleaseClaims(claims);
        Assert.Contains(findings, x => x.Code == "claim-without-evidence" && x.Key == "kurulum çalışıyor");
    }

    [Fact(DisplayName = "Y-087 · Bekleyici döngü N denemede kırmızı çıkar ve sonsuza dek beklemez")]
    public void Y087_WaitLoopIsBoundedAndSurfacesFailure()
    {
        var result = new Runner().WaitForOutcome("never-arrives", maxAttempts: 5);
        Assert.False(result.Completed);
        Assert.Equal(5, result.Attempts);
        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEmpty(result.Error);
    }

    [Fact(DisplayName = "Y-088 · Doctor yeşil olmak için kapsama ve ret oranını ölçer")]
    public void Y088_DoctorHealthRequiresCoverageAndRejectionTargets()
    {
        var result = new Doctor().Check(ScarFixture.Now);
        Assert.True(result.Coverage >= 0.95);
        Assert.True(result.RejectionRate <= 0.03);
        Assert.DoesNotContain(result.Items, x => x.Code == "uncovered-session");
    }

    [Fact(DisplayName = "Y-099 · Yedekler yalnız .oom backup altında ve git dışındadır")]
    public void Y099_BackupsStayUnderOomBackupAndAreIgnored()
    {
        var path = new Install().BackupPath("settings.local.json.yedek");
        Assert.Contains(Path.Combine(".oom", "backup"), path, StringComparison.OrdinalIgnoreCase);
        var ignore = File.ReadAllText(Path.Combine(ScarFixture.RepositoryRoot(), ".gitignore"));
        Assert.Contains(".oom/backup", ignore, StringComparison.OrdinalIgnoreCase);
    }

    // yazan: codex · gpt-5
    [Fact(DisplayName = "Y-109 · Main son çare olarak istisnayı rc 1'e çevirir ve WER kutusunu kapatır")]
    public void Y109_MainCatchesUnhandledExceptionAndDisablesWerDialog()
    {
        var program = typeof(Save).Assembly.GetType("Oom.Program", throwOnError: true)!;
        var main = program.GetMethod("Main", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            binder: null, [typeof(string[]), typeof(Func<string[], int>)], modifiers: null);
        Assert.NotNull(main);

        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var result = (int)main.Invoke(null,
                [Array.Empty<string>(), new Func<string[], int>(_ => throw new InvalidOperationException("dispatch kırıldı"))])!;

            Assert.Equal(1, result);
            Assert.StartsWith("hata: InvalidOperationException: dispatch kırıldı", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var source = File.ReadAllText(Path.Combine(ScarFixture.RepositoryRoot(), "src", "Oom", "Program.cs"));
        Assert.Contains("SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);", source, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Y-118 · Kapsama vault'un kendi penceresini ölçer; vault öncesi dosya arşiv sayılır, hata üretmez")]
    public void Y118_CoverageMeasuresTheVaultsOwnWindow()
    {
        var vault = ScarFixture.TempDirectory();
        // Y-006 excludes every path under the system temp directory as the mechanism's own trace,
        // so the fake sweep root has to sit outside %TEMP% — this repo's own gitignored obj/ does.
        var root = Path.Combine(ScarFixture.RepositoryRoot(), "tests", "Oom.Tests", "obj", "y118-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var now = DateTimeOffset.UtcNow;
            var install = now.AddHours(-2);
            var pre = Path.Combine(root, "pre.jsonl");
            var post = Path.Combine(root, "post.jsonl");
            File.WriteAllText(pre, ScarFixture.TranscriptJsonl(ScarFixture.Session("pre-vault", 3, lastTurnAt: now.AddDays(-40))));
            File.WriteAllText(post, ScarFixture.TranscriptJsonl(ScarFixture.Session("post-vault", 3, lastTurnAt: now.AddMinutes(-10))));
            File.SetLastWriteTimeUtc(pre, now.AddDays(-40).UtcDateTime);
            File.SetLastWriteTimeUtc(post, now.AddMinutes(-10).UtcDateTime);

            using var state = new State(null, null, Path.Combine(vault, "state.db"));
            state.WriteVaultStamp(install);
            // Vault öncesi dosya zaten damgalı ve başarısız — filtre olmasa oranı düşürürdü.
            var preInfo = new FileInfo(pre);
            state.WriteStamp(pre, ((DateTimeOffset)preInfo.LastWriteTime).ToString("O"), preInfo.Length, "retry");

            var settings = OomSettings.Defaults(vault) with { Sweep = new SweepSettings(8, 8, 3, 20, [root]) };
            new SweepRun(vault, settings, new Flush(state: state), state).Execute(dryRun: false);

            Assert.Equal(1, state.Scalar("SELECT total FROM coverage ORDER BY ts DESC LIMIT 1"));
            Assert.Equal(1, state.Scalar("SELECT covered FROM coverage ORDER BY ts DESC LIMIT 1"));
            Assert.Contains("1 dosya", Assert.Single(state.ReadColumn("SELECT detail FROM health WHERE code = 'arsiv' ORDER BY rowid DESC LIMIT 1")));

            var total = state.Scalar("SELECT total FROM coverage ORDER BY ts DESC LIMIT 1");
            var covered = state.Scalar("SELECT covered FROM coverage ORDER BY ts DESC LIMIT 1");
            var snapshot = new DoctorSnapshot([], total == 0 ? 1.0 : (double)covered / total, 0.0, 0, WindowTotal: (int)total);
            var health = new Doctor(null, _ => snapshot, null).Check(now);
            Assert.NotEqual(HealthLevel.Error, health.Items.Single(item => item.Code == "coverage").Level);
        }
        finally { ScarFixture.Remove(vault); ScarFixture.Remove(root); }
    }
}
