using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RelaxDesk.Windows;

public sealed record WindowsInputToken(bool Elevated, bool UiAccess, int IntegrityRid);

public static class WindowsInputPrivilege
{
    enum TokenInformation { Elevation = 20, IntegrityLevel = 25, UIAccess = 26 }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token, TokenInformation information,
        nint buffer, uint size, out uint required);

    [DllImport("advapi32.dll")]
    static extern nint GetSidSubAuthorityCount(nint sid);

    [DllImport("advapi32.dll")]
    static extern nint GetSidSubAuthority(nint sid, uint index);

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);

    public static string ForegroundDiagnostic()
    {
        uint process = 0;
        _ = GetWindowThreadProcessId(GetForegroundWindow(), out process);
        var token = ReadProcessToken();
        return $"foreground_pid={process} coordinator_integrity=0x{token.IntegrityRid:X4} coordinator_elevated={token.Elevated}";
    }

    public static WindowsInputToken ReadProcessToken()
    {
        using var threadIdentity = WindowsIdentity.GetCurrent(ifImpersonating: true);
        if (threadIdentity is not null) throw new IOException("RelaxDesk cannot inspect the input process token while impersonating.");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return new(Read(identity.AccessToken, TokenInformation.Elevation, Marshal.ReadInt32) != 0,
            Read(identity.AccessToken, TokenInformation.UIAccess, Marshal.ReadInt32) != 0,
            Read(identity.AccessToken, TokenInformation.IntegrityLevel, ReadIntegrity));
    }

    public static void EnsureOrdinaryProcess()
    {
        if (!AllowsOrdinaryHost(ReadProcessToken()))
            throw new IOException("Start RelaxDesk Host with an ordinary Windows token to share this desktop.");
    }

    public static void EnsureHostProcess()
    {
        var token = ReadProcessToken();
        if (!AllowsOrdinaryHost(token) && !AllowsSystemHost(token))
            throw new IOException("Start RelaxDesk Host with an ordinary Windows token or via the LocalSystem service.");
    }

    public static void EnsureSystemInputProcess()
    {
        var token = ReadProcessToken();
        if (!WindowsDesktopSwitch.IsLocalSystemProcess || token.IntegrityRid != 0x4000 || token.UiAccess)
            throw new IOException("Service input requires a LocalSystem interactive worker without UIAccess.");
    }

    public static void EnsureElevatedProcess()
    {
        if (!AllowsElevatedWorker(ReadProcessToken()))
            throw new IOException("Administrator input requires a high-integrity interactive administrator token.");
    }

    internal static bool AllowsOrdinaryHost(WindowsInputToken token) => !token.Elevated && !token.UiAccess && token.IntegrityRid <= 0x2000;
    internal static bool AllowsSystemHost(WindowsInputToken token) => WindowsDesktopSwitch.IsLocalSystemProcess && token.IntegrityRid >= 0x4000;
    internal static bool AllowsElevatedWorker(WindowsInputToken token) => token.Elevated && !token.UiAccess && token.IntegrityRid == 0x3000;

    static int ReadIntegrity(nint buffer)
    {
        nint sid = Marshal.ReadIntPtr(buffer);
        int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
        if (count == 0) throw new IOException("Windows token has no integrity level.");
        return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
    }

    static int Read(SafeAccessTokenHandle token, TokenInformation information, Func<nint, int> parse)
    {
        GetTokenInformation(token, information, 0, 0, out uint size);
        if (size is < 4 or > 4096) throw new IOException("Unable to read Windows token information.");
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetTokenInformation(token, information, buffer, size, out _))
                throw new IOException("Unable to read Windows token information.", new Win32Exception(Marshal.GetLastWin32Error()));
            return parse(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
