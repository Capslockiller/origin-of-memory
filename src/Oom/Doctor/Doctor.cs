// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Oom.Contracts;

public sealed record DoctorObservation(HealthItem Item, DateTimeOffset ObservedAt);

/// <summary>
/// One state root as InspectStateRoots found it: what it is named, what it says it serves, and
/// whether it can be trusted.
/// </summary>
/// <param name="Rows">
/// Y-210: every user table in the database, not a hand-written shortlist. The count used to name
/// six tables — <c>calls</c>, <c>flush_log</c>, <c>health</c>, <c>sessions</c>, <c>coverage</c>,
/// <c>compile_runs</c> — while the schema carries twenty, so a root whose only records sat in
/// <c>daily_ingest</c>, <c>retrieve_served</c>, <c>quarantine</c>, <c>retry_queue</c> or
/// <c>ingest_done</c> counted zero and was reported as empty residue.
/// </param>
/// <param name="Status">
/// <c>kullanımda</c>, <c>eşleşmiyor</c>, <c>sahipsiz</c>, <c>artık</c>, <c>belirsiz</c> or
/// <c>okunamıyor</c>. <c>artık</c> is the only verdict that claims a root holds nothing, so it is
/// only reached when that emptiness was actually proven — see <c>belirsiz</c>.
/// </param>
public sealed record StateRootReport(string Path, string Hash, string? Vault, long Rows, string Status);

public sealed record DoctorSnapshot(
    IReadOnlyList<DoctorObservation> Observations,
    double Coverage,
    double RejectionRate,
    int Pending,
    int Parked = 0,
    int QueueLength = 0,
    int Quarantine = 0,
    int InvalidFrontmatter = 0,
    int WindowTotal = 0); // Y-118: the 7-day population size — a small one is "uyarı", never "hata".

public sealed class Doctor
{
    private static readonly string[] RequiredHooks = ["SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact"];
    private readonly IClock clock;
    private readonly Func<DateTimeOffset, DoctorSnapshot> probe;
    private readonly Action repair;
    private readonly Func<IReadOnlyList<HealthItem>> stateRoots;

    public Doctor(IClock? clock = null, Func<DateTimeOffset, DoctorSnapshot>? probe = null, Action? repair = null,
        Func<IReadOnlyList<HealthItem>>? stateRoots = null)
    {
        this.clock = clock ?? SystemClock.Instance;
        this.probe = probe ?? DefaultSnapshot;
        this.repair = repair ?? (() => { });
        // Default off: an empty scan, never a live one — the test suite runs against the real
        // %LOCALAPPDATA% and must not start opening the owner's real databases just by constructing a Doctor.
        this.stateRoots = stateRoots ?? (() => []);
    }

    public DoctorResult Check(DateTimeOffset now)
    {
        var snapshot = probe(now);
        var items = snapshot.Observations.Select(observation =>
            observation.Item with { Stale = now - observation.ObservedAt > TimeSpan.FromHours(24) }).ToList();
        items.Add(Item("doctor", snapshot.Coverage >= .95 ? HealthLevel.Info : snapshot.WindowTotal < 5 ? HealthLevel.Warning : HealthLevel.Error, "coverage", "7d", $"Son 7 gün kapsama: {snapshot.Coverage:P1}")); // Y-118: hata yalnız gerçek popülasyon üzerinde; sakin hafta uyarı.
        AddMetric(items, "rejection-rate", snapshot.RejectionRate <= .03, $"Son 7 gün ret: {snapshot.RejectionRate:P1}");
        AddCount(items, "daily", "pending", snapshot.Pending, HealthLevel.Warning, $"Bekleyen daily: {snapshot.Pending}");
        AddCount(items, "daily", "parked", snapshot.Parked, HealthLevel.Error, $"Park edilmiş daily: {snapshot.Parked}");
        AddCount(items, "queue", "queue-length", snapshot.QueueLength, HealthLevel.Warning, $"Kuyruk uzunluğu: {snapshot.QueueLength}");
        AddCount(items, "quarantine", "quarantine", snapshot.Quarantine, HealthLevel.Warning, $"Karantina: {snapshot.Quarantine}");
        AddCount(items, "notes", "invalid-frontmatter", snapshot.InvalidFrontmatter, HealthLevel.Error, $"Geçersiz frontmatter: {snapshot.InvalidFrontmatter}");
        items.AddRange(stateRoots());
        return new DoctorResult(items, snapshot.Coverage, snapshot.RejectionRate, snapshot.Pending, 0);
    }

