using System.Runtime.InteropServices;

namespace RelaxDesk.Session;

public static class ServiceWorkerDiagnostics
{
    [DllImport("advapi32.dll",EntryPoint="RegisterEventSourceW",CharSet=CharSet.Unicode)]static extern nint RegisterEventSource(string? server,string source);
    [DllImport("advapi32.dll",EntryPoint="ReportEventW",CharSet=CharSet.Unicode)]static extern bool ReportEvent(nint source,ushort type,ushort category,uint id,nint user,ushort count,uint bytes,string[] text,nint data);
    [DllImport("advapi32.dll")]static extern bool DeregisterEventSource(nint source);
    public static void Message(string message)
    {
        nint source=RegisterEventSource(null,LocalAgentPipe.ServiceName);if(source==0)return;
        try{ReportEvent(source,4,0,1,0,1,0,[$"service_worker pid={Environment.ProcessId} "+message],0);}
        finally{DeregisterEventSource(source);}
    }
    public static void Record(string stage,Exception error)
    {
        nint source=RegisterEventSource(null,LocalAgentPipe.ServiceName);if(source==0)return;
        try{ReportEvent(source,1,0,1,0,1,0,[$"service_worker_failed stage={stage} pid={Environment.ProcessId} type={error.GetType().Name} hresult=0x{error.HResult:X8}"],0);}
        finally{DeregisterEventSource(source);}
    }
}
