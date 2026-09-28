using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Contracts;

public sealed record DoctorObservation(HealthItem Item, DateTimeOffset ObservedAt);

public sealed record DoctorSnapshot(
    IReadOnlyList<DoctorObservation> Observations,
    double Coverage,
    double RejectionRate,
    int Pending,
    int Parked = 0,
    int QueueLength = 0,
    int Quarantine = 0,
    int InvalidFrontmatter = 0,
    int Refused = 0,
    MemoryFreshness? Freshness = null,
    int WindowTotal = 0,
    // Defect 1: parked DAILIES (daily_ingest.status='parked'), each rendered as
    // "<day> (<rejection reason>)" for the detail; ParkedSessions is the separate
    // flush-session concept (retry_queue attempts >= 5) that used to be mislabelled "parked".
    IReadOnlyList<string>? ParkedDailies = null,
    int ParkedSessions = 0);

public sealed class Doctor
{
    private static readonly Regex WikiLinkTarget = new(@"\[\[(?<target>[^\]\|]+)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly string[] RequiredHooks = ["SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact"];
    private readonly IClock clock;
    private readonly Func<DateTimeOffset, DoctorSnapshot> probe;
    private readonly Action repair;

    public Doctor(IClock? clock = null, Func<DateTimeOffset, DoctorSnapshot>? probe = null, Action? repair = null)
    {
        this.clock = clock ?? SystemClock.Instance;
        this.probe = probe ?? DefaultSnapshot;
        this.repair = repair ?? (() => { });
    }

    public static string CoverageText(double coverage) => double.IsNaN(coverage) ? "ölçülmedi" : coverage.ToString("P0", System.Globalization.CultureInfo.CurrentCulture);

    public DoctorResult Check(DateTimeOffset now)
    {
        var snapshot = probe(now);
        var items = snapshot.Observations.Select(observation =>
            observation.Item with { Stale = now - observation.ObservedAt > TimeSpan.FromHours(24) }).ToList();
        var measuredAt = snapshot.Freshness?.Coverage?.MeasuredAt;
        items.Add(double.IsNaN(snapshot.Coverage)
            ? Item("doctor", HealthLevel.Warning, "coverage", "7d", "Son 7 gün kapsama ölçülmedi: oom sweep henüz koşmadı")
            : Item("doctor", snapshot.Coverage >= .95 ? HealthLevel.Info : snapshot.WindowTotal < 5 ? HealthLevel.Warning : HealthLevel.Error, "coverage", "7d",
                $"Son 7 gün kapsama: {snapshot.Coverage:P1}{(measuredAt is { } at ? $" (ölçüm: {Age(now - at)} önce)" : string.Empty)}"));
        AddMetric(items, "rejection-rate", snapshot.RejectionRate <= .03, $"Son 7 gün ret: {snapshot.RejectionRate:P1}");
        AddCount(items, "flush", "refused", snapshot.Refused, HealthLevel.Warning, $"Son 7 gün model reddi (refused): {snapshot.Refused}");
        if (snapshot.Freshness is { } freshness)
        {
            items.Add(freshness.LastSweep is { } sweep
                ? Item("sweep", freshness.SweepOverdue(now) ? HealthLevel.Error : HealthLevel.Info, "last-sweep", "sweep",
                    $"son tarama: {Math.Max(0, (int)(now - sweep).TotalHours)} saat önce")
                : Item("sweep", HealthLevel.Warning, "last-sweep", "sweep", "son tarama: hiç — oom sweep ölçmedi"));
            items.Add(Item("compile", HealthLevel.Info, "last-compile", "daily_ingest",
                $"son derleme: {(freshness.LastCompile is { } compiled ? compiled.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "hiç")}"));
        }
        items.Add(Item("daily", snapshot.Pending > MemoryFreshness.PendingRedCount ? HealthLevel.Error : snapshot.Pending > 0 ? HealthLevel.Warning : HealthLevel.Info,
            "pending", snapshot.Pending.ToString(), $"bekleyen daily: {snapshot.Pending}"));
        // Defect 1: parked dailies were counted under "Park edilmiş daily" only by accident —
        // the number came from retry_queue (flush sessions), so a real parked day was invisible
        // and a stuck flush session inflated it. Parked dailies now carry their day names and,
        // when stored, the rejection reasons; flush sessions get their own row.
        AddCount(items, "daily", "parked", snapshot.Parked, HealthLevel.Error,
            $"Park edilmiş daily: {snapshot.Parked}{(ParkedDetail(snapshot.ParkedDailies) is { } parked ? $" ({parked})" : string.Empty)}");
        // M4 (Claude review): a session parked at attempts>=5 is never retried again
        // (Flush.cs never re-queues a row once it is parked) — that is a dead session, not
        // a degradation to merely warn about.
        AddCount(items, "queue", "parked-session", snapshot.ParkedSessions, HealthLevel.Error,
            $"Park edilmiş flush oturumu: {snapshot.ParkedSessions} (5+ deneme)");
        AddCount(items, "queue", "queue-length", snapshot.QueueLength, HealthLevel.Warning, $"Kuyruk uzunluğu: {snapshot.QueueLength}");
        AddCount(items, "quarantine", "quarantine", snapshot.Quarantine, HealthLevel.Warning, $"Karantina: {snapshot.Quarantine}");
        AddCount(items, "notes", "invalid-frontmatter", snapshot.InvalidFrontmatter, HealthLevel.Error, $"Geçersiz frontmatter: {snapshot.InvalidFrontmatter}");
        return new DoctorResult(items, snapshot.Coverage, snapshot.RejectionRate, snapshot.Pending, ExitCode(items));
    }

    /// <summary>F5-4: any red row makes the doctor exit 1; the process and `--json` report the same code.</summary>
    private static int ExitCode(IEnumerable<HealthItem> items) => items.Any(item => item.Level is HealthLevel.Error) ? 1 : 0;

    private static string Age(TimeSpan age) => age.TotalHours < 24
        ? $"{Math.Max(0, (int)age.TotalHours)} saat"
        : $"{(int)age.TotalDays} gün";

    /// <summary>Defect 1: the parked day names (and their stored rejection reasons), capped so a
    /// long backlog cannot turn one row into a wall of text.</summary>
    private const int ParkedNamesShown = 5;

    private static string? ParkedDetail(IReadOnlyList<string>? dailies)
    {
        if (dailies is not { Count: > 0 })
            return null;

        var shown = string.Join(", ", dailies.Take(ParkedNamesShown));
        return dailies.Count > ParkedNamesShown ? $"{shown} … (+{dailies.Count - ParkedNamesShown})" : shown;
    }

    public static IReadOnlyList<HealthItem> VaultSchema(string vault, IReadOnlyList<Note> corpus, string catchAll)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(corpus);

        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string owner, string text)
        {
            foreach (Match match in WikiLinkTarget.Matches(text))
            {
                var target = match.Groups["target"].Value.Trim();
                var slug = target.Split('/')[^1].Trim();
                if (slug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    slug = slug[..^3];
                if (slug.Length > 0 && !string.Equals(slug, owner, StringComparison.OrdinalIgnoreCase))
                    linked.Add(slug);
            }
        }

        foreach (var note in corpus)
            Collect(Path.GetFileNameWithoutExtension(note.Name), note.Body);

        var hubDirectory = Path.Combine(vault, "knowledge", "hubs");
        if (Directory.Exists(hubDirectory))
            foreach (var file in Directory.EnumerateFiles(hubDirectory, "*.md", SearchOption.TopDirectoryOnly))
                try
                {
                    Collect(string.Empty, File.ReadAllText(file));
                }
                catch (IOException)
                {
                }

        var orphans = corpus.Count(note => !linked.Contains(Path.GetFileNameWithoutExtension(note.Name)));
        var hubless = corpus.Count(note => string.IsNullOrWhiteSpace(note.Hub) || string.Equals(note.Hub, catchAll, StringComparison.Ordinal));
        var schemaless = corpus.Count(note => string.IsNullOrWhiteSpace(note.Type) || string.IsNullOrWhiteSpace(note.Hub));

        var dailyDirectory = Path.Combine(vault, "daily");
        var dailies = 0;
        if (Directory.Exists(dailyDirectory))
            foreach (var file in Directory.EnumerateFiles(dailyDirectory, "*.md", SearchOption.TopDirectoryOnly))
                try
                {
                    if (File.ReadLines(file).FirstOrDefault()?.TrimStart('\uFEFF').Trim() != "---")
                        dailies++;
                }
                catch (IOException)
                {
                }

        return
        [
            Item("vault", orphans == 0 ? HealthLevel.Info : HealthLevel.Warning, "yetim-kavram", orphans.ToString(),
                $"Hiçbir kavram ya da hub'dan bağ almayan kavram: {orphans}/{corpus.Count}"),
            Item("vault", hubless == 0 ? HealthLevel.Info : HealthLevel.Warning, "hub-siz", hubless.ToString(),
                $"'hub' alanı eksik ya da '{catchAll}' olan kavram: {hubless}/{corpus.Count}"),
            Item("vault", dailies + schemaless == 0 ? HealthLevel.Info : HealthLevel.Warning, "sema-disi", (dailies + schemaless).ToString(),
                $"Şema dışı: frontmatter'sız {dailies} daily, 'type'/'hub' eksik {schemaless} kavram")
        ];
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

        // Defect 5: a settings.json that exists but does not parse is a red row naming the
        // file (previously the whole scope was skipped in silence). Defect 3: a scope with no
        // settings file at all is a warning, not "nothing to report".
        var user = ParseSettings(userSettings, UserSettingsKey, items);
        var project = ParseSettings(projectSettings, ProjectSettingsKey, items);
        try
        {
            // M5 minor (Claude review): a command merely containing "oom" as a SUBSTRING —
            // e.g. `node C:/tools/zoom-status.js` ("zoom" contains "oom") — is not an oom
            // hook. Only a command that actually names an oom.exe path (ExecutableOf) is a
            // candidate, both for duplicate-hook grouping and for the hook-path check below.
            var all = HookCommands(user).Concat(HookCommands(project))
                .Where(command => ExecutableOf(command) is not null).ToArray();

            if (userSettings.Length > 0 && userSettings == projectSettings)
                items.Add(Item("hooks", HealthLevel.Error, "duplicate-hook", "settings", "Aynı hook kullanıcı ve proje ayarında iki kez kayıtlı."));
            foreach (var duplicate in all.GroupBy(command => command, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
                if (!items.Any(item => item.Code == "duplicate-hook" && item.Key == duplicate.Key))
                    items.Add(Item("hooks", HealthLevel.Error, "duplicate-hook", duplicate.Key, "Hook kaydı birden çok kez bulundu."));

            // Defect 4: `oom install` writes <vault>/.claude/settings.json, so a hook present in
            // EITHER scope counts; checking only the user scope reported all four as missing
            // right after a successful install.
            foreach (var hook in RequiredHooks.Where(hook => !HasHook(user, hook) && !HasHook(project, hook)))
                items.Add(Item("hooks", HealthLevel.Warning, "missing-hook", hook, $"{hook} hook kaydı eksik."));

            // Minor (Claude review): a scope with no settings.json at all is only a warning
            // when the OTHER scope, alone, already registers all four required hooks — a
            // hooks-complete user profile (the common real setup: hooks installed once at
            // user scope, no per-vault .claude/settings.json) must not carry a permanent
            // "settings.json (proje) yok" (or the mirror, at user scope) warning.
            var userComplete = RequiredHooks.All(hook => HasHook(user, hook));
            var projectComplete = RequiredHooks.All(hook => HasHook(project, hook));
            if (userComplete && !projectComplete)
                items.RemoveAll(item => item.Code == "settings-missing" && item.Key == ProjectSettingsKey);
            if (projectComplete && !userComplete)
                items.RemoveAll(item => item.Code == "settings-missing" && item.Key == UserSettingsKey);

            // Defect 3: an oom.exe that is named but not on disk is an ERROR row too — a hook
            // that can never fire is worse than one that is merely unregistered. `all` is
            // already filtered to ExecutableOf(command) is not null, so the candidate below
            // is never null.
            foreach (var command in all)
            {
                var candidate = ExecutableOf(command)!;
                if (!Path.IsPathFullyQualified(candidate))
                    items.Add(Item("hooks", HealthLevel.Error, "hook-path", command, "Hook komutu mutlak oom.exe yolu kullanmıyor."));
                else if (!File.Exists(candidate))
                    items.Add(Item("hooks", HealthLevel.Error, "hook-path", command, $"Hook komutundaki oom.exe yok: {candidate}"));
            }
        }
        finally
        {
            user?.Dispose();
            project?.Dispose();
        }

        return items;
    }

    private const string UserSettingsKey = "settings.json (kullanıcı)";
    private const string ProjectSettingsKey = "settings.json (proje)";

    /// <summary>Defect 5/3: an unreadable (non-empty but unparsable) scope is an error row; an
    /// absent one is a warning row. Returns the document, or null when there is nothing usable.</summary>
    private static JsonDocument? ParseSettings(string json, string key, ICollection<HealthItem> items)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            items.Add(Item("hooks", HealthLevel.Warning, "settings-missing", key, "settings.json yok — hook kaydı bulunamadı."));
            return null;
        }

        if (TryJson(json, out var document))
            return document;

        items.Add(Item("hooks", HealthLevel.Error, "settings-unreadable", key, "settings.json okunamadı (geçersiz JSON) — oom doctor hook'ları buradan okuyamaz."));
        return null;
    }

