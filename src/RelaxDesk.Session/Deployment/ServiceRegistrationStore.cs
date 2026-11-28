using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RelaxDesk.Session;

internal sealed record ServiceRegistration(string Command,string Account,uint Type,uint Startup,uint State,RawSecurityDescriptor Security,bool PendingDelete=false);
internal interface IServiceRegistrationStore
{
    ServiceRegistration? Read();
    void Create(string command,string owner);
    void Configure(ServiceRegistration expected,string command,uint startup);
    Task Stop(ServiceRegistration expected,CancellationToken ct);
    Task Start(ServiceRegistration expected,CancellationToken ct);
    void Delete(ServiceRegistration expected);
    Task<bool> WaitDeleted(CancellationToken ct);
}
internal static class BrokerServiceSecurity
{
    internal static RawSecurityDescriptor Descriptor(string owner)=>new($"O:BAG:BAD:P(A;;0x000F01FF;;;SY)(A;;0x000F01FF;;;BA)(A;;0x00020005;;;{LocalAgentPipe.CanonicalUser(owner)})");
    internal static void Validate(RawSecurityDescriptor descriptor,string owner)
    {
        if(descriptor.Owner?.Value is not (ServiceFilePolicy.SystemSid or ServiceFilePolicy.AdminsSid)||descriptor.DiscretionaryAcl is null||(descriptor.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)throw new SecurityException("Service object owner/DACL is not protected");
        foreach(GenericAce generic in descriptor.DiscretionaryAcl)
        {
            if(generic is not CommonAce ace||ace.IsCallback)throw new SecurityException("Unknown service ACE");
            if(ace.AceQualifier!=AceQualifier.AccessAllowed)continue;
            string sid=ace.SecurityIdentifier.Value;
            if(sid is ServiceFilePolicy.SystemSid or ServiceFilePolicy.AdminsSid)continue;
            if(sid!=owner||(ace.AccessMask&~0x20005)!=0)throw new SecurityException("Untrusted service control permission");
        }
    }
}

internal sealed class WindowsServiceRegistrationStore:IServiceRegistrationStore
{
    [StructLayout(LayoutKind.Sequential)] struct Status{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Hint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)] struct Config{public uint Type,Start,Error;public nint Binary,Group;public uint Tag;public nint Dependencies,Account,Display;}
    sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid
    {private ServiceHandle():base(true){}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",EntryPoint="OpenSCManagerW",CharSet=CharSet.Unicode,SetLastError=true)] static extern ServiceHandle OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",EntryPoint="OpenServiceW",CharSet=CharSet.Unicode,SetLastError=true)] static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
    [DllImport("advapi32.dll",EntryPoint="CreateServiceW",CharSet=CharSet.Unicode,SetLastError=true)] static extern ServiceHandle CreateService(ServiceHandle manager,string name,string display,uint access,uint type,uint startup,uint error,string command,string? group,nint tag,string? dependencies,string account,string? password);
    [DllImport("advapi32.dll",EntryPoint="ChangeServiceConfigW",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ChangeServiceConfig(ServiceHandle service,uint type,uint startup,uint error,string command,string? group,nint tag,string? dependencies,string account,string? password,string? display);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatusEx(ServiceHandle service,int level,out Status status,uint size,out uint needed);
    [DllImport("advapi32.dll",EntryPoint="QueryServiceConfigW",SetLastError=true)] static extern bool QueryServiceConfig(ServiceHandle service,nint memory,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceObjectSecurity(ServiceHandle service,uint information,byte[]? buffer,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetServiceObjectSecurity(ServiceHandle service,uint information,byte[] descriptor);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool ControlService(ServiceHandle service,uint control,out NativeStatus status);
    [StructLayout(LayoutKind.Sequential)] struct NativeStatus{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Hint;}
    [DllImport("advapi32.dll",EntryPoint="StartServiceW",SetLastError=true)] static extern bool StartService(ServiceHandle service,uint count,nint arguments);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(nint service);
    static void Check(bool result){if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    internal static bool Administrator{get{using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
    static void DemandAdmin(){if(!Administrator)throw new UnauthorizedAccessException("Machine service changes require an explicitly elevated administrator; setup never elevates itself");}
    static ServiceHandle Manager(uint access){var handle=OpenSCManager(null,null,access);if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}return handle;}
    internal static int[] AbiSizes=>[Marshal.SizeOf<Status>(),Marshal.SizeOf<Config>(),Marshal.SizeOf<NativeStatus>()];
    public ServiceRegistration? Read()=>ReadService(LocalAgentPipe.ServiceName,true);
    internal ServiceRegistration? ReadWindowsProbe()=>ReadService("EventLog",false);
    ServiceRegistration? ReadService(string name,bool strict)
    {
        using var manager=Manager(1);using var service=OpenService(manager,name,1|4|0x20000);
        if(service.IsInvalid)
        {
            int error=Marshal.GetLastWin32Error();if(error==1060)return null;
            if(error==1072)return new("","",0,0,0,new RawSecurityDescriptor("D:P"),true);
            throw new Win32Exception(error);
        }
        Check(QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<Status>(),out _));
        _=QueryServiceConfig(service,0,0,out uint bytes);if(bytes<(uint)Marshal.SizeOf<Config>()||bytes>8192)throw new SecurityException("Invalid service configuration size");
        nint memory=Marshal.AllocHGlobal((int)bytes);
        try
        {
            Check(QueryServiceConfig(service,memory,bytes,out _));var config=Marshal.PtrToStructure<Config>(memory);
            if(strict&&(config.Group!=0&&!string.IsNullOrEmpty(Marshal.PtrToStringUni(config.Group))||config.Dependencies!=0&&Marshal.ReadInt16(config.Dependencies)!=0||config.Error!=1))throw new SecurityException("Unexpected service group/dependencies/error policy");
            _=QueryServiceObjectSecurity(service,1|4,null,0,out uint securityBytes);if(securityBytes is <20 or >65536)throw new SecurityException("Invalid service security size");
            byte[] security=new byte[securityBytes];Check(QueryServiceObjectSecurity(service,1|4,security,securityBytes,out _));
            return new(Marshal.PtrToStringUni(config.Binary)??"",Marshal.PtrToStringUni(config.Account)??"",config.Type,config.Start,status.State,new(security,0));
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    static bool Same(ServiceRegistration a,ServiceRegistration b)=>a.Command.Equals(b.Command,StringComparison.OrdinalIgnoreCase)&&a.Account.Equals(b.Account,StringComparison.OrdinalIgnoreCase)&&a.Type==b.Type&&a.Startup==b.Startup&&!a.PendingDelete&&!b.PendingDelete;
    ServiceHandle OpenExpected(ServiceRegistration expected,uint access)
    {
        DemandAdmin();var actual=Read();if(actual is null||!Same(actual,expected))throw new SecurityException("Service configuration changed concurrently");
        using var manager=Manager(1);var service=OpenService(manager,LocalAgentPipe.ServiceName,access);if(service.IsInvalid){int error=Marshal.GetLastWin32Error();service.Dispose();throw new Win32Exception(error);}return service;
    }
    public void Create(string command,string owner)
    {
        DemandAdmin();using var manager=Manager(1|2);
        using var service=CreateService(manager,LocalAgentPipe.ServiceName,"RelaxDesk local session broker",0xF01FF,16,3,1,command,null,0,null,"LocalSystem",null);
        if(service.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        try{var descriptor=BrokerServiceSecurity.Descriptor(owner);byte[] bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);Check(SetServiceObjectSecurity(service,1|4,bytes));}
        catch{_=DeleteService(service);throw;}
    }
    public void Configure(ServiceRegistration expected,string command,uint startup)
    {using var service=OpenExpected(expected,2);Check(ChangeServiceConfig(service,16,startup,1,command,null,0,null,"LocalSystem",null,null));}
    public async Task Stop(ServiceRegistration expected,CancellationToken ct)
    {
        using(var service=OpenExpected(expected,0x20))if(!ControlService(service,1,out _)&&Marshal.GetLastWin32Error()!=1062)throw new Win32Exception(Marshal.GetLastWin32Error());
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while(true){var current=Read();if(current is null||current.PendingDelete||!Same(current,expected))throw new SecurityException("Service changed during stop");if(current.State==1)return;await Task.Delay(100,deadline.Token);}
    }
    public async Task Start(ServiceRegistration expected,CancellationToken ct)
    {
        using(var service=OpenExpected(expected,0x10))if(!StartService(service,0,0)&&Marshal.GetLastWin32Error()!=1056)throw new Win32Exception(Marshal.GetLastWin32Error());
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while(true){var current=Read();if(current is null||current.PendingDelete||!Same(current,expected))throw new SecurityException("Service changed during start");if(current.State==4)return;if(current.State==1)throw new IOException("Service stopped during startup; inspect Event Log");await Task.Delay(100,deadline.Token);}
    }
    public void Delete(ServiceRegistration expected){using var service=OpenExpected(expected,0x10000);if(!DeleteService(service)&&Marshal.GetLastWin32Error()!=1072)throw new Win32Exception(Marshal.GetLastWin32Error());}
    public async Task<bool> WaitDeleted(CancellationToken ct)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        while(clock.Elapsed<TimeSpan.FromSeconds(10)){var current=Read();if(current is null)return true;if(!current.PendingDelete)throw new SecurityException("Service reappeared while deleting");await Task.Delay(100,ct);}return false;
    }
}
