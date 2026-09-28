using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class InstallScars
{
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

    [Fact(DisplayName = "Y-309 · Kanca komutu eğik çizgili yol yazar; ters bölü bash altında yutulur")]
    public void Y309_HookCommandUsesForwardSlashes()
    {
        // Since the final review (external finding #3, SPEC wave-5 L5d) both paths are always
        // double-quoted, so & ' ^ ( ) ; in a path survive bash and cmd; forward slashes stay.
        var hooks = HookTemplates.Build(@"E:\OdenaOS\.oom\bin\oom.exe", @"E:\OdenaOS");
        Assert.All(hooks, hook => Assert.StartsWith("\"E:/OdenaOS/.oom/bin/oom.exe\" --vault \"E:/OdenaOS\" ", hook.Command, StringComparison.Ordinal));
        var spaced = HookTemplates.Build(@"C:\Program Files\oom\oom.exe", @"D:\My Vault");
        Assert.All(spaced, hook => Assert.StartsWith("\"C:/Program Files/oom/oom.exe\" --vault \"D:/My Vault\" ", hook.Command, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Y-327 · süreçler aynı daily kilidini bekler ve ayar yedekleri korunur")]
    public async Task Y327_DailyWritersWaitAndSettingsKeepBackups()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            var daily = Path.Combine(Directory.CreateDirectory(Path.Combine(vault, "daily")).FullName, $"{ScarFixture.Now:yyyy-MM-dd}.md");
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { typeof(Program).Assembly.Location, "--vault", vault, "save", "karar: kayıt\ndüzeltme: yok\ndevir: tamam" })
                start.ArgumentList.Add(argument);
            start.Environment["OOM_FAKE_NOW"] = ScarFixture.Now.ToString("O");
            using var held = DailyFileLock.Acquire(daily);
            using var child = System.Diagnostics.Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            var session = ScarFixture.Session("y327-" + Guid.NewGuid().ToString("N"), 3);
            var flush = Task.Run(() => new Flush(new FlushOptions(VaultPath: vault)).FlushSession(session, "unused.jsonl", FlushReason.SessionEnd));
            await Task.Delay(600);
            Assert.False(child.HasExited);
            Assert.False(flush.IsCompleted);
            Assert.False(File.Exists(daily));
            held.Dispose();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(child.ExitCode == 0, await error);
            Assert.Equal(FlushOutcome.Ok, (await flush.WaitAsync(TimeSpan.FromSeconds(15))).Outcome);
            var text = File.ReadAllText(daily);
            Assert.Contains("karar: kayıt", text);
            Assert.Contains("session:" + session.Id, text);
            await output;
            var settings = Path.Combine(Directory.CreateDirectory(Path.Combine(vault, ".claude")).FullName, "settings.json");
            const string original = "{\"permissions\":{\"allow\":[\"Bash\"]}}";
            File.WriteAllText(settings, original);
            Assert.Equal(0, Program.RunInstall(["install"], vault));
            var installed = File.ReadAllText(settings);
            Assert.Equal(0, Program.RunInstall(["install", "--uninstall"], vault));
            var backups = Directory.GetFiles(Path.GetDirectoryName(settings)!, "settings.json.bak-*").Select(File.ReadAllText).ToArray();
            Assert.Equal(2, backups.Length);
            Assert.Contains(original, backups);
            Assert.Contains(installed, backups);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(settings)!, "*.tmp"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            ScarFixture.Remove(vault);
        }
    }
}
