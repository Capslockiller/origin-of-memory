// yazan: opus · lane D2, D3 merge
using System.Text;

namespace Oom.Contracts;

/// <summary>
/// The one machine boundary <see cref="Install"/> needs that no other component owns: the
/// <c>schtasks</c> registrar. The clock, the process runner and the state schema are INT-2's
/// (<see cref="SystemClock"/>, <see cref="WindowsProcessRunner"/>, <see cref="State"/>), so lane
/// D2's private copies of all three were deleted here rather than kept in parallel.
/// </summary>
internal static class InstallRuntime
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>What an ownership-checked task removal actually did. Never "removed" on a guess.</summary>
    internal enum TaskRemoval
    {
        /// <summary>No such task is registered; there was nothing to remove.</summary>
        Absent,

        /// <summary>The task exists but its registered action could not be read: left alone.</summary>
        Unreadable,

        /// <summary>The task exists and runs somebody else's binary: left alone.</summary>
        ForeignTarget,

        /// <summary>The task ran our executable and <c>schtasks /Delete</c> succeeded.</summary>
        Removed,

        /// <summary>The task was ours but <c>schtasks /Delete</c> failed; it is still registered.</summary>
        Failed
    }

    internal sealed class SchtasksScheduler : ITaskScheduler
    {
        private readonly IProcessRunner runner;

        internal SchtasksScheduler(IProcessRunner? runner = null) => this.runner = runner ?? new WindowsProcessRunner();

        public void Register(string name, string xml)
        {
            var path = Path.Combine(Path.GetTempPath(), $"oom-task-{Guid.NewGuid():N}.xml");
            File.WriteAllText(path, xml, Utf8);
            try
            {
                var result = runner.Run(Request(["/Create", "/TN", name, "/XML", path, "/F"]), TimeSpan.FromSeconds(30));
                if (result.ExitCode != 0) throw new InvalidOperationException("Zamanlanmış görev kaydedilemedi.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>
        /// Deletes the task only after the machine has said which binary it runs. The task name is
        /// a constant with no vault in it: it is the machine's name for a registration, never proof
        /// that this vault put it there. A second product, a second vault or a hand-made task under
        /// the same name would otherwise be deleted by any vault's <c>--uninstall</c>.
        /// </summary>
        internal TaskRemoval UnregisterOwned(string name, string ownedExecutable)
        {
            var query = runner.Run(Request(["/Query", "/TN", name, "/XML"]), TimeSpan.FromSeconds(30));
            if (query.ExitCode != 0) return TaskRemoval.Absent;
            if (RegisteredCommand(query.StandardOutput) is not { } command) return TaskRemoval.Unreadable;
            if (!InstallOwnership.PathsEqual(command, ownedExecutable)) return TaskRemoval.ForeignTarget;
            var deleted = runner.Run(Request(["/Delete", "/TN", name, "/F"]), TimeSpan.FromSeconds(30));
            return deleted.ExitCode == 0 ? TaskRemoval.Removed : TaskRemoval.Failed;
        }

        /// <summary>
        /// The <c>&lt;Command&gt;</c> of the task's first <c>Exec</c> action. Read by substring and
        /// not by an XML parser on purpose: <c>schtasks /XML</c> emits UTF-16 with a BOM and the
        /// occasional trailing banner line, and a parser that throws on the banner would report
        /// "unreadable" for a task we could in fact attribute.
        /// </summary>
        internal static string? RegisteredCommand(string? taskXml)
        {
            if (string.IsNullOrWhiteSpace(taskXml)) return null;
            const string open = "<Command>";
            const string close = "</Command>";
            var start = taskXml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += open.Length;
            var end = taskXml.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            var value = taskXml[start..end].Trim()
                .Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal)
                .Replace("&quot;", "\"", StringComparison.Ordinal)
                .Replace("&amp;", "&", StringComparison.Ordinal);
            // Task XML is allowed to quote the command; the machine stores it either way.
            if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            return value.Length == 0 ? null : Environment.ExpandEnvironmentVariables(value);
        }

        private static ProcessRequest Request(string[] arguments) =>
            new("schtasks.exe", arguments, Path.GetTempPath(), new Dictionary<string, string>(), string.Empty);
    }
}
