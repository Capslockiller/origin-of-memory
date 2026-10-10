using System.Security.Cryptography;
using System.Text;

namespace Oom.Contracts;

internal static class TurSiniflandirma
{
    private static readonly string[] Harness =
    [
        "<system-reminder", "<command-", "<task-notification", "<local-command-",
        "Another Claude session sent a message", "[SYSTEM NOTIFICATION", "<agent-",
        "<subagent", "<recommended_plugins", "<environment_context", "<permissions instructions>"
    ];

    internal static string Classify(string role, string kind, string text) =>
        kind is "tool_use" or "tool_result" or "thinking" or "system" ? "t" :
        kind is "envelope" or "compact" || Harness.Any(p => text.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? "h" :
        role == "assistant" ? "a" : "u";

    internal static string Id(string sessionId, int index) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sessionId + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant();
}
