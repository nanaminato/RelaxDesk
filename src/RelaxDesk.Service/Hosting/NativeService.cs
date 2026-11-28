using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using RelaxDesk.Session;

namespace RelaxDesk.Service;

internal static class NativeService
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ServiceMain(uint argc,nint argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint Handler(uint control,uint eventType,nint eventData,nint context);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Entry{[MarshalAs(UnmanagedType.LPWStr)]public string? Name;public ServiceMain? Main;}
    [StructLayout(LayoutKind.Sequential)] struct Status{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Hint;}
    [DllImport("advapi32.dll",EntryPoint="StartServiceCtrlDispatcherW",SetLastError=true)] static extern bool StartDispatcher([In] Entry[] entries);
    [DllImport("advapi32.dll",EntryPoint="RegisterServiceCtrlHandlerExW",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint RegisterHandler(string name,Handler handler,nint context);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetServiceStatus(nint service,ref Status status);
    [DllImport("advapi32.dll",EntryPoint="RegisterEventSourceW",CharSet=CharSet.Unicode)] static extern nint RegisterEventSource(string? server,string source);
    [DllImport("advapi32.dll",EntryPoint="ReportEventW",CharSet=CharSet.Unicode)] static extern bool ReportEvent(nint source,ushort type,ushort category,uint eventId,nint user,ushort strings,uint bytes,[In] string[] text,nint data);
    [DllImport("advapi32.dll")] static extern bool DeregisterEventSource(nint source);
    static void Log(string text,bool error=false)
    {
        nint source=RegisterEventSource(null,LocalAgentPipe.ServiceName);if(source==0)return;
        try{_=ReportEvent(source,error?(ushort)1:(ushort)4,0,1,0,1,0,[text],0);}finally{DeregisterEventSource(source);}
    }
    public static void Run(string approvedUser)
    {
        nint handle=0;SessionBrokerServer? broker=null;int stopRequested=0;
        void State(uint state,uint error=0)
        {
            var status=new Status{Type=16,State=state,Controls=state==4?1u|4u|128u:0,Exit=error,Checkpoint=state is 2 or 3?1u:0,Hint=state is 2 or 3?15000u:0};
            if(!SetServiceStatus(handle,ref status))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        Handler handler=(control,eventType,data,_)=>
        {
            try
            {
                if(control is 1 or 5){Interlocked.Exchange(ref stopRequested,1);State(3);broker?.RequestStop();return 0;}
                if(control==4)return 0;
                if(control==14)
                {
                    if(eventType is 2 or 4 or 6 or 7 or 11)
                    {if(data==0||Marshal.ReadInt32(data)!=8){broker?.RequestStop();return 0;}broker?.InvalidateSession(unchecked((uint)Marshal.ReadInt32(data,4)));}
                    return 0;
                }
                return 120;
            }
            catch{broker?.RequestStop();return 1064;}
        };
        ServiceMain main=(_,_)=>
        {
            uint exit=0;
            handle=RegisterHandler(LocalAgentPipe.ServiceName,handler,0);
            if(handle==0)return;
            try
            {
                State(2);
                using var identity=WindowsIdentity.GetCurrent();using var process=Process.GetCurrentProcess();
                bool allowedAccount = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true ||
                    identity.User?.IsWellKnown(WellKnownSidType.LocalServiceSid) == true;
                if(process.SessionId!=0||!allowedAccount)throw new SecurityException("Broker requires LocalSystem or LocalService in Session 0");
                ServiceDeploymentTrust.RequireRuntime(AppContext.BaseDirectory,approvedUser);
                broker=new(approvedUser,diagnostic:text=>Log(text));
                if(Volatile.Read(ref stopRequested)!=0)broker.RequestStop();
                broker.Run(CancellationToken.None,()=>State(4)).GetAwaiter().GetResult();
            }
            catch(Exception error){exit=1064;Log($"broker_failed error_type={error.GetType().Name} hresult=0x{error.HResult:X8}",true);}
            finally{try{State(1,exit);}catch(Exception error){Log($"broker_status_failed error_type={error.GetType().Name}",true);}}
        };
        if(!StartDispatcher([new(){Name=LocalAgentPipe.ServiceName,Main=main},new()]))throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(main);GC.KeepAlive(handler);
    }
}
