using System.Runtime.InteropServices;

namespace Oom.Contracts;

/// <summary>
/// The one place the product starts a process that outlives its parent (spec 6.1). A hook has
/// 15 seconds and the summary takes minutes, so <c>flush</c> re-launches itself with
/// <c>DETACHED_PROCESS | CREATE_NO_WINDOW</c> and returns inside 200 ms. .NET's
/// <c>ProcessStartInfo</c> cannot ask for those creation flags, so this is the only P/Invoke
/// on the write path; every handle is an <see cref="IntPtr"/>, never a truncated 32 bit value
/// (Y-097), and both handles the kernel hands back are closed.
/// </summary>
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

    /// <summary>Starts the child detached; returns its process id, or 0 when it could not start.</summary>
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
