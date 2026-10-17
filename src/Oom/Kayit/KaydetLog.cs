using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Oom.Contracts;

internal static class KaydetLog
{
    internal static string PathFor(string vault) => Path.Combine(VaultFiles.StateRoot(vault), "kaydet.jsonl");

    internal static string ErrorText(Exception error, int cap)
    {
        var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
        return error.GetType().Name + ": " + message[..Math.Min(cap, message.Length)];
    }

    internal static void Append(string path, DateTimeOffset now, string trigger, string? sessionId, Stopwatch timer, Kaydet.Result result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var held = Retry(() => new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                File.Move(path, Path.Combine(Path.GetDirectoryName(path)!, "kaydet.1.jsonl"), overwrite: true);
            using var stream = Retry(() => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            var row = new Dictionary<string, object?>
            {
                ["ts"] = now.ToString("O"), ["tetik"] = trigger, ["oturum"] = sessionId,
                ["ms"] = (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds), ["sonuc"] = result.Outcome,
                ["blok"] = result.Blocks, ["hata"] = result.Error is null ? null : Cap(ErrorText(result.Error, 300), 300),
                ["okunan_kb"] = (int)Math.Min(int.MaxValue, (result.BytesRead + 1023) / 1024)
            };
            if (trigger is "acilis" or "tara")
            {
                row["aday"] = result.Sessions;
                row["atlanan"] = result.Skipped;
                row["hata_sayisi"] = result.Errors;
            }
            else row["yol"] = result.ReadPath;
            writer.Write(JsonSerializer.Serialize(row) + "\n");
        }
        catch (Exception error) { Console.Error.WriteLine("kaydet log: " + ErrorText(error, 200)); }
    }

    private static string Cap(string text, int cap) => text[..Math.Min(cap, text.Length)];

    private static T Retry<T>(Func<T> open)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return open(); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && attempt < 20) { Thread.Sleep(10); }
        }
    }

    internal static int RecentErrors(string path, DateTimeOffset now)
    {
        var count = 0;
        // Rotation must not hide a failure from the last 24 hours.
        foreach (var file in new[] { Path.Combine(Path.GetDirectoryName(path)!, "kaydet.1.jsonl"), path }.Where(File.Exists))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var row = doc.RootElement;
                        if (HookPayload.Field(row, "sonuc") == "hata" && DateTimeOffset.TryParse(HookPayload.Field(row, "ts"), out var ts)
                            && ts >= now.AddHours(-24) && ts <= now) count++;
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception) { }
        }
        return count;
    }
}
