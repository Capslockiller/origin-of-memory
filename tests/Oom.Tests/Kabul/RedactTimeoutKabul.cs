using System.Reflection;
using System.Text;
using Oom.Contracts;

namespace Oom.Tests.Kabul;

/// <summary>
/// Oracle acceptance tests for lane L5e-redact-timeout (SPEC-3.1.0.md, external review
/// finding #7, verified REAL/minor/latent by an Opus judge on da8138d): a Redactor regex
/// match timeout falls back to a fail-closed masked result (<c>Mark, 1</c>) that is
/// indistinguishable from an ordinary single-secret match, so Flush commits it into
/// daily/ as if masking had succeeded. Written independently by the lane's oracle author
/// — the implementer never sees this file's reasoning, only the assertions below.
///
/// Every test here is UNIT-LEVEL, not process-boundary via KabulHarness. The finding's own
/// fix (assertion 1) explicitly requires "an injected tiny regex timeout (a
/// constructor/test seam on Redactor, not a process-wide setting)" — by the driver's own
/// wording the seam must NOT be a process-wide/CLI-observable setting, so the behaviour it
/// gates is, by design, not observable at the process boundary (no CLI flag or env var
/// exists — or should exist — to force a Redactor timeout from outside the process). A
/// black-box attempt to reach the same 1-second production MatchTimeout through crafted
/// transcript content was considered and rejected: every current Redactor pattern is
/// already hardened against catastrophic backtracking (see Redactor.cs's own "measured
/// 143 s ... fixed" comment on UrlCredentials), so no realistically-sized flush summary
/// reaches a genuine 1-second match under the CURRENT patterns — only a seam-injected,
/// abnormally short timeout can make the fail-closed branch fire deterministically and
/// fast. Consequently OOM_KABUL_EXE has no effect on the tests in this file (none of them
/// spawn a child oom.exe); see the lane's proof notes for what was run against the old
/// exe instead.
///
/// This file depends on TWO test seams that do not exist on this tree yet (named here per
/// the driver's instruction to name unavoidable not-yet-existing dependencies):
///   1. A Redactor constructor taking a single TimeSpan (the match timeout), e.g.
///      `public Redactor(TimeSpan matchTimeout)`.
///   2. A Flush constructor parameter of type Redactor (mirroring the existing
///      Guards/Runner/IClock/State injection points), e.g. adding `Redactor? redactor =
///      null` to the existing constructor.
/// Both are located via reflection (see TryCreateTimeoutRedactor / TryInjectRedactorIntoFlush
/// below) rather than called directly, so this file COMPILES against the current tree
/// (where neither seam exists) and FAILS at run time with a real, readable xUnit
/// assertion failure instead of a build break — a compile error is not proof a behaviour
/// is missing, a failing assertion is.
/// </summary>
public sealed class RedactTimeoutKabul
{
    private static readonly UTF8Encoding Utf8 = new(false);

    // ------------------------------------------------------------------
    // Reflection seams — see the class doc comment above for why these
    // exist instead of direct calls.
    // ------------------------------------------------------------------

    private static (Redactor? Instance, string? Failure) TryCreateTimeoutRedactor(TimeSpan timeout)
    {
        var ctor = typeof(Redactor).GetConstructor([typeof(TimeSpan)]);
        if (ctor is null)
        {
            return (null,
                "Redactor üzerinde tek bir System.TimeSpan parametresi alan bir kurucu " +
                "(eşleşme zaman aşımı test dikişi) yok. Assertion 1: 'an injected tiny regex " +
                "timeout (a constructor/test seam on Redactor, not a process-wide setting)'. " +
                "Beklenen: public Redactor(TimeSpan matchTimeout).");
        }

        return ((Redactor)ctor.Invoke([timeout]), null);
    }

    private static (bool Value, string? Failure) TryReadTimedOut(RedactionResult result)
    {
        var property = typeof(RedactionResult).GetProperty("TimedOut");
        if (property is null || property.PropertyType != typeof(bool))
        {
            return (false,
                "RedactionResult üzerinde bool 'TimedOut' özelliği yok. Assertion 1: " +
                "'Redactor reports TimedOut=true in its result'.");
        }

        return ((bool)property.GetValue(result)!, null);
    }

