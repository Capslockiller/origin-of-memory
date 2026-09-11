using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests;

/// <summary>
/// The transcript-to-model boundary. Flush gated the model's reply on its way into the vault
/// and gated nothing at all on the way out, so every secret the owner had ever pasted into a
/// conversation was rendered into the prompt and handed to an external model verbatim.
/// </summary>
public sealed class GonderimSiniriScars
{
    // Synthetic canaries: the shape is real, the value is invented. Never a live credential.
    private const string AnthropicCanary = "sk-ant-api03-SENTETIKKANARYASAHTEANAHTAR0123456789";
    private const string GithubCanary = "ghp_SENTETIKKANARYA0123456789ABCDEFGH";

    [Fact(DisplayName = "Y-126 · Özet istemi makineden çıkmadan önce sır kapısından geçer")]
    public void Y126_OutboundPromptIsGatedBeforeItLeavesTheMachine()
    {
        var sessionId = "y126-" + Guid.NewGuid().ToString("N")[..8];
        var directory = ScarFixture.TempDirectory();
        var transcriptPath = Path.Combine(directory, sessionId + ".jsonl");
        File.WriteAllText(transcriptPath, Transcript(sessionId));

        // The real Runner, with only the child process faked: what the recorder captures is the
        // stdin `claude -p` would have been fed, which is the byte stream that leaves the machine.
        var process = new RecordingProcess(JsonSerializer.Serialize(new { result = ScarFixture.ValidSummary() }));
        var chain = new Dictionary<ComponentKind, IReadOnlyList<string>> { [ComponentKind.Flush] = ["claude"] };
        var runner = new Runner(process, null, null, null, "http://127.0.0.1:11434/v1", true, chain);
        var notifier = new RecordingNotifier();

        try
        {
            var result = new Flush(new FlushOptions(), null, runner, null, notifier)
                .FlushSession(sessionId, transcriptPath, FlushReason.Sweep);

            var sent = process.StandardInput;
            Assert.NotNull(sent);

            // The whole point: the bytes bound for the model carry no secret.
            Assert.DoesNotContain(AnthropicCanary, sent, StringComparison.Ordinal);
            Assert.DoesNotContain(GithubCanary, sent, StringComparison.Ordinal);
            Assert.Contains("[SIR:anthropic-key]", sent, StringComparison.Ordinal);
            Assert.Contains("[SIR:github-token]", sent, StringComparison.Ordinal);

            // Redaction, not refusal: the conversation still reaches the model and the session
            // still becomes memory — only the credential is replaced by its class.
            Assert.Contains("dağıtım anahtarını", sent, StringComparison.Ordinal);
            Assert.Equal(FlushOutcome.Ok, result.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(result.Summary));

            // Not silent: a masked credential is announced, the way a parked session is.
            Assert.Contains(notifier.Messages, message => message.Contains("sır maskelendi", StringComparison.Ordinal));

            // Not lossy either: the unmasked original never left the local raw channel or disk.
            Assert.Contains(AnthropicCanary, File.ReadAllText(transcriptPath), StringComparison.Ordinal);

            // The vault-side gate is an addition away from, not a move: a secret that reaches
            // Flush in the model's reply is still masked on its way into the daily file.
            var reply = new Guards().Gate(
                ScarFixture.ValidSummary() + "\nKalan anahtar: " + AnthropicCanary, Direction.Out, ComponentKind.Flush);
            Assert.DoesNotContain(AnthropicCanary, reply.Text, StringComparison.Ordinal);
            Assert.Contains("secret", reply.Findings);
        }
        finally
        {
            ScarFixture.Remove(directory);
        }
    }

    private static string Transcript(string sessionId)
    {
        var start = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.FromHours(3));
        string[] texts =
        [
            "Dün akşam dağıtım anahtarını yanlışlıkla sohbete yapıştırdım: " + AnthropicCanary,
            "Depo jetonu da aynı mesajdaydı: " + GithubCanary + " — ikisini de döndürmek gerekiyor.",
            "İkisini de iptal ettim; yeni anahtarlar yalnız ortam değişkeninde duruyor."
        ];

        return string.Join('\n', texts.Select((text, index) => JsonSerializer.Serialize(new
        {
            session_id = sessionId,
            index,
            role = index % 2 == 0 ? "user" : "assistant",
            kind = "text",
            text,
            timestamp = start.AddMinutes(index).ToString("O")
        })));
    }

    private sealed class RecordingProcess(string standardOutput) : IProcessRunner
    {
        public string? StandardInput { get; private set; }

        public ProcessResult Run(ProcessRequest request, TimeSpan timeout)
        {
            StandardInput = request.StandardInput;
            return new ProcessResult(0, standardOutput, string.Empty, true);
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<string> Messages { get; } = [];

        public void Notify(string text) => Messages.Add(text);
    }
}
