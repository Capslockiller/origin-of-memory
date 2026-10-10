using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom.Tests.Gates;

internal static class GateFixture
{
    internal static readonly UTF8Encoding Utf8 = new(false);

    private const string RelatedSection =
        "\n## İlgili Kavramlar\n- [[kavram-01]] — aynı sentetik gövdeden türer.\n- [[kavram-02]] — aynı sentetik gövdeden türer.\n";

    internal static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Oom.sln")))
            current = current.Parent;

        return current?.FullName ?? throw new InvalidOperationException("Oom.sln bulunamadı.");
    }

    internal static string Sample(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Oom", "Ingest", "Samples", name), Utf8);

    internal static void WriteVault(string vault)
    {
        var concepts = Path.Combine(vault, "knowledge", "concepts");
        Directory.CreateDirectory(concepts);
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));

        for (var i = 1; i <= 19; i++)
        {
            var topic = i <= 7 ? "Tokenizasyon ölçüsü bu notta sabitlenir." : "Gövde yalnız sentetik cümleler taşır.";
            WriteNote(concepts, $"kavram-{i:D2}", $"Kavram {i}", $"Sentetik kavram {i} gövdesi. {topic}");
        }

        WriteNote(concepts, "gizli-anahtar-maskesi", "Gizli anahtar maskesi",
            "Sentetik kavram gövdesi. Sızdırılan anahtar günlüğe [SIR:anthropic-key] olarak yazılır.");

        File.WriteAllText(Path.Combine(vault, "knowledge", "index.md"),
            "- **Bellek** (20 kavram) — sentetik kavram gövdeleri → [[hubs/bellek]]\n", Utf8);
        File.WriteAllText(Path.Combine(vault, ".oom", "hub-config.json"),
            "{\"catch_all\":\"bellek\",\"hubs\":[{\"id\":\"bellek\",\"ad\":\"Bellek\",\"kapsam\":\"sentetik\",\"tags\":[],\"title_keys\":[]}]}", Utf8);
    }

    internal static void WriteNote(string concepts, string slug, string title, string body) =>
        File.WriteAllText(Path.Combine(concepts, slug + ".md"),
            $"---\ntitle: {title}\naliases: [{slug}]\ntags: [bellek]\nsources: [2026-09-08.md]\ncreated: 2026-09-01\nupdated: 2026-09-08\n---\n# {title}\n{body}\n{RelatedSection}",
            Utf8);

    internal static string Executable()
    {
        var bin = Path.Combine(RepositoryRoot(), "src", "Oom", "bin");
        var candidates = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "oom.exe", SearchOption.AllDirectories).ToArray()
            : [];
        var separator = Path.DirectorySeparatorChar;
        var configuration = AppContext.BaseDirectory.Contains($"{separator}Debug{separator}", StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
        return candidates.FirstOrDefault(path => path.Contains($"{separator}{configuration}{separator}", StringComparison.OrdinalIgnoreCase))
            ?? candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            ?? throw new InvalidOperationException("oom.exe bulunamadı; src/Oom derlenmemiş.");
    }

    // O24: every child process launched through Run/RunScoped/RunWithInput used to leave
    // OOM_LOCALAPPDATA unset whenever a caller passed no explicit override (`Run` and
    // `RunWithInput` always called RunScoped(null, ...)) — an unset OOM_LOCALAPPDATA falls
    // through to the developer's REAL %LOCALAPPDATA%\oom (Boundaries.LocalAppData). Two
    // "Kapı 12-1" JSON tests (ContextScars/GetirmeScars) call `Run(...)` this way today.
    // This isolated root — created once per test-assembly run, under this test assembly's
    // own build output (never %TEMP%, the same rule KabulHarness follows) — replaces
    // "unset" as the default so no Gate/Scars child process spawned through GateFixture
    // ever touches the real profile. A caller that still needs a specific root (e.g. its
    // own TempVault-scoped scratch directory, as DoctorScars's Gate 12-1 test already
    // does) passes it explicitly and wins, exactly as before.
    internal static readonly string IsolatedLocalAppDataRoot = CreateIsolatedLocalAppDataRoot();

    private static string CreateIsolatedLocalAppDataRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "gate-localappdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>
    /// The state root a <paramref name="vault"/> resolves to UNDER <see cref="IsolatedLocalAppDataRoot"/>
    /// specifically — never <see cref="VaultIdentity.StateRoot"/>, which reads OOM_LOCALAPPDATA
    /// from THIS TEST PROCESS's own environment (always unset here, since tests never mutate
    /// process-wide env vars) and would therefore still name a directory under the real
    /// %LOCALAPPDATA%\oom.
    /// </summary>
    internal static string IsolatedStateRoot(string vault) =>
        Path.Combine(IsolatedLocalAppDataRoot, "oom", VaultIdentity.Hash(vault));

    internal static (int ExitCode, string StandardOutput, string StandardError) Run(params string[] arguments) =>
        RunScoped(null, arguments);

    internal static (int ExitCode, string StandardOutput, string StandardError) RunWithInput(string standardInput, params string[] arguments) =>
        RunScoped(null, standardInput, arguments);

    internal static (int ExitCode, string StandardOutput, string StandardError) RunScoped(string? localAppData, params string[] arguments) =>
        RunScoped(localAppData, null, arguments);

    private static (int ExitCode, string StandardOutput, string StandardError) RunScoped(string? localAppData, string? standardInput, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Executable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Path.GetTempPath()
        };

        // O24: never left unset — see IsolatedLocalAppDataRoot's own comment above.
        startInfo.Environment["OOM_LOCALAPPDATA"] = localAppData is { Length: > 0 } ? localAppData : IsolatedLocalAppDataRoot;

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("oom.exe başlatılamadı.");
        if (standardInput is not null)
            process.StandardInput.Write(standardInput);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "oom.exe 60 saniyede bitmedi.");
        return (process.ExitCode, output, error);
    }

    internal static string StateRoot(string vault) => VaultIdentity.StateRoot(vault);
}