    /// <summary>
    /// Builds a Flush wired to <paramref name="redactor"/> by probing every public
    /// constructor for one with a Redactor-typed parameter, then filling every OTHER
    /// parameter by matching its TYPE (not its position) against the values given here.
    /// Robust to the implementer choosing any parameter order/name for the new seam, and
    /// to any default value for parameters this test does not care about.
    /// </summary>
    private static (Flush? Instance, string? Failure) TryInjectRedactorIntoFlush(
        FlushOptions options, Runner runner, State state, Redactor redactor)
    {
        var ctor = typeof(Flush).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(Redactor)));
        if (ctor is null)
        {
            return (null,
                "Flush'ın hiçbir genel kurucusunda Redactor tipinde bir parametre yok; test, " +
                "kısa zaman aşımlı özel bir Redactor'ı Flush'a enjekte edemiyor (assertion 2 " +
                "için gereken dikiş). Beklenen: mevcut Flush kurucusuna 'Redactor? redactor = " +
                "null' gibi isteğe bağlı bir parametre eklenmesi (Guards/Runner/IClock/State " +
                "ile aynı desende).");
        }

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            args[i] =
                type == typeof(FlushOptions) ? options :
                type == typeof(Runner) ? runner :
                type == typeof(State) ? state :
                type == typeof(Redactor) ? redactor :
                parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }

        return ((Flush)ctor.Invoke(args), null);
    }

    private static string LargeFillerText(int approximateLength)
    {
        const string seed =
            "Bu satır L5e-redact-timeout kabul testi için üretilen sentetik doldurma metnidir, " +
            "gerçek bir içerik ya da sır taşımaz. ";
        var builder = new StringBuilder(approximateLength + seed.Length);
        while (builder.Length < approximateLength)
            builder.Append(seed);
        return builder.ToString();
    }

    // ------------------------------------------------------------------
    // Assertion 1 — Redactor itself: an injected tiny timeout reports
    // TimedOut=true and still returns the fail-closed masked text.
    // ------------------------------------------------------------------

    [Fact(DisplayName =
        "Assertion 1 · enjekte edilen çok kısa zaman aşımıyla Mask() TimedOut=true döner ve çıktı tamamen maskeli kalır")]
    public void Mask_WhenMatchTimesOut_ReportsTimedOutTrue_AndStaysFullyMasked()
    {
        var (redactor, ctorFailure) = TryCreateTimeoutRedactor(TimeSpan.FromMilliseconds(1));
        if (redactor is null)
        {
            Assert.Fail(ctorFailure);
            return;
        }

        // Large enough that the regex engine's periodic timeout check fires well before any
        // pattern finishes scanning it, regardless of which pattern runs first — no
        // catastrophic-backtracking trick needed, sheer length is enough against a 1 ms budget.
        var text = LargeFillerText(300_000);

        var result = redactor.Mask(text);

        var (timedOut, timedOutFailure) = TryReadTimedOut(result);
        if (timedOutFailure is not null)
        {
            Assert.Fail(timedOutFailure);
            return;
        }

        Assert.True(timedOut,
            "Redactor.Mask, enjekte edilen 1 ms'lik zaman aşımıyla gerçek bir " +
            "RegexMatchTimeoutException yakalamalıydı ve TimedOut=true dönmeliydi.");

        // The fail-closed behaviour ITSELF already exists on the current tree (review
        // finding: the DEFECT is that this branch is indistinguishable from a normal
        // single-secret mask, not that it fails to mask) — this half of the assertion is a
        // regression guard, not expected to fail now.
        Assert.Equal(1, result.Count);
        Assert.DoesNotContain("sentetik doldurma metnidir", result.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Assertion 2 — Flush: a flush whose summary masking times out is
    // quarantined like a refused summary (byte-identical daily/, red/ +
    // .reason naming 'maskeleme zaman aşımı', outcome wired to 'refused',
    // one stderr line, and a health row the session-start notice reads).
    // ------------------------------------------------------------------

    [Fact(DisplayName =
        "Assertion 2 · özet maskelemesi zaman aşımına uğrayan flush 'maskeleme zaman aşımı' ile karantinaya alınır, günlük değişmez, outcome 'refused' ve tek stderr satırı")]
    public void FlushSession_WhenSummaryMaskingTimesOut_QuarantinesWithTimeoutReason()
    {
        var (redactor, redactorFailure) = TryCreateTimeoutRedactor(TimeSpan.FromMilliseconds(1));
        if (redactor is null)
        {
            Assert.Fail(redactorFailure);
            return;
        }

        var root = Path.Combine(AppContext.BaseDirectory, "l5e-redact-timeout-runs", Guid.NewGuid().ToString("N"));
        var vaultDir = Path.Combine(root, "vault");
        var stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(vaultDir);
        Directory.CreateDirectory(stateDir);
        var dailyDir = Path.Combine(vaultDir, "daily");

        try
        {
            using var state = new State(null, null, Path.Combine(stateDir, "state.db"));

            var sessionId = "l5e-timeout-" + Guid.NewGuid().ToString("N")[..8];
            var options = new FlushOptions(
                MinTurns: 3,
                MaxTurns: 30,
                MaxCharacters: 200_000,
                VaultPath: vaultDir,
                RejectionPath: stateDir);

            // configured:false makes Runner.Attempt fail with the "yapılandırma yok" marker,
            // which Flush.FlushSession already falls back to ExtractiveSummary(range) for
            // (existing behaviour, no test seam needed) — a deterministic, five-heading
            // summary built straight from the turn text below, with no subprocess involved.
            var runner = new Runner((IProcessRunner?)null, null, configured: false);

            var baseTime = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(3));
            var turns = Enumerable.Range(0, 8)
                .Select(i => new Turn(i, i % 2 == 0 ? "user" : "assistant", "text",
                    $"Tur {i}: " + LargeFillerText(15_000), baseTime.AddMinutes(i)))
                .ToArray();
            var session = new Session(sessionId, "claude", turns, baseTime);

            var (flush, flushFailure) = TryInjectRedactorIntoFlush(options, runner, state, redactor);
            if (flush is null)
            {
                Assert.Fail(flushFailure);
                return;
            }

            var originalError = Console.Error;
            var captured = new StringWriter();
            FlushResult result;
            try
            {
                Console.SetError(captured);
                result = flush.FlushSession(session, "unit://l5e-redact-timeout", FlushReason.SessionEnd);
            }
            finally
            {
                Console.SetError(originalError);
            }

            // daily/ stays byte-identical: Commit() only ever creates vault\daily on an Ok
            // outcome, and no daily directory existed before this call, so its continued
            // absence IS byte-identical (there is nothing to diff against — the strongest
            // form of "unchanged").
            Assert.False(Directory.Exists(dailyDir),
                $"maskeleme zaman aşımına rağmen daily/ dizini oluşturuldu: {dailyDir}");

            Assert.Equal(FlushOutcome.Refused, result.Outcome);
            Assert.Equal("maskeleme zaman aşımı", result.Error);

            // The exact wire name flush_log stores (Program.Flush.cs: FlushOutcomes.Wire(result.Outcome)).
            Assert.Equal("refused", FlushOutcomes.Wire(result.Outcome));

            var redDirectory = Path.Combine(stateDir, "red");
            Assert.True(Directory.Exists(redDirectory), $"red/ dizini yok: {redDirectory}");
            var redCandidates = Directory.GetFiles(redDirectory, $"*{sessionId}*");
            var mainFiles = redCandidates.Where(f => !f.EndsWith(".reason", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.True(mainFiles.Length >= 1,
                $"'{sessionId}' için red/ dosyası yok: {string.Join(", ", redCandidates)}");

            var reasonFiles = redCandidates.Where(f => f.EndsWith(".reason", StringComparison.OrdinalIgnoreCase)).ToArray();
            var reasonFile = Assert.Single(reasonFiles);
            Assert.Equal("maskeleme zaman aşımı", File.ReadAllText(reasonFile, Utf8).Trim());

            // The session-start notice path (Program.Context.cs's QuarantineNotifications)
            // reads exactly this table/column/code contract (State.RecordQuarantine's own doc
            // comment: "The session-start notice reads these rows") — querying it the same
            // way proves this timeout-refusal is visible to that path like any other refused
            // summary, without depending on Program.Context.cs's private method directly.
            var quarantineRows = state.Scalar(
                "SELECT COUNT(*) FROM health WHERE component = 'flush' AND code = 'karantina' " +
                $"AND key = '{sessionId}' AND detail LIKE '%maskeleme zaman aşımı%'");
            Assert.True(quarantineRows >= 1,
                "session-start bildirim yolunun okuduğu health satırı ('flush'/'karantina', " +
                "sebep 'maskeleme zaman aşımı') yazılmadı.");

            var stderrLines = captured.ToString()
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(stderrLines.Length == 1,
                $"tam olarak bir stderr satırı bekleniyordu, {stderrLines.Length} alındı: " +
                string.Join(" | ", stderrLines));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------
    // Assertion 3 — no behaviour change when masking does not time out.
    // ------------------------------------------------------------------

    [Fact(DisplayName =
        "Assertion 3a · zaman aşımı olmadığında bilinen üç sır biçimi eskisi gibi maskelenir (new Redactor())")]
    public void Mask_WithoutTimeout_KnownSecretShapesStillMasked_DefaultConstructor()
    {
        // Regression guard using the EXISTING, unchanged Redactor() overload — deliberately
        // NOT expected to fail on the current tree; three shapes RedactorKabul already
        // covers (S3 prefixed token, B7 keyword+separator+backtick password, S3/B6
        // hex-near-keyword), pinned again here so this lane's own file documents the "no
        // regression" contract it depends on.
        var ghToken = "ghp_" + string.Concat(Enumerable.Range(0, 36).Select(i => "0123456789abcdefghijklmnopqrstuvwxyz"[i % 36]));
        var hex48 = string.Concat(Enumerable.Range(0, 48).Select(i => "0123456789abcdef"[i % 16]));
        var redactor = new Redactor();

        var r1 = redactor.Mask($"kayıt: {ghToken}");
        Assert.DoesNotContain(ghToken, r1.Text, StringComparison.Ordinal);
        Assert.Contains("ghp_****(maskelendi)", r1.Text, StringComparison.Ordinal);

        var r2 = redactor.Mask("şifre: `hunter22x`");
        Assert.DoesNotContain("hunter22x", r2.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", r2.Text, StringComparison.Ordinal);

        var r3 = redactor.Mask($"MERCAN paneli anahtarı: {hex48}");
        Assert.DoesNotContain(hex48, r3.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", r3.Text, StringComparison.Ordinal);
    }

    [Fact(DisplayName =
        "Assertion 3b · seam'li Redactor normal (üretim) zaman aşımıyla kurulduğunda TimedOut=false döner ve maskeleme değişmez")]
    public void Mask_WithSeamConstructorAtProductionTimeout_BehavesLikeDefault()
    {
        var (redactor, failure) = TryCreateTimeoutRedactor(TimeSpan.FromSeconds(1));
        if (redactor is null)
        {
            Assert.Fail(failure);
            return;
        }

        var hex48 = string.Concat(Enumerable.Range(0, 48).Select(i => "0123456789abcdef"[i % 16]));
        var result = redactor.Mask($"MERCAN paneli anahtarı: {hex48} — normal metin, zaman aşımı yok.");

        Assert.DoesNotContain(hex48, result.Text, StringComparison.Ordinal);
        Assert.Contains("maskelendi", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, result.Count);

        var (timedOut, timedOutFailure) = TryReadTimedOut(result);
        if (timedOutFailure is not null)
        {
            Assert.Fail(timedOutFailure);
            return;
        }

        Assert.False(timedOut, "zaman aşımına uğramayan normal bir eşleşmede TimedOut=true dönmemeli.");
    }
}