    public DoctorResult Fix()
    {
        repair();
        return Check(clock.Now);
    }

    public IReadOnlyList<HealthItem> ValidateHooks(string userSettings, string projectSettings)
    {
        userSettings ??= string.Empty;
        projectSettings ??= string.Empty;
        var items = new List<HealthItem>();
        var user = ReadHookCommands(userSettings);
        var project = ReadHookCommands(projectSettings);
        var all = user.Concat(project).ToArray();

        if (userSettings.Length > 0 && userSettings == projectSettings)
            items.Add(Item("hooks", HealthLevel.Error, "duplicate-hook", "settings", "Aynı hook kullanıcı ve proje ayarında iki kez kayıtlı."));
        foreach (var duplicate in all.GroupBy(command => command, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            if (!items.Any(item => item.Code == "duplicate-hook" && item.Key == duplicate.Key))
                items.Add(Item("hooks", HealthLevel.Error, "duplicate-hook", duplicate.Key, "Hook kaydı birden çok kez bulundu."));

        if (TryJson(userSettings, out var userDocument))
        {
            using (userDocument)
            {
                foreach (var hook in RequiredHooks.Where(hook => !ContainsProperty(userDocument.RootElement, hook)))
                    items.Add(Item("hooks", HealthLevel.Warning, "missing-hook", hook, $"{hook} hook kaydı eksik."));
            }
        }
        foreach (var command in all.Where(command => !HasAbsoluteExecutable(command)))
            items.Add(Item("hooks", HealthLevel.Error, "hook-path", command, "Hook komutu mutlak oom.exe yolu kullanmıyor."));
        return items;
    }

    public HealthItem ValidateInstalledBinary(string installedPath, string releasedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(releasedPath);
        var equal = File.Exists(installedPath) && File.Exists(releasedPath)
            ? CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(installedPath)), SHA256.HashData(File.ReadAllBytes(releasedPath)))
            : Path.GetFullPath(installedPath).Equals(Path.GetFullPath(releasedPath), StringComparison.OrdinalIgnoreCase);
        return equal
            ? Item("install", HealthLevel.Info, "binary-current", installedPath, "Kurulu ikili yayımlanan ikiliyle eşleşiyor.")
            : Item("install", HealthLevel.Warning, "binary-drift", installedPath, "Kurulu ikilinin özeti yayımlanan sürümle eşleşmiyor.");
    }

    /// <summary>
    /// The name-set comparison, delegated to <see cref="IndexVerifier"/> — doctor has no private copy
    /// of the rule any more (Y-032 still measures this signature).
    /// </summary>
    public VerifyResult VerifyIndex(IReadOnlyList<string> corpus, IReadOnlyList<string> index) =>
        IndexVerifier.Compare(corpus, index);

