using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

namespace RelaxDesk.Session;

public static class ServiceIdentity
{
    [StructLayout(LayoutKind.Sequential)] struct Status{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Hint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)] struct Config{public uint Type,Start,Error;public nint Binary,Group;public uint Tag;public nint Dependencies,Account,Display;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern nint OpenService(nint manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatusEx(nint service,int level,out Status status,uint size,out uint needed);
    [DllImport("advapi32.dll",EntryPoint="QueryServiceConfigW",SetLastError=true)] static extern bool QueryServiceConfig(nint service,nint memory,uint size,out uint needed);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(nint handle);
    public static void RequireBroker(uint pipePid,uint pipeSession)
    {
        if(pipePid==0||pipeSession!=0)throw new SecurityException("Broker must run in Session 0");
        nint manager=OpenSCManager(null,null,1);if(manager==0)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint service=OpenService(manager,LocalAgentPipe.ServiceName,1|4);if(service==0)throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<Status>(),out _)||status.Type!=16||status.State!=4||status.Pid!=pipePid)throw new SecurityException("Pipe server does not match the running SCM service");
                _=QueryServiceConfig(service,0,0,out uint bytes);
                if(bytes<(uint)Marshal.SizeOf<Config>()||bytes>8192)throw new SecurityException("Invalid broker service configuration");
                nint memory=Marshal.AllocHGlobal((int)bytes);
                try
                {
                    if(!QueryServiceConfig(service,memory,bytes,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
                    var config=Marshal.PtrToStructure<Config>(memory);
                    if(config.Type!=16||!MachineServiceSetup.IsAllowedServiceAccount(Marshal.PtrToStringUni(config.Account) ?? ""))
                        throw new SecurityException("Broker must use the dedicated LocalSystem or legacy LocalService process");
                }
                finally{Marshal.FreeHGlobal(memory);}
            }
            finally{CloseServiceHandle(service);}
        }
        finally{CloseServiceHandle(manager);}
    }
}
