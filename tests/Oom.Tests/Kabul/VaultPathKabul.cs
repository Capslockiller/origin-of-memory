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



    // ------------------------------------------------------------------
    // Assertion 4: `oom save --session-json` writes through the vault, not
    // a CWD-relative daily/, no matter which directory it is run from.
    // ------------------------------------------------------------------



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
