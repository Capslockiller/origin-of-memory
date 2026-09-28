using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// L1-vault-path acceptance (SPEC-3.1.0.md F6-2, F6-3, NB-2, R1). One vault identity:
/// every form a vault path can arrive in — as typed on the command line, lower-cased,
/// forward-slashed, or discovered through the installed <c>vault.json</c> descriptor —
/// must resolve to the SAME on-disk state directory, and the well-known live hash
/// (<c>E:\OdenaOS</c> → <c>71635227ca8425d0</c>) must never move. <c>save</c> must accept
/// free text (no mandatory 3-field rule) and must write through the vault, never a
/// CWD-relative <c>daily/</c>.
///
/// These tests are the INDEPENDENT oracle for this lane: written against the acceptance
/// text only, never against this lane's own implementation, per SPEC R3.
/// </summary>
public sealed class VaultPathKabul
{
    // ------------------------------------------------------------------
    // Assertion 1: four forms of the same vault path share exactly one
    // state directory under oom/, named Hash(on-disk-case path).
    // ------------------------------------------------------------------

    [Fact(DisplayName = "L1-vault-path #1 · aynı kasa yolunun dört biçimi (harfi harfine, küçük harf, ileri eğik çizgi, vault.json) tek durum dizini paylaşır")]
    public void FourFormsOfTheSameVaultPath_ShareExactlyOneStateDirectory()
    {
        using var harness = new KabulHarness();

        // A mixed-case fixture directory, created with this EXACT casing so it is the
        // on-disk spelling every form below is measured against.
        var onDiskCase = harness.NewScratchDirectory("KabulVault");

        var asIs = onDiskCase;
        var lowerCased = onDiskCase.ToLowerInvariant();
        var forwardSlashTrailing = onDiskCase.Replace('\\', '/').TrimEnd('/') + "/";

        var resultAsIs = harness.Run(asIs, ["doctor", "--fix"]);
        var resultLower = harness.Run(lowerCased, ["doctor", "--fix"]);
        var resultForwardSlash = harness.Run(forwardSlashTrailing, ["doctor", "--fix"]);
        var resultDescriptor = RunDoctorFixViaDescriptor(harness, forwardSlashTrailing);

        Assert.Equal(0, resultAsIs.ExitCode);
        Assert.Equal(0, resultLower.ExitCode);
        Assert.Equal(0, resultForwardSlash.ExitCode);
        Assert.Equal(0, resultDescriptor.ExitCode);

        var stateDirectory = Path.Combine(harness.LocalAppData, "oom");
        var created = Directory.Exists(stateDirectory)
            ? Directory.GetDirectories(stateDirectory).Select(Path.GetFileName).ToArray()
            : [];

        // The oracle for "what the single directory should be named" is the SAME public
        // hash function the product uses (VaultIdentity.Hash), called on the already
        // on-disk-cased path — that holds regardless of whether the fix resolves case
        // inside Hash itself or in its callers (VaultPaths.UseVault/ReadVault).
        var expectedName = VaultIdentity.Hash(onDiskCase);

        Assert.True(created.Length == 1,
            $"beklenen: oom/ altında tam 1 dizin; bulunan: {created.Length} ({string.Join(", ", created)}). " +
            $"harfi harfine='{asIs}' küçük='{lowerCased}' ileri-eğik='{forwardSlashTrailing}'");
        Assert.Equal(expectedName, created[0]);
    }

