using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Gates;
using Oom.Tests.Kabul;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class DoctorScars
{
    [Fact(DisplayName = "Y-034 · Telemetri uyarı değildir, doctor bekleyen daily sayısını gösterir")]
    public void Y034_TelemetryDoesNotHidePendingDailies()
    {
        var doctor = new Doctor().Check(ScarFixture.Now);
        Assert.DoesNotContain(doctor.Items, x => x.Code == "registry-truncated" && x.Level == HealthLevel.Warning);
        Assert.True(doctor.Pending > 0);
    }

    [Fact(DisplayName = "Y-049 · Kanca hata yolu kalıcı doctor bulgusu bırakır")]
    public void Y049_HookFailureIsPersisted()
    {
        var request = new ProcessRequest("missing-interpreter", [], Path.GetTempPath(), new Dictionary<string, string>(), "{}");
        var process = new Runner().RunProcess(request, TimeSpan.FromSeconds(1));
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains(new Doctor().Check(ScarFixture.Now).Items, x => x.Code == "hook-failed");
    }

    [Fact(DisplayName = "Y-088 · Doctor yeşil olmak için kapsama ve ret oranını ölçer")]
    public void Y088_DoctorHealthRequiresCoverageAndRejectionTargets()
    {
        var result = new Doctor().Check(ScarFixture.Now);
        Assert.True(result.Coverage >= 0.95);
        Assert.True(result.RejectionRate <= 0.03);
        Assert.DoesNotContain(result.Items, x => x.Code == "uncovered-session");
    }

    [Theory(DisplayName = "R22 · kapsama %95 altında ve pencerede >= 5 oturum varsa hata, < 5 oturumda uyarı, %95 ve üstü bilgi")]
    [InlineData(9, 10, HealthLevel.Error, 1)]
    [InlineData(5, 6, HealthLevel.Error, 1)]
    [InlineData(3, 4, HealthLevel.Warning, 0)]
    [InlineData(19, 20, HealthLevel.Info, 0)]
    public void R22_CoverageBelowNinetyFivePercentIsAnErrorOnceTheWindowHasFiveSessions(int covered, int total, HealthLevel expected, int exitCode)
    {
        var snapshot = new DoctorSnapshot([], (double)covered / total, 0, 0, WindowTotal: total);
        var result = new Doctor(null, _ => snapshot).Check(ScarFixture.Now);
        Assert.Equal(expected, Assert.Single(result.Items, item => item.Code == "coverage").Level);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact(DisplayName = "Y-113 · Erişilebilirlik yoklaması kalıcı kanca hatasından bağımsızdır ve başarı onu iyileştirir")]
    public void Y113_ReachabilityProbeIsIndependentOfTaskFailuresAndHealsOnSuccess()
    {
        var directory = ScarFixture.TempDirectory();
        var claudePath = Path.Combine(directory, "claude.exe");
        try
        {
            File.WriteAllText(claudePath, "sahte");
            var doctor = new Doctor();
            Assert.Equal(HealthLevel.Info, doctor.CheckClaudeReachability(directory).Level);
            Assert.Equal(HealthLevel.Warning, doctor.CheckClaudeReachability(string.Empty).Level);

            var request = new ProcessRequest(claudePath, [], Path.GetTempPath(), new Dictionary<string, string>(), string.Empty);
            new Runner(new Y113ProcessRunner(1)).RunProcess(request, TimeSpan.FromSeconds(1));
            Assert.Contains(new Doctor().Check(ScarFixture.Now).Items,
                x => x.Code == "hook-failed" && x.Key == claudePath && x.Level == HealthLevel.Error);
            Assert.Equal(HealthLevel.Info, doctor.CheckClaudeReachability(directory).Level);

            new Runner(new Y113ProcessRunner(0)).RunProcess(request, TimeSpan.FromSeconds(1));
            Assert.DoesNotContain(new Doctor().Check(ScarFixture.Now).Items,
                x => x.Code == "hook-failed" && x.Key == claudePath && x.Level == HealthLevel.Error);
        }
        finally { ScarFixture.Remove(directory); }
    }

    private sealed class Y113ProcessRunner(int exitCode) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request, TimeSpan timeout) => new(exitCode, "{}", string.Empty, true);
    }

    [Fact(DisplayName = "Y-325 · doctor kanca hatasını gözlemler, install uyarır, kaldırma uyarmaz")]
    public void Y325_HookValidationRunsInProductPaths()
    {
        var vault = ScarFixture.TempDirectory();
        var stderr = Console.Error;
        using var captured = new StringWriter();
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".claude"));
            var hooks = HookTemplates.Build("relative/oom.exe", vault).ToDictionary(
                registration => registration.Event,
                registration => new[]
                {
                    new
                    {
                        hooks = new[]
                        {
                            new { type = "command", command = registration.Command, timeout = registration.TimeoutSeconds }
                        }
                    }
                });
            File.WriteAllText(Path.Combine(vault, ".claude", "settings.json"), JsonSerializer.Serialize(new { hooks }));
            var snapshot = Program.Snapshot(ScarFixture.Now, vault, OomSettings.Defaults(vault), null);
            Assert.Contains(snapshot.Observations, o => o.Item.Code == "hook-path" && o.Item.Level == HealthLevel.Error);
            Console.SetError(captured);
            Assert.Equal(0, Program.RunInstall(["install"], vault));
            Assert.Contains("kurulum uyarısı: hook-path:", captured.ToString());
            captured.GetStringBuilder().Clear();
            Assert.Equal(0, Program.RunInstall(["install", "--uninstall"], vault));
            Assert.DoesNotContain("kurulum uyarısı:", captured.ToString());
        }
        finally
        {
            Console.SetError(stderr);
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-326 · karantina sayısı yalnız gerçek markdown dosyalarından gelir")]
    public void Y326_QuarantineUsesFilesWithoutATable()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            using var state = new State(null, null, Path.Combine(vault, "state.db"));
            var settings = OomSettings.Defaults(vault);
            Assert.Equal(0, Program.Snapshot(ScarFixture.Now, vault, settings, state).Quarantine);
            var quarantine = Directory.CreateDirectory(Path.Combine(vault, ".oom", "quarantine")).FullName;
            File.WriteAllText(Path.Combine(quarantine, "one.md"), "one");
            File.WriteAllText(Path.Combine(quarantine, "two.md"), "two");
            File.WriteAllText(Path.Combine(quarantine, "other.txt"), "other");
            Assert.Equal(2, Program.Snapshot(ScarFixture.Now, vault, settings, state).Quarantine);
            Assert.Equal(0, state.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'quarantine'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-331 · doctor eksik indeks ile kavram korpusu farkını bildirir")]
    public void Y331_DoctorComparesIndexWithConceptCorpus()
    {
        var vault = ScarFixture.RetrievalVault();
        var previous = Environment.GetEnvironmentVariable("OOM_LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", vault);
            VaultPaths.UseVault(vault);
            var settings = OomSettings.Defaults(vault);
            Assert.Contains(Program.Snapshot(ScarFixture.Now, vault, settings, null).Observations, o => o.Item.Code == "index-mismatch");
            var retrieve = new Retrieve(new RetrieveOptions(VaultPath: vault, IndexPath: VaultIdentity.EnsureDatabase(vault)));
            Assert.Equal(0, retrieve.Build().ExitCode);
            Assert.Contains(Program.Snapshot(ScarFixture.Now, vault, settings, null).Observations, o => o.Item.Code == "index-sound");
            using var state = new State(null, null, VaultIdentity.DatabasePath(vault));
            state.Scalar("DELETE FROM notes_fts WHERE rowid IN (SELECT rowid FROM notes_fts LIMIT 1)");
            Assert.Contains(Program.Snapshot(ScarFixture.Now, vault, settings, state).Observations, o => o.Item.Code == "index-mismatch");
        }
        finally
        {
            VaultPaths.UseVault(null);
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", previous);
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-332 · oom dışı bir kanca (başka aracın PreToolUse komutu) hook-path hatası üretmez")]
    public void Y332_ForeignHooksAreNotJudged()
    {
        var user = "{\"hooks\":{\"PreToolUse\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"grep -qE 'remove' && printf x\"}]}],\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"E:/v/.oom/bin/oom.exe --vault E:/v context\"}]}]}}";
        var items = new Doctor().ValidateHooks(user, string.Empty);
        // The foreign PreToolUse command is never judged. The oom hook's exe (E:/v/...) does
        // not exist on disk, and since the final review (O15) that alone is a hook-path error.
        Assert.DoesNotContain(items, item => item.Code == "hook-path" && (item.Key + item.Detail).Contains("grep", StringComparison.Ordinal));
        Assert.DoesNotContain(items, item => item.Code == "duplicate-hook");
    }

    // SPEC R26: the doctor-path S7 scar (HealthWriteFailure_IsPrinted_AndShownByALaterDoctorRun) was
    // superseded by R24(b) — doctor never writes health. S7 stays covered on the write path by
    // DoctorKabul #8 and on the read-only path by LegacyStateKabul #3.

    [Fact(DisplayName = "S7 · RootMap göçü bir kavram notunu yazamazsa Regenerate hatayı fırlatır, sessizce geçmez")]
    public void RootMapMigrationWriteFailure_IsThrown()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            var concepts = Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts")).FullName;
            File.WriteAllText(Path.Combine(concepts, "hafiza-capasi.md"),
                "---\ntitle: Hafıza çapası\naliases: [capa]\ntags: [bellek]\nsources: [2026-09-08.md]\ncreated: 2026-09-01\nupdated: 2026-09-08\n---\n# Hafıza çapası\n\nÇapa.\n\n## İlgili Kavramlar\n- [[gunluk-log]] — çapa günlüğe yazılır.\n");

            Assert.Throws<IOException>(() => new RootMap(vault, files: new LockedFiles()).Regenerate());
        }
        finally { ScarFixture.Remove(vault); }
    }

    private sealed class LockedFiles : IFileOperations
    {
        public void Replace(string source, string destination) => throw new IOException($"kilitli: {destination}");
    }

    [Fact(DisplayName = "Kapı 12-1 · doctor --json şema sürümünü, söz verilen alanları ve gerçek çıkış kodunu taşır")]
    public void Gate12DoctorJsonCarriesItsSchema()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);

        var scanRoot = Path.Combine(vault.Path, "profil");
        Directory.CreateDirectory(Path.Combine(scanRoot, "oom"));
        var run = GateFixture.RunScoped(scanRoot, "doctor", "--json", "--vault", vault.Path);

        var root = JsonDocument.Parse(run.StandardOutput).RootElement;
        Assert.Equal(run.ExitCode, root.GetProperty("exit_code").GetInt32());
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        foreach (var field in new[] { "schema_version", "coverage", "rejection_rate", "pending", "exit_code", "items" })
            Assert.True(root.TryGetProperty(field, out _), $"doctor --json '{field}' alanını kaybetti.");

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        // R5 (should-fix, review, applied): machine identifiers — JSON keys among them — are
        // English lowercase. "Component"/"Code"/"Key"/"Detail" used to leak their C# casing.
        foreach (var field in new[] { "component", "level", "code", "key", "detail", "stale" })
            Assert.True(items[0].TryGetProperty(field, out _), $"doctor --json item '{field}' alanını kaybetti.");
        foreach (var field in new[] { "Component", "Code", "Key", "Detail" })
            Assert.False(items[0].TryGetProperty(field, out _), $"doctor --json item '{field}' büyük harfle sızdırılmamalı (R5).");
    }

    [Fact(DisplayName = "Should-fix (review, applied) · OOM_USERPROFILE gerçek ~/.claude yerine sahte profili kullanır, doctor gerçek profile hiç dokunmaz")]
    public void HookHealth_RespectsUserProfileOverride_NeverTouchesRealProfile()
    {
        var vault = ScarFixture.TempDirectory();
        var fakeProfile = ScarFixture.TempDirectory();
        var previous = Environment.GetEnvironmentVariable("OOM_USERPROFILE");
        try
        {
            Directory.CreateDirectory(Path.Combine(fakeProfile, ".claude"));
            File.WriteAllText(Path.Combine(fakeProfile, ".claude", "settings.json"),
                "{\"hooks\":{\"SessionStart\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"relative/oom.exe context\"}]}]}}");
            Environment.SetEnvironmentVariable("OOM_USERPROFILE", fakeProfile);

            var snapshot = Program.Snapshot(ScarFixture.Now, vault, OomSettings.Defaults(vault), null);

            // Proves isolation, not just override plumbing: this machine's real profile has all
            // four hooks registered with absolute oom.exe paths (measured), so seeing the fake
            // profile's relative-path hook AND its three missing hooks means the real profile
            // was never consulted.
            Assert.Contains(snapshot.Observations, o => o.Item.Code == "hook-path" && o.Item.Key.Contains("relative/oom.exe", StringComparison.Ordinal));
            foreach (var hook in new[] { "UserPromptSubmit", "SessionEnd", "PreCompact" })
                Assert.Contains(snapshot.Observations, o => o.Item.Code == "missing-hook" && o.Item.Key == hook);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OOM_USERPROFILE", previous);
            ScarFixture.Remove(vault);
            ScarFixture.Remove(fakeProfile);
        }
    }

    [Fact(DisplayName = "S7 · farklı bileşen/kod için başarılı health yazımı yalnız o kaydın izini siler, ilgisiz bulguyu korur")]
    public void HealthWriteFailure_ClearingIsPerComponentCode_DoesNotEraseUnrelatedFindings()
    {
        var vault = ScarFixture.TempDirectory();
        var previousLocalAppData = Environment.GetEnvironmentVariable("OOM_LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", vault);
            VaultPaths.UseVault(vault);
            var database = VaultIdentity.EnsureDatabase(vault);
            using (new State(null, null, database)) { } // provisions the schema
            SqliteConnection.ClearAllPools();

            // Component/code values are deliberately fictitious (never written by production
            // code) so this test's entries in HealthLedger's process-wide Memory cache cannot
            // collide with another test's real-code lookups (measured: reusing "compile"/
            // "bozuk-zaman-damgasi" here made CompileScars's own SingleOrDefault see two rows).
            File.SetAttributes(database, File.GetAttributes(database) | FileAttributes.ReadOnly);
            try
            {
                HealthLedger.Record(new HealthItem("scar-test-a", HealthLevel.Error, "birinci-scar-hatasi", "s1", "ilk hata"), ScarFixture.Now);
                HealthLedger.Record(new HealthItem("scar-test-b", HealthLevel.Error, "ikinci-scar-hatasi", "2026-09-27.md", "ikinci hata"), ScarFixture.Now);
            }
            finally
            {
                File.SetAttributes(database, File.GetAttributes(database) & ~FileAttributes.ReadOnly);
            }
            SqliteConnection.ClearAllPools();

            var failurePath = Path.Combine(Path.GetDirectoryName(database)!, HealthLedger.FailureFile);
            Assert.True(File.Exists(failurePath));
            Assert.Equal(2, File.ReadAllLines(failurePath).Length);

            // A later, unrelated write for the FIRST item's component/code succeeds now.
            HealthLedger.Record(new HealthItem("scar-test-a", HealthLevel.Info, "birinci-scar-hatasi", "s2", "artık yazılabiliyor"), ScarFixture.Now);

            Assert.True(File.Exists(failurePath), "İlgisiz bir kaydın çözülmesi diğer bulguyu silmemeli.");
            var remaining = File.ReadAllLines(failurePath);
            Assert.Single(remaining);
            Assert.Contains("ikinci-scar-hatasi", remaining[0], StringComparison.Ordinal);

            // Clean up the process-wide Memory cache entries this test created so no later
            // test observes them.
            HealthLedger.Record(new HealthItem("scar-test-b", HealthLevel.Info, "ikinci-scar-hatasi", "2026-09-27.md", "temizlendi"), ScarFixture.Now);
        }
        finally
        {
            VaultPaths.UseVault(null);
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", previousLocalAppData);
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }
}
