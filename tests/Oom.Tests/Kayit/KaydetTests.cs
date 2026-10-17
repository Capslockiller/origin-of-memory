using Oom.Contracts;
using Oom.Tests.Gates;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oom.Tests.Kayit;

public sealed partial class KaydetTests
{
    [Fact]
    public void Kaydet_HarnessMesajiSahipSayilmaz()
    {
        var read = ClaudeTranscript.Read("""
            {"sessionId":"fixture","uuid":"h-1","type":"user","message":{"content":"<system-reminder>harness text</system-reminder>"}}
            """, SystemClock.Instance);
        Assert.NotEqual("user", Assert.Single(read.Turns).Role);
    }

    [Fact]
    public void Kaydet_CodexAgentsMdVeOrtamMetniSahipSayilmaz()
    {
        // Gerçek rollout (2026-10-10): Codex ilk "user" mesajı olarak AGENTS.md'yi ve ortam bağlamını gönderir.
        Assert.Equal("h", TurSiniflandirma.Classify("user", "text", "# AGENTS.md instructions\n\n<INSTRUCTIONS>\n# Codex — Team Context"));
        Assert.Equal("h", TurSiniflandirma.Classify("user", "text", "<environment_context>\n  <cwd>E:/x</cwd>\n</environment_context>"));
        Assert.Equal("u", TurSiniflandirma.Classify("user", "text", "agents.md dosyasını güncelle"));
    }

    [Fact]
    public void Kaydet_ImlecSonUuidCapasindanTurer()
    {
        using var f = new Fixture();
        f.Claude(3);
        f.Daily("## kayit\n<!-- session:fixture ts:2026-10-09T12:00:00+03:00 turns:0-99 source:claude kayit:v1 son:m-1 -->\n", -7);
        Assert.Equal(1, f.Write());
        Assert.DoesNotContain("[u m-0]", f.Read());
        Assert.DoesNotContain("[a m-1]", f.Read());
        Assert.Contains("[u m-2]", f.Read());
    }

    [Fact]
    public void Kaydet_ImlecEskiTurnsCapasindanTurer()
    {
        using var f = new Fixture();
        f.Claude(3);
        const string old = "---\ntype: daily\n---\n### Oturum\n<!-- session:fixture ts:2026-10-10T12:00:00+03:00 turns:0-1 source:claude -->\neski özet\n";
        f.Daily(old);
        Assert.Equal(1, f.Write());
        Assert.StartsWith(old, f.Read());
        Assert.DoesNotContain("[u m-0]", f.Read());
        Assert.Contains("[u m-2]", f.Read());
    }

    [Fact]
    public void Kaydet_IkinciKosuHicbirSeyYazmaz()
    {
        using var f = new Fixture();
        f.Claude(3);
        Assert.Equal(1, f.Write());
        var first = f.Read();
        Assert.Equal(0, f.Write());
        Assert.Equal(first, f.Read());
        Assert.False(Directory.Exists(Path.Combine(f.Root, "local", "oom")));
    }

