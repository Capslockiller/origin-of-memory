using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Kabul.Fixtures;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Kabul;

/// <summary>
/// L5b-coverage (SPEC-3.1.0.md F7-5): the user-facing commands driven IN-PROCESS through
/// Program.Main, so coverlet counts Oom.Program itself instead of only the child processes
/// KabulHarness starts. Every test asserts an exit code together with stdout/stderr or a
/// file effect.
///
/// Isolation, honestly stated (review finding, applied): this file owns exactly one test
/// class and may not edit src/Oom — an injected, non-process-global state-root/profile-root
/// seam (mirroring VaultPaths.UseVault, which already exists only for the vault path) would
/// need to live in src/Oom/Infrastructure/Boundaries.cs and src/Oom/Cli/Program.Doctor.cs,
/// both out of OWNS. `OOM_LOCALAPPDATA` / `OOM_USERPROFILE` env-var seams exist there, but
/// the hard rule for this lane is never to mutate a process-wide env var from a test, so
/// they are not used here. Two residual, MEASURED facts follow from that:
///  - `doctor` (tests #9, #10) reads the real ~/.claude/settings.json read-only, through
///    Program.Doctor.cs's HookHealth/UserProfileRoot; nothing here is asserted against its
///    content, but the file's presence/shape on the machine that runs this suite can add or
///    remove warning/error rows, which is why the assertions below derive an expected exit
///    code from the summary line instead of hardcoding it.
///  - Only read-only commands run here (context, doctor without --fix, retrieve, sweep
///    --dry-run, compile --dry-run, help/version and bad-argument paths). Every one of them
///    resolves a state-root path under %LOCALAPPDATA%\oom\&lt;hash of the fixture vault&gt;
///    (Directory.Exists/File.Exists only — VaultIdentity.ExistingDatabase/State.OpenReadOnly
///    never call Directory.CreateDirectory) but never CREATES it in the current
///    implementation (measured: R24(b) read-only commands never write state). A future
///    regression that made one of them write would write there for real, so `Kasa` checks
///    that the directory does not exist BEFORE the command runs (fails loud on a dirty
///    environment) and asserts it still does not exist from `Dispose()` — not only from an
///    explicit call at the end of a test body — so the check still runs even when an earlier
///    assertion in the same test already failed and threw.
///  - Fixtures live under ScarFixture.TempDirectory() (the test output folder), and every
///    oom.json sets sweep.roots to a fixture folder, so ~/.claude/projects and ~/.codex are
///    never scanned.
///  - Program.Dispatch sets Console.InputEncoding, which re-binds Console.In to the real
///    standard input; stdin-reading paths (context hook payload) therefore call Announce
///    directly with Console.SetIn, and `mcp` (which reads Console.In until EOF) runs only
///    on its disabled path.
/// </summary>
public sealed class ProgramKapsamKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly Type ProgramType = typeof(Save).Assembly.GetType("Oom.Program", throwOnError: true)!;

    private static readonly DateTimeOffset Today = KabulVaultBuilder.DefaultToday;

    [Fact(DisplayName = "L5b #1 · --help ve --version exit 0; kullanım metni ve sürüm stdout'ta")]
    public void HelpAndVersion_PrintToStdout_ExitZero()
    {
        var help = Run(["--help"]);
        Assert.Equal(0, help.Code);
        Assert.Contains("oom [--vault <yol>] <komut>", help.Out, StringComparison.Ordinal);
        Assert.Contains("compile [--dry-run]", help.Out, StringComparison.Ordinal);
        Assert.Contains("kit install [--dry-run] [--force]", help.Out, StringComparison.Ordinal);
        Assert.Equal(string.Empty, help.Err);

        var version = Run(["--version"]);
        Assert.Equal(0, version.Code);
        Assert.Equal(BuildInfo.Version, version.Out.Trim());
    }

    [Fact(DisplayName = "L5b #2 · komutsuz, bilinmeyen komut ve bilinmeyen kit alt komutu exit 1 ile kullanım metnini basar")]
    public void MissingOrUnknownCommand_PrintsUsage_ExitOne()
    {
        using var kasa = new Kasa();

        var none = Run([]);
        Assert.Equal(1, none.Code);
        Assert.Contains("oom [--vault <yol>] <komut>", none.Out, StringComparison.Ordinal);

        var unknown = Run(["--vault", kasa.Vault, "yok-boyle-komut"]);
        Assert.Equal(1, unknown.Code);
        Assert.Contains("oom [--vault <yol>] <komut>", unknown.Out, StringComparison.Ordinal);

        var kit = Run(["--vault", kasa.Vault, "kit", "yok-boyle"]);
        Assert.Equal(1, kit.Code);
        Assert.Contains("kit status [--json] [--kit <dizin>]", kit.Out, StringComparison.Ordinal);

        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #3 · --vault yokken ve vault.json yokken kasa isteyen komut 'vault: yok' ile exit 1")]
    public void NoVault_IsReported_ExitOne()
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "vault.json")),
            "fixture precondition: no vault.json beside the test assembly");

        var result = Run(["doctor"]);

        Assert.Equal(1, result.Code);
        Assert.Contains("vault: yok", result.Err, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Out);
    }

    [Fact(DisplayName = "L5b #4 · ayar uyarıları: bilinmeyen anahtar, geçersiz değer ve bozuk oom.json stderr'e yazılır")]
    public void ConfigurationWarnings_AreReportedOnStderr()
    {
        using var kasa = new Kasa();
        kasa.WriteSettings("""{ "bogusKey": 1, "retrieve": { "top": "çok" }, "sweep": { "roots": [ ROOT ] } }""");

        var warned = Run(["--vault", kasa.Vault, "retrieve", "--session", "x"]);
        Assert.Equal(1, warned.Code);
        Assert.Contains("bilinmeyen ayar: bogusKey", warned.Err, StringComparison.Ordinal);
        Assert.Contains("geçersiz ayar değeri: retrieve.top", warned.Err, StringComparison.Ordinal);
        Assert.Contains("hata: retrieve --session kaldırıldı", warned.Err, StringComparison.Ordinal);

        kasa.WriteRawSettings("{ bozuk");
        var broken = Run(["--vault", kasa.Vault, "kit", "yok-boyle"]);
        Assert.Equal(1, broken.Code);
        Assert.Contains("ayar dosyası okunamadı, varsayılanlar kullanılıyor", broken.Err, StringComparison.Ordinal);

        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #5 · mcp.enabled=false iken `mcp` 'mcp: kapalı' ile exit 1")]
    public void McpDisabled_RefusesWithExitOne()
    {
        using var kasa = new Kasa();
        kasa.WriteSettings("""{ "mcp": { "enabled": false }, "sweep": { "roots": [ ROOT ] } }""");

        var result = Run(["--vault", kasa.Vault, "mcp"]);

        Assert.Equal(1, result.Code);
        Assert.Contains("mcp: kapalı", result.Err, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Out);
        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #6 · elle `context` metni ve `context --json` bölümleri kasadan gelir; state yazılmaz")]
    public void ManualContext_PrintsVaultContext_TextAndJson()
    {
        using var kasa = new Kasa();

        var text = Announce(kasa, [], stdin: string.Empty);
        Assert.Equal(0, text.Code);
        Assert.Contains("Konu 01 — sentetik aktif iş", text.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("hookSpecificOutput", text.Out, StringComparison.Ordinal);

        var json = Announce(kasa, ["--json"], stdin: string.Empty);
        Assert.Equal(0, json.Code);
        using var document = JsonDocument.Parse(json.Out);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.True(root.GetProperty("sections").GetArrayLength() > 0);
        var body = root.GetProperty("text").GetString()!;
        Assert.Equal(body.Length, root.GetProperty("chars").GetInt32());
        Assert.Contains("Konu 01 — sentetik aktif iş", body, StringComparison.Ordinal);

        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #7 · SessionStart kanca yükü ile `context` hookSpecificOutput zarfı basar")]
    public void HookContext_PrintsSessionStartEnvelope()
    {
        using var kasa = new Kasa();

        var result = Announce(kasa, [], stdin: """{"session_id":"l5b-kanca","hook_event_name":"SessionStart"}""");

        Assert.Equal(0, result.Code);
        using var document = JsonDocument.Parse(result.Out);
        var output = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("SessionStart", output.GetProperty("hookEventName").GetString());
        Assert.Contains("Konu 01 — sentetik aktif iş", output.GetProperty("additionalContext").GetString(), StringComparison.Ordinal);
        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #8 · oom.json extensions[] çalıştırılmaz ve uyarılır; okunamayan oom.json da uyarılır")]
    public void ContextExtensions_AreIgnoredWithWarning()
    {
        using var kasa = new Kasa();
        var marker = Path.Combine(kasa.Root, "extension-ran.txt");
        kasa.WriteSettings("""
            { "sweep": { "roots": [ ROOT ] },
              "extensions": [ { "name": "a", "contextLine": "cmd /c echo x > MARKER" }, { "name": "b", "contextLine": "echo y" } ] }
            """.Replace("MARKER", JsonEncodedText.Encode(marker).ToString(), StringComparison.Ordinal));

        var warned = Announce(kasa, [], stdin: string.Empty);
        Assert.Equal(0, warned.Code);
        Assert.Contains("context: oom.json extensions[].contextLine artık çalıştırılmıyor — 2 uzantı yok sayıldı", warned.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(marker), "an extension contextLine must never run");

        kasa.WriteRawSettings("{ bozuk");
        var broken = Announce(kasa, [], stdin: string.Empty);
        Assert.Equal(0, broken.Code);
        Assert.Contains("context: oom.json okunamadı, extensions denetlenemedi", broken.Err, StringComparison.Ordinal);
        Assert.Contains("Konu 01 — sentetik aktif iş", broken.Out, StringComparison.Ordinal);

        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #9 · `doctor` state yokken state-yok satırı, bekleyen sayısı ve hata sayısıyla tutarlı exit kodu")]
    public void Doctor_WithoutState_ReportsHonestly()
    {
        using var kasa = new Kasa();

        var result = Run(["--vault", kasa.Vault, "doctor"]);

        Assert.Contains("state-yok", result.Out, StringComparison.Ordinal);
        Assert.Contains("bekleyen 2", result.Out, StringComparison.Ordinal);
        Assert.Contains("— tümü: oom doctor --all", result.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("fts5-ok", result.Out, StringComparison.Ordinal);
        Assert.Equal(SummaryErrors(result.Out) > 0 ? 1 : 0, result.Code);

        var all = Run(["--vault", kasa.Vault, "doctor", "--all"]);
        Assert.Contains("fts5-ok", all.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("— tümü: oom doctor --all", all.Out, StringComparison.Ordinal);
        Assert.Equal(SummaryErrors(all.Out) > 0 ? 1 : 0, all.Code);

        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #10 · `doctor --json` ve `doctor --quiet` aynı sonucu makine ve stderr biçiminde verir")]
    public void Doctor_JsonAndQuiet()
    {
        using var kasa = new Kasa();

        var json = Run(["--vault", kasa.Vault, "doctor", "--json"]);
        using var document = JsonDocument.Parse(json.Out);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(json.Code, root.GetProperty("exit_code").GetInt32());
        Assert.Equal(2, root.GetProperty("pending").GetInt32());
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(items, item => item.GetProperty("code").GetString() == "state-yok" && item.GetProperty("level").GetString() == "warning");
        Assert.Equal(items.Any(item => item.GetProperty("level").GetString() == "error") ? 1 : 0, json.Code);

        var quiet = Run(["--vault", kasa.Vault, "doctor", "--quiet"]);
        Assert.Equal(json.Code, quiet.Code);
        Assert.Equal(string.Empty, quiet.Out);
        Assert.Contains("state: Durum veritabanı yok", quiet.Err, StringComparison.Ordinal);

        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #11 · `retrieve --query` kavram notunu bulur; --json ve --top biçimi; indeks yokken state yazılmaz")]
    public void Retrieve_FindsConceptNote_TextAndJson()
    {
        using var kasa = new Kasa();
        const string query = "Kabul harness sentetik kavram 02";

        var text = Run(["--vault", kasa.Vault, "retrieve", "--query", query]);
        Assert.Equal(0, text.Code);
        Assert.Contains("kabul-sentetik-kavram-02", text.Out, StringComparison.Ordinal);

        var json = Run(["--vault", kasa.Vault, "retrieve", "--query", query, "--json", "--top", "2"]);
        Assert.Equal(0, json.Code);
        using var document = JsonDocument.Parse(json.Out);
        Assert.Equal(query, document.RootElement.GetProperty("query").GetString());
        var hits = document.RootElement.GetProperty("hits").EnumerateArray().ToArray();
        Assert.InRange(hits.Length, 1, 2);
        Assert.Contains(hits, hit => hit.GetProperty("name").GetString()!.Contains("kabul-sentetik-kavram-02", StringComparison.Ordinal));

        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #12 · `retrieve --batch` her geçerli satıra bir JSON satırı basar; boş ve bozuk JSON satırı atlanır")]
    public void RetrieveBatch_OneJsonLinePerQuery()
    {
        using var kasa = new Kasa();
        var batch = Path.Combine(kasa.Root, "sorgular.txt");
        File.WriteAllText(batch, string.Join('\n',
            "Kabul harness sentetik kavram 01",
            "",
            """{"soru":"Kabul harness sentetik kavram 03"}""",
            """{"query":"Kabul harness sentetik kavram 02"}""",
            "{ bozuk json"), Utf8);

        var result = Run(["--vault", kasa.Vault, "retrieve", "--batch", batch]);

        Assert.Equal(0, result.Code);
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var queries = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("query").GetString() ?? string.Empty).ToArray();
        Assert.Equal(["Kabul harness sentetik kavram 01", "Kabul harness sentetik kavram 03", "Kabul harness sentetik kavram 02"], queries);
        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #13 · `sweep --dry-run` dökümü görür, sonuç satırını basar ve hiçbir şey yazmaz")]
    public void SweepDryRun_SeesTranscript_WritesNothing()
    {
        using var kasa = new Kasa();
        WriteTranscript(Path.Combine(kasa.Transcripts, "l5b-kuru-kosum.jsonl"), "l5b-kuru-kosum", 6, Today.AddHours(-2));
        // Should-fix (review, applied): a dry run snapshotted only daily/ before; mutating
        // any OTHER vault file (or overwriting an existing concept without renaming it)
        // would have passed. Snapshot every fixture-root-relative path and its bytes.
        var before = SnapshotTree(kasa.Root);
        var stateRootBefore = Directory.Exists(VaultIdentity.StateRoot(kasa.Vault));

        var result = Run(["--vault", kasa.Vault, "sweep", "--dry-run"]);

        Assert.Equal(0, result.Code);
        Assert.Contains("tarama (kuru koşum): 1 dosya, 1 değişmiş, 1 oturum", result.Out, StringComparison.Ordinal);
        Assert.Contains("sonuçlar: ", result.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("indeks:", result.Out, StringComparison.Ordinal);
        AssertTreeUnchanged(before, SnapshotTree(kasa.Root));
        Assert.Equal(stateRootBefore, Directory.Exists(VaultIdentity.StateRoot(kasa.Vault)));
        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #14 · `compile --dry-run` planı basar: son derleme=hiç, bekleyen daily sayısı, gün başına istem boyu; not yazmaz")]
    public void CompileDryRun_PrintsPlan_WritesNothing()
    {
        using var kasa = new Kasa();
        // Should-fix (review, applied): a dry run snapshotted only concept FILENAMES before
        // — overwriting an existing concept's bytes without renaming it would have passed.
        // Snapshot every fixture-root-relative path and its bytes.
        var before = SnapshotTree(kasa.Root);
        var stateRootBefore = Directory.Exists(VaultIdentity.StateRoot(kasa.Vault));

        var result = Run(["--vault", kasa.Vault, "compile", "--dry-run"]);

        Assert.Equal(0, result.Code);
        Assert.Contains("derleme planı (kuru koşum): son derleme=hiç · bekleyen daily=2 · bu koşumda=2 · kavram=3", result.Out, StringComparison.Ordinal);
        Assert.Contains($"  {Today:yyyy-MM-dd}.md: kayıt defteri=", result.Out, StringComparison.Ordinal);
        Assert.Contains($"  {Today.AddDays(-1):yyyy-MM-dd}.md: kayıt defteri=", result.Out, StringComparison.Ordinal);
        Assert.Contains("backend: claude claude-sonnet-5", result.Out, StringComparison.Ordinal);
        AssertTreeUnchanged(before, SnapshotTree(kasa.Root));
        Assert.Equal(stateRootBefore, Directory.Exists(VaultIdentity.StateRoot(kasa.Vault)));
        kasa.AssertNoStateWritten();
    }

    [Fact(DisplayName = "L5b #15 · kit: bozuk manifest 'manifest okunamadı' exit 1; olmayan kit kökünde status 'kit yok'")]
    public void Kit_BrokenManifestAndMissingRoot()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            var home = Path.Combine(root, "home");
            var kit = Path.Combine(root, "kit");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(kit);
            File.WriteAllText(Path.Combine(kit, "manifest.json"), "{ bozuk", Utf8);

            var install = RunKit(["kit", "install", "--kit", kit], home);
            Assert.Equal(1, install.Code);
            Assert.StartsWith("manifest okunamadı:", install.Err, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(home));

            // Should-fix (review, applied): assert the intended fixed contract directly
            // instead of deriving the expected exit code from the implementation's own
            // output punctuation (src/Oom/Cli/Program.Kit.cs RunKitStatus: a missing kit
            // root yields zero rows, `rows.All(...)` is vacuously true over an empty
            // sequence, and only "kit yok" is printed — no row lines, so no ": ").
            var missing = RunKit(["kit", "status", "--kit", Path.Combine(root, "kit-yok")], home);
            Assert.Equal($"kit yok{Environment.NewLine}", missing.Out);
            Assert.Equal(0, missing.Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(home));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static int SummaryErrors(string stdout)
    {
        var match = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) yeşil · (\d+) uyarı · (\d+) hata");
        Assert.True(match.Success, $"doctor summary line missing:\n{stdout}");
        return int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private static Result Run(string[] args)
    {
        var main = ProgramType.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic, binder: null, [typeof(string[])], modifiers: null)!;
        return Capture(string.Empty, () => (int)main.Invoke(null, [args])!);
    }

    private static Result Announce(Kasa kasa, string[] args, string stdin)
    {
        var announce = ProgramType.GetMethod("Announce", BindingFlags.Static | BindingFlags.NonPublic)!;
        return Capture(stdin, () =>
        {
            VaultPaths.UseVault(kasa.Vault);
            return (int)announce.Invoke(null, [(string[])["context", .. args], kasa.Vault, OomSettings.Load(kasa.Vault), Today])!;
        });
    }

    private static Result RunKit(string[] args, string home) =>
        Capture(string.Empty, () => Program.RunKit(args, home));

    private static Result Capture(string stdin, Func<int> action)
    {
        var (previousOut, previousError, previousIn) = (Console.Out, Console.Error, Console.In);
        var (outputEncoding, inputEncoding) = (Console.OutputEncoding, Console.InputEncoding);
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Console.SetIn(new StringReader(stdin));
            var code = action();
            return new Result(code, output.ToString(), error.ToString());
        }
        finally
        {
            VaultPaths.UseVault(null);
            Console.OutputEncoding = outputEncoding;
            Console.InputEncoding = inputEncoding;
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Console.SetIn(previousIn);
        }
    }

    private static void WriteTranscript(string path, string sessionId, int turns, DateTimeOffset start)
    {
        var lines = new StringBuilder();
        for (var i = 0; i < turns; i++)
        {
            var user = i % 2 == 0;
            var text = $"Sentetik tur {i:D2}: kapsam senaryosu için {(user ? "kullanıcı sorusu" : "asistan yanıtı")}, gerçek içerik değildir.";
            object content = user ? text : new[] { new { type = "text", text } };
            lines.Append(JsonSerializer.Serialize(new
            {
                sessionId,
                type = user ? "user" : "assistant",
                timestamp = (start + TimeSpan.FromMinutes(i)).ToString("O", CultureInfo.InvariantCulture),
                message = new { role = user ? "user" : "assistant", content }
            })).Append('\n');
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, lines.ToString(), Utf8);
    }

    /// <summary>Every file under <paramref name="root"/>, keyed by its root-relative path,
    /// with its exact bytes — the write-safety check for a dry run (should-fix, review
    /// applied): a filename-only or single-subfolder snapshot lets a write to any other file,
    /// or an in-place overwrite of an existing file's bytes, slip through undetected.</summary>
    private static Dictionary<string, byte[]> SnapshotTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertTreeUnchanged(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before)
            Assert.True(bytes.AsSpan().SequenceEqual(after[path]), $"dry run bytes değiştirdi: {path}");
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed record Result(int Code, string Out, string Err);

    /// <summary>A KabulVaultBuilder vault (today's daily plus one from yesterday, three
    /// concept notes, the real-size hand layer) whose sweep.roots point at a fixture folder.</summary>
    private sealed class Kasa : IDisposable
    {
        public Kasa()
        {
            Root = ScarFixture.TempDirectory();
            Vault = KabulVaultBuilder.Build(Root);
            Transcripts = Path.Combine(Root, "transcripts");
            Directory.CreateDirectory(Transcripts);
            File.WriteAllText(Path.Combine(Vault, "daily", $"{Today.AddDays(-1):yyyy-MM-dd}.md"),
                $"# {Today.AddDays(-1):yyyy-MM-dd}\n\n- dünün sentetik günlük satırı, kapsam fixture'ı.\n", Utf8);
            WriteSettings("""{ "context": { "capChars": 16000 }, "sweep": { "roots": [ ROOT ] } }""");

            // Pre-flight half of the review-required write-safety check (applied): fail loud
            // here, before any command runs, if the real state directory this vault's hash
            // resolves to already exists — a dirty environment must never be mistaken for a
            // command that wrote it during this test.
            Assert.False(Directory.Exists(VaultIdentity.StateRoot(Vault)),
                $"kasa kurulmadan önce zaten var: {VaultIdentity.StateRoot(Vault)}");
        }

        public string Root { get; }

        public string Vault { get; }

        public string Transcripts { get; }

        public void WriteSettings(string json) =>
            WriteRawSettings(json.Replace("ROOT", JsonSerializer.Serialize(Transcripts), StringComparison.Ordinal));

        public void WriteRawSettings(string text) =>
            File.WriteAllText(Path.Combine(Vault, ".oom", "oom.json"), text, Utf8);

        /// R24(b): the in-process state root is the real %LOCALAPPDATA%\oom\&lt;hash&gt;; a
        /// read-only command must never create it. Public and called explicitly at the end
        /// of every test body (keeps the failure attributed to that test's assertion), and
        /// again — unconditionally — from Dispose, so a regression that both writes state AND
        /// throws on an earlier assertion in the same test still gets caught rather than
        /// slipping past on the early return.
        public void AssertNoStateWritten() =>
            Assert.False(Directory.Exists(VaultIdentity.StateRoot(Vault)),
                $"read-only command created state under {VaultIdentity.StateRoot(Vault)}");

        public void Dispose()
        {
            AssertNoStateWritten();
            TryDelete(Root);
        }
    }
}
