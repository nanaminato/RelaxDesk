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
    const string AdminPrefix = "RelaxDesk.AdminInput.";
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
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] image, ref uint length);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ProcessIdToSessionId(uint pid,out uint session);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(SafeAccessTokenHandle token,int information,out uint value,uint size,out uint returned);
    [DllImport("advapi32.dll",EntryPoint="GetTokenInformation",SetLastError=true)] static extern bool QueryTokenBuffer(SafeAccessTokenHandle token,int information,nint buffer,uint size,out uint returned);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] static extern nint GetSidSubAuthority(nint sid,uint index);
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
    static string AdminName(Guid grant) => grant == Guid.Empty
        ? throw new ArgumentException("A non-empty administrator grant is required", nameof(grant))
        : AdminPrefix + grant.ToString("N");

    public static NamedPipeServerStream CreateAdminServer(Guid grant, string owner)
    {
        owner = CanonicalUser(owner);
        return CreateServerCore(AdminName(grant), $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;RC;;;OW)(A;;0x{ClientAccess:X8};;;{owner})", true, 1);
    }

    public static Task<NamedPipeClientStream> ConnectAdmin(Guid grant, CancellationToken cancellation) =>
        ConnectCore(AdminName(grant), 0x10000, cancellation); // Identification only. The server cannot impersonate the elevated client.

    public static void AuthenticateAdminCoordinator(NamedPipeClientStream pipe, int expectedPid, string expectedUser)
    {
        Check(GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid));
        Check(GetNamedPipeServerSessionId(pipe.SafePipeHandle, out uint session));
        RequirePeer(pid, session, expectedPid);
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(OpenProcessToken(process, 8, out var token));
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User?.Value != CanonicalUser(expectedUser))
                throw new SecurityException("Administrator input coordinator SID mismatch");
            const int TokenElevation = 20, TokenUiAccess = 26;
            Check(GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out uint elevationSize));
            Check(GetTokenInformation(token, TokenUiAccess, out uint uiAccess, sizeof(uint), out uint uiAccessSize));
            if (elevationSize != sizeof(uint) || uiAccessSize != sizeof(uint) || elevated != 0 || uiAccess != 0 ||
                CoordinatorIntegrity(token) > 0x2000)
                throw new SecurityException("Administrator input requires an ordinary coordinator without UIAccess");
        }
    }

    static uint CoordinatorIntegrity(SafeAccessTokenHandle token)
    {
        const int TokenIntegrityLevel = 25;
        _ = QueryTokenBuffer(token, TokenIntegrityLevel, 0, 0, out uint size);
        if (size < IntPtr.Size || size > 4096) throw new SecurityException("Coordinator integrity is unavailable");
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Check(QueryTokenBuffer(token, TokenIntegrityLevel, buffer, size, out _));
            nint sid = Marshal.ReadIntPtr(buffer);
            if (sid == 0) throw new SecurityException("Coordinator integrity SID is missing");
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) throw new SecurityException("Coordinator integrity SID has no subauthority");
            return unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void AuthenticateAdminWorker(NamedPipeServerStream pipe, int? expectedPid, string? expectedImage = null, string? elevatedOwner = null, bool systemWorker = false)
    {
        Check(GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid));
        Check(GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint session));
        if (systemWorker)
        {
            // Windows denies an ordinary process OpenProcess/ProcessIdToSessionId
            // on a SYSTEM process. Use kernel-reported pipe PID/session, the PID
            // supplied by the authenticated SCM broker, and the pipe token instead.
            // The broker, not the ordinary coordinator, verifies the protected image.
            if (expectedPid is null or <= 0 || expectedImage is null || pid != (uint)expectedPid || session == 0 ||
                !ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession) ||
                session != ownSession || !SessionActive(session))
                throw new SecurityException("Service input pipe PID/session does not match the authenticated broker launch");
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(true);
                if (identity?.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true ||
                    CoordinatorIntegrity(identity.AccessToken) != 0x4000)
                    throw new SecurityException("Service input worker identity mismatch");
            });
            return;
        }
        if (expectedPid is null && (expectedImage is null || elevatedOwner is null)) throw new SecurityException("Scheduled worker identity must be pinned");
        RequirePeer(pid, session, expectedPid ?? checked((int)pid));
        if (expectedImage is null) return;
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var image = new char[32_768];
        uint length = (uint)image.Length;
        Check(QueryFullProcessImageName(process, 0, image, ref length));
        if (!image.AsSpan(0, checked((int)length)).Equals(expectedImage.AsSpan(), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Administrator input worker image mismatch");
        if (elevatedOwner is null) return;
        Check(OpenProcessToken(process, 8, out var token));
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            Check(GetTokenInformation(token, 20, out uint elevated, sizeof(uint), out uint size));
            bool ownerMatches = identity.User?.Value == CanonicalUser(elevatedOwner!);
            if (!ownerMatches || size != sizeof(uint) || elevated != 1 || CoordinatorIntegrity(token) < 0x3000)
                throw new SecurityException("Scheduled worker owner or integrity mismatch");
        }
    }

    static void RequirePeer(uint pid, uint session, int expectedPid)
    {
        bool ownKnown = ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession);
        int ownError = ownKnown ? 0 : Marshal.GetLastWin32Error();
        bool peerKnown = ProcessIdToSessionId(pid, out uint peerSession);
        int peerError = peerKnown ? 0 : Marshal.GetLastWin32Error();
        bool active = SessionActive(session);
        if (expectedPid <= 0 || pid != (uint)expectedPid || !ownKnown || !peerKnown ||
            session != ownSession || session != peerSession || !active)
            throw new SecurityException($"Input pipe identity/session mismatch: pid={pid} expected_pid={expectedPid} pipe_session={session} own_session={ownSession} peer_session={peerSession} own_error={ownError} peer_error={peerError} active={active}");
    }

    public static string SecurityDescriptor(string user,string server)
    {
        user=CanonicalUser(user);server=new SecurityIdentifier(server).Value;
        return $"D:P(A;;GA;;;SY)(A;;GA;;;{server})(A;;0x{ClientAccess:X8};;;{user})";
    }
    public static NamedPipeServerStream CreateServer(string name,string user,string server,bool first)
    {
        ValidateName(name);
        return CreateServerCore(name, SecurityDescriptor(user, server), first, 9);
    }

    static NamedPipeServerStream CreateServerCore(string name, string sddl, bool first, uint instances)
    {
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor(sddl,1,out var descriptor,out _));
        try
        {
            var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
            var handle=CreateNamedPipe(@"\\.\pipe\"+name,3|0x40000000u|(first?0x80000u:0),8,instances,4096,4096,0,ref attributes); // Overlapped, local-only; one spare instance for accept-race replacement.
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
            try{return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);}catch{handle.Dispose();throw;}
        }
        finally{LocalFree(descriptor);}
    }
    public static async Task<NamedPipeClientStream> Connect(string name,CancellationToken ct)
    {
        ValidateName(name);
        return await ConnectCore(name, 0x20000, ct);
    }

    static async Task<NamedPipeClientStream> ConnectCore(string name, uint impersonationLevel, CancellationToken ct)
    {
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            var handle=CreateFile(@"\\.\pipe\"+name,ClientAccess,0,0,3,0x40000000|0x100000|impersonationLevel,0); // Overlapped + explicit impersonation SQOS.
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
