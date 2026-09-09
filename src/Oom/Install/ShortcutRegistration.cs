// yazan: opus · lane D2
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace Oom.Contracts;

/// <summary>
/// Toast prerequisites for an unpackaged executable (D3): a Start-menu shortcut carrying
/// AppUserModelID <c>OdenaStudio.OriginOfMemory</c>. Windows refuses to raise a toast for a
/// process with no AUMID, so the shortcut *is* the registration; its presence is therefore the
/// only honest answer to "can this machine show a toast?" and <see cref="Notify"/> reads it.
/// Registration is best effort: when it fails the install still completes and notifications fall
/// back to the SessionStart line (spec 6.8, 6.11).
/// </summary>
public static class ShortcutRegistration
{
    public const string ApplicationUserModelId = "OdenaStudio.OriginOfMemory";
    private const string EventLogSource = "oom";
    private const string EventLogKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\oom";

    /// <summary><c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Origin of Memory.lnk</c>.</summary>
    public static string ShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "Origin of Memory.lnk");

    /// <summary>True when a toast can carry an identity; false means SessionStart-only notification.</summary>
    public static bool IsRegistered
    {
        get
        {
            try { return File.Exists(ShortcutPath()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>Writes the shortcut and stamps the AUMID on it. Never throws; false means "no toast".</summary>
    public static bool TryRegister(string executable)
    {
        try
        {
            Register(executable);
            return File.Exists(ShortcutPath());
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException
            or InvalidCastException or NotSupportedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Deletes the shortcut; <c>--uninstall</c> removes AUMID and shortcut together.</summary>
    public static bool TryRemove()
    {
        try
        {
            var path = ShortcutPath();
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The Event Log source lives under HKLM and its creation needs elevation. Reading the key
    /// does not, so an installer that is already elevated (or a machine where a previous elevated
    /// run created it) registers it; every other run skips it and <c>logs\</c> suffices — spec
    /// 6.11's own fallback. Nothing is claimed in <c>InstallResult.Registrations</c> unless this
    /// returns true.
    /// </summary>
    public static bool TryRegisterEventLogSource()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using (var existing = Registry.LocalMachine.OpenSubKey(EventLogKey))
                if (existing is not null) return true;
            using var created = Registry.LocalMachine.CreateSubKey(EventLogKey, writable: true);
            if (created is null) return false;
            created.SetValue("EventMessageFile", Path.Combine(Environment.SystemDirectory, "EventCreate.exe"), RegistryValueKind.ExpandString);
            created.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Removes the Event Log source when this process may; a leftover key is harmless.</summary>
    public static bool TryRemoveEventLogSource()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var parent = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application", writable: true);
            if (parent?.OpenSubKey(EventLogSource) is null) return false;
            parent.DeleteSubKeyTree(EventLogSource, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    private static void Register(string executable)
    {
        var path = ShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)(object)new ShellLink();
        link.SetPath(executable);
        link.SetDescription("Origin of Memory");
        // PKEY_AppUserModel_ID — the property Windows reads to attribute a toast to this exe.
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5FA"), 5);
        var value = PropVariant.FromString(ApplicationUserModelId);
        try
        {
            var store = (IPropertyStore)link;
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
            ((IPersistFile)link).Save(path, true);
        }
        finally { value.Dispose(); Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(IntPtr arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(IntPtr iconPath, int size, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey
    {
        private readonly Guid formatId;
        private readonly uint propertyId;
        internal PropertyKey(Guid formatId, uint propertyId) { this.formatId = formatId; this.propertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)] private ushort type;
        [FieldOffset(8)] private IntPtr pointer;
        internal static PropVariant FromString(string value) => new() { type = 31, pointer = Marshal.StringToCoTaskMemUni(value) };
        public void Dispose() { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); pointer = IntPtr.Zero; type = 0; }
    }
}