    [Fact]
    public void Kaydet_MesajDokuzBekleyenTurdaSessizKalir()
    {
        using var f = new Fixture();
        f.Claude(9);
        var result = f.Run("--tetik", "mesaj");
        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Out + result.Error);
        Assert.False(File.Exists(f.DailyPath));
    }

    [Fact]
    public void Kaydet_MesajOnBekleyenTurdaBirBlokYazar()
    {
        using var f = new Fixture();
        f.Claude(10);
        Assert.Equal(0, f.Run("--tetik", "mesaj").Exit);
        Assert.Equal(1, Regex.Matches(f.Read(), "(?m)^## kayit").Count);
        Assert.Equal(10, Regex.Matches(f.Read(), "(?m)^### ").Count);
    }

    [Fact]
    public void Kaydet_AracTurlariVeSonuclariYazilmaz()
    {
        using var f = new Fixture();
        f.Claude(1);
        f.AppendClaude("tool-result", "user", new object[] { new { type = "tool_result", content = "SECRET_TOOL_RESULT" } });
        f.AppendClaude("agent", "assistant", new object[] {
            new { type = "text", text = "görünen ajan sözü" },
            new { type = "tool_use", name = "Bash", input = "SECRET_TOOL_INPUT" },
            new { type = "tool_use", name = "Bash", input = "SECRET_TOOL_INPUT" } });
        Assert.Equal(1, f.Write());
        Assert.DoesNotContain("tool-result", f.Read());
        Assert.DoesNotContain("SECRET_TOOL", f.Read());
        Assert.DoesNotContain("[t ", f.Read());
        Assert.Contains("[a agent] 12:00 · araç: Bash ×2", f.Read());
        Assert.Contains("görünen ajan sözü", f.Read());
    }

    [Fact]
    public void Kaydet_AjanMetniDortBinKarakterdeKesilir()
    {
        using var f = new Fixture();
        f.AppendClaude("agent", "assistant", new string('x', 4_021));
        Assert.Equal(1, f.Write());
        Assert.Contains(new string('x', 4_000) + " (+21 kr)", f.Read());
        Assert.DoesNotContain(new string('x', 4_001), f.Read());
    }

    [Fact]
    public void Kaydet_SahipMetniYirmiBinKarakterdeKesilir()
    {
        using var f = new Fixture();
        f.AppendClaude("owner", "user", new string('x', 20_005));
        Assert.Equal(1, f.Write());
        Assert.Contains(new string('x', 20_000) + " (+5 kr)", f.Read());
    }

    [Fact]
    public void Kaydet_SahipTurundakiSirMaskelenir()
    {
        using var f = new Fixture();
        var token = "ghp_" + new string('A', 40);
        f.AppendClaude("owner", "user", "anahtar " + token);
        Assert.Equal(1, f.Write());
        Assert.DoesNotContain(token, f.Read());
        Assert.Contains("ghp_****(maskelendi)", f.Read());
        Assert.Contains("[maske:tur]", f.Read());
        Assert.DoesNotContain(Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories), p => p.Contains("raw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Kaydet_MaskeZamanAsimindaMetniYazmaz()
    {
        using var f = new Fixture();
        var body = "ghp_" + new string('A', 100_000);
        f.AppendClaude("owner", "user", body);
        var redactor = new Redactor(TimeSpan.FromTicks(1));
        Assert.True(redactor.Mask(body).TimedOut);
        Assert.Equal(1, f.Write(redactor: redactor));
        Assert.Contains("[maske: zaman aşımı, 100004 kr]", f.Read());
        Assert.DoesNotContain("AAAA", f.Read());
    }

    [Fact]
    public void Kaydet_CodexRolloutCodexKaynakliBlokUretir()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Transcript, """
            {"type":"session_meta","timestamp":"2026-10-10T12:00:00+03:00","payload":{"id":"fixture"}}
            {"type":"response_item","ordinal":1,"timestamp":"2026-10-10T12:00:00+03:00","payload":{"type":"message","id":"codex-own-id","role":"user","content":[{"type":"input_text","text":"Codex sahibi"}]}}
            {"type":"response_item","ordinal":2,"timestamp":"2026-10-10T12:01:00+03:00","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Codex ajanı"}]}}
            """, Fixture.Utf8);
        Assert.Equal(0, f.Run().Exit);
        Assert.Contains("source:codex", f.Read());
        Assert.Contains("[u codex-own-id]", f.Read());
        var fallback = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes("fixture:2"))).ToLowerInvariant();
        Assert.Contains("[a " + fallback + "]", f.Read());
        Assert.Equal(0, f.Write());
    }

    [Fact]
    public void Kaydet_KilitliDailyTekJsonBildirirVeSifirDoner()
    {
        using var f = new Fixture();
        f.Claude(1);
        f.Daily("mevcut içerik\n");
        using var held = DailyFileLock.Acquire(f.DailyPath);
        var timer = Stopwatch.StartNew();
        var result = f.Run();
        Assert.Contains("IOException", Message(result));
        Assert.Equal("hata", Assert.Single(Rows(f)).GetProperty("sonuc").GetString());
        Assert.Equal("mevcut içerik\n", f.Read());
        Assert.False(File.Exists(f.DailyPath + ".oom-tmp"));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Kaydet_EskiAyarAnahtarlariSessizceYokSayilir()
    {
        using var f = new Fixture();
        f.Claude(1);
        f.Config("""{"backend":{"claude":{"fast":5}},"flush":{"mode":"unknown"},"extensions":[{}],"future":true}""");
        var settings = OomSettings.Load(f.Root);
        Assert.Null(settings.LoadError);
        Assert.Empty(settings.UnknownKeys);
        Assert.Empty(settings.InvalidValues);
        Assert.Equal(string.Empty, f.Run().Error);
        Assert.Contains("[u m-0]", f.Read());
    }

    [Fact]
    public void Kaydet_YardimYalnizDortKomutListeler()
    {
        var help = GateFixture.Run("--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Equal(new[] { "context", "kaydet", "retrieve", "doctor" },
            Regex.Matches(help.StandardOutput, @"(?m)^  (\w+) ").Select(m => m.Groups[1].Value).ToArray());
        foreach (var command in new[] { "flush", "sweep", "compile", "save", "mcp", "install", "nudge", "kit" })
            Assert.DoesNotContain(command + " ", help.StandardOutput);
    }

    [Theory]
    [InlineData("<system-reminder>gizli harness")]
    [InlineData("<command-name>gizli harness")]
    [InlineData("Another Claude session sent a message gizli harness")]
    [InlineData("[SYSTEM NOTIFICATION gizli harness")]
    [InlineData("<task-notification>gizli harness")]
    public void Kaydet_HarnessYalnizBasliklaKaydedilir(string body)
    {
        using var f = new Fixture();
        f.Claude(1);
        f.AppendClaude("harness", "user", body);
        Assert.Equal(1, f.Write());
        Assert.Contains("[h harness] 12:00 · ajan mesajı", f.Read());
        Assert.DoesNotContain("gizli harness", f.Read());
    }

    [Fact]
    public void Kaydet_BaskaOturumunUuidCapasiImlecOlmaz()
    {
        using var f = new Fixture();
        f.Claude(1);
        f.Daily("## kayit\n<!-- session:other ts:2026-10-10T12:00:00+03:00 turns:0-0 source:claude kayit:v1 son:m-0 -->\n### [u m-0] 12:00\n");
        Assert.Equal(1, f.Write());
        Assert.Equal(2, Regex.Matches(f.Read(), @"\[u m-0\]").Count);
    }

    [Fact]
    public void Kaydet_KayitliUuidImlecSonrasindaTekrarlanmaz()
    {
        using var f = new Fixture();
        f.Claude(2);
        Assert.Equal(1, f.Write());
        f.AppendClaude("m-0", "user", "tekrar");
        f.AppendClaude("new", "user", "yeni");
        Assert.Equal(1, f.Write());
        Assert.Equal(1, Regex.Matches(f.Read(), @"\[u m-0\]").Count);
        Assert.Contains("[u new]", f.Read());
    }

    [Fact]
    public void Kaydet_HookPayloadStdinUzerindenOkunur()
    {
        using var f = new Fixture();
        f.Claude(1);
        var result = f.ProcessRun(new[] { "--vault", f.Root, "kaydet", "--tetik", "compact" },
            JsonSerializer.Serialize(new { session_id = "fixture", transcript_path = f.Transcript }));
        Assert.Equal(0, result.Exit);
        Assert.Equal(string.Empty, result.Out + result.Error);
        Assert.Contains("kayit: v1", f.Read());
    }

    [Fact]
    public void Kaydet_VaultEksikseIkiDigerHatalardaBirDoner()
    {
        using var f = new Fixture();
        Assert.Equal(2, f.ProcessRun(new[] { "kaydet" }).Exit);
        var bad = f.ProcessRun(new[] { "--vault", f.Root, "kaydet", "--tetik", "yanlis" });
        Assert.Equal(1, bad.Exit);
        Assert.Single(bad.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, f.Run().Exit);
        Assert.Equal("dosya-yok", Rows(f)[^1].GetProperty("sonuc").GetString());
    }

    [Fact]
    public void Kaydet_TaramaKirkSekizSaatVeKokSiniriniKullanir()
    {
        using var f = new Fixture();
        f.Claude(1);
        var projects = Path.Combine(f.Root, "projects");
        Directory.CreateDirectory(projects);
        File.WriteAllText(Path.Combine(projects, "recent.jsonl"), File.ReadAllText(f.Transcript).Replace("\"fixture\"", "\"recent\""), Fixture.Utf8);
        File.Copy(f.Transcript, Path.Combine(projects, "old.jsonl"));
        File.WriteAllText(Path.Combine(projects, "bad.jsonl"), """{"type":"session_meta","payload":{"id":"bad"}}""", Fixture.Utf8);
        File.Copy(f.Transcript, Path.Combine(projects, "agent-helper.jsonl"));
        File.SetLastWriteTimeUtc(Path.Combine(projects, "recent.jsonl"), Fixture.Now.UtcDateTime.AddHours(-47));
        File.SetLastWriteTimeUtc(Path.Combine(projects, "bad.jsonl"), Fixture.Now.UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(projects, "old.jsonl"), Fixture.Now.UtcDateTime.AddHours(-49));
        f.Config(JsonSerializer.Serialize(new { sweep = new { roots = new[] { projects } } }));
        var result = f.Run("--tetik", "tara");
        Assert.Equal(1, result.Exit);
        Assert.Equal("tara: 2 oturum, 1 blok, 1 hata", result.Out.Trim());
        Assert.Single(result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("[u m-0]", f.Read());
    }

    [Fact]
    public void Kaydet_KancalarYeniTetikleriKullanir()
    {
        var hooks = HookTemplates.Build(@"E:\bin\oom.exe", @"E:\test vault");
        Assert.Equal(new[] { "SessionStart", "SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact" }, hooks.Select(h => h.Event));
        Assert.EndsWith(" context", hooks[0].Command);
        Assert.EndsWith(" kaydet --tetik acilis", hooks[1].Command);
        Assert.Equal(15, hooks[1].TimeoutSeconds);
        Assert.EndsWith(" kaydet --tetik mesaj", hooks[2].Command);
        Assert.EndsWith(" kaydet --tetik kapanis", hooks[3].Command);
        Assert.EndsWith(" kaydet --tetik compact", hooks[4].Command);
        Assert.All(hooks, h => Assert.Contains("--vault \"E:/test vault\"", h.Command));
    }

    [Fact]
    public void Kaydet_ModelRecursionDegiskeniKaydiEngellemez()
    {
        using var f = new Fixture();
        f.Claude(1);
        var result = f.ProcessRun(["--vault", f.Root, "kaydet", "--session", "fixture", "--transcript", f.Transcript], invokedBy: "legacy-runner");
        Assert.Equal(0, result.Exit);
        Assert.Contains("[u m-0]", f.Read());
    }

    [Fact]
    public void Kaydet_AltAjanVeSeritTranskriptleriniKaydetmez()
    {
        using var f = new Fixture();
        f.Claude(1);
        var helper = Path.Combine(f.Root, "agent-helper.jsonl");
        File.Copy(f.Transcript, helper);
        Assert.Equal(0, Oom.Contracts.Kaydet.Write(f.Root, "fixture", helper, "kapanis", Fixture.Now));
        File.WriteAllText(f.Transcript, """
            {"type":"session_meta","payload":{"id":"fixture","source":{"subagent":{"thread_spawn":{"parent_thread_id":"owner"}}}}}
            {"type":"response_item","payload":{"type":"message","role":"user","content":"alt ajan"}}
            """, Fixture.Utf8);
        Assert.Equal(0, f.Write());
        Assert.False(File.Exists(f.DailyPath));
    }

    [Fact]
    public void Kaydet_MesajEsigineHarnessTurlariDahilDegildir()
    {
        using var f = new Fixture();
        f.Claude(9);
        for (var i = 0; i < 12; i++) f.AppendClaude("h-" + i, "user", "<system-reminder>harness</system-reminder>");
        Assert.Equal(0, f.Write("mesaj"));
        Assert.False(File.Exists(f.DailyPath));
        Assert.Equal(1, f.Write("compact"));
        Assert.Equal(21, Regex.Matches(f.Read(), "(?m)^### ").Count);
    }

    [Fact]
    public void Kaydet_EszamanliTetiklerAyniUuidYazmaz()
    {
        using var f = new Fixture();
        f.Claude(10);
        var results = Task.WhenAll(Task.Run(() => f.Run()), Task.Run(() => f.Run())).GetAwaiter().GetResult();
        Assert.All(results, r => Assert.Equal(0, r.Exit));
        Assert.Single(Regex.Matches(f.Read(), "(?m)^## kayit").Cast<Match>());
        Assert.Single(Regex.Matches(f.Read(), @"\[u m-0\]").Cast<Match>());
    }

    [Fact]
    public void Kaydet_GunDegisinceAyniKimlikleDevamEder()
    {
        using var f = new Fixture();
        f.Claude(1);
        Assert.Equal(1, f.Write());
        f.AppendClaude("new-day", "assistant", "ertesi gün");
        Assert.Equal(1, Oom.Contracts.Kaydet.Write(f.Root, "fixture", f.Transcript, "kapanis", Fixture.Now.AddDays(1)));
        var next = File.ReadAllText(Path.Combine(f.Root, "daily", "2026-10-11.md"));
        Assert.DoesNotContain("[u m-0]", next);
        Assert.Contains("[a new-day]", next);
    }

    [Fact]
    public void Kaydet_YalnizAracCagrisiMesajEsiginiArtirmaz()
    {
        using var f = new Fixture();
        f.Claude(9);
        f.AppendClaude("tool-only", "assistant", new object[] { new { type = "tool_use", name = "Bash", input = "SECRET_TOOL_INPUT" } });
        Assert.Equal(0, f.Write("mesaj"));
        Assert.Equal(1, f.Write());
        Assert.Equal(9, Regex.Matches(f.Read(), "(?m)^### ").Count);
        Assert.DoesNotContain("tool-only", f.Read());
        Assert.DoesNotContain("SECRET_TOOL_INPUT", f.Read());
        Assert.Contains("· araç: Bash ×1", f.Read());
    }

    [Fact]
    public void Kaydet_EskiDuzCodexKaydindaDosyaYoluVeKendiIdAlaniKullanilir()
    {
        using var f = new Fixture();
        var directory = Path.Combine(f.Root, ".codex", "sessions");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.jsonl");
        File.WriteAllText(path, """{"session_id":"fixture","id":"flat-own-id","role":"user","kind":"text","text":"eski Codex"}""", Fixture.Utf8);
        Assert.Equal(1, Oom.Contracts.Kaydet.Write(f.Root, "fixture", path, "kapanis", Fixture.Now));
        Assert.Contains("source:codex", f.Read());
        Assert.Contains("[u flat-own-id]", f.Read());
    }

    [Fact]
    public void Kaydet_ImlecUuidTekrariAradakiYeniTuruGizlemez()
    {
        using var f = new Fixture();
        f.Claude(1);
        Assert.Equal(1, f.Write());
        f.AppendClaude("new", "assistant", "yeni tur");
        f.AppendClaude("m-0", "user", "tekrarlanan eski tur");
        Assert.Equal(1, f.Write());
        Assert.Contains("[a new]", f.Read());
        Assert.Single(Regex.Matches(f.Read(), @"\[u m-0\]").Cast<Match>());
        Assert.Equal(0, f.Write());
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly UTF8Encoding Utf8 = new(false);
        internal static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(3));
        internal string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? throw new InvalidOperationException("TMPDIR gerekli"), "kayit-" + Guid.NewGuid().ToString("N"));
        internal string Transcript => Path.Combine(Root, "fixture.jsonl");
        internal string DailyPath => Path.Combine(Root, "daily", "2026-10-10.md");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal void Claude(int count)
        {
            for (var i = 0; i < count; i++) AppendClaude("m-" + i, i % 2 == 0 ? "user" : "assistant", "metin " + i);
        }
        internal void AppendClaude(string id, string role, object content) => File.AppendAllText(Transcript,
            JsonSerializer.Serialize(new { sessionId = "fixture", uuid = id, type = role, timestamp = Now.ToString("O"), cwd = "sentetik-proje", message = new { role, content } }) + "\n", Utf8);
        internal void Daily(string text, int days = 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DailyPath)!);
            File.WriteAllText(Path.Combine(Root, "daily", Now.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md"), text, Utf8);
        }
        internal void Config(string json)
        {
            Directory.CreateDirectory(Path.Combine(Root, ".oom"));
            File.WriteAllText(Path.Combine(Root, ".oom", "oom.json"), json, Utf8);
        }
        internal string Read() => File.ReadAllText(DailyPath);
        internal int Write(string trigger = "kapanis", Redactor? redactor = null) => Oom.Contracts.Kaydet.Write(Root, "fixture", Transcript, trigger, Now, redactor);
        internal (int Exit, string Out, string Error) Run(params string[] args) => ProcessRun(["--vault", Root, "kaydet", "--session", "fixture", "--transcript", Transcript, .. args]);
        internal (int Exit, string Out, string Error) ProcessRun(string[] args, string input = "", string? invokedBy = null)
        {
            var info = new ProcessStartInfo(GateFixture.Executable()) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            info.Environment["OOM_FAKE_NOW"] = Now.ToString("O");
            info.Environment["OOM_LOCALAPPDATA"] = Path.Combine(Root, "local");
            info.Environment["OOM_USERPROFILE"] = Path.Combine(Root, "profile");
            if (invokedBy is not null) info.Environment["OOM_INVOKED_BY"] = invokedBy;
            using var process = Process.Start(info)!;
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(10_000));
            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        public void Dispose()
        {
            var boundary = Path.GetFullPath(Environment.GetEnvironmentVariable("TMPDIR")!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(boundary, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(Root, recursive: true);
        }
    }
}
