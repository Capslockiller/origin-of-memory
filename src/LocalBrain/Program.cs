// yazan: codex
// model: gpt-5.6-sol
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("Local Brain")]
[assembly: AssemblyDescription("yazan: codex · model: gpt-5.6-sol")]
[assembly: AssemblyCompany("Origin of Memory")]
[assembly: AssemblyVersion("0.1.0.0")]

namespace OriginOfMemory.LocalBrain
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string scriptPath = Path.Combine(baseDirectory, "beyin.ps1");
                if (!File.Exists(scriptPath))
                {
                    throw new FileNotFoundException("beyin.ps1 must be beside LocalBrain.exe.", scriptPath);
                }

                string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string powerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powerShell))
                {
                    powerShell = "powershell.exe";
                }

                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = powerShell;
                start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath.Replace("\"", "\\\"") + "\"";
                start.WorkingDirectory = baseDirectory;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;
                start.WindowStyle = ProcessWindowStyle.Hidden;

                Process process = Process.Start(start);
                if (process == null)
                {
                    throw new InvalidOperationException("The Local Brain process did not start.");
                }
                process.Dispose();
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    error.Message,
                    "Local Brain could not start",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
        }
    }
}