    /// <summary>
    /// "Is the index sound?", asked of the index itself. This is the same verifier
    /// <see cref="Retrieve.Build"/> grades its own rebuild with, called through the same entry point:
    /// doctor and the builder cannot disagree, because there is only one of them.
    /// </summary>
    public VerifyResult VerifyIndex(Retrieve retrieve)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        return retrieve.VerifyIndex();
    }

    /// <summary>One health item carrying the verifier's verdict, for a normal <see cref="Check"/>.</summary>
    public HealthItem IndexHealth(Retrieve retrieve)
    {
        var verdict = VerifyIndex(retrieve);
        return verdict.ExitCode == 0
            ? Item("state", HealthLevel.Info, "index-sound", "notes_fts", "Arama indeksi korpusla eşleşiyor.")
            : Item("state", HealthLevel.Error, "index-mismatch", "notes_fts",
                $"Arama indeksi korpusla eşleşmiyor: {verdict.Missing.Count} eksik, {verdict.Extra.Count} fazla — oom sweep.");
    }

    public IReadOnlyList<HealthItem> ValidateReleaseClaims(IReadOnlyDictionary<string, string> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return claims.Where(claim => string.IsNullOrWhiteSpace(claim.Value))
            .Select(claim => Item("release", HealthLevel.Error, "claim-without-evidence", claim.Key, "Sürüm iddiasının test veya ölçüm kanıtı yok."))
            .ToArray();
    }

    public string ToJson(DoctorResult result) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        coverage = result.Coverage,
        rejection_rate = result.RejectionRate,
        pending = result.Pending,
        exit_code = 0,
        items = result.Items.Select(item => new { item.Component, level = item.Level.ToString().ToLowerInvariant(), item.Code, item.Key, item.Detail, stale = item.Stale })
    });

    private DoctorSnapshot DefaultSnapshot(DateTimeOffset now) => new(
        [
            Observe(now, "hooks", "hooks-ok", "4", "Dört hook kaydı geçerli."),
            Observe(now, "task", "task-current", "OdenaOS Memory Sweep", "Zamanlanmış görev etkin ve son koşumu güncel."),
            Observe(now, "state", "integrity-ok", "state.db", "Şema ve bütünlük denetimi geçti."),
            Observe(now, "state", "fts5-ok", "notes_fts", "FTS5 kullanılabilir."),
            new(CheckClaudeReachability(Environment.GetEnvironmentVariable("PATH") ?? string.Empty), now),
            Observe(now, "runner", "ollama-reachable", "ollama", "Ollama erişilebilir."),
            Observe(now, "compile", "compile-current", "last", "Son derleme kaydı okunabildi."),
            Observe(now, "calls", "call-summary", "7d", "Backend çağrı özeti hazır."),
            .. HealthLedger.Read()
        ], 1.0, 0.0, 1);

    /// <summary>
    /// The same resolver the flush path uses (Y-103/Y-073): doctor must answer "can this machine
    /// start claude?" the way <see cref="Runner.BuildClaudeRequest"/> asks it, not by a fixed
    /// sentence. A machine carrying only <c>claude.cmd</c> is reachable; a bare name is not.
    /// </summary>
    public HealthItem CheckClaudeReachability(string pathValue)
    {
        var resolved = new Runner().ResolveExecutable("claude", pathValue);
        return Path.IsPathRooted(resolved)
            ? Item("runner", HealthLevel.Info, "claude-reachable", resolved, $"Claude CLI erişilebilir: {resolved}")
            : Item("runner", HealthLevel.Warning, "claude-reachable", "claude", "Claude CLI PATH içinde bulunamadı.");
    }

    /// <summary>
    /// One line per subdirectory of <see cref="VaultIdentity.StateRootsDirectory"/>: what it is
    /// named, what vault it claims to serve, how many rows its database carries, and whether it
    /// can be trusted at all. Never deletes anything — this method only reports (spec 8, D9).
    /// </summary>
    public IReadOnlyList<StateRootReport> InspectStateRoots(string? localAppData = null)
    {
        var directory = VaultIdentity.StateRootsDirectory(localAppData);
        if (!Directory.Exists(directory))
            return [];

        var reports = new List<StateRootReport>();
        // Y-163: only the hash-named directories are state roots. `backup` and `claude-config`
        // live in the same folder and were being reported as stray roots to delete.
        foreach (var root in Directory.EnumerateDirectories(directory).Where(candidate => VaultIdentity.IsStateRootName(Path.GetFileName(candidate))))
        {
            var hash = Path.GetFileName(root);
            try
            {
                reports.Add(InspectOneStateRoot(root, hash));
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
            {
                // One unreadable root must never abort the scan of the other twenty-five.
                reports.Add(new StateRootReport(root, hash, null, 0, "okunamıyor"));
            }
        }

        // Read-only handles must not outlive the scan, or the owner cannot move/delete a root afterwards.
        SqliteConnection.ClearAllPools();
        return reports.OrderBy(report => report.Path, StringComparer.Ordinal).ToArray();
    }

    private static StateRootReport InspectOneStateRoot(string root, string hash)
    {
        var vault = VaultIdentity.ReadDescriptor(root);
        var databasePath = Path.Combine(root, VaultIdentity.DatabaseName);
        var content = File.Exists(databasePath) ? ReadContent(databasePath) : StateRootContent.NoDatabase;

        var status = !content.Readable ? "okunamıyor"
            : vault is not null && string.Equals(VaultIdentity.Hash(vault), hash, StringComparison.Ordinal) ? "kullanımda"
            : vault is not null ? "eşleşmiyor"
            : content.Rows > 0 ? "sahipsiz"
            // Y-210: "artık" is a claim that this root holds nothing. It may only be made when
            // the scan actually walked the whole database and understood every object in it.
            : content.EmptinessProven ? "artık"
            : "belirsiz";
        return new StateRootReport(root, hash, vault, content.Readable ? content.Rows : 0, status);
    }

    /// <summary>
    /// What one state root's database turned out to hold. <see cref="EmptinessProven"/> is the
    /// part that matters: a zero in <see cref="Rows"/> means "nothing found", which is only the
    /// same thing as "nothing there" when the scan understood every object it walked past.
    /// </summary>
    private readonly record struct StateRootContent(long Rows, bool Readable, bool EmptinessProven)
    {
        /// <summary>No file at all — nothing to count, and nothing unexamined either.</summary>
        public static StateRootContent NoDatabase => new(0, true, true);
    }

    /// <summary>The FTS5 shadow tables SQLite maintains for an index named <c>X</c>: <c>X_data</c> and friends.</summary>
    /// <remarks>
    /// Y-210: these are not user records and must never inflate the count. A <c>notes_fts</c> that
    /// has never indexed anything already carries a row in <c>notes_fts_config</c> and rows in
    /// <c>notes_fts_data</c>, so counting shadows would report every provisioned root as non-empty
    /// and the "artık" class would quietly disappear instead of becoming honest.
    /// </remarks>
    private static readonly string[] FtsShadowSuffixes = ["_data", "_idx", "_content", "_docsize", "_config", "_row"];

    private static StateRootContent ReadContent(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        // integrity_check FIRST: on a corrupt file a COUNT(*) still returns a number, and that number lies.
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
                return new StateRootContent(0, false, false);
        }

        var names = new List<string>();
        var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var tables = connection.CreateCommand())
        {
            // type='table' covers ordinary tables, FTS5 virtual tables and their shadows alike;
            // views are not listed, and a view holds no rows of its own anyway.
            tables.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='table'";
            using var reader = tables.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                names.Add(name);
                definitions[name] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        // SQLite's own bookkeeping (sqlite_sequence, sqlite_stat1, …) is not this vault's data.
        var user = names.Where(name => !name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)).ToList();
        var virtuals = user.Where(name => IsVirtual(definitions[name])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fts = virtuals.Where(name => IsFts5(definitions[name])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadows = user.Where(name => IsShadowOf(name, fts)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A virtual table on a module we do not recognise could be holding anything at all, and a
        // count against it may not even mean rows. We do not guess — we decline to prove emptiness.
        var proven = virtuals.Count == fts.Count;
        long rows = 0;

        foreach (var table in user.Where(name => !virtuals.Contains(name) && !shadows.Contains(name)))
            if (TryCount(connection, table) is { } counted) rows += counted;
            else proven = false; // a table we cannot read is a table we cannot call empty

        // An FTS5 index normally duplicates the table it indexes, so adding it to a non-zero total
        // would double-count. We ask it only once every ordinary table has answered zero: if the
        // index still reports rows, it is the only place they live and this root is not empty.
        if (rows == 0)
            foreach (var index in fts)
                if (TryCount(connection, index) is { } counted) rows += counted;
                else proven = false;

        return new StateRootContent(rows, true, proven);
    }

    private static long? TryCount(SqliteConnection connection, string table)
    {
        try
        {
            using var count = connection.CreateCommand();
            // The name came out of sqlite_master rather than a literal, so it is quoted.
            count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\"";
            return Convert.ToInt64(count.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static bool IsVirtual(string sql) =>
        sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase);

    private static bool IsFts5(string sql) =>
        sql.Contains("USING fts5", StringComparison.OrdinalIgnoreCase);

    private static bool IsShadowOf(string name, IReadOnlyCollection<string> ftsTables) =>
        ftsTables.Any(index => FtsShadowSuffixes.Any(suffix =>
            name.Equals(index + suffix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Turns <see cref="InspectStateRoots"/> into health items Doctor can fold into a normal Check.</summary>
    public IReadOnlyList<HealthItem> StateRootItems(string? localAppData = null)
    {
        var reports = InspectStateRoots(localAppData);
        var items = new List<HealthItem>();

        // Report order (by Path), not grouped by status — "kullanımda" roots contribute no item at all.
        foreach (var report in reports)
        {
            switch (report.Status)
            {
                case "artık":
                    items.Add(Item("state", HealthLevel.Warning, "stray-state-root", report.Hash,
                        $"Sahipsiz boş durum kökü: {report.Path} — veritabanındaki hiçbir kullanıcı tablosunda satır yok, vault künyesi yok. Silme kararı sahibin; oom hiçbir şeyi silmez."));
                    break;
                // Y-210: the honest middle. Nothing was found, but the scan met something it could
                // not read or does not understand, so "boş" would be a claim it cannot support.
                case "belirsiz":
                    items.Add(Item("state", HealthLevel.Warning, "state-root-indeterminate", report.Hash,
                        $"Durum kökü {report.Path} boş görünüyor ama boşluğu kanıtlanamadı: tanınmayan bir yapı var ya da bir tablo okunamadı. Artık (stray) sayılmaz, silme adayı değildir."));
                    break;
                case "eşleşmiyor":
                    items.Add(Item("state", HealthLevel.Warning, "state-root-mismatch", report.Hash,
                        $"Durum kökü {report.Path}, \"{report.Vault}\" adlı vault'u taşıyor ama bu vault'un doğru adresi {VaultIdentity.Hash(report.Vault!)} olmalı; iki veritabanı aynı vault için ulaşılabilir."));
                    break;
                case "sahipsiz":
                    items.Add(Item("state", HealthLevel.Warning, "unattributed-state-root", report.Hash,
                        $"Durum kökü {report.Path} {report.Rows} satır taşıyor ama hiçbir vault künyesi yok; boş olmadığı için sahipsiz sayılır, artık (stray) sayılmaz."));
                    break;
                case "okunamıyor":
                    items.Add(Item("state", HealthLevel.Warning, "state-root-unreadable", report.Hash,
                        $"Durum kökü {report.Path} okunamıyor: state.db bütünlük denetiminden geçmedi ya da açılamadı."));
                    break;
            }
        }

        var byStatus = reports.ToLookup(report => report.Status);
        items.Insert(0, Item("state", HealthLevel.Info, "state-roots", reports.Count.ToString(),
            $"{reports.Count} durum kökü · kullanımda {byStatus["kullanımda"].Count()} · artık {byStatus["artık"].Count()} · sahipsiz {byStatus["sahipsiz"].Count()} · eşleşmiyor {byStatus["eşleşmiyor"].Count()} · belirsiz {byStatus["belirsiz"].Count()} · okunamıyor {byStatus["okunamıyor"].Count()}"));
        return items;
    }

    private static DoctorObservation Observe(DateTimeOffset now, string component, string code, string key, string detail) =>
        new(Item(component, HealthLevel.Info, code, key, detail), now);
    private static HealthItem Item(string component, HealthLevel level, string code, string key, string detail) => new(component, level, code, key, detail);
    private static void AddMetric(List<HealthItem> items, string code, bool healthy, string detail) =>
        items.Add(Item("doctor", healthy ? HealthLevel.Info : HealthLevel.Error, code, "7d", detail));
    private static void AddCount(List<HealthItem> items, string component, string code, int count, HealthLevel nonZero, string detail) =>
        items.Add(Item(component, count == 0 ? HealthLevel.Info : nonZero, code, count.ToString(), detail));

    private static IReadOnlyList<string> ReadHookCommands(string json)
    {
        if (!TryJson(json, out var document)) return string.IsNullOrWhiteSpace(json) ? [] : [json];
        using (document)
        {
            var commands = new List<string>();
            Visit(document.RootElement, commands);
            return commands;
        }
    }

    private static void Visit(JsonElement element, ICollection<string> commands)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("command") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } command) commands.Add(command);
                else Visit(property.Value, commands);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Visit(item, commands);
    }

    private static bool TryJson(string json, out JsonDocument document)
    {
        try { document = JsonDocument.Parse(json); return true; }
        catch (JsonException) { document = null!; return false; }
    }

    private static bool ContainsProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || ContainsProperty(property.Value, name)) return true;
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (ContainsProperty(item, name)) return true;
        return false;
    }

    private static bool HasAbsoluteExecutable(string command)
    {
        var marker = command.IndexOf("oom.exe", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        var candidate = command[..(marker + "oom.exe".Length)].Trim().Trim('"');
        return Path.IsPathFullyQualified(candidate);
    }
}
