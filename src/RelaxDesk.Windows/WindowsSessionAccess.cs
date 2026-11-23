using System.Runtime.InteropServices;
using System.Text;

namespace RelaxDesk.Windows;

public sealed record WindowsSessionSnapshot(uint? SessionId, int? ConnectionState, bool? VisibleWindowStation,
    string? ThreadDesktop, string? InputDesktop)
{
    public string? Denial => SessionId is null ? "session-query-failed" : SessionId == 0 ? "session-zero" :
        ConnectionState != 0 ? "session-not-active" : VisibleWindowStation != true ? "window-station-not-visible" :
        ThreadDesktop != "Default" ? "thread-desktop-unsupported" : InputDesktop != "Default" ? "input-desktop-unavailable" : null;
    public void EnsureAllowed() { if (Denial is {} reason) throw new WindowsSessionUnavailableException(reason); }
}

public sealed class WindowsSessionUnavailableException(string reason) : IOException(
    "当前 Windows 会话或安全桌面不支持控制；请回到原用户的普通桌面后重新连接并授权。 reason=" + reason);

/// <summary>Read-only checks of this process/session. Never changes desktops, tokens or sessions.</summary>
public static class WindowsSessionAccess
{
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ProcessIdToSessionId(uint processId,out uint sessionId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError=true)] static extern nint OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint="GetUserObjectInformationW", SetLastError=true)]
    static extern bool GetName(nint handle,int index,[Out] byte[] buffer,uint length,out uint needed);
    [DllImport("wtsapi32.dll", EntryPoint="WTSQuerySessionInformationW", SetLastError=true)]
    static extern bool QuerySession(nint server,uint sessionId,int info,out nint buffer,out uint bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(nint buffer);

    static string? Name(nint handle)
    {
        byte[] bytes=new byte[512];
        if(handle==0||!GetName(handle,2,bytes,(uint)bytes.Length,out uint length)||length<2||length>bytes.Length||length%2!=0)return null;
        return Encoding.Unicode.GetString(bytes,0,(int)length-2);
    }
    public static WindowsSessionSnapshot Read()
    {
        uint? id=ProcessIdToSessionId((uint)Environment.ProcessId,out uint sessionId)?sessionId:null;
        int? state=null; nint memory=0;
        try { if(id is {} value&&QuerySession(0,value,8,out memory,out uint length)&&memory!=0&&length==4)state=Marshal.ReadInt32(memory); }
        finally { if(memory!=0)WTSFreeMemory(memory); }
        byte[] flags=new byte[12]; nint station=GetProcessWindowStation();
        bool? visible=station!=0&&GetName(station,1,flags,12,out uint size)&&size==12?(BitConverter.ToUInt32(flags,8)&1)!=0:null;
        string? thread=Name(GetThreadDesktop(GetCurrentThreadId()));
        nint input=OpenInputDesktop(0,false,1); string? inputName;
        try { inputName=Name(input); } finally { if(input!=0)CloseDesktop(input); }
        return new(id,state,visible,thread,inputName);
    }
    public static void EnsureAllowed()=>Read().EnsureAllowed();

    public static async Task Watch(CancellationToken ct,Func<WindowsSessionSnapshot>? read=null,TimeSpan? interval=null)
    {
        var delay=interval??TimeSpan.FromMilliseconds(250);
        if(delay<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(interval));
        while(true){ct.ThrowIfCancellationRequested();(read??Read)().EnsureAllowed();await Task.Delay(delay,ct);}
    }
}
