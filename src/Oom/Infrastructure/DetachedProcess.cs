using System.Runtime.InteropServices;

namespace Oom.Contracts;

internal static class DetachedProcess
{
    private const uint DetachedProcessFlag = 0x00000008;
    private const uint CreateNoWindowFlag = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int cb;
        internal IntPtr lpReserved, lpDesktop, lpTitle;
        internal int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        internal short wShowWindow, cbReserved2;
        internal IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr hProcess, hThread;
        internal int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    internal static int Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var commandLine = string.Join(' ', new[] { executablePath }.Concat(arguments).Select(Quote));
        var startup = new StartupInfo();
        startup.cb = Marshal.SizeOf<StartupInfo>();

        if (!CreateProcessW(executablePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                DetachedProcessFlag | CreateNoWindowFlag | CreateUnicodeEnvironment, IntPtr.Zero,
                Directory.Exists(workingDirectory) ? workingDirectory : null, ref startup, out var information))
            return 0;

        var id = information.dwProcessId;
        CloseHandle(information.hThread);
        CloseHandle(information.hProcess);
        return id;
    }

    private static string Quote(string value) =>
        value.Length > 0 && !value.Contains(' ', StringComparison.Ordinal) ? value : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