    /// <summary>
    /// Runs the exe as <c>oom doctor --fix</c> with NO <c>--vault</c> flag, relying
    /// instead on the installed descriptor (<c>VaultPaths.ReadVault()</c> in
    /// Boundaries.cs: an <c>AppContext.BaseDirectory\vault.json</c> beside the exe — the
    /// same mechanism <c>oom install</c> writes). <see cref="KabulHarness.Run"/> always
    /// injects <c>--vault</c> itself, so this form is driven directly rather than through
    /// the harness. The descriptor file sits beside the shared oom.exe build output, so
    /// any pre-existing file there is backed up and restored — safe because this test
    /// suite runs with parallelism disabled (xunit.runner.json: parallelizeAssembly=false,
    /// parallelizeTestCollections=false), so no other process-boundary test can observe
    /// the file mid-swap.
    /// </summary>
    private static KabulResult RunDoctorFixViaDescriptor(KabulHarness harness, string vaultFormForDescriptor)
    {
        var exeDirectory = Path.GetDirectoryName(harness.ExePath)!;
        var descriptorPath = Path.Combine(exeDirectory, "vault.json");
        var backupPath = descriptorPath + ".l1kahin-backup-" + Guid.NewGuid().ToString("N");
        var hadExisting = File.Exists(descriptorPath);
        if (hadExisting)
            File.Move(descriptorPath, backupPath);

        try
        {
            File.WriteAllText(descriptorPath,
                JsonSerializer.Serialize(new { vault = vaultFormForDescriptor, schema = 1 }),
                new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = harness.ExePath,
                WorkingDirectory = harness.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            startInfo.ArgumentList.Add("doctor");
            startInfo.ArgumentList.Add("--fix");
            startInfo.Environment.Remove("OOM_INVOKED_BY");
            startInfo.Environment["OOM_LOCALAPPDATA"] = harness.LocalAppData;
            // Same profile isolation as KabulHarness.Run: default sweep roots expand
            // %USERPROFILE%, which on a CI runner has no ~/.claude/projects at all.
            startInfo.Environment["OOM_USERPROFILE"] = harness.UserProfile;
            startInfo.Environment["USERPROFILE"] = harness.UserProfile;

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new TimeoutException("oom doctor --fix (vault.json biçimi) 30 saniyede tamamlanmadı.");
            }

            return new KabulResult(process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult(), TimeSpan.Zero);
        }
        finally
        {
            File.Delete(descriptorPath);
            if (hadExisting)
                File.Move(backupPath, descriptorPath);
        }
    }

    // ------------------------------------------------------------------
    // Assertion 2: the hash of the canonical live-vault string is stable,
    // and an existing fixture directory hashes the same regardless of the
    // case it is spelled in.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "L1-vault-path #2a · 'E:\\OdenaOS' dizgesinin hash'i 71635227ca8425d0 (salt dizge girdisi, canlı kasa hiç okunmaz)")]
    public void Hash_OfCanonicalLiveVaultString_MatchesTheKnownLiveValue()
    {
        // Pure string input: this calls VaultIdentity.Hash on a literal, never touching
        // the filesystem, so it never stats the real E:\OdenaOS (hard rule: never touch
        // the live vault).
        Assert.Equal("71635227ca8425d0", VaultIdentity.Hash(@"E:\OdenaOS"));
    }

    [Fact(DisplayName = "L1-vault-path #2b · var olan bir fixture dizini hangi harf biçiminde verilirse verilsin diskteki yazılışıyla aynı hash'i verir")]
    public void Hash_OfExistingFixtureDirectory_IsIndependentOfInputCase()
    {
        var root = Path.Combine(Path.GetTempPath(), "oom-l1-kahin-hash-" + Guid.NewGuid().ToString("N")[..12]);
        var onDiskCase = Path.Combine(root, "KabulVault");
        Directory.CreateDirectory(onDiskCase);
        try
        {
            var expected = VaultIdentity.Hash(onDiskCase);

            Assert.Equal(expected, VaultIdentity.Hash(onDiskCase.ToLowerInvariant()));
            Assert.Equal(expected, VaultIdentity.Hash(onDiskCase.ToUpperInvariant()));
            Assert.Equal(expected, VaultIdentity.Hash(onDiskCase.Replace('\\', '/')));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------
    // Assertion 3: `oom save "basit not"` accepts free text (no mandatory
    // 3-field rule) and writes it to today's daily file.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "L1-vault-path #3 · oom save serbest metni kabul eder, zorunlu 3 alan aranmaz")]
    public void Save_AcceptsFreeText_ExitsZeroAndWritesToTodaysDaily()
    {
        using var harness = new KabulHarness();
        var vault = harness.NewScratchDirectory("vault-save-freetext");
        var today = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));

        var result = harness.Run(vault, ["save", "basit not"], fakeNow: today);

        Assert.Equal(0, result.ExitCode);

        var dailyPath = Path.Combine(vault, "daily", $"{today:yyyy-MM-dd}.md");
        Assert.True(File.Exists(dailyPath),
            $"beklenen dosya yok: {dailyPath}\nexit={result.ExitCode}\nstdout: {result.Stdout}\nstderr: {result.Stderr}");
        Assert.Contains("basit not", File.ReadAllText(dailyPath), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Assertion 4: `oom save --session-json` writes through the vault, not
    // a CWD-relative daily/, no matter which directory it is run from.
    // ------------------------------------------------------------------

    [Fact(DisplayName = "L1-vault-path #4 · oom save --session-json her iki farklı CWD'den de aynı kasanın daily dosyasına yazar, CWD'ye asla değil")]
    public void SaveSessionJson_WritesThroughTheVault_RegardlessOfCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "oom-l1-kahin-save-" + Guid.NewGuid().ToString("N")[..12]);
        var vault = Path.Combine(root, "vault");
        var cwdA = Path.Combine(root, "cwd-a");
        var cwdB = Path.Combine(root, "cwd-b");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(cwdA);
        Directory.CreateDirectory(cwdB);

        var originalCwd = Environment.CurrentDirectory;
        try
        {
            // Runs Program.RunSave directly, in-process (same technique as
            // SaveScars.Y107SaveSessionJsonRejectsMalformedInputWithoutEscapingProgram),
            // rather than through KabulHarness as a child process. Reason: every
            // process-boundary `--session-json` invocation with --vault set makes
            // Runner see itself as "configured" (Runner.cs: _configured = configured ??
            // VaultPaths.ReadVault() is not null, and ReadVault() is non-null whenever
            // --vault was passed) and it then actually launches the real `claude` CLI as
            // a subprocess to summarize — slow, non-deterministic, and a real call
            // against the live API on whatever machine runs this suite. Clearing the
            // in-process VaultPaths override below makes Runner see itself as
            // unconfigured instead (same "no real claude call" seam the Kabul harness's
            // sibling test, Gate11SaveSessionJsonWritesImportedDailyBlock, gets by
            // constructing its own Runner(configured: false) explicitly) — Flush then
            // takes its deterministic extractive-summary fallback path, which still
            // exercises the exact same DailyPath()/Commit() code this bug lives in.
            VaultPaths.UseVault(null);

            var baseTime = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));
            var markerA = "l1-cwd-a-turu-" + Guid.NewGuid().ToString("N")[..8];
            var markerB = "l1-cwd-b-turu-" + Guid.NewGuid().ToString("N")[..8];
            var fileA = Path.Combine(cwdA, "session.json");
            var fileB = Path.Combine(cwdB, "session.json");
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(fileA, SessionJson("id-" + markerA, markerA, baseTime), utf8);
            File.WriteAllText(fileB, SessionJson("id-" + markerB, markerB, baseTime.AddMinutes(5)), utf8);

