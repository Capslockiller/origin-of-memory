using System.Diagnostics;
using System.Text.Json;

namespace Oom.Contracts;

/// <summary>One user-level hook registration (Spec 6.1).</summary>
public sealed record HookRegistration(string Event, string Command, int TimeoutSeconds);

/// <summary>
/// The four user-level <c>settings.json</c> entries. Install writes them, doctor --fix repairs
/// them, and the duplicate detection here is the data lane D's Doctor reports (10.1 #6).
/// </summary>
public static class HookTemplates
{
    /// <summary>Builds the four registrations with an absolute exe path; no PowerShell, no Python.</summary>
    public static IReadOnlyList<HookRegistration> Build(string executablePath) =>
    [
        new("SessionStart", Quote(executablePath) + " context", 15),
        new("UserPromptSubmit", Quote(executablePath) + " retrieve --hook", 5),
        new("SessionEnd", Quote(executablePath) + " flush --reason sessionend", 15),
        new("PreCompact", Quote(executablePath) + " flush --reason precompact", 15)
    ];

    /// <summary>The settings.json fragment that install merges into the user-level file.</summary>
    public static string Render(string executablePath)
    {
        var hooks = Build(executablePath).ToDictionary(
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

    /// <summary>
    /// Events registered at both user and project level fire twice and write two daily blocks; the
    /// pairs are returned as data, the report belongs to doctor.
    /// </summary>
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

    /// <summary>
    /// The hook process re-launches itself detached (DETACHED_PROCESS | CREATE_NO_WINDOW) and
    /// returns inside 200 ms; the summary is written by the child, the hook never blocks the user.
    /// </summary>
    public static int LaunchDetached(string executablePath, string sessionId, FlushReason reason)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true
        };

        start.ArgumentList.Add("flush");
        start.ArgumentList.Add("--session");
        start.ArgumentList.Add(sessionId);
        start.ArgumentList.Add("--reason");
        start.ArgumentList.Add(reason == FlushReason.PreCompact ? "precompact" : "sessionend");
        start.Environment["OOM_INVOKED_BY"] = "hook";

        using var child = Process.Start(start);
        child?.StandardInput.Close();
        return child?.Id ?? 0;
    }

    private static string Quote(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
