using System.Text.Json;
using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class NudgeScars
{
    [Fact(DisplayName = "Y-300 · nudge on beşinci mesajda hatırlatır, aradaki on dördünde susar")]
    public void Y300_NudgeSpeaksOnEveryFifteenthPromptAndNotBetween()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            using var state = new State(null, null, Path.Combine(root, "state.db"));
            var session = "y300-" + Guid.NewGuid().ToString("N")[..8];
            var spoken = new List<int>();

            for (var prompt = 1; prompt <= 30; prompt++)
                if (Nudge.Count(state, session, ScarFixture.Now, every: 15) is not null)
                    spoken.Add(prompt);

            Assert.Equal([15, 30], spoken);

            var envelope = Nudge.Envelope(Nudge.Reminder(15));
            var additional = JsonDocument.Parse(envelope).RootElement
                .GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();
            Assert.Equal("[Memory] prompt 15. Before the session ends, update 🔮 850-Companion/Last-Session.md and Threads.md.", additional);

            Assert.Equal(string.Empty, Nudge.Envelope(null));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }

    [Fact(DisplayName = "Y-318 · nudge her mesajda saat satırı basar: yeni oturum, alt dakika, kırk dakikalık ara, iki günlük ara")]
    public void Y318_NudgePrintsAClockLineOnEveryPrompt()
    {
        var root = ScarFixture.TempDirectory();
        try
        {
            using var state = new State(null, null, Path.Combine(root, "state.db"));
            var session = "y318-" + Guid.NewGuid().ToString("N")[..8];
            var start = new DateTimeOffset(2026, 9, 12, 16, 40, 0, TimeSpan.FromHours(3));

            Assert.Equal("[Zaman] 2026-09-12 16:40 · oturum şimdi başladı · 1. mesaj",
                Nudge.Lines(state, session, start, every: 15));

            Assert.Equal("[Zaman] 2026-09-12 23:42 · oturum 16:40'ta başladı (7 sa 2 dk) · son mesaj 7 sa 2 dk önce · 7 sa 2 dk ara · 2. mesaj",
                Nudge.Lines(state, session, start.AddHours(7).AddMinutes(2), every: 15));

            Assert.Equal("[Zaman] 2026-09-12 23:48 · oturum 16:40'ta başladı (7 sa 8 dk) · son mesaj 6 dk önce · 3. mesaj",
                Nudge.Lines(state, session, start.AddHours(7).AddMinutes(8), every: 15));

            Assert.Equal("[Zaman] 2026-09-13 00:28 · oturum 16:40'ta başladı (7 sa 48 dk) · son mesaj 40 dk önce · 40 dk ara · 4. mesaj",
                Nudge.Lines(state, session, start.AddHours(7).AddMinutes(48), every: 15));

            Assert.Equal("[Zaman] 2026-09-15 03:28 · oturum 16:40'ta başladı (2 gün 10 sa) · son mesaj 2 gün 3 sa önce · 2 gün 3 sa ara · 5. mesaj",
                Nudge.Lines(state, session, start.AddDays(2).AddHours(10).AddMinutes(48), every: 15));

            var fresh = "y318b-" + Guid.NewGuid().ToString("N")[..8];
            Nudge.Lines(state, fresh, start, every: 15);
            Assert.Equal("[Zaman] 2026-09-12 16:40 · oturum şimdi başladı · son mesaj az önce · 2. mesaj",
                Nudge.Lines(state, fresh, start.AddSeconds(20), every: 15));

            var every = new List<string>();
            for (var prompt = 3; prompt <= 15; prompt++)
                every.Add(Nudge.Lines(state, fresh, start.AddMinutes(prompt), every: 15));

            Assert.All(every.Take(12), line => Assert.DoesNotContain("[Memory]", line, StringComparison.Ordinal));
            Assert.Equal("[Memory] prompt 15. Before the session ends, update 🔮 850-Companion/Last-Session.md and Threads.md.",
                every[^1].Split('\n')[1]);
            Assert.StartsWith("[Zaman] ", every[^1], StringComparison.Ordinal);
            Assert.True(every[^1].Split('\n')[0].Length < 120);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            ScarFixture.Remove(root);
        }
    }
}
