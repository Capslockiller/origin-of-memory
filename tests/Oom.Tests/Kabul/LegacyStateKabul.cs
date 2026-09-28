using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance oracle for lane L45-legacy-state (SPEC-3.1.0.md R24, S5). Written
/// independently of the implementation by the lane's oracle author — the implementer never
/// sees this file's reasoning, only the assertions below. Drives a real oom.exe through
/// <see cref="KabulHarness"/>, following CompileKabul/DoctorKabul's precedent of NOT reusing
/// <c>KabulVaultBuilder</c> (that fixture shapes the F1 context-budget lane's hand layer, not
/// legacy daily_ingest bookkeeping) and seeding state directly at the exact on-disk path the
/// child process resolves (KabulHarness.LocalAppData/oom/&lt;hash of the vault path&gt;/state.db
/// — the same convention DoctorKabul/FlushKabul already established).
///
/// A "legacy-shaped" state.db here is built with RAW ADO.NET (Microsoft.Data.Sqlite), never
/// through <see cref="State"/> — the whole point is a database <see cref="State"/>'s own
/// Provision()/migration has never touched: daily_ingest(name, digest, status, attempts,
/// reasons, ts) with NO `rejections` column, flush_log with NO `masked` column, and no
/// `coverage` table at all — the 3.0.x shape SPEC-3.1.0.md's R24 evidence describes, with a
/// digest column that already existed but held a different (pre-3.1.0) hash algorithm's output.
///
/// MEASURED against this tree (current, pre-fix code) before writing these assertions:
///   - Assertion 1/2 (R24a/b): CompileQueue.IsPending (CompileQueue.cs) compares ANY recorded
///     digest — legacy or not — byte-for-byte against ContentDigest(currentText); a legacy
///     digest from a different algorithm never matches, so every already-'ingested' legacy day
///     comes back pending. Program.Compile.cs:25 opens `compile --dry-run` with OpenState()
///     (StateAccess.ReadWrite) unconditionally — schema migration (ALTER TABLE ... ADD COLUMN)
///     runs and changes state.db bytes even though nothing else about a dry run should write.
///   - Assertion 3 (R24b): CompileQueue.LastCompile (CompileQueue.cs:45) calls
///     HealthLedger.Record on a malformed daily_ingest.ts REGARDLESS of the caller's state
///     access mode — HealthLedger.Record (HealthLedger.cs:24) always opens its OWN plain
///     read-write SqliteConnection straight at VaultIdentity.ExistingDatabase(), ignoring
///     whatever access `state` the caller (Program.Context.cs's manual, read-only path
///     included) actually holds. A manual `oom context` run therefore inserts a `health` row
///     into state.db even though Program.Context.cs:16 chose OpenStateForReading() precisely
///     to avoid writing anything on a manual run (S5's own contract).
///   - Assertion 5 (R24a): since digest comparison treats a legacy digest as directly
///     comparable, a real write-path compile of one new day, on top of legacy 'ingested' rows,
///     immediately reports every legacy day pending again on the very next `oom doctor` (the
///     "storm" R24 describes) — the fix must keep them done.
/// Also MEASURED against eski-exe/oom.exe (OOM_KABUL_EXE): assertions 2, 3, 4 and 5 are RED
/// there too, but for a different reason than on this tree — eski-exe has no per-command
/// read/write access split at all (every command, including a manual `context`, opens the
/// database read-write and always runs its own schema setup), so it fails assertion 2's and
/// 3's byte-identity checks on every command, and it has no v2-prefix concept at all for
/// assertion 5 (digest stays empty after a real compile). Assertion 4's derived-pending count
/// (16) does not match eski-exe's own count either (it prints 21 — the exact old B8 defect
/// SPEC-3.1.0.md's own evidence for that count already describes). Assertion 1 alone is GREEN
/// against eski-exe: eski-exe never compares ANY digest — old or new — against content at
/// all, so a legacy 'ingested' row is trusted verbatim there, which happens to already leave
/// it "done" for an entirely different reason than R24(a) requires (this tree instead compares
/// it and gets a mismatch). See the lane's proof notes for the exact eski-exe transcript.
/// </summary>
public sealed class LegacyStateKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));

    // ------------------------------------------------------------------
    // Oracle assertion 1 — R24(a): an unprefixed (legacy) digest counts as
    // "no digest" — a day already 'ingested' under the old algorithm stays
    // done; it must not be re-queued just because the new algorithm's hash
    // does not match the old one's.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "R24(a) #1 · Legacy (v2 önekesiz) digest karşılaştırılabilir sayılmaz: zaten 'ingested' 5 gün bekleyende 0 çıkar")]
    public void LegacyDigests_AreNotComparable_AlreadyIngestedDaysStayDone()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);

        var names = new[] { "2026-08-01.md", "2026-08-02.md", "2026-08-03.md", "2026-08-04.md", "2026-08-05.md" };
        foreach (var name in names)
            WriteDaily(vault, name);

        var dbPath = StateDbPath(harness, vault);
        CreateLegacyStateDb(dbPath, names.Select(name => new LegacyDailyRow(name, LegacyDigest(name), "ingested", 1, string.Empty, Today.AddDays(-30).ToString("O"))));

        var doctor = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);
        Assert.Equal(0, doctor.ExitCode);
        var pending = ReadIntField(doctor.Stdout, "pending");
        Assert.True(pending == 0, $"R24(a) #1: doctor --json pending = {pending}, beklenen 0. stdout: {doctor.Stdout}");

        var dry = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Today);
        Assert.Equal(0, dry.ExitCode);
        Assert.Contains("bekleyen daily=0", dry.Stdout, StringComparison.Ordinal);
        foreach (var name in names)
            Assert.DoesNotContain(name, dry.Stdout, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Oracle assertion 2 — R24(b): every read-only command (manual/hookless
    // context, doctor, compile --dry-run, sweep --dry-run, retrieve) opens
    // state read-only and never writes — state.db (and -wal/-shm, if
    // present) is byte-identical before and after each one, independently.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "R24(b) #2 · Yedi salt okunur komuttan her biri state.db'yi (ve -wal/-shm) bayt bazında değiştirmeden bırakır")]
    public void ReadOnlyCommands_LeaveStateDbByteIdentical_Individually()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);
        RouteSweepRootsToEmptyDirectory(harness, vault);

        var names = new[] { "2026-08-10.md", "2026-08-11.md" };
        foreach (var name in names)
            WriteDaily(vault, name);

        var dbPath = StateDbPath(harness, vault);
        CreateLegacyStateDb(dbPath, names.Select(name => new LegacyDailyRow(name, LegacyDigest(name), "ingested", 1, string.Empty, Today.AddDays(-10).ToString("O"))));

        var pristine = SnapshotFiles(dbPath);

        (string Label, string[] Args)[] commands =
        [
            ("context", ["context"]),
            ("context --json", ["context", "--json"]),
            ("doctor", ["doctor"]),
            ("doctor --json", ["doctor", "--json"]),
            ("compile --dry-run", ["compile", "--dry-run"]),
            ("sweep --dry-run", ["sweep", "--dry-run"]),
            ("retrieve --query x", ["retrieve", "--query", "x"]),
        ];

        var failures = new List<string>();
        foreach (var (label, args) in commands)
        {
            ResetFiles(dbPath, pristine);
            var result = harness.Run(vault, args, fakeNow: Today);
            var after = SnapshotFiles(dbPath);
            if (!BytesEqual(pristine.Main, after.Main))
                failures.Add($"'{label}': state.db değişti (exit={result.ExitCode}). stdout: {Trunc(result.Stdout)} stderr: {Trunc(result.Stderr)}");
            if (!BytesEqual(pristine.Wal, after.Wal))
                failures.Add($"'{label}': state.db-wal değişti (exit={result.ExitCode}).");
            if (!BytesEqual(pristine.Shm, after.Shm))
                failures.Add($"'{label}': state.db-shm değişti (exit={result.ExitCode}).");
        }

        Assert.True(failures.Count == 0, "R24(b) #2: " + string.Join(" || ", failures));
    }

    // ------------------------------------------------------------------
    // Oracle assertion 3 — R24(b): a malformed daily_ingest.ts must still be
    // surfaced by `oom doctor` (a health finding), but a manual `oom
    // context` must exit 0 and write NOTHING — no state.db byte change, no
    // health-yazilamadi.log marker file — regardless of how that finding
    // gets produced internally.
    // ------------------------------------------------------------------
    [Fact(DisplayName = "R24(b) #3 · Bozuk zaman damgalı satır: elle 'oom context' exit 0 ve hiçbir şey yazmaz; 'oom doctor' yine de satır olarak gösterir")]
    public void MalformedTimestamp_ManualContextWritesNothing_DoctorStillSurfacesIt()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);

        var dbPath = StateDbPath(harness, vault);
        CreateLegacyStateDb(dbPath, [new LegacyDailyRow("2026-07-01.md", LegacyDigest("2026-07-01.md"), "ingested", 1, string.Empty, "bu-gecerli-bir-zaman-damgasi-degil")]);

        var markerPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "health-yazilamadi.log");
        Assert.False(File.Exists(markerPath), "R24(b) #3: fixture kurulumu markerı zaten yaratmış — test kendi başına anlamsız.");

        var pristine = SnapshotFiles(dbPath);

        var context = harness.Run(vault, ["context"], fakeNow: Today);
        Assert.True(context.ExitCode == 0, $"R24(b) #3: exit={context.ExitCode} stdout: {Trunc(context.Stdout)} stderr: {Trunc(context.Stderr)}");

        var afterContext = SnapshotFiles(dbPath);
        Assert.True(BytesEqual(pristine.Main, afterContext.Main),
            $"R24(b) #3: elle 'oom context' state.db'yi değiştirdi. stdout: {Trunc(context.Stdout)} stderr: {Trunc(context.Stderr)}");
        Assert.True(BytesEqual(pristine.Wal, afterContext.Wal), "R24(b) #3: elle 'oom context' state.db-wal yarattı/değiştirdi.");
        Assert.True(BytesEqual(pristine.Shm, afterContext.Shm), "R24(b) #3: elle 'oom context' state.db-shm yarattı/değiştirdi.");
        Assert.False(File.Exists(markerPath),
            $"R24(b) #3: elle 'oom context' hiçbir yazma denemesi yapmamalıydı, ama health-yazilamadi.log oluştu: {context.Stderr}");

        // The malformed row must still be visible on a plain `oom doctor` run — the fix must
        // not simply make CompileQueue.LastCompile forget about it to stop writing.
        var doctor = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);
        Assert.Contains("bozuk-zaman-damgasi", doctor.Stdout, StringComparison.Ordinal);

        // Reading it (even via `doctor`, which the fix must also make read-only per assertion
        // 2) must not persist the finding to disk either.
        var afterDoctor = SnapshotFiles(dbPath);
        Assert.True(BytesEqual(pristine.Main, afterDoctor.Main),
            $"R24(b) #3: 'oom doctor --json' state.db'yi değiştirdi. stdout: {Trunc(doctor.Stdout)}");
    }

    // ------------------------------------------------------------------
    // Oracle assertion 5 — R24(a): after one real write-path compile on top
    // of legacy-done days, the newly written digest starts with 'v2:' and
    // the legacy days do NOT reappear pending on the next `oom doctor`
    // (no storm).
    // ------------------------------------------------------------------
    [Fact(DisplayName = "R24(a) #5 · Gerçek yazma yolundan sonra yeni digest 'v2:' ile başlar; sıradaki 'oom doctor' eski günleri fırtına gibi geri getirmez")]
    public void RealWritePath_NewDigestGetsV2Prefix_LegacyDaysStayDone_NoStorm()
    {
        using var harness = new KabulHarness();
        var vault = BuildMinimalVault(harness.Root);

        var legacyNames = new[] { "2026-08-01.md", "2026-08-02.md" };
        foreach (var name in legacyNames)
            WriteDaily(vault, name);
        const string freshName = "2026-09-27.md";
        WriteDaily(vault, freshName);

        var dbPath = StateDbPath(harness, vault);
        CreateLegacyStateDb(dbPath, legacyNames.Select(name => new LegacyDailyRow(name, LegacyDigest(name), "ingested", 1, string.Empty, Today.AddDays(-40).ToString("O"))));

        var before = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);
        Assert.True(before.Stdout.TrimStart().StartsWith('{'), $"R24(a) #5: doctor --json exit={before.ExitCode} stdout: {Trunc(before.Stdout)} stderr: {Trunc(before.Stderr)}");
        Assert.Equal(1, ReadIntField(before.Stdout, "pending"));

        var okOutput = FileBlock("legacy-storm-kavrami", "Legacy Storm Kavrami") + "=== DONE ===\n";
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(okOutput));
        using var pathScope = fake.ReplaceProcessPath();

        var compile = harness.Run(vault, ["compile"], fakeNow: Today);
        Assert.Equal(0, compile.ExitCode);
        Assert.Contains($"derleme {freshName}:", compile.Stdout, StringComparison.Ordinal);

        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT digest FROM daily_ingest WHERE name = $n";
            command.Parameters.AddWithValue("$n", freshName);
            var digest = command.ExecuteScalar() as string;
            Assert.True(digest is not null && digest.StartsWith("v2:", StringComparison.Ordinal),
                $"R24(a) #5: yeni derlenen günün digest'i 'v2:' ile başlamıyor: '{digest}'");
        }
        SqliteConnection.ClearAllPools();

        var after = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);
        Assert.Equal(0, ReadIntField(after.Stdout, "pending"));
    }

    // ------------------------------------------------------------------
    // Oracle assertion 4 — R24(c): a COPY of the real legacy state.db
    // backup, placed under an isolated OOM_LOCALAPPDATA for a copy of the
    // real vault, reports the SAME derived pending count `oom doctor`
    // would — computed independently in this test from daily/*.md minus
    // knowledge/log.md minus legacy 'done' rows, never a literal number —
    // and the read-only commands leave state.db unchanged.
    // `oom sweep --dry-run` is deliberately NOT exercised against this real
    // vault: its own committed .oom/oom.json points sweep.roots at the
    // REAL %USERPROFILE%\.claude\projects and \.codex\sessions, and the
    // lane's hard rules forbid ever touching those, even to read. Assertion
    // 2 above already proves `sweep --dry-run` read-only-ness on a fixture
    // with harmless sweep.roots.
    // ------------------------------------------------------------------
    [Trait("Kabul", "OzelVault")]
    [Fact(DisplayName = "R24(c) #4 · Gerçek legacy yedek kopyası: doctor'ın bekleyen sayısı bağımsız türetilen sayıyla eşleşir, salt okunur komutlar state.db'yi değiştirmez")]
    public void PrivateVaultCopy_DerivedPendingMatchesDoctor_StateUnchangedByReadOnlyCommands()
    {
        var vault = LoadPrivateVaultPath();
        var backup = LoadLegacyBackupPath();
        Assert.True(Directory.Exists(vault), "R24(c) #4: private vault_path yok. Bu test hiç atlanmaz.");
        Assert.True(File.Exists(backup), "R24(c) #4: legacy_state_backup dosyası yok. Bu test hiç atlanmaz.");

        using var harness = new KabulHarness();
        var dbPath = StateDbPath(harness, vault);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.Copy(backup, dbPath, overwrite: true);
        foreach (var side in new[] { "-wal", "-shm" })
            if (File.Exists(backup + side))
                File.Copy(backup + side, dbPath + side, overwrite: true);

        var expectedPending = DerivePendingCount(vault, dbPath);

        var pristine = SnapshotFiles(dbPath);

        var doctor = harness.Run(vault, ["doctor", "--json"], fakeNow: Today);
        Assert.True(doctor.ExitCode is 0 or 1, $"R24(c) #4: 'oom doctor --json' beklenmedik exit kodu {doctor.ExitCode}. stdout: {Trunc(doctor.Stdout)} stderr: {Trunc(doctor.Stderr)}");
        var pending = ReadIntField(doctor.Stdout, "pending");
        Assert.True(pending == expectedPending,
            $"R24(c) #4: doctor --json pending = {pending}, türetilen bekleyen = {expectedPending}.");

        var afterDoctor = SnapshotFiles(dbPath);
        Assert.True(BytesEqual(pristine.Main, afterDoctor.Main), "R24(c) #4: 'oom doctor --json' gerçek legacy kopyasını değiştirdi.");
        Assert.True(BytesEqual(pristine.Wal, afterDoctor.Wal), "R24(c) #4: 'oom doctor --json' -wal yarattı/değiştirdi.");
        Assert.True(BytesEqual(pristine.Shm, afterDoctor.Shm), "R24(c) #4: 'oom doctor --json' -shm yarattı/değiştirdi.");

        var dry = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Today);
        var dryPendingMatch = Regex.Match(dry.Stdout, @"bekleyen daily=(\d+)");
        Assert.True(dryPendingMatch.Success, $"R24(c) #4: 'compile --dry-run' çıktısında 'bekleyen daily=' yok: {Trunc(dry.Stdout)}");
        Assert.Equal(expectedPending, int.Parse(dryPendingMatch.Groups[1].Value));

        (string Label, string[] Args)[] otherReadOnly =
        [
            ("context", ["context"]),
            ("context --json", ["context", "--json"]),
            ("retrieve --query x", ["retrieve", "--query", "x"]),
        ];

        var failures = new List<string>();
        foreach (var (label, args) in otherReadOnly)
        {
            ResetFiles(dbPath, pristine);
            var result = harness.Run(vault, args, fakeNow: Today);
            var after = SnapshotFiles(dbPath);
            if (!BytesEqual(pristine.Main, after.Main))
                failures.Add($"'{label}': state.db değişti (exit={result.ExitCode}).");
            if (!BytesEqual(pristine.Wal, after.Wal))
                failures.Add($"'{label}': state.db-wal değişti (exit={result.ExitCode}).");
            if (!BytesEqual(pristine.Shm, after.Shm))
                failures.Add($"'{label}': state.db-shm değişti (exit={result.ExitCode}).");
        }
        // compile --dry-run already ran once above (its own byte-identity is proven the same
        // way by assertion 2's fixture test); re-check it here too since it just ran against
        // this exact real copy.
        ResetFiles(dbPath, pristine);
        var dryAgain = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Today);
        var afterDry = SnapshotFiles(dbPath);
        if (!BytesEqual(pristine.Main, afterDry.Main))
            failures.Add($"'compile --dry-run': state.db değişti (exit={dryAgain.ExitCode}).");

        Assert.True(failures.Count == 0, "R24(c) #4: " + string.Join(" || ", failures));
    }

    // ---- fixtures -----------------------------------------------------------------

    private static string BuildMinimalVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static void WriteDaily(string vault, string name) =>
        File.WriteAllText(Path.Combine(vault, "daily", name),
            $"# {Path.GetFileNameWithoutExtension(name)}\n\n- sentetik günlük satırı, kabul harness fixture'ı, gerçek içerik değil.\n", Utf8);

    private static void RouteSweepRootsToEmptyDirectory(KabulHarness harness, string vault)
    {
        var emptyRoot = harness.NewScratchDirectory("no-sweep-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"),
            $$"""
            {
              "sweep": { "roots": [{{JsonSerializer.Serialize(emptyRoot)}}] }
            }
            """, Utf8);
    }

    // Same convention as DoctorKabul.StateDbPath/FlushKabul.StateDbPath: the exact on-disk
    // path the child oom.exe process resolves OOM_LOCALAPPDATA/oom/<hash of the canonical
    // vault path>/state.db to.
    private static string StateDbPath(KabulHarness harness, string vault)
    {
        var canonical = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar);
        return Path.Combine(harness.LocalAppData, "oom", VaultIdentity.Hash(canonical), VaultIdentity.DatabaseName);
    }

    private sealed record LegacyDailyRow(string Name, string Digest, string Status, int Attempts, string Reasons, string Ts);

    /// <summary>Builds a state.db with RAW SQL mirroring the pre-3.1.0 schema: daily_ingest
    /// has no `rejections` column, flush_log has no `masked` column, and there is no
    /// `coverage` table at all. `sessions` is given the CURRENT (prompt_count/last_prompt_ts)
    /// shape on purpose — StateStore.RetireOlderShape only retires the whole file for a
    /// `sessions` table that predates those two columns, which the real live backup this
    /// fixture stands in for does not (it has been through that migration for a long time);
    /// giving it the current shape here keeps this fixture's only "legacy" axis the one R24
    /// is actually about — daily_ingest's digest/rejections and flush_log's masked/coverage
    /// — instead of also exercising the unrelated whole-file-retirement path.</summary>
    private static void CreateLegacyStateDb(string path, IEnumerable<LegacyDailyRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.Delete(path);
        foreach (var side in new[] { "-wal", "-shm" })
            if (File.Exists(path + side))
                File.Delete(path + side);

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText =
                    "CREATE TABLE sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT, prompt_count INTEGER NOT NULL DEFAULT 0, first_seen TEXT, last_prompt_ts TEXT);" +
                    "CREATE TABLE flush_log(ts TEXT, session_id TEXT, reason TEXT, outcome TEXT, turns INTEGER, chars INTEGER, backend TEXT);" +
                    "CREATE TABLE retry_queue(session_id TEXT PRIMARY KEY, attempts INTEGER, next_at TEXT, last_error TEXT);" +
                    "CREATE TABLE sweep_stamps(path TEXT PRIMARY KEY, mtime TEXT, size INTEGER, outcome TEXT);" +
                    "CREATE TABLE daily_ingest(name TEXT PRIMARY KEY, digest TEXT, status TEXT, attempts INTEGER, reasons TEXT, ts TEXT);" +
                    "CREATE TABLE health(ts TEXT, component TEXT, level TEXT, code TEXT, key TEXT, detail TEXT);" +
                    "CREATE TABLE notes(name TEXT PRIMARY KEY, title TEXT, aliases TEXT, tags TEXT, body TEXT, updated TEXT);" +
                    "CREATE VIRTUAL TABLE notes_fts USING fts5(name UNINDEXED, title, aliases, tags, body);" +
                    "CREATE TABLE oom_index_meta(generation INTEGER NOT NULL, manifest_digest TEXT NOT NULL, built_at TEXT NOT NULL);";
                ddl.ExecuteNonQuery();
            }

            foreach (var row in rows)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO daily_ingest(name, digest, status, attempts, reasons, ts) VALUES ($n,$d,$s,$a,$r,$t)";
                insert.Parameters.AddWithValue("$n", row.Name);
                insert.Parameters.AddWithValue("$d", row.Digest);
                insert.Parameters.AddWithValue("$s", row.Status);
                insert.Parameters.AddWithValue("$a", row.Attempts);
                insert.Parameters.AddWithValue("$r", row.Reasons);
                insert.Parameters.AddWithValue("$t", row.Ts);
                insert.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>A deterministic 64-lowercase-hex string per name that never equals
    /// CompileQueue.ContentDigest of anything WriteDaily writes — mirrors the shape (SHA-256
    /// hex) of a real legacy digest, from a different (synthetic) input, exactly R24's "an
    /// older algorithm" framing.</summary>
    private static string LegacyDigest(string name) =>
        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes("legacy-algoritma-v0:" + name))).ToLowerInvariant();

    private readonly record struct FileSnapshot(byte[] Main, byte[]? Wal, byte[]? Shm);

    private static FileSnapshot SnapshotFiles(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        var main = File.ReadAllBytes(dbPath);
        var wal = File.Exists(dbPath + "-wal") ? File.ReadAllBytes(dbPath + "-wal") : null;
        var shm = File.Exists(dbPath + "-shm") ? File.ReadAllBytes(dbPath + "-shm") : null;
        return new FileSnapshot(main, wal, shm);
    }

    private static void ResetFiles(string dbPath, FileSnapshot pristine)
    {
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(dbPath, pristine.Main);
        WriteOrDelete(dbPath + "-wal", pristine.Wal);
        WriteOrDelete(dbPath + "-shm", pristine.Shm);
    }

    private static void WriteOrDelete(string path, byte[]? content)
    {
        if (content is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        File.WriteAllBytes(path, content);
    }

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.AsSpan().SequenceEqual(b);
    }

    private static string Trunc(string text) => text.Length <= 400 ? text : text[..400] + "…";

    private static int ReadIntField(string json, string field)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(field).GetInt32();
    }

    /// <summary>B8's own derivation, reimplemented independently here (never delegating to
    /// CompileQueue.Pending/IsPending — the code under test): daily/*.md file names minus
    /// days already logged in knowledge/log.md minus days already 'done' in state, REGARDLESS
    /// of that row's digest (a legacy digest must never turn a done day back into pending —
    /// that is exactly R24(a)'s contract, so this independent derivation intentionally never
    /// even reads the digest column).</summary>
    private static int DerivePendingCount(string vault, string dbPath)
    {
        var dailyDirectory = Path.Combine(vault, "daily");
        var dailyNames = Directory.Exists(dailyDirectory)
            ? Directory.EnumerateFiles(dailyDirectory, "*.md", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).OfType<string>().ToArray()
            : [];

        var logPath = Path.Combine(vault, "knowledge", "log.md");
        var loggedDays = File.Exists(logPath)
            ? Regex.Matches(File.ReadAllText(logPath, Utf8), @"compile\s*\|\s*(\S+\.md)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var doneInState = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM daily_ingest WHERE status IN ('ingested','adopted','parked','partial','low-confidence')";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                doneInState.Add(reader.GetString(0));
        }
        SqliteConnection.ClearAllPools();

        return dailyNames.Count(name => !loggedDays.Contains(name) && !doneInState.Contains(name));
    }

    /// <summary>One well-formed, self-contained "=== FILE: ... ===" block — mirrors
    /// CompileKabul.FileBlock (that copy is private to that test class; this one is this
    /// file's own).</summary>
    private static string FileBlock(string slug, string title) =>
        $"=== FILE: knowledge/concepts/{slug}.md ===\n" +
        "---\n" +
        $"title: {title}\n" +
        "aliases: []\n" +
        "tags: [sentetik]\n" +
        "sources: [2026-09-27.md]\n" +
        "created: 2026-09-27\n" +
        "updated: 2026-09-27\n" +
        "type: concept\n" +
        "hub: genel\n" +
        "---\n" +
        $"# {title}\n" +
        "Kalıcı bilgi, kabul harness fixture'ı.\n\n" +
        "## İlgili Kavramlar\n" +
        "- [[ilk-kavram]] ilk bağlantı gerekçesi\n" +
        "- [[ikinci-kavram]] ikinci bağlantı gerekçesi\n" +
        "=== END FILE ===\n";

    private static FakeClaudeResponse CompileOutput(string modelText) =>
        new(Stdout: JsonSerializer.Serialize(new { result = modelText }));

    // ---- private acceptance data (Kabul=OzelVault only) ----------------------------

    private const string PrivateEnvironmentVariable = "OOM_KABUL_PRIVATE";

    /// <summary>Same resolution order as OzelKabul.PrivateEval.Load: an explicit
    /// OOM_KABUL_PRIVATE, else the fixed location beside the private retrieval eval set. Never
    /// echoes the resolved path's value in a failure message — only the environment variable
    /// name and the failure category (OzelKabul's own privacy convention).</summary>
    private static string ResolvePrivateJsonPath()
    {
        var overridden = Environment.GetEnvironmentVariable(PrivateEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Oom.sln")))
                return Path.Combine(directory.FullName, "..", "oom-surum-3.1", "degerlendirme", "ozel-kabul.json");

        throw new InvalidOperationException($"{PrivateEnvironmentVariable}: no Oom.sln above {AppContext.BaseDirectory}; cannot derive the default path.");
    }

    private static JsonElement LoadPrivateRoot()
    {
        var path = ResolvePrivateJsonPath();
        if (!File.Exists(path))
            throw new InvalidOperationException($"{PrivateEnvironmentVariable}: private acceptance data not found. This is never skipped.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string LoadPrivateVaultPath() =>
        LoadPrivateRoot().TryGetProperty("vault_path", out var value) && value.GetString() is { Length: > 0 } vault
            ? vault
            : throw new InvalidOperationException($"{PrivateEnvironmentVariable}: private JSON missing 'vault_path'.");

    /// <summary>Never printed anywhere — only used to File.Copy a COPY into the isolated
    /// harness LocalAppData; the lane brief forbids ever committing or echoing this value.</summary>
    private static string LoadLegacyBackupPath() =>
        LoadPrivateRoot().TryGetProperty("legacy_state_backup", out var value) && value.GetString() is { Length: > 0 } backup
            ? backup
            : throw new InvalidOperationException($"{PrivateEnvironmentVariable}: private JSON missing 'legacy_state_backup'.");
}
