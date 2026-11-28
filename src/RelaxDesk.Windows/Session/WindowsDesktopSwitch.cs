using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RelaxDesk.Windows;

/// <summary>
/// Allows a LocalSystem interactive session agent to dynamically follow
/// Default &lt;-&gt; Winlogon desktop switches for UAC capture and input.
/// Ordinary user processes are no-ops.
/// </summary>
public static class WindowsDesktopSwitch
{
    const uint GenericAll = 0x10000000;
    const uint DesktopReadObjects = 0x0001;
    const uint DesktopCreateWindow = 0x0002;
    const uint DesktopWriteObjects = 0x0080;
    const uint DesktopSwitchDesktop = 0x0100;
    const uint InputDesktopAccess = DesktopReadObjects | DesktopCreateWindow | DesktopWriteObjects | DesktopSwitchDesktop;

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    static extern unsafe bool GetUserObjectInformation(nint handle, int index, void* buffer, uint length, out uint needed);
    [DllImport("sas.dll", SetLastError = true)] static extern void SendSAS(bool asUser);

    static readonly Lazy<bool> LocalSystem = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsLocalSystemProcess => LocalSystem.Value;
    [ThreadStatic] internal static string? ExpectedInputDesktop;
    [ThreadStatic] internal static Action? ExpectedInputAuthorization;
    internal static void EnsureExpectedInputDesktop()
    {
        ExpectedInputAuthorization?.Invoke();
        if (ExpectedInputDesktop is null) return;
        nint input = OpenInputDesktop(0, false, DesktopReadObjects);
        try
        {
            if (input == 0 || ReadDesktopName(input) != ExpectedInputDesktop)
                throw new InputDesktopChangedException();
        }
        finally { if (input != 0) CloseDesktop(input); }
    }
    [ThreadStatic] static nint attachedDesktop;
    [ThreadStatic] static nint originalDesktop;

    internal static void ReleaseThreadDesktop()
    {
        if (attachedDesktop == 0) return;
        if (originalDesktop != 0 && SetThreadDesktop(originalDesktop)) CloseDesktop(attachedDesktop);
        attachedDesktop = 0;
        originalDesktop = 0;
    }

    static unsafe string? ReadDesktopName(nint handle)
    {
        if (handle == 0) return null;
        const int capacity = 512;
        byte* bytes = stackalloc byte[capacity];
        if (!GetUserObjectInformation(handle, 2, bytes, capacity, out uint length) ||
            length < 2 || length > capacity || length % 2 != 0)
            return null;
        return Encoding.Unicode.GetString(new ReadOnlySpan<byte>(bytes, (int)length - 2));
    }

    /// <summary>
    /// Ensures the calling thread is attached to the current active input desktop
    /// (e.g. Default or Winlogon) when running as LocalSystem.
    /// Returns true if the thread desktop was switched.
    /// </summary>
    public static bool SyncThreadToInputDesktop()
    {
        if (!IsLocalSystemProcess) return false;
        nint input = OpenInputDesktop(0, false, InputDesktopAccess);
        if (input == 0) input = OpenInputDesktop(0, false, GenericAll);
        if (input == 0) return false;
        try
        {
            string? inputName = ReadDesktopName(input);
            string? threadName = ReadDesktopName(GetThreadDesktop(GetCurrentThreadId()));
            if (inputName is not null && string.Equals(inputName, threadName, StringComparison.OrdinalIgnoreCase))
                return false;
            nint current = GetThreadDesktop(GetCurrentThreadId());
            if (!SetThreadDesktop(input))
                throw new IOException($"Input desktop association failed: Win32 {Marshal.GetLastWin32Error()}");
            if (originalDesktop == 0) originalDesktop = current;
            if (attachedDesktop != 0) CloseDesktop(attachedDesktop);
            attachedDesktop = input;
            input = 0; // Keep the handle alive while the thread uses this desktop.
            return true;
        }
        finally
        {
            if (input != 0) CloseDesktop(input);
        }
    }

    public static bool TrySendSas()
    {
        if (!IsLocalSystemProcess) return false;
        try
        {
            SendSAS(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class InputDesktopChangedException() : IOException("Input desktop changed before native submission");
