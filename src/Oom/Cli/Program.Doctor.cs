using System.Globalization;
using System.Text.Json;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    /// NB-9: the rows the default view always prints, green or not; every other green row
    /// is only counted.
    private static readonly string[] HeadlineCodes = ["coverage", "rejection-rate", "refused", "last-sweep", "last-compile", "pending"];

    private static int Health(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var fixing = args.Contains("--fix");
        var all = args.Contains("--all");
        DoctorResult result;
        try
        {
            using var state = fixing ? OpenState() : OpenStateForReading();
            var doctor = new Doctor(null,
                moment =>
                {
                    var snapshot = Snapshot(moment, vault, settings, state);
                    return all ? snapshot : snapshot with { Observations = [.. snapshot.Observations.Where(o => o.Item.Component != "kit")] };
                },
                () => { if (state is not null) Repair(vault, settings, state); });
            result = fixing ? doctor.Fix() : doctor.Check(now);
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or StateSchemaException)
        {
            // Minor (Claude review): a corrupt state.db (measured: SQLite "file is not a
            // database", error 26) used to crash the whole `oom doctor` invocation with an
            // unhandled exception and NO rows printed at all — context's own error message
            // tells the user to run doctor for exactly this diagnosis, so doctor dying too
            // left no way forward. One red "state" row names the failure and doctor exits 1
            // like any other red row, instead of dying.
            result = CorruptStateResult(error);
        }

        if (args.Contains("--json"))
        {
            Console.WriteLine(new Doctor().ToJson(result));
            return result.ExitCode;
        }

        if (args.Contains("--quiet"))
        {
            var loud = result.Items.Where(item => item.Level is not HealthLevel.Info).ToArray();
            foreach (var item in loud)
                Console.Error.WriteLine($"{item.Component}: {item.Detail}");
            return result.ExitCode;
        }

        Console.WriteLine($"{"bileşen",-12} {"düzey",-8} {"kod",-22} {"anahtar",-14} ayrıntı");
        foreach (var item in result.Items.Where(item => all || item.Level is not HealthLevel.Info || HeadlineCodes.Contains(item.Code)))
            Console.WriteLine($"{item.Component,-12} {Level(item.Level),-8} {item.Code,-22} {Short(item.Key),-14} {item.Detail}{(item.Stale ? " (eski)" : string.Empty)}");

        Console.WriteLine($"kapsama {Doctor.CoverageText(result.Coverage)} · ret {result.RejectionRate:P0} · bekleyen {result.Pending}");
        int Count(HealthLevel level) => result.Items.Count(item => item.Level == level);
        Console.WriteLine($"{Count(HealthLevel.Info)} yeşil · {Count(HealthLevel.Warning)} uyarı · {Count(HealthLevel.Error)} hata{(all ? string.Empty : " — tümü: oom doctor --all")}");
        return result.ExitCode;
    }

    /// <summary>Minor (Claude review): the single red row doctor reports in place of crashing
    /// when opening or reading state.db throws — a corrupt file, or a schema it cannot
    /// migrate. Coverage/rejection/pending are unmeasurable, not zero, so JSON reports
    /// coverage as null (NaN) rather than a false "0%".</summary>
    private static DoctorResult CorruptStateResult(Exception error) => new(
        [new HealthItem("state", HealthLevel.Error, "state-bozuk", "state.db", $"state.db okunamadı (bozuk): {error.Message}")],
        double.NaN, 0.0, 0, 1);

    internal static DoctorSnapshot Snapshot(DateTimeOffset now, string vault, OomSettings settings, State? state)
    {
        long Count(string sql) => state is null ? 0 : state.Scalar(sql);

        var flushes = Count("SELECT COUNT(*) FROM flush_log WHERE outcome <> 'summary'");
        var week = RecentOutcomes(state, now.AddDays(-7));
        var rejected = week.Count(outcome => outcome is "retry" or "parked");
        var refused = week.Count(outcome => outcome is "refused");
        var freshness = MemoryFreshness.Read(vault, state, now);
        var invalid = Concepts(vault).Count - Corpus(vault).Count;
        var database = VaultPaths.StateDatabase();
        // Defect 6: a configured sweep root that is not on disk can never be scanned, so it is
        // a red row naming the root rather than a silently empty half of the sweep.
        //
        // R33/M6 (Claude review, amends R32's last sentence): a missing root is only an
        // ERROR when every configured root is missing (nothing can ever be swept) or the
        // roots were set EXPLICITLY in oom.json (the user asked for exactly this list, so a
        // missing one is a real misconfiguration). A missing DEFAULT root — e.g.
        // ~/.codex/sessions on a Claude-only machine that never touched oom.json — while
        // another root still exists is a WARNING, not a hard failure.
        var missingRoots = settings.Sweep.Roots.Where(root => !Directory.Exists(root)).ToArray();
        var allRootsMissing = missingRoots.Length > 0 && missingRoots.Length == settings.Sweep.Roots.Count;
        var missingRootLevel = allRootsMissing || SweepRootsExplicitlyConfigured(vault) ? HealthLevel.Error : HealthLevel.Warning;
        List<DoctorObservation> observations =
        [
            state is null
                ? new DoctorObservation(new HealthItem("state", HealthLevel.Warning, "state-yok", "state.db",
                    $"Durum veritabanı yok: {database} — bu vault için hiçbir yazan komut koşmamış. Salt okunur komutlar onu yaratmaz; `oom sweep` ya da `oom doctor --fix` yaratır."), now)
                : Observe(now, "state", "state-db", "state.db", $"{database} · {(database is not null && File.Exists(database) ? new FileInfo(database).Length : 0)} bayt"),
            Observe(now, "state", "fts5-ok", "notes_fts", $"İndekste {(state is null ? 0 : Rows(state, "notes_fts"))} not"),
            Observe(now, "notes", "corpus", "concepts", $"{Corpus(vault).Count} geçerli kavram notu"),
            Observe(now, "runner", "backend", "claude", $"{settings.Backend.Claude.Fast} / {settings.Backend.Claude.Smart}"),
            Observe(now, "sweep", "roots", "sweep.roots", string.Join(" · ", settings.Sweep.Roots)),
            .. missingRoots.Select(root => new DoctorObservation(
                new HealthItem("sweep", missingRootLevel, "missing-root", root,
                    $"Yapılandırılmış sweep kökü yok: {root} — bu kök taranmıyor."), now)),
            Observe(now, "sweep", "flush-log", "rows", $"{flushes} flush_log satırı"),
            .. settings.UnknownKeys.Select(key => new DoctorObservation(
                new HealthItem("config", HealthLevel.Warning, "unknown-key", key, $"oom.json içinde bilinmeyen anahtar: {key}"), now)),
            .. settings.LoadError is null ? Array.Empty<DoctorObservation>() : [new DoctorObservation(
                new HealthItem("config", HealthLevel.Error, "hata", "json",
                    $"oom.json okunamadı, varsayılanlar kullanılıyor — {settings.LoadError}"), now)],
            .. HookHealth(vault).Select(item => new DoctorObservation(item, now)),
            new(new Doctor().IndexHealth(MakeRetrieve(vault, settings, settings.Retrieve.Top)), now),
            .. VaultSchemaObservations(now, vault),
            // Defect 2: a failed hook subprocess is reported whatever PATH says — this reads
            // back whatever a REAL prior hook invocation persisted (HealthLedger.Read()), so
            // an intermittently broken hook stays visible on a later doctor run even though
            // that run's own PATH now resolves `claude` fine. (Misleading comment fixed,
            // Claude review: this method itself emits no separate "claude reachable" row —
            // Doctor.CheckClaudeReachability/DefaultSnapshot is a different code path the CLI
            // does not call.)
            .. HealthLedger.Read(),
            .. KitObservations(now)
        ];

        // Defect 6: the 7-day coverage contract is read off the sweep stamps themselves —
        // ok / (ok + partial + unreadable) — with 'skipped' excluded from the denominator.
        // A vault whose stamps carry no contract status in the window falls back to the last
        // numeric coverage row, so pre-3.1.0 vaults keep reporting a measurement.
        //
        // M3 (Claude review): the stamp count alone is not enough — a budget-skipped or
        // never-swept session never gets a stamp written for it at all (SweepRun only
        // writes one after actually reading/flushing a candidate), so it is invisible to
        // StampCoverage even though the sweep's OWN coverage row already counted it as
        // uncovered. Take the STRICTER (lower) of the two fractions, so a session the row
        // knows about but the stamps do not still drags coverage down instead of vanishing.
        var window = freshness.Coverage;
        var stamped = StampCoverage(state, now);
        (double Fraction, int Total)? stampReading = stamped is { Total: > 0 } counts
            ? ((double)counts.Covered / counts.Total, counts.Total)
            : null;
        (double Fraction, int Total)? rowReading = window is null
            ? null
            : (window.Total == 0 ? 1.0 : (double)window.Covered / window.Total, window.Total);
        var stricter = stampReading is null ? rowReading
            : rowReading is null ? stampReading
            : stampReading.Value.Fraction <= rowReading.Value.Fraction ? stampReading : rowReading;
        var coverage = stricter?.Fraction ?? double.NaN;
        var windowTotal = stricter?.Total ?? 0;
        var parkedDailies = ParkedDailies(state);
        return new DoctorSnapshot(observations,
            coverage,
            week.Count == 0 ? 0.0 : (double)rejected / week.Count,
            freshness.Pending,
            parkedDailies.Count,
            (int)Count("SELECT COUNT(*) FROM retry_queue"),
            Directory.Exists(Path.Combine(vault, ".oom", "quarantine"))
                ? Directory.EnumerateFiles(Path.Combine(vault, ".oom", "quarantine"), "*.md").Count() : 0,
            Math.Max(0, invalid),
            refused,
            freshness,
            windowTotal,
            parkedDailies,
            (int)Count("SELECT COUNT(*) FROM retry_queue WHERE attempts >= 5"));
    }

    /// <summary>
    /// R33/M6: whether <c>sweep.roots</c> was set EXPLICITLY in oom.json, as opposed to the
    /// settings loader falling back to the OS default roots. Read directly from the file
    /// (rather than threading a new field through <c>OomSettings</c>/<c>SweepSettings</c>,
    /// which every other command would then have to carry too) since only this one
    /// Error-vs-Warning decision needs it. Any read/parse failure — file missing, no
    /// 'sweep.roots' key, or a malformed oom.json (already reported elsewhere as its own
    /// 'hata' row) — means "not explicit", the safer (Warning) default.
    /// </summary>
    private static bool SweepRootsExplicitlyConfigured(string vault)
    {
        try
        {
            var path = Path.Combine(vault, ".oom", "oom.json");
            if (!File.Exists(path))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(path, Utf8).TrimStart('﻿'));
            return document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("sweep", out var sweep)
                && sweep.ValueKind is JsonValueKind.Object
                && sweep.TryGetProperty("roots", out var roots)
                && roots.ValueKind is JsonValueKind.Array;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Defect 1: the parked DAILIES — <c>daily_ingest.status='parked'</c> — named with the last
    /// recorded rejection reason, so a day parked after three compile rejections is visible
    /// instead of being absent from the pending count and from every report.
    /// </summary>
    private static IReadOnlyList<string> ParkedDailies(State? state)
    {
        if (state is null) return [];
        IReadOnlyList<(string Name, string? Value)> rows;
        try
        {
            rows = state.ReadNamedColumn("SELECT name, reasons FROM daily_ingest WHERE status = 'parked' ORDER BY name");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return [];
        }

        return [.. rows.Select(row => LastReason(row.Value) is { Length: > 0 } reason
            ? $"{row.Name} ({reason})"
            : row.Name)];
    }

    private static string LastReason(string? reasons) =>
        reasons?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

    /// <summary>
    /// Defect 6: (covered, total) over the last 7 days of sweep stamps. 'ok' is covered,
    /// 'partial' and 'unreadable' are not covered but ARE in the denominator, and 'skipped'
    /// is excluded from it entirely. R35: the raw outcome is normalised (<see cref="NormalizeStampOutcome"/>)
    /// before any of that, so a pre-3.1.0 (legacy) status is judged honestly instead of
    /// silently leaving the denominator as an unrecognised string.
    /// </summary>
    private static (int Covered, int Total)? StampCoverage(State? state, DateTimeOffset now)
    {
        if (state is null) return null;
        IReadOnlyList<(string Name, string? Value)> rows;
        try
        {
            // Minor (Claude review): ReadNamedColumn reads column 0 with GetString(), which
            // throws on a NULL value — a hand-edited or pre-3.1.0 row with a NULL outcome
            // must not crash doctor. COALESCE it to '', which NormalizeStampOutcome then
            // maps like any other unrecognised status (R35: 'partial', not excluded).
            rows = state.ReadNamedColumn("SELECT COALESCE(outcome, ''), mtime FROM sweep_stamps");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }

        var since = now.AddDays(-7);
        var covered = 0;
        var total = 0;
        foreach (var row in rows)
        {
            var status = NormalizeStampOutcome(row.Name);
            // 'skipped' (too-short/mechanism, legacy or native) leaves the denominator
            // rather than dragging coverage down.
            if (status is not ("ok" or "partial" or "unreadable")) continue;
            if (!DateTimeOffset.TryParse(row.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp) || stamp < since) continue;
            total++;
            if (status == "ok") covered++;
        }

        return (covered, total);
    }

    /// <summary>
    /// R35 (driver ruling, measured on the author's live state: 1,799 legacy 3.0.x stamps —
    /// no-turns 1203, ok 277, no-new-turns 219, retry 82, bos 18): sweep_stamps written by
    /// 3.0.x keep their own, different outcome vocabulary — normalise it to the R32
    /// ok/partial/unreadable/skipped contract before counting, rather than treating every
    /// unrecognised legacy string as an implicit 'skipped' (which used to read as an
    /// artificially high, sometimes exactly 100%, coverage number on an upgraded vault).
    /// The sweep lane implements the same table in SweepRun.cs; no migration rewrites the
    /// stored rows.
    /// </summary>
    private static string NormalizeStampOutcome(string outcome) => outcome switch
    {
        "ok" or "no-new-turns" or "bos" or "refused" => "ok",
        "no-turns" => "skipped",
        "skipped" => "skipped",
        "retry" or "parked" or "locked" => "partial",
        "unreadable" => "unreadable",
        _ => "partial"
    };

    // F5-1: flush outcomes of the last seven days only (never an all-time ratio). Stamps are
    // round-trip strings with offsets, so the window is applied after parsing, not in SQL.
    private static IReadOnlyList<string> RecentOutcomes(State? state, DateTimeOffset since) =>
        state is null
            ? []
            : [.. state.ReadNamedColumn("SELECT COALESCE(outcome, ''), ts FROM flush_log WHERE outcome <> 'summary'")
                .Where(row => DateTimeOffset.TryParse(row.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts) && ts >= since)
                .Select(row => row.Name)];

    // S7: a corrupt hub-config.json is a red row naming the file, not a silent 'genel'.
    private static IEnumerable<DoctorObservation> VaultSchemaObservations(DateTimeOffset now, string vault)
    {
        string catchAll;
        try
        {
            catchAll = new RootMap(vault).CatchAllHub;
        }
        catch (InvalidDataException error)
        {
            return [new DoctorObservation(new HealthItem("vault", HealthLevel.Error, "hub-config", "hub-config.json", error.Message), now)];
        }

        return Doctor.VaultSchema(vault, Corpus(vault), catchAll).Select(item => new DoctorObservation(item, now));
    }

    private static IEnumerable<DoctorObservation> KitObservations(DateTimeOffset now) =>
        KitObservations(now, DefaultKitRoot(), HomeRoot());

    internal static IEnumerable<DoctorObservation> KitObservations(DateTimeOffset now, string kitRoot, string homeRoot)
    {
        var kit = new Kit(kitRoot, homeRoot);
        if (kit.KitRootMissing)
            return [new DoctorObservation(new HealthItem("kit", HealthLevel.Info, "kit-yok", "kit", "kit yok"), now)];

        IReadOnlyList<KitRow> rows;
        try
        {
            rows = kit.Status();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return [new DoctorObservation(new HealthItem("kit", HealthLevel.Error, "kit-okunamadi", "kit", $"manifest okunamadı: {error.Message}"), now)];
        }

        return rows.Select(row => new DoctorObservation(
            new HealthItem("kit", KitLevel(row.State), row.State.Text(), row.Name, $"{row.Target} → {row.State.Text()}: {row.Detail}"), now));
    }

    private static HealthLevel KitLevel(KitState state) => state switch
    {
        KitState.Guncel => HealthLevel.Info,
        KitState.Bozuk => HealthLevel.Error,
        _ => HealthLevel.Warning
    };

    private static void Repair(string vault, OomSettings settings, State state)
    {
        Directory.CreateDirectory(Path.Combine(state.WorkDirectory, "logs"));
        state.SeedCursors(new SweepRun(vault, settings, MakeFlush(vault, settings, state), state).ReadAnchors());
        state.SweepRetention(Clock.Now);
        new RootMap(vault).Regenerate();
        ReportIndex(MakeRetrieve(vault, settings, settings.Retrieve.Top).Build());
    }

    private static IReadOnlyList<HealthItem> HookHealth(string vault) => new Doctor().ValidateHooks(
        ReadIfPresent(Path.Combine(UserProfileRoot(), ".claude", "settings.json")),
        ReadIfPresent(Path.Combine(vault, ".claude", "settings.json")));

    // Should-fix (review, applied): measured that Environment.GetFolderPath(UserProfile)
    // resolves via the Windows known-folder API and ignores a USERPROFILE env-var override
    // (a child process with USERPROFILE redirected to a synthetic profile still read this
    // machine's real ~/.claude/settings.json) — so every doctor invocation, including under a
    // process-boundary test harness, read real personal hook configuration with no isolation
    // seam. OOM_USERPROFILE lets a harness redirect the profile root the same way
    // OOM_LOCALAPPDATA already redirects state; unset, behaviour is unchanged.
    private static string UserProfileRoot() =>
        Environment.GetEnvironmentVariable("OOM_USERPROFILE") is { Length: > 0 } overridden
            ? overridden
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static long Rows(State state, string table)
    {
        try
        {
            return state.Scalar($"SELECT COUNT(*) FROM {table}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return 0;
        }
    }

    private static DoctorObservation Observe(DateTimeOffset now, string component, string code, string key, string detail) =>
        new(new HealthItem(component, HealthLevel.Info, code, key, detail), now);

    private static string Level(HealthLevel level) => level switch
    {
        HealthLevel.Error => "hata",
        HealthLevel.Warning => "uyarı",
        _ => "bilgi"
    };

    private static string Short(string value) => value.Length <= 14 ? value : value[..14];
}