    // M5 (Claude review): a hook event counts as REGISTERED only when a command found
    // under that named property is itself an oom command (ExecutableOf != null) — the old
    // ContainsProperty check matched any property literally named e.g. "SessionStart",
    // even when every command under it belonged to some other tool (`echo hi`), so four
    // foreign hooks silently satisfied all four required oom hooks.
    private static bool HasHook(JsonDocument? document, string hook) =>
        document is not null && EventCommands(document.RootElement, hook).Any(command => ExecutableOf(command) is not null);

    public VerifyResult VerifyIndex(IReadOnlyList<string> corpus, IReadOnlyList<string> index) =>
        IndexVerifier.Compare(corpus, index);

    public VerifyResult VerifyIndex(Retrieve retrieve)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        return retrieve.VerifyIndex();
    }

    public HealthItem IndexHealth(Retrieve retrieve)
    {
        var verdict = VerifyIndex(retrieve);
        return verdict.ExitCode == 0
            ? Item("state", HealthLevel.Info, "index-sound", "notes_fts", "Arama indeksi korpusla eşleşiyor.")
            : Item("state", HealthLevel.Error, "index-mismatch", "notes_fts",
                $"Arama indeksi korpusla eşleşmiyor: {verdict.Missing.Count} eksik, {verdict.Extra.Count} fazla — oom sweep.");
    }

    public string ToJson(DoctorResult result) => JsonSerializer.Serialize(new
    {
        schema_version = 1,
        coverage = double.IsNaN(result.Coverage) ? (double?)null : result.Coverage,
        rejection_rate = result.RejectionRate,
        pending = result.Pending,
        exit_code = result.ExitCode,
        // Should-fix (review, applied): R5 requires machine identifiers — JSON keys among
        // them — to be English lowercase. The item fields were emitted with their C# property
        // casing (Component/Code/Key/Detail); only `level` and `stale` were already lowered.
        items = result.Items.Select(item => new { component = item.Component, level = item.Level.ToString().ToLowerInvariant(), code = item.Code, key = item.Key, detail = item.Detail, stale = item.Stale })
    });

    private DoctorSnapshot DefaultSnapshot(DateTimeOffset now) => new(
        [
            Observe(now, "hooks", "hooks-ok", "4", "Dört hook kaydı geçerli."),
            Observe(now, "state", "fts5-ok", "notes_fts", "FTS5 kullanılabilir."),
            new(CheckClaudeReachability(Environment.GetEnvironmentVariable("PATH") ?? string.Empty), now),
            Observe(now, "compile", "compile-current", "last", "Son derleme kaydı okunabildi."),
            .. HealthLedger.Read()
        ], 1.0, 0.0, 1);

    public HealthItem CheckClaudeReachability(string pathValue)
    {
        var resolved = new Runner().ResolveExecutable("claude", pathValue);
        return Path.IsPathRooted(resolved)
            ? Item("runner", HealthLevel.Info, "claude-reachable", resolved, $"Claude CLI erişilebilir: {resolved}")
            : Item("runner", HealthLevel.Warning, "claude-reachable", "claude", "Claude CLI PATH içinde bulunamadı.");
    }

    private static DoctorObservation Observe(DateTimeOffset now, string component, string code, string key, string detail) =>
        new(Item(component, HealthLevel.Info, code, key, detail), now);
    private static HealthItem Item(string component, HealthLevel level, string code, string key, string detail) => new(component, level, code, key, detail);
    private static void AddMetric(List<HealthItem> items, string code, bool healthy, string detail) =>
        items.Add(Item("doctor", healthy ? HealthLevel.Info : HealthLevel.Error, code, "7d", detail));
    private static void AddCount(List<HealthItem> items, string component, string code, int count, HealthLevel nonZero, string detail) =>
        items.Add(Item(component, count == 0 ? HealthLevel.Info : nonZero, code, count.ToString(), detail));

    private static IReadOnlyList<string> HookCommands(JsonDocument? document)
    {
        if (document is null) return [];
        var commands = new List<string>();
        Visit(document.RootElement, commands);
        return commands;
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

    /// <summary>M5: every "command" string found under a property named <paramref name="name"/>
    /// (case-insensitive), searched anywhere in the tree — mirrors the old ContainsProperty's
    /// search reach, but returns the commands themselves instead of just proving the property
    /// name exists, so the caller can judge whether any of them is actually an oom command.</summary>
    private static IEnumerable<string> EventCommands(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var commands = new List<string>();
                    Visit(property.Value, commands);
                    foreach (var command in commands) yield return command;
                }

                foreach (var command in EventCommands(property.Value, name)) yield return command;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var command in EventCommands(item, name)) yield return command;
    }

    /// <summary>Defect 3: the oom.exe path a hook command names, or null when it names none.
    /// The command looks like <c>"&lt;exe&gt;" hook --event X</c>, so the executable is
    /// everything up to and including the oom.exe token.</summary>
    private static string? ExecutableOf(string command)
    {
        var marker = command.IndexOf("oom.exe", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        return command[..(marker + "oom.exe".Length)].Trim().Trim('"');
    }
}
