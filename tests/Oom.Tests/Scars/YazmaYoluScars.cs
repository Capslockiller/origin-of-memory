using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class YazmaYoluScars
{
    [Fact(DisplayName = "Y-001 · Sınırlandırılmış flush işlenmeyen öneği kaybetmez")]
    public void Y001_BoundedFlushCoversEveryTurnExactlyOnce()
    {
        var ranges = new Flush().PlanRanges(ScarFixture.Session("ddd44fc9", 206), -1, 30, 15_000);
        Assert.Equal(7, ranges.Count);
        Assert.All(ranges, range => Assert.InRange(range.Turns.Count, 1, 30));
        Assert.Equal(Enumerable.Range(0, 206), ranges.SelectMany(range => range.Turns).Select(turn => turn.Index));
    }

    [Fact(DisplayName = "Y-002 · PreCompact atlanan öneği sonraki taramaya bırakır")]
    public void Y002_PreCompactCommitsOnlyProcessedRange()
    {
        var flush = new Flush();
        var ranges = flush.PlanRanges(ScarFixture.Session("precompact", 61), -1, 30, 15_000);
        var daily = ranges.Aggregate(string.Empty, (text, range) => flush.AppendDaily(text, $"turns:{range.Start}-{range.End}", "precompact"));
        Assert.Equal(61, ranges.SelectMany(x => x.Turns).Select(x => x.Index).Distinct().Count());
        Assert.Equal(3, daily.Split("turns:", StringSplitOptions.None).Length - 1);
    }

    [Fact(DisplayName = "Y-003 · Kancasız yirmi oturum tek sweep ile kapsanır")]
    public void Y003_SweepCoversSessionsWithoutHooks()
    {
        var sessions = Enumerable.Range(1, 20).Select(i => ScarFixture.Session($"s-{i}", 3)).ToArray();
        var result = new Sweep().Run(sessions, new SweepOptions());
        Assert.Equal(20, result.Total);
        Assert.Equal(20, result.Covered);
        Assert.Empty(result.UncoveredIds);
    }

    [Fact(DisplayName = "Y-004 · Sonuçsuz kanca girişi overdue ve stderr ile görünürdür")]
    public void Y004_IngressWithoutTerminalOutcomeBecomesOverdue()
    {
        var ingress = new IngressRecord("hook-1", ScarFixture.Now.AddMinutes(-16), "received", "logs/hook-1.stderr");
        var result = new Sweep().Reconcile([ingress], ScarFixture.Now);
        var overdue = Assert.Single(result.Overdue);
        Assert.Equal("hook-1", overdue.Id);
        Assert.EndsWith(".stderr", overdue.StandardErrorPath);
    }

    [Fact(DisplayName = "Y-005 · Araç bloğu ve uzun tur kayıpsız ham kanalda saklanır")]
    public void Y005_RawTranscriptRoundTripsWithoutLoss()
    {
        var session = ScarFixture.Session("raw", 3, 20_000);
        var jsonl = ScarFixture.TranscriptJsonl(session, includeTool: true);
        var stored = new Flush().StoreRawTranscript(jsonl);
        Assert.Contains("tool-payload", stored);
        Assert.Contains(new string('a', 20_000), stored);
        Assert.Equal(jsonl, stored);
    }

    [Fact(DisplayName = "Y-006 · Mekanizma izi dışlanır, gerçek proje transkripti geçer")]
    public void Y006_MechanismTranscriptsAreExcluded()
    {
        var flush = new Flush();
        Assert.True(flush.IsMechanismTranscript(@"C:\temp\oom\stage-compile\x.jsonl", @"D:\work\project"));
        Assert.False(flush.IsMechanismTranscript(@"D:\work\project\session.jsonl", @"D:\work\project"));
    }

    [Fact(DisplayName = "Y-007 · Yaş kapısı yalnız damgalanmış kaynağa uygulanır")]
    public void Y007_UnstampedOldTranscriptIsProcessed()
    {
        var sweep = new Sweep();
        var old = ScarFixture.Now.AddDays(-30);
        Assert.True(sweep.ShouldProcess(old, stamped: false, sinceHours: 8, ScarFixture.Now));
        Assert.False(sweep.ShouldProcess(old, stamped: true, sinceHours: 8, ScarFixture.Now));
    }

    [Fact(DisplayName = "Y-008 · Daily tarihi son turun olay zamanından gelir")]
    public void Y008_EventTimeComesFromLastTurn()
    {
        var yesterday = ScarFixture.Now.AddDays(-1).AddHours(-2);
        var session = ScarFixture.Session("dated", 3, lastTurnAt: yesterday);
        var actual = new Flush().EventTime(session, ScarFixture.Now.AddMinutes(-1), ScarFixture.Now);
        Assert.Equal(yesterday, actual);
        Assert.Equal("2026-09-08", actual.ToString("yyyy-MM-dd"));
    }

    [Fact(DisplayName = "Y-009 · Görev XML'inde süre yoktur ve son koşum dokuz saat içindedir")]
    public void Y009_ScheduledTaskOmitsDurationAndDoctorAcceptsWakeRun()
    {
        var xml = new Sweep().BuildScheduledTaskXml("oom.exe");
        Assert.DoesNotContain("Duration", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PT0S", xml, StringComparison.OrdinalIgnoreCase);
        var doctor = new Doctor().Check(ScarFixture.Now);
        Assert.DoesNotContain(doctor.Items, x => x.Code == "task-overdue");
    }

    [Fact(DisplayName = "Y-010 · Makine zarfları kullanıcı hafızasına özetlenmez")]
    public void Y010_MachineEnvelopeIsNotSummarized()
    {
        var transcript = "<task-notification>derleme makine anlatısı</task-notification>\nGerçek kullanıcı kararı";
        var result = new Runner().Run(transcript, ModelTier.Fast, ComponentKind.Flush, "summary");
        Assert.DoesNotContain("derleme makine anlatısı", result.Text);
        Assert.Contains("Gerçek kullanıcı kararı", result.Text);
    }

    [Fact(DisplayName = "Y-011 · Claude runner plan modundan yalıtılır ve biçimsiz yanıt fallback'e gider")]
    public void Y011_ClaudeRunnerIsIsolatedAndFallsBack()
    {
        var request = new Runner().BuildClaudeRequest("özetle", "claude-haiku-4-5-20251001", @"D:\vault", @"D:\vault\.oom\claude-config");
        Assert.Equal(@"D:\vault\.oom\claude-config", request.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.DoesNotContain(@"D:\vault", request.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        var validation = new Flush().ValidateSummary("<ExitPlanMode/>\n" + ScarFixture.ValidSummary(), "isolated");
        Assert.False(validation.Accepted);
        Assert.NotNull(validation.RejectionPath);
    }

    [Fact(DisplayName = "Y-012 · Çifte ret kaynağı tüketmez, kuyruğa alır ve bir kez bildirir")]
    public void Y012_RejectedSummaryDoesNotAdvanceCursor()
    {
        var retry = new Flush().Retry("retry-me", 4, "bozuk ham çıktı");
        Assert.True(retry.Parked);
        Assert.Equal(5, retry.Attempts);
        Assert.Equal(1, retry.Notifications);
        var result = new Flush().FlushSession("retry-me", "retry.jsonl", FlushReason.Sweep);
        Assert.Equal(0, result.Cursor);
    }

    [Fact(DisplayName = "Y-013 · Kayıp transkript session id ile yeniden bulunur")]
    public void Y013_MissingTranscriptIsRelocatedBySessionId()
    {
        var found = new Sweep().RelocateMissingTranscript("abc-13", "gone/session.jsonl", new Dictionary<string, string> { ["other/abc-13.jsonl"] = ScarFixture.TranscriptJsonl(ScarFixture.Session("abc-13", 3)) });
        Assert.Equal("abc-13", found.Id);
        Assert.Equal(3, found.Turns.Count);
    }

    [Fact(DisplayName = "Y-014 · Eşzamanlı daily yazıcıları iki bloğu da korur")]
    public void Y014_ConcurrentDailyAppendsLoseNoBlock()
    {
        var daily = new Flush().AppendDailyConcurrently("", ["blok-a", "blok-b"]);
        Assert.Contains("blok-a", daily);
        Assert.Contains("blok-b", daily);
        Assert.Equal(2, daily.Split("blok-", StringSplitOptions.None).Length - 1);
    }

    [Fact(DisplayName = "Y-015 · Sıfır yerleşme penceresi filtrelemez, pozitif pencere skipped sayar")]
    public void Y015_ZeroFreshWindowDisablesFiltering()
    {
        var future = ScarFixture.Now.AddMinutes(5);
        var disabled = new Sweep().EvaluateFreshness(future, freshSeconds: 0, ScarFixture.Now);
        var enabled = new Sweep().EvaluateFreshness(future, freshSeconds: 60, ScarFixture.Now);
        Assert.True(disabled.ShouldProcess);
        Assert.Equal(0, disabled.Skipped);
        Assert.False(enabled.ShouldProcess);
        Assert.Equal(1, enabled.Skipped);
    }

    [Fact(DisplayName = "Y-089 · BOM'lu hook girdisi okunur ve oom BOM yazmaz")]
    public void Y089_BomInputIsAcceptedButOutputHasNoBom()
    {
        var input = ScarFixture.WithBom("{\"id\":\"bom-session\"}");
        var parsed = new Flush().ReadHookInput(input);
        Assert.Equal("bom-session", parsed.Id);
        Assert.False(parsed.Status.StartsWith("\uFEFF", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Y-091 · Yeni tur yoksa model çağrısı yapılmaz")]
    public void Y091_NoNewTurnsSkipsModelCall()
    {
        var ranges = new Flush().PlanRanges(ScarFixture.Session("same", 3), lastTurnIndex: 2, maxTurns: 30, maxCharacters: 15_000);
        Assert.Empty(ranges);
        var result = new Flush().FlushSession("same", "same.jsonl", FlushReason.Sweep);
        Assert.Equal(FlushOutcome.NoNewTurns, result.Outcome);
    }

    [Fact(DisplayName = "Y-092 · MinTurns sayımı iki tavandan sonraki gerçek tur sayısını kullanır")]
    public void Y092_MinTurnsUsesPostCapTurnCount()
    {
        var ranges = new Flush().PlanRanges(ScarFixture.Session("caps", 5, 5_100), -1, 30, 15_000);
        var range = Assert.Single(ranges);
        Assert.Equal(2, range.Turns.Count);
        var result = new Flush().FlushSession("caps", "caps.jsonl", FlushReason.Sweep);
        Assert.Equal(FlushOutcome.NoTurns, result.Outcome);
    }

    [Fact(DisplayName = "Y-101 · save kontrol noktasını günlük dosyaya yazar ve geri okuyup doğrular")]
    public void Y101_CheckpointIsWrittenToDailyAndVerified()
    {
        var vault = ScarFixture.TempDirectory();
        try
        {
            const string text = "karar: sandık kapandı\ndüzeltme: yerel kütüphane exe içinde\ndevir: şerit FIX";
            var written = new Save().WriteCheckpointToVault(vault, text, ["karar", "düzeltme", "devir"], ScarFixture.Now);
            Assert.True(written.Written, written.Error);
            Assert.Null(written.Error);
            var daily = Path.Combine(vault, "daily", $"{ScarFixture.Now:yyyy-MM-dd}.md");
            Assert.True(File.Exists(daily), $"{daily} yazılmadı.");
            var body = File.ReadAllText(daily);
            Assert.Contains("karar: sandık kapandı", body);
            Assert.Contains("devir: şerit FIX", body);

            // A writer that cannot verify its own write never reports success.
            var refused = new Save(checkpointWriter: _ => false).WriteCheckpoint(text, ["karar", "düzeltme", "devir"]);
            Assert.False(refused.Written);
            Assert.NotNull(refused.Error);
        }
        finally { ScarFixture.Remove(vault); }
    }

    [Fact(DisplayName = "Y-102 · save metni komuttan sonraki ilk konumsal argümandır, --vault yutulmaz")]
    public void Y102_SaveTextIsTheFirstPositionalAfterTheCommand()
    {
        string[] plain = ["save", "karar: a"];
        string[] withVault = ["--vault", @"D:\kasa", "save", "karar: a"];
        Assert.Equal("save", CommandLine.Command(plain));
        Assert.Equal("save", CommandLine.Command(withVault));
        Assert.Equal("karar: a", CommandLine.Argument(plain, 0));
        Assert.Equal("karar: a", CommandLine.Argument(withVault, 0));
        Assert.Equal(@"D:\kasa", CommandLine.Value(withVault, "--vault"));
        Assert.Null(CommandLine.Argument(["--vault", @"D:\kasa", "save"], 0));
    }

    [Fact(DisplayName = "Y-106 · ingest kaynağı komuttan sonraki ilk konumsal argümandır, --vault yutulmaz")]
    public void Y106_IngestSourceIsTheFirstPositionalAfterTheCommand()
    {
        Assert.Equal("codex", CommandLine.Argument(["ingest", "codex"], 0) ?? "claude");
        Assert.Equal("codex", CommandLine.Argument(["--vault", @"D:\kasa", "ingest", "codex"], 0) ?? "claude");
        Assert.Equal("claude", CommandLine.Argument(["ingest"], 0) ?? "claude");

        var program = File.ReadAllText(Path.Combine(ScarFixture.RepositoryRoot(), "src", "Oom", "Program.cs"));
        Assert.Contains("var source = Argument(args, 0) ?? \"claude\";", program, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Y-107 · save --session-json bozuk dış sözleşmede çökmek yerine rc 1 döndürür")]
    public void Y107_SaveSessionJsonRejectsMalformedInputWithoutEscapingProgram()
    {
        const string malformed = """
            {"id":"x","source":"s","turns":[{"index":0,"role":"user","kind":"text","text":{"value":"a"}}]}
            """;
        Assert.Throws<FormatException>(() => new Save().SaveSessionJson(malformed));

        var vault = ScarFixture.TempDirectory();
        var sessionJson = Path.Combine(vault, "session.json");
        var previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            File.WriteAllText(sessionJson, malformed, new System.Text.UTF8Encoding(false));
            var program = typeof(Save).Assembly.GetType("Oom.Program", throwOnError: true)!;
            var runSave = program.GetMethod("RunSave", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            Console.SetError(error);

            var returnCode = (int)runSave.Invoke(null, [new[] { "save", "--session-json", sessionJson }, vault])!;

            Assert.Equal(1, returnCode);
            Assert.Contains("kayıt yazılmadı: Dış oturum JSON sözleşmesine uymuyor.", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(previousError);
            ScarFixture.Remove(vault);
        }
    }

    [Fact(DisplayName = "Y-112 · ingest yapılandırılmış sweep kökleri altındaki transkriptleri kendisi bulur")]
    public void Y112_IngestDiscoversTranscriptsUnderConfiguredRoots()
    {
        var tmp = ScarFixture.TempDirectory();
        try
        {
            var claudeProject = Path.Combine(tmp, "claude", "projects", "p1");
            var codexSessions = Path.Combine(tmp, "codex", "sessions");
            var subagents = Path.Combine(claudeProject, "subagents");
            Directory.CreateDirectory(claudeProject);
            Directory.CreateDirectory(codexSessions);
            Directory.CreateDirectory(subagents);

            var a = Path.Combine(claudeProject, "a.jsonl");
            var b = Path.Combine(claudeProject, "b.jsonl");
            var c = Path.Combine(codexSessions, "c.jsonl");
            var sidecar = Path.Combine(subagents, "agent-1.jsonl");

            // Sub-agent sidecars are all isSidechain, ClaudeParser rejects the turn-less result
            // (real machine: Y-112 crashed the whole batch on the oldest one until this excluded them).
            File.WriteAllText(sidecar,
                "{\"sessionId\":\"claude-fixed-a\",\"isSidechain\":true,\"type\":\"user\",\"timestamp\":\"2026-08-01T08:00:00+03:00\",\"message\":{\"content\":\"iç görev\"}}",
                new System.Text.UTF8Encoding(false));
            File.SetLastWriteTimeUtc(sidecar, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

            File.WriteAllText(a,
                "{\"sessionId\":\"claude-fixed-a\",\"type\":\"user\",\"timestamp\":\"2026-09-09T08:00:00+03:00\",\"message\":{\"content\":\"merhaba a\"}}\n" +
                "{\"sessionId\":\"claude-fixed-a\",\"type\":\"assistant\",\"timestamp\":\"2026-09-09T08:00:01+03:00\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"merhaba Master Mind\"}]}}",
                new System.Text.UTF8Encoding(false));
            File.WriteAllText(b,
                "{\"sessionId\":\"claude-fixed-b\",\"type\":\"user\",\"timestamp\":\"2026-09-09T09:00:00+03:00\",\"message\":{\"content\":\"merhaba b\"}}\n" +
                "{\"sessionId\":\"claude-fixed-b\",\"type\":\"assistant\",\"timestamp\":\"2026-09-09T09:00:01+03:00\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"merhaba Master Mind\"}]}}",
                new System.Text.UTF8Encoding(false));
            File.WriteAllText(c,
                "{\"type\":\"session_meta\",\"timestamp\":\"2026-09-09T08:00:00+03:00\",\"payload\":{\"id\":\"codex-fixed\",\"timestamp\":\"2026-09-09T08:00:00+03:00\"}}\n" +
                "{\"type\":\"event_msg\",\"timestamp\":\"2026-09-09T08:00:01+03:00\",\"payload\":{\"type\":\"user_message\",\"message\":\"merhaba\"}}",
                new System.Text.UTF8Encoding(false));

            // b is the older file even though its own timestamps are later — discovery orders by
            // last WRITE time (an archive backfill signal), not by content.
            File.SetLastWriteTimeUtc(b, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(a, new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(c, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

            var roots = new[] { Path.Combine(tmp, "claude", "projects"), Path.Combine(tmp, "codex", "sessions") };
            var ingest = new Ingest();

            var claudeFiles = ingest.Discover("claude", roots);
            Assert.Equal([b, a], claudeFiles);

            var codexFiles = ingest.Discover("codex", roots);
            Assert.Equal([c], codexFiles);

            var capped = ingest.Discover("claude", roots, max: 1);
            Assert.Equal([b], capped);

            var missing = ingest.Discover("claude", [Path.Combine(tmp, "kayip-kok")]);
            Assert.Empty(missing);

            var flushed = new List<string>();
            var driven = new Ingest(flushSession: (session, path) =>
            {
                flushed.Add(session.Id);
                return new FlushResult(FlushOutcome.Ok, 0, null, null);
            });
            var first = driven.RunWithOutcome("claude", claudeFiles);
            Assert.Equal(2, first.Sessions.Count);
            Assert.Equal(0, first.Skipped);
            Assert.Equal(["claude-fixed-b", "claude-fixed-a"], flushed);

            var second = driven.RunWithOutcome("claude", claudeFiles);
            Assert.Empty(second.Sessions);
            Assert.Equal(2, second.Skipped);
        }
        finally
        {
            ScarFixture.Remove(tmp);
        }
    }
}
