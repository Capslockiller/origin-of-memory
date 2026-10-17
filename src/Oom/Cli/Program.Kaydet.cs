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
        var hookDriven = trigger is "mesaj" or "compact" or "kapanis" or "acilis";
        string? id = trigger == "tara" ? null : Value(args, "--session");
        var result = new Kaydet.Result();
        Exception? failure = null;
        try
        {
            if (trigger is not ("mesaj" or "compact" or "kapanis" or "acilis" or "tara")) throw new ArgumentException("geçersiz tetik");
            var transcript = Value(args, "--transcript");
            if (trigger == "acilis" || trigger != "tara" && id is null && transcript is null)
            {
                var hook = HookPayload.Read(ReadStandardInput());
                id = hook.SessionId;
                transcript = hook.TranscriptPath;
            }
            if (settings.LoadError is { } error) throw new InvalidDataException(error);
            if (trigger != "tara" && (string.IsNullOrWhiteSpace(id) || trigger != "acilis" && string.IsNullOrWhiteSpace(transcript)))
                throw new ArgumentException("oturum ve transkript gerekli");
            if (trigger is "tara" or "acilis")
            {
                var days = 2;
                if (Value(args, "--gun") is { } value && (!int.TryParse(value, out days) || days < 1))
                    throw new ArgumentException("gun pozitif bir tamsayı olmalı");
                result = Kaydet.Scan(vault, settings, now, days, trigger == "acilis" ? id : null, trigger == "acilis" ? transcript : null);
                failure = result.Error;
            }
            else result = Kaydet.WriteResult(vault, id!, transcript!, trigger, now,
                onRead: (bytes, path) => result = result with { BytesRead = result.BytesRead + bytes, ReadPath = path });
        }
        catch (Exception error)
        {
            failure = error;
            result = result with { Outcome = "hata", Errors = result.Errors + 1, Error = error };
        }
        var log = KaydetLog.PathFor(vault);
        KaydetLog.Append(log, now, trigger, id, timer, result);
        if (!hookDriven)
        {
            if (trigger == "tara") Console.WriteLine($"tara: {result.Sessions} oturum, {result.Blocks} blok, {result.Errors} hata");
            if (failure is not null) Console.Error.WriteLine($"kaydet: {KaydetLog.ErrorText(failure, 200)}");
            return failure is null ? 0 : 1;
        }
        var message = failure is null ? null : $"kaydet: {KaydetLog.ErrorText(failure, 200)} (oturum {(string.IsNullOrEmpty(id) ? "-" : id[..Math.Min(8, id.Length)])})";
        var recent = trigger == "acilis" ? KaydetLog.RecentErrors(log, now) : 0;
        if (recent > 0) message = (message is null ? "" : message + "; ") + $"kaydet: son 24 saatte {recent} hata — {log}";
        if (message is not null)
        {
            if (trigger == "acilis") Console.WriteLine(JsonSerializer.Serialize(new
            {
                systemMessage = message,
                hookSpecificOutput = new { hookEventName = "SessionStart", additionalContext = "[Bildirim] " + message }
            }));
            else Console.WriteLine(JsonSerializer.Serialize(new { systemMessage = message }));
        }
        return 0;
    }
}
