using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Oom.Contracts;

namespace Oom.Tests.Gates;

/// <summary>
/// Everything the two gate suites need to build a world of their own: a synthetic vault under
/// the temp directory, the fixed ingest samples the repository carries, and the published
/// executable. Nothing here reads a real transcript, calls a model or touches a real vault.
/// </summary>
internal static class GateFixture
{
    internal static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>The five headings a concept note needs to parse, plus a topic word per note.</summary>
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

    /// <summary>
    /// A whole synthetic vault: twenty concept notes, a root map and a hub config. Seven notes
    /// carry the topic word so a search for it has more candidates than the MCP limit allows,
    /// which is what makes the clamp observable; one note carries the secret mask token, which
    /// is what makes the query side of <c>Guards.Gate(In)</c> observable.
    /// </summary>
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

    /// <summary>The built single-file executable; the gate 12 schemas are its own output, not a library's.</summary>
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

    internal static (int ExitCode, string StandardOutput, string StandardError) Run(params string[] arguments)
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

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("oom.exe başlatılamadı.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "oom.exe 60 saniyede bitmedi.");
        return (process.ExitCode, output, error);
    }

    /// <summary>
    /// Where the executable puts a vault's state (D9). The tests recompute it so the directory
    /// the run created under %LOCALAPPDATA% is removed again with the vault itself. This used to
    /// hand-roll a third copy of the hash: a copy means the gate suite measured a function
    /// production does not use, and its <see cref="Path.GetFullPath(string)"/> step was the
    /// spelling production had already rejected — so it asks <see cref="VaultIdentity"/> instead.
    /// </summary>
    internal static string StateRoot(string vault) => VaultIdentity.StateRoot(vault);
}

/// <summary>A temp directory that removes itself, and the state root the executable derived from it.</summary>
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
        // A pooled SQLite connection keeps the file open long after State was disposed, and a
        // held state.db is what leaves a temp directory behind (spec: every fixture is removed).
        SqliteConnection.ClearAllPools();
        Remove(Path);
        Remove(GateFixture.StateRoot(Path));
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
            // A temp directory that will not go now is the operating system's problem, not a failure.
        }
    }
}

/// <summary>
/// Acceptance gate 11 (spec 11-11) made measurable: both ingest parsers against the fixed
/// samples in the repository, the three MCP tools end to end through the stdio loop with a
/// fake client, and <c>save --session-json</c> through the normal flush path.
/// </summary>
public sealed class Gate11Contracts
{
    private static readonly DateTimeOffset SampleStart = new(2026, 9, 9, 8, 0, 0, TimeSpan.FromHours(3));

    [Fact(DisplayName = "Kapı 11-1 · Claude sabit örneği oturum, kaynak, tur ve zaman damgası verir")]
    public void Gate11ClaudeFixedSampleParses()
    {
        var session = new Ingest().ParseClaude(GateFixture.Sample("claude-fixed.jsonl"));

        Assert.Equal("claude-fixed", session.Id);
        Assert.Equal("claude", session.Source);
        Assert.Equal(2, session.Turns.Count);
        Assert.Equal(new[] { "user", "assistant" }, session.Turns.Select(turn => turn.Role));
        Assert.Equal(SampleStart, session.Turns[0].Timestamp);
        Assert.Equal(SampleStart.AddSeconds(1), session.Turns[1].Timestamp);
        Assert.Equal(SampleStart, session.StartedAt);
    }

    [Fact(DisplayName = "Kapı 11-1 · Codex sabit örneği oturum, kaynak, tur ve başlangıç zamanı verir")]
    public void Gate11CodexFixedSampleParses()
    {
        var session = new Ingest().ParseCodex(GateFixture.Sample("codex-fixed.jsonl"));

        Assert.Equal("codex-fixed", session.Id);
        Assert.Equal("codex", session.Source);
        Assert.Equal(2, session.Turns.Count);
        Assert.Equal(["user", "assistant"], session.Turns.Select(turn => turn.Role));
        Assert.Equal(new[] { "merhaba", "merhaba Master Mind" }, session.Turns.Select(turn => turn.Text));
        Assert.Equal(SampleStart, session.StartedAt);
    }

