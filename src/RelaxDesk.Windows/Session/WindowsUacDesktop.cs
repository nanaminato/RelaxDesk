using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RelaxDesk.Windows;

// Winlogon is also used for lock/logon. Only an unlocked session with the
// Windows consent executable in the foreground is eligible for UAC remoting.
internal static class WindowsUacDesktop
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", EntryPoint="QueryFullProcessImageNameW", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char[] path, ref uint size);
    public static bool ConsentIsForeground()
    {
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out uint processId);
        if (processId == 0) return false;
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) return false;
        var path = new char[32768]; uint size = (uint)path.Length;
        return QueryFullProcessImageName(process, 0, path, ref size) &&
            path.AsSpan(0, (int)size).Equals(Path.Combine(Environment.SystemDirectory,"consent.exe"), StringComparison.OrdinalIgnoreCase);
    }
}
