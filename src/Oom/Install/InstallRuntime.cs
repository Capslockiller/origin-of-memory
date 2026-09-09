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

        internal void Unregister(string name) => runner.Run(Request(["/Delete", "/TN", name, "/F"]), TimeSpan.FromSeconds(30));

        private static ProcessRequest Request(string[] arguments) =>
            new("schtasks.exe", arguments, Path.GetTempPath(), new Dictionary<string, string>(), string.Empty);
    }
}
