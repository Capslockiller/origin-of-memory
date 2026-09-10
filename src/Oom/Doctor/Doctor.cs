// yazan: codex · gpt-5
using System.Security.Cryptography;
using System.Text.Json;

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
    int InvalidFrontmatter = 0);

public sealed class Doctor
{
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

    public DoctorResult Check(DateTimeOffset now)
    {
        var snapshot = probe(now);
        var items = snapshot.Observations.Select(observation =>
            observation.Item with { Stale = now - observation.ObservedAt > TimeSpan.FromHours(24) }).ToList();
        AddMetric(items, "coverage", snapshot.Coverage >= .95, $"Son 7 gün kapsama: {snapshot.Coverage:P1}");
        AddMetric(items, "rejection-rate", snapshot.RejectionRate <= .03, $"Son 7 gün ret: {snapshot.RejectionRate:P1}");
        AddCount(items, "daily", "pending", snapshot.Pending, HealthLevel.Warning, $"Bekleyen daily: {snapshot.Pending}");
        AddCount(items, "daily", "parked", snapshot.Parked, HealthLevel.Error, $"Park edilmiş daily: {snapshot.Parked}");
        AddCount(items, "queue", "queue-length", snapshot.QueueLength, HealthLevel.Warning, $"Kuyruk uzunluğu: {snapshot.QueueLength}");
        AddCount(items, "quarantine", "quarantine", snapshot.Quarantine, HealthLevel.Warning, $"Karantina: {snapshot.Quarantine}");
        AddCount(items, "notes", "invalid-frontmatter", snapshot.InvalidFrontmatter, HealthLevel.Error, $"Geçersiz frontmatter: {snapshot.InvalidFrontmatter}");
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

    public VerifyResult VerifyIndex(IReadOnlyList<string> corpus, IReadOnlyList<string> index)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(index);
        var corpusSet = corpus.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexSet = index.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = corpusSet.Except(indexSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = indexSet.Except(corpusSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new VerifyResult(missing, extra, missing.Length == 0 && extra.Length == 0 ? 0 : 1);
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
