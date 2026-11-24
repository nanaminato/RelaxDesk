using System.Runtime.InteropServices;
using System.Text;

namespace RelaxDesk.Windows;

public sealed record WindowsSessionSnapshot(uint? SessionId, int? ConnectionState, bool? VisibleWindowStation,
    string? ThreadDesktop, string? InputDesktop, bool? Locked = null, uint? ConsoleSessionId = null,
    int? InputDesktopOpenError = null)
{
    public string? Denial => SessionId is null ? "session-query-failed" : SessionId == 0 ? "session-zero" :
        ConnectionState != 0 ? "session-not-active" : Locked == true ? "session-locked" : VisibleWindowStation != true ? "window-station-not-visible" :
        ThreadDesktop != "Default" ? "thread-desktop-unsupported" : InputDesktop is null ? "input-desktop-unavailable" :
        InputDesktop != "Default" ? "input-desktop-unsupported" : null;
    public void EnsureAllowed() { if (Denial is {} reason) throw new WindowsSessionUnavailableException(reason); }
}

public sealed class WindowsSessionUnavailableException(string reason) : IOException(
    "当前 Windows 会话或安全桌面不支持控制；请回到原用户的普通桌面后重新连接并授权。 reason=" + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>Read-only checks of this process/session. Never changes desktops, tokens or sessions.</summary>
public static class WindowsSessionAccess
{
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ProcessIdToSessionId(uint processId,out uint sessionId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError=true)] static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", SetLastError=true)]
    static extern unsafe bool GetName(nint handle,int index,void* buffer,uint length,out uint needed);
    [DllImport("wtsapi32.dll", EntryPoint="WTSQuerySessionInformationW", SetLastError=true)]
    static extern bool QuerySession(nint server,uint sessionId,int info,out nint buffer,out uint bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(nint buffer);
    const int ConnectState = 8, SessionInfoEx = 25;

    static (int? State, bool? Locked) ReadSessionState(uint sessionId)
    {
        nint memory = 0;
        // WTSINFOEXW level 1 holds an 8-byte-aligned union. Its prefix is
        // the session ID, connection state, and lock flags.
        try
        {
            if (QuerySession(0, sessionId, SessionInfoEx, out memory, out uint length) && memory != 0 &&
                length >= 20 && Marshal.ReadInt32(memory) == 1 && unchecked((uint)Marshal.ReadInt32(memory, 8)) == sessionId)
                return (Marshal.ReadInt32(memory, 12), Marshal.ReadInt32(memory, 16) switch { 0 => true, 1 => false, _ => null });
        }
        finally { if (memory != 0) WTSFreeMemory(memory); }
        memory = 0;
        try
        {
            return (QuerySession(0, sessionId, ConnectState, out memory, out uint length) && memory != 0 && length == 4
                ? Marshal.ReadInt32(memory) : null, null);
        }
        finally { if (memory != 0) WTSFreeMemory(memory); }
    }

    static unsafe string? Name(nint handle)
    {
        const int capacity = 512;
        byte* bytes = stackalloc byte[capacity];
        if(handle==0||!GetName(handle,2,bytes,capacity,out uint length)||length<2||length>capacity||length%2!=0)return null;
        return Encoding.Unicode.GetString(new ReadOnlySpan<byte>(bytes, (int)length - 2));
    }
    public static unsafe WindowsSessionSnapshot Read()
    {
        uint? id=ProcessIdToSessionId((uint)Environment.ProcessId,out uint sessionId)?sessionId:null;
        (int? state, bool? locked) = id is { } value ? ReadSessionState(value) : (null, null);
        uint* flags = stackalloc uint[3];
        nint station=GetProcessWindowStation();
        bool? visible=station!=0&&GetName(station,1,flags,12,out uint size)&&size==12?(flags[2]&1)!=0:null;
        string? thread=Name(GetThreadDesktop(GetCurrentThreadId()));
        nint input=OpenInputDesktop(0,false,1);
        int? inputOpenError = input == 0 ? Marshal.GetLastPInvokeError() : null;
        string? inputName;
        try { inputName=Name(input); } finally { if(input!=0)CloseDesktop(input); }
        return new(id,state,visible,thread,inputName,locked,WTSGetActiveConsoleSessionId(),inputOpenError);
    }
    public static void EnsureAllowed()=>Read().EnsureAllowed();

    public static async Task Watch(CancellationToken ct,Func<WindowsSessionSnapshot>? read=null,TimeSpan? interval=null, Action<WindowsSessionSnapshot>? authorize=null)
    {
        var delay=interval??TimeSpan.FromMilliseconds(250);
        if(delay<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(interval));
        uint? originalSession = null;
        uint? originalConsole = null;
        bool first = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = (read ?? Read)();
            if (snapshot.SessionId is null)
                throw new WindowsSessionUnavailableException("session-query-failed");
            if (first)
            {
                originalSession = snapshot.SessionId;
                if (snapshot.ConsoleSessionId == snapshot.SessionId) originalConsole = snapshot.ConsoleSessionId;
                first = false;
            }
            else if (snapshot.SessionId != originalSession || originalConsole is not null && snapshot.ConsoleSessionId != originalConsole)
                throw new WindowsSessionUnavailableException("physical-console-session-changed");
            if (authorize is null) snapshot.EnsureAllowed();
            else authorize(snapshot);
            await Task.Delay(delay, ct);
        }
    }
}