            var program = typeof(Save).Assembly.GetType("Oom.Program", throwOnError: true)!;
            var runSave = program.GetMethod("RunSave", BindingFlags.Static | BindingFlags.NonPublic)!;

            Directory.SetCurrentDirectory(cwdA);
            var rcA = (int)runSave.Invoke(null, [new[] { "save", "--session-json", fileA }, vault])!;

            Directory.SetCurrentDirectory(cwdB);
            var rcB = (int)runSave.Invoke(null, [new[] { "save", "--session-json", fileB }, vault])!;

            Assert.Equal(0, rcA);
            Assert.Equal(0, rcB);

            // Both event times land the same calendar day (5 minutes apart, computed the
            // same way Flush.EventTime does), so both must land in the SAME vault daily
            // file — never a directory rooted at either CWD.
            var eventTimeA = TimeZoneInfo.ConvertTime(baseTime.AddMinutes(2), TimeZoneInfo.Local);
            var eventTimeB = TimeZoneInfo.ConvertTime(baseTime.AddMinutes(5).AddMinutes(2), TimeZoneInfo.Local);
            Assert.Equal(eventTimeA.Date, eventTimeB.Date);

            var expectedDaily = Path.Combine(vault, "daily", $"{eventTimeA:yyyy-MM-dd}.md");
            Assert.True(File.Exists(expectedDaily),
                $"beklenen dosya yok: {expectedDaily}\nrcA={rcA} rcB={rcB}\n" +
                $"cwd-a/daily var mı: {Directory.Exists(Path.Combine(cwdA, "daily"))}\n" +
                $"cwd-b/daily var mı: {Directory.Exists(Path.Combine(cwdB, "daily"))}");

            var body = File.ReadAllText(expectedDaily, utf8);
            Assert.Contains(markerA, body, StringComparison.Ordinal);
            Assert.Contains(markerB, body, StringComparison.Ordinal);

            Assert.False(Directory.Exists(Path.Combine(cwdA, "daily")), "CWD-A altında 'daily/' oluşmamalı (kasaya değil CWD'ye yazılmış olurdu)");
            Assert.False(Directory.Exists(Path.Combine(cwdB, "daily")), "CWD-B altında 'daily/' oluşmamalı (kasaya değil CWD'ye yazılmış olurdu)");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            VaultPaths.UseVault(null);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string SessionJson(string id, string marker, DateTimeOffset start)
    {
        var turns = Enumerable.Range(0, 3).Select(i => new
        {
            index = i,
            role = i % 2 == 0 ? "user" : "assistant",
            kind = "text",
            text = $"{marker} mesaj {i}",
            timestamp = start.AddMinutes(i).ToString("O")
        });
        return JsonSerializer.Serialize(new { id, source = "l1-kabul", turns, startedAt = start.ToString("O") });
    }
}