internal sealed class TempVault : IDisposable
{
    internal TempVault()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oom-gate-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        // O24: NOT GateFixture.StateRoot(Path) (= VaultIdentity.StateRoot, which resolves
        // via OOM_LOCALAPPDATA read from THIS TEST PROCESS's own, always-unset
        // environment and would therefore name a directory under the developer's REAL
        // %LOCALAPPDATA%\oom again). Every GateFixture-driven child process now defaults
        // to GateFixture.IsolatedLocalAppDataRoot, so cleanup targets the SAME isolated
        // root the child actually wrote into. Computed BEFORE Path is removed: the hash
        // canonicalizes the vault path's on-disk case, which needs Path to still exist.
        var isolatedStateRoot = GateFixture.IsolatedStateRoot(Path);
        Remove(Path);
        Remove(isolatedStateRoot);
    }

    private static void Remove(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class Gate11Contracts
{
    private static readonly DateTimeOffset SampleStart = new(2026, 9, 9, 8, 0, 0, TimeSpan.FromHours(3));

    [Fact(DisplayName = "Y-320 · Gerçek Codex rollout biçiminde response_item turları okunur, developer ve enjekte bloklar dışarıda kalır")]
    public void Y320CodexRealShapeParsesResponseItems()
    {
        var session = CodexParser.Parse(GateFixture.Sample("codex-real-shape.jsonl"));

        Assert.Equal("01a09772-81ab-7ff3-aef6-bd8a09fac45c", session.Id);
        Assert.Equal("codex", session.Source);
        Assert.Equal(4, session.Turns.Count);
        Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, session.Turns.Select(turn => turn.Role));
        Assert.Equal(new[] { 7, 9, 10, 11 }, session.Turns.Select(turn => turn.Index));
        Assert.Equal("Ilk satir kullanici sorusu.\nIkinci satir ayni soruya ait.", session.Turns[0].Text);
        Assert.All(session.Turns, turn => Assert.Equal("text", turn.Kind));
        foreach (var excluded in new[] { "gelistirici", "recommended_plugins", "Enjekte edilen" })
            Assert.DoesNotContain(session.Turns, turn => turn.Text.Contains(excluded, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(DateTimeOffset.Parse("2026-09-13T09:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), session.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T09:00:01.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), session.Turns[0].Timestamp);
    }

    [Fact(DisplayName = "Y-321 · Eski Codex event_msg biçimi ayrıştırılmaya devam eder")]
    public void Y321CodexLegacyEventMessageFormatStillParses()
    {
        var session = CodexParser.Parse(GateFixture.Sample("codex-fixed.jsonl"));

        Assert.Equal("codex-fixed", session.Id);
        Assert.Equal("codex", session.Source);
        Assert.Equal(2, session.Turns.Count);
        Assert.Equal(new[] { "user", "assistant" }, session.Turns.Select(turn => turn.Role));
        Assert.Equal(new[] { "merhaba", "merhaba Master Mind" }, session.Turns.Select(turn => turn.Text));
        Assert.Equal(SampleStart, session.StartedAt);
    }





    private static string Text(JsonElement answer) =>
        answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
}
