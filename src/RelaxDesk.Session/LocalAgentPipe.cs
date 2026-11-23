using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Core;

namespace RelaxDesk.Session;

public static class LocalAgentPipe
{
    public const string Name="RelaxDesk.SessionBroker.v1",ServiceName="RelaxDeskBroker";
    public const uint ClientAccess=0x0012019B; // Individual rights: excludes FILE_CREATE_PIPE_INSTANCE (4).
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes{public int Length;public nint Descriptor;public int Inherit;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out nint descriptor,out uint bytes);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafePipeHandle CreateNamedPipe(string name,uint mode,uint pipeMode,uint instances,uint output,uint input,uint timeout,ref SecurityAttributes attributes);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafePipeHandle CreateFile(string name,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe,out uint session);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe,out uint session);
    [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ProcessIdToSessionId(uint pid,out uint session);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("wtsapi32.dll",EntryPoint="WTSQuerySessionInformationW",SetLastError=true)] static extern bool QuerySession(nint server,uint session,int info,out nint memory,out uint bytes);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(nint memory);
    static void Check(bool result){if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    public static string CanonicalUser(string user)
    {
        var sid=new SecurityIdentifier(user);
        if(sid.IsWellKnown(WellKnownSidType.LocalSystemSid)||sid.IsWellKnown(WellKnownSidType.LocalServiceSid)||sid.IsWellKnown(WellKnownSidType.NetworkServiceSid)||sid.IsWellKnown(WellKnownSidType.AnonymousSid)||sid.IsWellKnown(WellKnownSidType.WorldSid)||sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))throw new SecurityException("Explicit interactive user SID required");
        return sid.Value;
    }
    public static void ValidateName(string name)
    {
        if(name!=Name&&!(name.StartsWith("RelaxDesk.Test.",StringComparison.Ordinal)&&SessionAgentProtocol.Identifier(name[15..])))throw new ArgumentException("Invalid local agent pipe name");
    }
    public static string SecurityDescriptor(string user,string server)
    {
        user=CanonicalUser(user);server=new SecurityIdentifier(server).Value;
        return $"D:P(A;;GA;;;SY)(A;;GA;;;{server})(A;;0x{ClientAccess:X8};;;{user})";
    }
    public static NamedPipeServerStream CreateServer(string name,string user,string server,bool first)
    {
        ValidateName(name);Check(ConvertStringSecurityDescriptorToSecurityDescriptor(SecurityDescriptor(user,server),1,out var descriptor,out _));
        try
        {
            var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
            var handle=CreateNamedPipe(@"\\.\pipe\"+name,3|0x40000000u|(first?0x80000u:0),8,9,4096,4096,0,ref attributes); // Overlapped, local-only; one spare instance for accept-race replacement.
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
            try{return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);}catch{handle.Dispose();throw;}
        }
        finally{LocalFree(descriptor);}
    }
    public static async Task<NamedPipeClientStream> Connect(string name,CancellationToken ct)
    {
        ValidateName(name);
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var handle=CreateFile(@"\\.\pipe\"+name,ClientAccess,0,0,3,0x40000000|0x100000|0x20000,0); // Overlapped + explicit impersonation SQOS.
            if(!handle.IsInvalid){try{return new NamedPipeClientStream(PipeDirection.InOut,true,handle);}catch{handle.Dispose();throw;}}
            int error=Marshal.GetLastWin32Error();handle.Dispose();
            if(error is not (2 or 231))throw new Win32Exception(error);
            await Task.Delay(50,ct);
        }
    }
    public static AuthenticatedAgent AuthenticateClient(NamedPipeServerStream pipe,string approvedUser)
    {
        Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle,out uint pid));Check(GetNamedPipeClientSessionId(pipe.SafePipeHandle,out uint session));
        if(pid==0||pid>int.MaxValue||session==0)throw new SecurityException("Noninteractive agent rejected");
        AuthenticatedAgent? result=null;
        pipe.RunAsClient(()=>
        {
            using var identity=WindowsIdentity.GetCurrent(true);
            if(identity?.User?.Value!=approvedUser||identity.ImpersonationLevel<TokenImpersonationLevel.Impersonation)throw new SecurityException("Agent SID/impersonation rejected");
            var process=OpenProcess(0x1000|0x100000,false,pid);
            try
            {
                if(process.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
                Check(ProcessIdToSessionId(pid,out uint actualSession));
                Check(OpenProcessToken(process,8,out var token));
                using(token)using(var processIdentity=new WindowsIdentity(token.DangerousGetHandle()))
                    if(actualSession!=session||processIdentity.User?.Value!=approvedUser)throw new SecurityException("Agent process identity changed");
                result=new(new(approvedUser,session,(int)pid),process);process=null!;
            }
            finally{process?.Dispose();}
        });
        var agent=result??throw new SecurityException("Agent identity missing");
        try{agent.EnsureActive();return agent;}catch{agent.Dispose();throw;}
    }
    public static void AuthenticateServer(NamedPipeClientStream pipe,int? expectedTestPid=null)
    {
        Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint pid));Check(GetNamedPipeServerSessionId(pipe.SafePipeHandle,out uint session));
        if(expectedTestPid is null){ServiceIdentity.RequireBroker(pid,session);return;}
        if(pid!=(uint)expectedTestPid.Value||!ProcessIdToSessionId((uint)Environment.ProcessId,out uint ownSession)||session!=ownSession)throw new SecurityException("Test broker process/session mismatch");
        using var process=OpenProcess(0x1000,false,pid);if(process.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(OpenProcessToken(process,8,out var token));
        using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))using(var own=WindowsIdentity.GetCurrent())
            if(identity.User!=own.User)throw new SecurityException("Test broker SID mismatch");
    }
    internal static bool SessionActive(uint session)
    {
        nint memory=0;
        try{return session!=0&&QuerySession(0,session,8,out memory,out uint bytes)&&memory!=0&&bytes==4&&Marshal.ReadInt32(memory)==0;}
        finally{if(memory!=0)WTSFreeMemory(memory);}
    }
}

public sealed class AuthenticatedAgent(SessionAgentIdentity identity,SafeProcessHandle process):IDisposable
{
    public SessionAgentIdentity Identity=>identity;
    public void EnsureActive(){if(LocalAgentPipe.WaitForSingleObject(process,0)!=258||!LocalAgentPipe.SessionActive(identity.SessionId))throw new IOException("Agent process/session unavailable");}
    public void Dispose()=>process.Dispose();
}
