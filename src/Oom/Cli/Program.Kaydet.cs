using System.Diagnostics;
using System.Text.Json;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunKaydet(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        var timer = Stopwatch.StartNew();
        var trigger = Value(args, "--tetik") ?? "kapanis";
        if (trigger == "acilis")
        {
            var acilisMessage = RunAcilis(vault, settings, now, gunArg: Value(args, "--gun"), fromAcilisCommand: true);
            var acilisLog = KaydetLog.PathFor(vault);
            var recent = KaydetLog.RecentErrors(acilisLog, now);
            if (recent > 0) acilisMessage = (acilisMessage is null ? "" : acilisMessage + "; ") + $"kaydet: son 24 saatte {recent} hata — {acilisLog}";
            if (acilisMessage is not null)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    systemMessage = acilisMessage,
                    hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = "[Bildirim] " + acilisMessage }
                }));
            return 0;
        }
        var hookDriven = trigger is "mesaj" or "compact" or "kapanis";
        string? id = trigger == "tara" ? null : Value(args, "--session");
        var result = new Kaydet.Result();
        Exception? failure = null;
        try
        {
            if (trigger is not ("mesaj" or "compact" or "kapanis" or "tara")) throw new ArgumentException("geçersiz tetik");
            var transcript = Value(args, "--transcript");
            if (trigger != "tara" && id is null && transcript is null)
            {
                var hook = HookPayload.Read(ReadStandardInput());
                (id, transcript) = (hook.SessionId, hook.TranscriptPath);
            }
            if (settings.LoadError is { } error) throw new InvalidDataException(error);
            if (trigger != "tara" && (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(transcript)))
                throw new ArgumentException("oturum ve transkript gerekli");
            if (trigger == "tara")
            {
                var days = 2;
                if (Value(args, "--gun") is { } value && (!int.TryParse(value, out days) || days < 1))
                    throw new ArgumentException("gun pozitif bir tamsayı olmalı");
                result = Kaydet.Scan(vault, settings, now, days);
                failure = result.Error;
            }
            else result = Kaydet.WriteResult(vault, id!, transcript!, trigger, now);
        }
        catch (Exception error)
        {
            failure = error;
            result = result with { Outcome = "hata", Errors = result.Errors + 1, Error = error };
        }
        KaydetLog.Append(KaydetLog.PathFor(vault), now, trigger, id, timer, result);
        if (!hookDriven)
        {
            if (trigger == "tara") Console.WriteLine($"tara: {result.Sessions} oturum, {result.Blocks} blok, {result.Errors} hata");
            if (failure is not null) Console.Error.WriteLine($"kaydet: {KaydetLog.ErrorText(failure, 200)}");
            return failure is null ? 0 : 1;
        }
        if (FailureMessage(failure, id) is { } message) Console.WriteLine(JsonSerializer.Serialize(new { systemMessage = message }));
        return 0;
    }

    internal static string? RunAcilis(string vault, OomSettings settings, DateTimeOffset now, string? id = null, string? transcript = null, string? gunArg = null, bool fromAcilisCommand = false)
    {
        var timer = Stopwatch.StartNew();
        var result = new Kaydet.Result();
        Exception? failure = null;
        try
        {
            if (fromAcilisCommand)
            {
                var hook = HookPayload.Read(ReadStandardInput());
                (id, transcript) = (hook.SessionId, hook.TranscriptPath);
            }
            if (settings.LoadError is { } error) throw new InvalidDataException(error);
            if (fromAcilisCommand && string.IsNullOrWhiteSpace(id)) throw new ArgumentException("oturum ve transkript gerekli");
            var days = 2;
            if (gunArg is not null && (!int.TryParse(gunArg, out days) || days < 1))
                throw new ArgumentException("gun pozitif bir tamsayı olmalı");
            result = Kaydet.Scan(vault, settings, now, days, id ?? string.Empty, transcript);
            failure = result.Error;
        }
        catch (Exception error)
        {
            failure = error;
            result = result with { Outcome = "hata", Errors = result.Errors + 1, Error = error };
        }
        KaydetLog.Append(KaydetLog.PathFor(vault), now, "acilis", id, timer, result);
        return FailureMessage(failure, id);
    }

    private static string? FailureMessage(Exception? failure, string? id) =>
        failure is null ? null : $"kaydet: {KaydetLog.ErrorText(failure, 200)} (oturum {(string.IsNullOrEmpty(id) ? "-" : id[..Math.Min(8, id.Length)])})";
}
