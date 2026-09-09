using System.Runtime.InteropServices;
using System.Text;

namespace Oom.Contracts;

/// <summary>
/// Prepares the isolated <c>CLAUDE_CONFIG_DIR</c> of spec 6.6. The whole point of the
/// directory is that <c>claude -p</c> cannot see the user's hooks, skills or plan mode —
/// that inheritance rejected 21% of v0's flushes (Y-011, scar 10.1 #31) — so it holds an
/// empty <c>settings.json</c> and nothing else but the session credential.
///
/// On Windows, Claude Code 2.1.x keeps its OAuth credential in <c>.credentials.json</c>
/// inside the config directory (there is no keychain), so an isolated directory without it
/// is unauthenticated and every call fails. The credential is therefore *linked*: a hard
/// link when the two paths share a volume, a copy when they do not (the vault may be on
/// another drive). Either way the copy is refreshed whenever the source is newer, so a
/// token the user's own Claude Code refreshed reaches the isolated directory on the next
/// run. Nothing is ever written back into the user's own configuration directory, and no
/// credential value is read, logged or printed by this code.
/// </summary>
internal static class ClaudeIsolation
{
    private const string Credentials = ".credentials.json";

    private static readonly UTF8Encoding Utf8 = new(false);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>Creates or refreshes the directory and reports what it did, in one Turkish word.</summary>
    internal static string Prepare(string configDirectory)
    {
        try
        {
            Directory.CreateDirectory(configDirectory);
            var settings = Path.Combine(configDirectory, "settings.json");
            if (!File.Exists(settings))
                File.WriteAllText(settings, "{}\n", Utf8);

            var source = Path.Combine(UserConfigDirectory(), Credentials);
            var target = Path.Combine(configDirectory, Credentials);
            if (!File.Exists(source))
                return File.Exists(target) ? "mevcut" : "kimlik yok";

            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source))
                return "güncel";

            if (File.Exists(target))
                File.Delete(target);

            if (SameVolume(source, target) && CreateHardLinkW(target, source, IntPtr.Zero))
                return "bağlandı";

            File.Copy(source, target, overwrite: true);
            return "kopyalandı";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A config directory that cannot be prepared is a runner error, never a crash:
            // the call fails, the session queues, and doctor reports it.
            return "hazırlanamadı";
        }
    }

    /// <summary>The user's own Claude Code configuration directory; read only, never written.</summary>
    internal static string UserConfigDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static bool SameVolume(string first, string second) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(first)), Path.GetPathRoot(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}
