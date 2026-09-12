using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class SadeScars
{
    [Fact(DisplayName = "Y-300 · nudge on beşinci mesajda hatırlatır, aradaki on dördünde susar")]
    public void Y300_NudgeSpeaksOnEveryFifteenthPromptAndNotBetween()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            using var state = new State(null, null, Path.Combine(root, "state.db"));
            var session = "y300-" + Guid.NewGuid().ToString("N")[..8];
            var spoken = new List<int>();

            for (var prompt = 1; prompt <= 30; prompt++)
                if (Nudge.Count(state, session, ScarFixture.Now, every: 15) is not null)
                    spoken.Add(prompt);

            Assert.Equal([15, 30], spoken);

            var envelope = Nudge.Envelope(Nudge.Reminder(15));
            var additional = JsonDocument.Parse(envelope).RootElement
                .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();
            Assert.Equal("[Memory] prompt 15. Before the session ends, update 🔮 850-Companion/Last-Session.md and Threads.md.", additional);

            Assert.Equal(string.Empty, Nudge.Envelope(null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-301 · Last-Session.md dokunulmadan kapanan uzun oturum yansıma borcu bırakır, context onu bir kez basar")]
    public void Y301_UntouchedCompanionAfterALongSessionLeavesExactlyOneReflectionDebtRow()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        var companion = Path.Combine(vault, ScarFixture.CompanionDir);
        Directory.CreateDirectory(companion);
        try
        {
            var settings = OomSettings.Defaults(vault);
            using var state = new State(null, null, Path.Combine(root, "state.db"));
            var session = "y301-" + Guid.NewGuid().ToString("N")[..8];
            var started = DateTimeOffset.UtcNow;

            var lastSession = Path.Combine(companion, "Last-Session.md");
            File.WriteAllText(lastSession, "## Session: eski\n");
            File.SetLastWriteTimeUtc(lastSession, started.AddDays(-2).UtcDateTime);

            for (var prompt = 0; prompt < settings.ReflectionMinPrompts; prompt++)
                Nudge.Count(state, session, started, settings.NudgeEvery);

            Program.RecordReflectionDebt(state, vault, settings, session);
            Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM health WHERE component = 'hafiza' AND code = 'yansima-borcu'"));

            var debt = state.TakeReflectionDebt();
            Assert.Equal(Nudge.ReflectionDebt, debt);
            Assert.Equal(0, state.Scalar("SELECT COUNT(*) FROM health WHERE component = 'hafiza' AND code = 'yansima-borcu'"));

            var block = new Context(settings.Context with { PendingNotification = debt }).Build(vault, started);
            Assert.StartsWith("[Bildirim]\n" + Nudge.ReflectionDebt, block.Text, StringComparison.Ordinal);

            File.SetLastWriteTimeUtc(lastSession, started.AddMinutes(1).UtcDateTime);
            Program.RecordReflectionDebt(state, vault, settings, session);
            Assert.Equal(0, state.Scalar("SELECT COUNT(*) FROM health WHERE component = 'hafiza' AND code = 'yansima-borcu'"));
            Assert.False(Nudge.OwesReflection(vault, settings.Context.CompanionDir, started, 1, settings.ReflectionMinPrompts));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-302 · Durum dosyası silinebilir; yeni açılış on tabloyu sıfırdan kurar ve sürüm damgası aramaz")]
    public void Y302_DeletedStateFileIsRebuiltFromScratchOnTheNextOpen()
    {
        string[] expected =
        [
            "daily_ingest", "flush_log", "health", "notes", "notes_fts",
            "oom_index_meta", "quarantine", "retry_queue", "sessions", "sweep_stamps"
        ];

        var root = ScarFixture.TempDirectory();
        var database = Path.Combine(root, "state.db");
        try
        {
            using (var first = new State(null, null, database))
            {
                Assert.Equal(expected, Tables(first));
                first.RecordFlush(ScarFixture.Now, "y302", "sessionend", "ok", 3, 30, "claude");
                Assert.Equal(1, first.Scalar("SELECT COUNT(*) FROM flush_log"));
                Assert.Equal(0, first.Scalar("PRAGMA user_version"));
                Assert.Equal(0, first.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'view'"));
            }

            SqliteConnection.ClearAllPools();
            File.Delete(database);
            Assert.False(File.Exists(database));

            using var rebuilt = new State(null, null, database);
            Assert.Equal(expected, Tables(rebuilt));
            Assert.Equal(0, rebuilt.Scalar("SELECT COUNT(*) FROM flush_log"));
            rebuilt.RecordFlush(ScarFixture.Now, "y302", "sessionend", "ok", 3, 30, "claude");
            Assert.Equal(1, rebuilt.Scalar("SELECT COUNT(*) FROM flush_log"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-303 · install yalnız vault'un içine yazar; kanca kaydı birleştirilir, kaldırma yalnız dört kancayı düşürür")]
    public void Y303_ProjectScopeInstallTouchesNothingOutsideTheVault()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        var outside = Path.Combine(root, "profil");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(outside);
        var previous = Environment.GetEnvironmentVariable("OOM_LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", outside);
            Assert.StartsWith(outside, VaultIdentity.StateRoot(vault), StringComparison.OrdinalIgnoreCase);

            var settingsPath = Path.Combine(vault, ".claude", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath,
                """{"permissions":{"allow":["Bash"]},"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"kendi-betigim.cmd"}]}]}}""");

            var before = Snapshot(outside);
            Assert.Equal(0, Program.RunInstall(["install"], vault));
            Assert.Equal(before, Snapshot(outside));

            Assert.True(File.Exists(Path.Combine(vault, ".oom", "vault.json")));
            Assert.True(File.Exists(Path.Combine(vault, ".oom", "oom.json")));

            var hooks = JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement;
            Assert.True(hooks.TryGetProperty("permissions", out _), "install ilgisiz anahtarı sildi");
            foreach (var registration in HookTemplates.Build(Environment.ProcessPath!, vault))
                Assert.Contains(registration.Command, Commands(hooks, registration.Event), StringComparer.Ordinal);

            Assert.Contains("kendi-betigim.cmd", Commands(hooks, "SessionStart"), StringComparer.Ordinal);
            Assert.Contains("nudge", Commands(hooks, "UserPromptSubmit").Single(), StringComparison.Ordinal);

            Assert.Equal(0, Program.RunInstall(["install", "--uninstall"], vault));
            var after = JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement;
            Assert.True(after.TryGetProperty("permissions", out _), "kaldırma ilgisiz anahtarı sildi");
            Assert.Equal(["kendi-betigim.cmd"], Commands(after, "SessionStart"));
            Assert.Empty(Commands(after, "UserPromptSubmit"));
            Assert.Equal(before, Snapshot(outside));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OOM_LOCALAPPDATA", previous);
            ScarFixture.Remove(root);
        }
    }

    private static string[] Tables(State state)
    {
        var names = new List<string>();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(state.WorkDirectory, "state.db")};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' " +
            "AND name NOT LIKE 'notes\\_fts\\_%' ESCAPE '\\' ORDER BY name";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return [.. names];
    }

    private static string[] Commands(JsonElement settings, string @event)
    {
        if (!settings.TryGetProperty("hooks", out var hooks) || !hooks.TryGetProperty(@event, out var entries))
            return [];

        return [.. entries.EnumerateArray()
            .SelectMany(entry => entry.TryGetProperty("hooks", out var inner) ? inner.EnumerateArray() : default)
            .Select(hook => hook.TryGetProperty("command", out var command) ? command.GetString() ?? string.Empty : string.Empty)];
    }

    private static string[] Snapshot(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(path => File.Exists(path) ? $"{path}:{new FileInfo(path).Length}" : path)
            .Order(StringComparer.Ordinal)];

    [Fact(DisplayName = "Y-304 · Eski şemalı state.db göç edilmez: kenara alınır, yenisi sıfırdan kurulur, eski dosya silinmez")]
    public void OlderShapeIsRetiredNotMigrated()
    {
        var root = Directory.CreateTempSubdirectory("oom-scar-y304-").FullName;
        try
        {
            var path = Path.Combine(root, "state.db");
            using (var old = new SqliteConnection($"Data Source={path}"))
            {
                old.Open();
                using var ddl = old.CreateCommand();
                ddl.CommandText = "CREATE TABLE sessions(session_id TEXT PRIMARY KEY, transcript_path TEXT, last_turn_index INTEGER, last_flush_ts TEXT); INSERT INTO sessions VALUES ('s-eski', 'x', 3, 't'); PRAGMA user_version = 5;";
                ddl.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using (var state = new State(null, null, path))
            {
                Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'prompt_count'"));
                Assert.Equal(0, state.Scalar("SELECT COUNT(*) FROM sessions"));
                Assert.Equal(1, state.Scalar("SELECT COUNT(*) FROM health WHERE code = 'eski-sema'"));
            }
            SqliteConnection.ClearAllPools();

            var retired = Directory.GetFiles(root, "state.db.eski-*").Where(f => !f.EndsWith("-wal") && !f.EndsWith("-shm")).ToArray();
            Assert.Single(retired);
            using var check = new SqliteConnection($"Data Source={retired[0]};Mode=ReadOnly");
            check.Open();
            using var count = check.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sessions";
            Assert.Equal(1L, count.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(DisplayName = "Y-318 · nudge her mesajda saat satırı basar: yeni oturum, alt dakika, kırk dakikalık ara, iki günlük ara")]
    public void Y318_NudgePrintsAClockLineOnEveryPrompt()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            using var state = new State(null, null, Path.Combine(root, "state.db"));
            var session = "y318-" + Guid.NewGuid().ToString("N")[..8];
            var start = new DateTimeOffset(2026, 9, 12, 16, 40, 0, TimeSpan.FromHours(3));

            Assert.Equal("[Zaman] 2026-09-12 16:40 · oturum şimdi başladı · 1. mesaj",
                Nudge.Lines(state, session, start, every: 15));

            Assert.Equal("[Zaman] 2026-09-12 23:42 · oturum 16:40'ta başladı (7 sa 2 dk) · son mesaj 7 sa 2 dk önce · 7 sa 2 dk ara · 2. mesaj",
                Nudge.Lines(state, session, start.AddHours(7).AddMinutes(2), every: 15));

            Assert.Equal("[Zaman] 2026-09-12 23:48 · oturum 16:40'ta başladı (7 sa 8 dk) · son mesaj 6 dk önce · 3. mesaj",
                Nudge.Lines(state, session, start.AddHours(7).AddMinutes(8), every: 15));

            Assert.Equal("[Zaman] 2026-09-13 00:28 · oturum 16:40'ta başladı (7 sa 48 dk) · son mesaj 40 dk önce · 40 dk ara · 4. mesaj",
                Nudge.Lines(state, session, start.AddHours(7).AddMinutes(48), every: 15));

            Assert.Equal("[Zaman] 2026-09-15 03:28 · oturum 16:40'ta başladı (2 gün 10 sa) · son mesaj 2 gün 3 sa önce · 2 gün 3 sa ara · 5. mesaj",
                Nudge.Lines(state, session, start.AddDays(2).AddHours(10).AddMinutes(48), every: 15));

            var fresh = "y318b-" + Guid.NewGuid().ToString("N")[..8];
            Nudge.Lines(state, fresh, start, every: 15);
            Assert.Equal("[Zaman] 2026-09-12 16:40 · oturum şimdi başladı · son mesaj az önce · 2. mesaj",
                Nudge.Lines(state, fresh, start.AddSeconds(20), every: 15));

            var every = new List<string>();
            for (var prompt = 3; prompt <= 15; prompt++)
                every.Add(Nudge.Lines(state, fresh, start.AddMinutes(prompt), every: 15));

            Assert.All(every.Take(12), line => Assert.DoesNotContain("[Memory]", line, StringComparison.Ordinal));
            Assert.Equal("[Memory] prompt 15. Before the session ends, update 🔮 850-Companion/Last-Session.md and Threads.md.",
                every[^1].Split('\n')[1]);
            Assert.StartsWith("[Zaman] ", every[^1], StringComparison.Ordinal);
            Assert.True(every[^1].Split('\n')[0].Length < 120);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-319 · context açılış bloğu son oturum bitişini bilir, hiç yoksa bilmediğini söyler")]
    public void Y319_ContextOpensWithAClockLine()
    {
        var root = ScarFixture.TempDirectory();
        var vault = Path.Combine(root, "kasa");
        Directory.CreateDirectory(Path.Combine(vault, ScarFixture.CompanionDir));
        try
        {
            var settings = OomSettings.Defaults(vault);
            var now = new DateTimeOffset(2026, 9, 12, 23, 48, 0, TimeSpan.FromHours(3));
            using var state = new State(null, null, Path.Combine(root, "state.db"));

            Assert.Null(state.LastSessionEnd());
            var blank = new Context(settings.Context with { LastSessionEnd = state.LastSessionEnd() }).Build(vault, now);
            Assert.Contains("Zaman", blank.Sections);
            Assert.Equal("[Zaman] 2026-09-12 23:48 · son oturum bilinmiyor", blank.Text.Split('\n')[1]);

            state.RecordFlush(new DateTimeOffset(2026, 9, 12, 15, 30, 0, TimeSpan.FromHours(3)), "y319", "manual", "summary", 4, 100, "cli");
            Assert.Equal(new DateTimeOffset(2026, 9, 12, 15, 30, 0, TimeSpan.FromHours(3)), state.LastSessionEnd());

            var known = new Context(settings.Context with { LastSessionEnd = state.LastSessionEnd() }).Build(vault, now);
            Assert.Equal("[Zaman] 2026-09-12 23:48 · son oturum bitişi: 2026-09-12 15:30 (8 sa 18 dk önce)", known.Text.Split('\n')[1]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

}