    [Fact(DisplayName = "Kapı 11-1 · Gerçek Claude Code biçiminde sidechain, meta ve araç satırları dışarıda kalır")]
    public void Gate11ClaudeRealShapeExcludesSidechainMetaAndToolLines()
    {
        var session = new Ingest().ParseClaude(GateFixture.Sample("claude-code-real-shape.jsonl"));

        Assert.Equal("e2e-11111111-aaaa-4001-8001-000000000001", session.Id);
        Assert.Equal("claude", session.Source);
        Assert.Equal(4, session.Turns.Count);
        Assert.Equal(new[] { "user", "assistant", "user", "assistant" }, session.Turns.Select(turn => turn.Role));
        Assert.All(session.Turns, turn => Assert.Equal("text", turn.Kind));
        foreach (var excluded in new[] { "alt ajan", "bu blok ozete girmemeli", "bu satir ozete girmemeli", "/status", "notlar.md" })
            Assert.DoesNotContain(session.Turns, turn => turn.Text.Contains(excluded, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(new DateTimeOffset(2026, 9, 9, 9, 1, 0, TimeSpan.FromHours(3)), session.StartedAt);
    }

    [Fact(DisplayName = "Kapı 11-1 · Bilinmeyen satır türü iki ayrıştırıcıda da yok sayılır")]
    public void Gate11UnknownLineTypeIsTolerated()
    {
        var ingest = new Ingest();
        var claude = ingest.ParseClaude(GateFixture.Sample("claude-fixed.jsonl") +
            "\n{\"sessionId\":\"claude-fixed\",\"type\":\"telemetri\",\"timestamp\":\"2026-09-09T08:00:02+03:00\",\"message\":{\"content\":\"gorunmez\"}}");
        var codex = ingest.ParseCodex(GateFixture.Sample("codex-fixed.jsonl") +
            "\n{\"type\":\"turn_context\",\"timestamp\":\"2026-09-09T08:00:03+03:00\",\"payload\":{\"type\":\"cwd\",\"message\":\"gorunmez\"}}");

        Assert.Equal(2, claude.Turns.Count);
        Assert.Equal(2, codex.Turns.Count);
        Assert.DoesNotContain(claude.Turns.Concat(codex.Turns), turn => turn.Text.Contains("gorunmez", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Kapı 11-1 · Yarım yazılmış son satır iki ayrıştırıcıyı da patlatmaz")]
    public void Gate11TruncatedLastLineDoesNotThrow()
    {
        var ingest = new Ingest();
        var claude = ingest.ParseClaude(GateFixture.Sample("claude-fixed.jsonl") + "\n{\"sessionId\":\"claude-fixed\",\"type\":\"user\",\"mes");
        var codex = ingest.ParseCodex(GateFixture.Sample("codex-fixed.jsonl") + "\n{\"type\":\"event_msg\",\"payload\":{\"type\":\"user_mes");

        Assert.Equal(2, claude.Turns.Count);
        Assert.Equal(2, codex.Turns.Count);
    }

    [Fact(DisplayName = "Kapı 11-2 · MCP stdio döngüsü sahte istemciyle uçtan uca yanıtlar ve EOF'ta biter")]
    public void Gate11McpStdioLoopAnswersThreeToolsAndEndsAtEndOfInput()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);
        var mcp = new Mcp(new Guards(), new Retrieve(new RetrieveOptions(Top: 5, VaultPath: vault.Path)), vault.Path);

        var script = string.Join('\n',
        [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_search\",\"arguments\":{\"query\":\"tokenizasyon ölçüsü\",\"limit\":9}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_search\",\"arguments\":{\"query\":\"sk-ant-api03-AAAAAAAAAAAAAAAAAAAA\"}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_root_map\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_note\",\"arguments\":{\"name\":\"kavram-01.md\"}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_note\",\"arguments\":{\"name\":\"olmayan-not.md\"}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_sil\",\"arguments\":{}}}",
            "{bozuk",
            string.Empty
        ]);

        var writer = new StringWriter();
        mcp.Run(new StringReader(script), writer);
        var answers = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement).ToArray();

        // The notification and the blank trailing line produce no answer; EOF ends the loop.
        Assert.Equal(9, answers.Length);
        Assert.Equal("oom", answers[0].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());

        var tools = answers[1].GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(new[] { "memory_search", "memory_root_map", "memory_note" }, tools);

        // limit 9 is clamped to 5 even though seven notes carry the topic word.
        Assert.StartsWith("[Hafıza — 5 not]", Text(answers[2]), StringComparison.Ordinal);

        // The query crosses Guards.Gate(In): the raw key never reaches the ranking, its mask does.
        Assert.Contains("gizli-anahtar-maskesi.md", Text(answers[3]), StringComparison.Ordinal);

        Assert.Contains("[[hubs/bellek]]", Text(answers[4]), StringComparison.Ordinal);
        Assert.Contains("title: Kavram 1", Text(answers[5]), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Text(answers[6]));

        Assert.Equal(-32602, answers[7].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32700, answers[8].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact(DisplayName = "Kapı 11-2 · Not adı tek dosya adı olmalıdır, dizin yürüyüşü JSON-RPC hatasıdır")]
    public void Gate11McpRefusesPathTraversalInNoteName()
    {
        using var vault = new TempVault();
        GateFixture.WriteVault(vault.Path);
        var mcp = new Mcp(new Guards(), new Retrieve(new RetrieveOptions(Top: 5, VaultPath: vault.Path)), vault.Path);

        var answer = JsonDocument.Parse(mcp.HandleJsonRpc(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_note\",\"arguments\":{\"name\":\"..\\\\..\\\\vault.json\"}}}")).RootElement;

        Assert.Equal(-32602, answer.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact(DisplayName = "Kapı 11-3 · save --session-json dış oturumu normal flush yolundan daily bloğuna yazar")]
    public void Gate11SaveSessionJsonWritesImportedDailyBlock()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Path, "daily"));
        var id = "disari-" + Guid.NewGuid().ToString("N")[..8];
        var start = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.FromHours(3));
        var turns = Enumerable.Range(0, 6).Select(i => new
        {
            index = i,
            role = i % 2 == 0 ? "user" : "assistant",
            kind = "text",
            text = $"Dış ayrıştırıcıdan gelen {i}. tur metni.",
            timestamp = start.AddMinutes(i).ToString("O")
        }).ToArray();
        var json = JsonSerializer.Serialize(new { id, source = "web-disari", turns, startedAt = start.ToString("O") });

        // No model is reachable, so the write path takes its own extractive fallback (INT lane).
        var save = new Save(flush: new Flush(new FlushOptions(VaultPath: vault.Path),
            new FixedClock(start.AddHours(1)), new Runner(null, configured: false)));

        var first = save.SaveSessionJson(json);
        var second = save.SaveSessionJson(json);

        Assert.Equal(FlushOutcome.Ok, first.Outcome);
        Assert.Equal(6, first.Cursor);
        Assert.NotNull(first.DailyPath);
        Assert.Contains("fallback_backend: extractive", first.Summary!, StringComparison.Ordinal);

        var eventTime = TimeZoneInfo.ConvertTime(start.AddMinutes(5), TimeZoneInfo.Local);
        Assert.Equal(Path.Combine(vault.Path, "daily", $"{eventTime:yyyy-MM-dd}.md"), first.DailyPath);

        var daily = File.ReadAllText(first.DailyPath!, GateFixture.Utf8);
        Assert.Contains($"### Oturum ({eventTime:HH:mm}), içe aktarım:web-disari", daily, StringComparison.Ordinal);
        Assert.Contains($"<!-- session:{id} ts:{eventTime:yyyy-MM-ddTHH:mm:sszzz} turns:0-5 source:web-disari -->", daily, StringComparison.Ordinal);
        foreach (var heading in new[] { "## Bağlam", "## Önemli Konuşmalar", "## Alınan Kararlar", "## Öğrenilenler", "## Yapılacaklar" })
            Assert.Contains(heading, daily, StringComparison.Ordinal);

        Assert.Equal(FlushOutcome.NoNewTurns, second.Outcome);
        Assert.Equal(6, second.Cursor);
    }

    private static string Text(JsonElement answer) =>
        answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }
}
