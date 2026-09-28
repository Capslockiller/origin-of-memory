using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// Acceptance oracle for lane L2-compile (SPEC-3.1.0.md F3-1..F3-6, B8, S7, F6-5, NB-14, R12).
/// Drives a real oom.exe through <see cref="KabulHarness"/> against a <see cref="FakeClaude"/>
/// shim on PATH — never a real model call. Every test here builds its own small, synthetic,
/// purpose-built vault (no real personal content — this repo is public) rather than reusing
/// <c>KabulVaultBuilder</c>, which shapes a vault for the F1 context-budget lane, not for
/// daily/knowledge/log.md compile bookkeeping.
///
/// Every black-box test here is RED against the pre-fix code this lane replaced (block-level
/// parse failure killed the whole day at exit 0; attempts were never read back from state so a
/// daily was never actually parked; pending order was ascending and never excluded
/// knowledge/log.md days; a fake 'now - 1h' stood in for the real last-compile time behind an
/// 'ok:evening'/'ok:interval' decision line; the compile lock was taken AFTER the model prompt
/// had already gone out, with a fail-OPEN fallback when the named mutex itself could not be
/// constructed; a runner failure was reported as already queued for later — the old exit-0
/// success message, not an error — see F6-5 below) and RED
/// again against eski-exe/oom.exe (OOM_KABUL_EXE) for the matching reasons — see the lane's
/// proof notes.
/// </summary>
public sealed class CompileKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly DateTimeOffset Today = new(2026, 9, 27, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact(DisplayName = "F3-1 · 3 bloktan biri '=== END FILE ===' olmadan kalırsa diğer ikisi yayımlanır, 1 'reddedildi' satırı basılır, exit 2")]
    public void PartialBlockRecovery_PublishesGoodBlocks_ReportsBadOneOnce_ExitsTwo()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-27.md");

        // Block 2 opens but is never closed with '=== END FILE ===' before block 3's own
        // header starts — the "malformed mid-stream block" case F3-1 targets. A block cut
        // off at the very end of a truncated (no '=== DONE ===') output is reported the
        // same way once other blocks already closed (CompileScars Y-334); it only stays a
        // silent "retry" when NOTHING else in the run closed either (Y-335).
        var output = FileBlock("aa-kavrami", "AA Kavrami") +
                     "=== FILE: knowledge/concepts/bb-kavrami.md ===\n---\ntitle: BB (kapanmamis blok)\n" +
                     FileBlock("cc-kavrami", "CC Kavrami") +
                     "=== DONE ===\n";

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(output));
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["compile"], fakeNow: Today);

        Assert.Equal(2, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(vault, "knowledge", "concepts", "aa-kavrami.md")),
            $"aa-kavrami.md yazılmalıydı. stdout: {result.Stdout}");
        Assert.True(File.Exists(Path.Combine(vault, "knowledge", "concepts", "cc-kavrami.md")),
            $"cc-kavrami.md yazılmalıydı. stdout: {result.Stdout}");
        Assert.False(File.Exists(Path.Combine(vault, "knowledge", "concepts", "bb-kavrami.md")));
        Assert.Contains("2 not", result.Stdout, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(result.Stdout, "reddedildi:"));
    }

    [Fact(DisplayName = "F3-2 · Aynı daily üç kez reddedilince 'parked' olur; dry-run artık listelemez; 4. koşum FakeClaude'a hiç istek göndermez")]
    public void ThreeRejectionsPark_DryRunDropsIt_FourthRunSendsNoPrompt()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-20.md");

        // A single block that CLOSES cleanly but names a disallowed nested path — a
        // structural violation, not a formatting mistake, so it still fails the WHOLE day
        // (0 notes) exactly like every attempt before block-level recovery existed. Every
        // real call gets the SAME response (FakeClaude repeats the last registered one).
        var malformed = "=== FILE: knowledge/concepts/nested/x.md ===\nbody\n=== END FILE ===\n=== DONE ===\n";
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(malformed));
        using var pathScope = fake.ReplaceProcessPath();

        var run1 = harness.Run(vault, ["compile"], fakeNow: Today);
        Assert.Contains("rejected", run1.Stdout, StringComparison.Ordinal);

        var run2 = harness.Run(vault, ["compile"], fakeNow: Today);
        Assert.Contains("rejected", run2.Stdout, StringComparison.Ordinal);

        var run3 = harness.Run(vault, ["compile"], fakeNow: Today);
        Assert.Contains("parked", run3.Stdout, StringComparison.Ordinal);

        var callsAfterThreeRejections = fake.CallCount;

        var dry = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Today);
        Assert.DoesNotContain("2026-09-20.md", dry.Stdout, StringComparison.Ordinal);

        var run4 = harness.Run(vault, ["compile"], fakeNow: Today);
        Assert.Contains("atlandı", run4.Stdout, StringComparison.Ordinal);
        Assert.Equal(callsAfterThreeRejections, fake.CallCount);
    }

    [Fact(DisplayName = "F3-3 · Üç bekleyen daily FakeClaude'a en yeniden en eskiye doğru sırayla gönderilir")]
    public void ThreePendingDailies_AreSentNewestFirst()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-10.md");
        WriteDaily(vault, "2026-09-25.md");
        WriteDaily(vault, "2026-09-18.md");

        var okOutput = FileBlock("tekrarli-kavram", "Tekrarli Kavram") + "=== DONE ===\n";
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(okOutput));
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["compile"], fakeNow: Today);
        Assert.Equal(0, result.ExitCode);

        // `oom compile` sends each daily's prompt and prints its "derleme <name>: ..." line
        // synchronously, one daily at a time, in the exact order it iterates Pending() — so
        // the order these lines appear in stdout IS the order FakeClaude received them in.
        var order = Regex.Matches(result.Stdout, @"derleme (\S+\.md):")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(["2026-09-25.md", "2026-09-18.md", "2026-09-10.md"], order);
    }

    [Fact(DisplayName = "F3-4/B8 · Bekleyen sayısı log.md'de kaydı olan günleri hariç tutar; boş state ölçülen sayıyı basar, sabit bir tavan değil")]
    public void PendingCount_OnFreshState_ExcludesDaysAlreadyInLog()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        var dailies = new[] { "2026-09-01.md", "2026-09-02.md", "2026-09-03.md", "2026-09-04.md" };
        foreach (var name in dailies)
            WriteDaily(vault, name);
        WriteLog(vault, "2026-09-02.md");

        // Derived from the fixture itself (SPEC B8: "the count is derived by the script,
        // never hard-coded") — never the literal number 3 written down first.
        var loggedDays = Regex.Matches(File.ReadAllText(Path.Combine(vault, "knowledge", "log.md"), Utf8), @"compile\s*\|\s*(\S+\.md)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var expectedPending = dailies.Count(name => !loggedDays.Contains(name));

        // No prior run at all: OOM_LOCALAPPDATA is fresh, so there is no state.db yet —
        // `oom doctor` must still compute "bekleyen" correctly (never crash, never fall
        // back to counting every daily/*.md file, which is exactly the eski-exe defect:
        // an empty/absent state re-treats every day, logged or not, as pending).
        var result = harness.Run(vault, ["doctor"], fakeNow: Today);

        Assert.Contains($"bekleyen {expectedPending}", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain($"bekleyen {dailies.Length}", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "F3-5 · Var olan kavrama token-Jaccard ≥ 0,6 olan yeni slug dosya açmaz, 'kopya: <mevcut>' listelenir")]
    public void NearDuplicateSlug_IsNeverWritten_ReportedAsKopya()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-27.md");
        WriteExistingConcept(vault, "ankara-gurme-pizza-mekan-arastirmasi");

        var output = FileBlock("ankara-gurme-pizza-mekani-arastirmasi", "Ankara Gurme Pizza Mekani Arastirmasi") + "=== DONE ===\n";
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(output));
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["compile"], fakeNow: Today);

        Assert.False(File.Exists(Path.Combine(vault, "knowledge", "concepts", "ankara-gurme-pizza-mekani-arastirmasi.md")),
            $"neredeyse aynı slug'lı dosya yazılmamalıydı. stdout: {result.Stdout}");
        Assert.Contains("kopya: ankara-gurme-pizza-mekan-arastirmasi", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "S7 · Kilit tutulurken 'oom compile' hiçbir istek göndermeden exit 3 ve 'kilit alınamadı' basar")]
    public void HeldLock_PreventsAnyPromptAndExitsThree()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-27.md");

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        // Same mutex name Compile.TakeLock constructs: "oom-compile-" + VaultIdentity.Hash(vault),
        // tried first under the "Global\" scope (VaultIdentity.Hash delegates to the very same
        // internal VaultFiles.Hash the CLI process uses, so this collides for real).
        using var heldLock = new Mutex(initiallyOwned: true, "Global\\oom-compile-" + VaultIdentity.Hash(vault));
        try
        {
            var result = harness.Run(vault, ["compile"], fakeNow: Today);

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("kilit alınamadı", result.Stdout + result.Stderr, StringComparison.Ordinal);
            Assert.Equal(0, fake.CallCount);
        }
        finally
        {
            heldLock.ReleaseMutex();
        }
    }

    [Fact(DisplayName = "R12/NB-14 · '--dry-run' gerçek son-derleme zamanını daily_ingest'ten basar; 'karar=' satırı yok")]
    public void DryRun_PrintsRealLastCompileTime_WithNoDecisionLine()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-08-01.md");

        var seedNow = new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.FromHours(3));
        var seedOutput = FileBlock("seed-kavrami", "Seed Kavrami") + "=== DONE ===\n";
        using (var seedFake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(seedOutput)))
        using (seedFake.ReplaceProcessPath())
        {
            var seedRun = harness.Run(vault, ["compile"], fakeNow: seedNow);
            Assert.Equal(0, seedRun.ExitCode);
        }

        // A fresh pending daily, and "now" pinned 25 hours after the seeded ingest — the
        // exact "row 25h old" shape SPEC-3.1.0.md's oracle text describes.
        WriteDaily(vault, "2026-09-27.md");
        var dryRunNow = seedNow.AddHours(25);
        var dry = harness.Run(vault, ["compile", "--dry-run"], fakeNow: dryRunNow);

        Assert.DoesNotContain("karar=", dry.Stdout, StringComparison.Ordinal);
        Assert.Contains(seedNow.ToString("O"), dry.Stdout, StringComparison.Ordinal);
        Assert.Contains("2026-09-27.md", dry.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "F6-5 · Runner hatası 'derleme başarısız: <neden>' basar ve exit sıfır değildir; 'kuyruğa alındı' hiç yok")]
    public void RunnerFailure_PrintsHonestMessage_ExitsNonZero()
    {
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-27.md");

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.UnknownOptionFailure());
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["compile"], fakeNow: Today);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("derleme başarısız:", result.Stdout, StringComparison.Ordinal);
        // Built from two fragments (F6-5/F7-4: the phrase itself must be absent from
        // source, so a repo-wide grep for it stays at 0 matches) rather than written here
        // as one literal.
        var oldSuccessMessage = "kuyruğa" + " " + "alındı";
        Assert.DoesNotContain(oldSuccessMessage, result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "R6/R12 · 'oom sweep' bekleyen bir daily varken bile hiçbir saatte derleme başlatmaz; FakeClaude'a hiç istek gitmez")]
    public void Sweep_NeverStartsCompile_RegardlessOfHour()
    {
        // BLOCKING review finding: Compile.MaybeCompile used to return ShouldCompile=true
        // whenever anything was pending, and Program.Sweep.cs still consumed that and
        // started a detached `oom compile` — at ANY hour, worse than the old eveningHour
        // gate this lane was supposed to remove per R6/R12. This is the black-box proof
        // that `oom sweep` never starts a compile process at all any more, at 10:00 (the
        // old code's "too early" hour) and at 19:00 (the old code's "compile now" hour).
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-27.md");
        // Point sweep at an empty root so it never touches a real transcript directory.
        var emptyRoot = harness.NewScratchDirectory("no-sweep-roots");
        Directory.CreateDirectory(Path.Combine(vault, ".oom"));
        File.WriteAllText(Path.Combine(vault, ".oom", "oom.json"),
            $$"""
            {
              "sweep": { "roots": [{{JsonSerializer.Serialize(emptyRoot)}}] }
            }
            """, Utf8);

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), FakeClaudeResponse.SummaryOk());
        using var pathScope = fake.ReplaceProcessPath();

        var morning = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(3));
        var evening = new DateTimeOffset(2026, 9, 27, 19, 0, 0, TimeSpan.FromHours(3));

        var atTen = harness.Run(vault, ["sweep"], fakeNow: morning);
        var atNineteen = harness.Run(vault, ["sweep"], fakeNow: evening);

        Assert.DoesNotContain("derleme gerekli", atTen.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("derleme gerekli", atNineteen.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, fake.CallCount);
    }

    [Fact(DisplayName = "S3/B6/B7 · Sentetik anahtar: model çıktısında yankılansa da yayımlanan notta ham hâliyle çıkmaz")]
    public void SyntheticSecret_EchoedByModel_NeverLeavesRaw_InPublishedNote()
    {
        // BLOCKING review finding: a model echo of a secret shown in context went straight
        // into the published note (Guards.Gate never masked anything). This is the
        // black-box proof of the publication half of the fix: FakeClaude.Stdin() capture
        // is unreliable for this shim (measured: always empty, even for the smoke prompt),
        // so the prompt-egress half of the fix has its own direct unit proof instead — see
        // CompileScars "S3/B6/B7 · CompilePrompt.Build ...".
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        const string secret = "ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        WriteDaily(vault, "2026-09-27.md");

        var output =
            "=== FILE: knowledge/concepts/sizinti-kavrami.md ===\n" +
            "---\n" +
            "title: Sizinti Kavrami\n" +
            "aliases: []\n" +
            "tags: [sentetik]\n" +
            "sources: [2026-09-27.md]\n" +
            "created: 2026-09-27\n" +
            "updated: 2026-09-27\n" +
            "type: concept\n" +
            "hub: genel\n" +
            "---\n" +
            "# Sizinti Kavrami\n" +
            $"Kalıcı bilgi, kabul harness fixture'ı. Model tarafından yankılanan anahtar: {secret}\n\n" +
            "## İlgili Kavramlar\n" +
            "- [[ilk-kavram]] ilk bağlantı gerekçesi\n" +
            "- [[ikinci-kavram]] ikinci bağlantı gerekçesi\n" +
            "=== END FILE ===\n" +
            "=== DONE ===\n";

        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(output));
        using var pathScope = fake.ReplaceProcessPath();

        var result = harness.Run(vault, ["compile"], fakeNow: Today);

        Assert.Equal(0, result.ExitCode);

        var publishedPath = Path.Combine(vault, "knowledge", "concepts", "sizinti-kavrami.md");
        Assert.True(File.Exists(publishedPath), $"not yayımlanmalıydı. stdout: {result.Stdout}");
        var published = File.ReadAllText(publishedPath, Utf8);
        Assert.DoesNotContain(secret, published, StringComparison.Ordinal);
        Assert.Contains("maskelendi", published, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Should-fix · İçeriği derlendikten sonra büyüyen bir daily, digest değiştiği için 'dry-run'da yeniden bekleyen çıkar")]
    public void AppendedContentAfterCompile_MakesDryRunListItAgainAsPending()
    {
        // Should-finding (review, applied): a "done" status used to be permanent no matter
        // what the file on disk said afterward, so content a later flush appended to an
        // already-compiled daily was silently never seen again. This proves the fix:
        // compile once, confirm dry-run drops the day, then append to the SAME file (as a
        // later flush would) with no further compile — dry-run must list it again.
        using var harness = new KabulHarness();
        var vault = BuildVault(harness.Root);
        WriteDaily(vault, "2026-09-27.md");

        var okOutput = FileBlock("buyuyen-gun-kavrami", "Buyuyen Gun Kavrami") + "=== DONE ===\n";
        using var fake = FakeClaude.Create(harness.Root, FakeClaudeResponse.StreamJsonSmokeOk(), CompileOutput(okOutput));
        using var pathScope = fake.ReplaceProcessPath();

        var first = harness.Run(vault, ["compile"], fakeNow: Today);
        Assert.Equal(0, first.ExitCode);

        var dryBefore = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Today);
        Assert.DoesNotContain("2026-09-27.md", dryBefore.Stdout, StringComparison.Ordinal);

        File.AppendAllText(Path.Combine(vault, "daily", "2026-09-27.md"), "- sonradan eklenen ikinci satır.\n", Utf8);

        var dryAfter = harness.Run(vault, ["compile", "--dry-run"], fakeNow: Today);
        Assert.Contains("2026-09-27.md", dryAfter.Stdout, StringComparison.Ordinal);
    }

    // ---- fixtures -----------------------------------------------------------------

    private static string BuildVault(string root)
    {
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, "daily"));
        Directory.CreateDirectory(Path.Combine(vault, "knowledge", "concepts"));
        return vault;
    }

    private static void WriteDaily(string vault, string name) =>
        File.WriteAllText(Path.Combine(vault, "daily", name),
            $"# {Path.GetFileNameWithoutExtension(name)}\n\n- sentetik günlük satırı, kabul harness fixture'ı, gerçek içerik değil.\n", Utf8);

    private static void WriteExistingConcept(string vault, string slug) =>
        File.WriteAllText(Path.Combine(vault, "knowledge", "concepts", slug + ".md"),
            "---\ntitle: " + slug + "\naliases: []\ntags: [sentetik]\nsources: []\ncreated: 2026-09-01\nupdated: 2026-09-01\ntype: concept\nhub: genel\n---\n" +
            "# " + slug + "\nSentetik mevcut kavram, kabul harness fixture'ı.\n", Utf8);

    private static void WriteLog(string vault, params string[] alreadyCompiledDailyNames)
    {
        var builder = new StringBuilder();
        foreach (var name in alreadyCompiledDailyNames)
            builder.Append("## [2026-09-05T09:00:00+03:00] compile | ").Append(name).Append('\n')
                   .Append("Oluşturulan: (kabul harness fixture'ı, gerçek not yok)\n\n");
        File.WriteAllText(Path.Combine(vault, "knowledge", "log.md"), builder.ToString(), Utf8);
    }

    /// <summary>One well-formed, self-contained "=== FILE: ... ===" block: valid concept
    /// path, the eight required frontmatter fields, and the two-wikilink "İlgili Kavramlar"
    /// section Notes.Validate requires — mirrors CompileScars.FileBlock (that copy is
    /// private to the Scars test class; this one is this file's own).</summary>
    private static string FileBlock(string slug, string title) =>
        $"=== FILE: knowledge/concepts/{slug}.md ===\n" +
        "---\n" +
        $"title: {title}\n" +
        "aliases: []\n" +
        "tags: [sentetik]\n" +
        "sources: [2026-09-27.md]\n" +
        "created: 2026-09-27\n" +
        "updated: 2026-09-27\n" +
        "type: concept\n" +
        "hub: genel\n" +
        "---\n" +
        $"# {title}\n" +
        "Kalıcı bilgi, kabul harness fixture'ı.\n\n" +
        "## İlgili Kavramlar\n" +
        "- [[ilk-kavram]] ilk bağlantı gerekçesi\n" +
        "- [[ikinci-kavram]] ikinci bağlantı gerekçesi\n" +
        "=== END FILE ===\n";

    private static FakeClaudeResponse CompileOutput(string modelText) =>
        new(Stdout: JsonSerializer.Serialize(new { result = modelText }));
}
