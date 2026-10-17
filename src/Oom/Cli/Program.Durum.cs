using System.Text.Json;
using Oom.Contracts;

namespace Oom;

internal static partial class Program
{
    private static int RunDurum(string[] args, string vault, OomSettings settings, DateTimeOffset now)
    {
        try
        {
            var (id, transcript) = (Value(args, "--session"), Value(args, "--transcript"));
            if (id is null && transcript is null)
            {
                var hook = HookPayload.Read(ReadStandardInput());
                (id, transcript) = (hook.SessionId, hook.TranscriptPath);
            }
            var (longMode, jsonMode) = (args.Contains("--uzun"), args.Contains("--json"));
            var r = Durum.Evaluate(vault, settings, now, id, transcript, longMode || jsonMode);
            if (jsonMode)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    durum = r.IsRed ? "KIRMIZI" : "yeşil", yesil = !r.IsRed, satir = r.Line, son_kayit = r.LastRecordText,
                    son_kayit_dk = r.LastRecordMinutes, kayitsiz = r.UnrecordedIds.Count,
                    kayitsiz_oturum = r.UnrecordedIds.Select(s => s[..Math.Min(8, s.Length)]).ToArray(),
                    kaydet_hatasi = r.RecentErrors, maske_tur = r.MaskedTurns, daily_dosya = r.DailyFiles,
                    daily_mb = Math.Round(r.DailyMegabytes, 2), eski_daily = r.OldDailyFiles,
                    tetikler = r.Triggers, toplam_hata = r.SevenDayErrors, ms = r.ElapsedMs
                }, HookJsonOptions));
            else Console.WriteLine(longMode ? Durum.FormatLong(r) : r.Line);
            return r.IsRed ? 1 : 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"[Durum] KIRMIZI · kaydet: {KaydetLog.ErrorText(error, 120)}");
            return 1;
        }
    }
}
