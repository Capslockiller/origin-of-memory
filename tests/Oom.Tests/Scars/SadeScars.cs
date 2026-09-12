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
            Assert.Equal("[Hafıza] 15. mesaj. Oturum sonunda 🔮 850-Companion/Last-Session.md ve Threads.md güncellemeyi unutma.", additional);

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
}
