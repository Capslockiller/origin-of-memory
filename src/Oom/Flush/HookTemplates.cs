using System.Text.Json;

namespace Oom.Contracts;

public sealed record HookRegistration(string Event, string Command, int TimeoutSeconds);

public static class HookTemplates
{
    public static IReadOnlyList<HookRegistration> Build(string executablePath, string vault)
    {
        var prefix = Quote(executablePath) + " --vault " + Quote(vault);
        return
        [
            new("SessionStart", prefix + " context", 15),
            new("UserPromptSubmit", prefix + " nudge", 5),
            new("SessionEnd", prefix + " flush --reason sessionend", 15),
            new("PreCompact", prefix + " flush --reason precompact", 15)
        ];
    }

    public static string Render(string executablePath, string vault)
    {
        var hooks = Build(executablePath, vault).ToDictionary(
            registration => registration.Event,
            registration => new[]
            {
                new
                {
                    hooks = new[]
                    {
                        new { type = "command", command = registration.Command, timeout = registration.TimeoutSeconds }
                    }
                }
            });

        return JsonSerializer.Serialize(new { hooks }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static IReadOnlyList<string> Duplicates(string userSettings, string projectSettings)
    {
        var duplicates = new List<string>();
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "SessionEnd", "PreCompact" })
        {
            var marker = $"\"{name}\"";
            if (userSettings.Contains(marker, StringComparison.Ordinal) && projectSettings.Contains(marker, StringComparison.Ordinal))
                duplicates.Add(name);
        }

        return duplicates;
    }

    public static int LaunchDetached(string executablePath, string sessionId, FlushReason reason, string? transcriptPath = null)
    {
        List<string> arguments =
        [
            "flush", "--detached",
            "--session", sessionId,
            "--reason", reason == FlushReason.PreCompact ? "precompact" : "sessionend"
        ];

        if (!string.IsNullOrEmpty(transcriptPath))
            arguments.AddRange(["--transcript", transcriptPath]);

        return DetachedProcess.Start(executablePath, arguments, Path.GetTempPath());
    }

    private static string Quote(string path)
    {
        var forward = path.Replace('\\', '/');
        return forward.Contains(' ', StringComparison.Ordinal) ? $"\"{forward}\"" : forward;
    }
}
