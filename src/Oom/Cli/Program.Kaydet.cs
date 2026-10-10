using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunKaydet(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        if (settings.LoadError is { } error) throw new InvalidDataException(error);
        var trigger = Value(args, "--tetik") ?? "kapanis";
        if (trigger is not ("mesaj" or "compact" or "kapanis" or "tara")) throw new ArgumentException("geçersiz tetik");
        if (trigger == "tara")
        {
            var result = Kaydet.Scan(vault, settings, now);
            Console.WriteLine($"tara: {result.Sessions} oturum, {result.Blocks} blok, {result.Errors} hata");
            if (result.Errors > 0) Console.Error.WriteLine("kaydet: taramada hata");
            return result.Errors > 0 ? 1 : 0;
        }
        var id = Value(args, "--session");
        var transcript = Value(args, "--transcript");
        if (id is null && transcript is null)
        {
            var hook = HookPayload.Read(ReadStandardInput());
            id = hook.SessionId;
            transcript = hook.TranscriptPath;
        }
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(transcript)) throw new ArgumentException("oturum ve transkript gerekli");
        Kaydet.Write(vault, id, transcript, trigger, now);
        return 0;
    }
}
