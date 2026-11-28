using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

// Opt-in acceptance helper. The medium child requests elevation only to run
// this executable's harmless --version command. The UAC probe cancels it.
internal static class UacConsentTrigger
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct StartupInfo
    {
        public uint Size;public string? Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;
        public ushort Show,Reserved2;public nint Reserved3,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation{public nint Process,Thread;public uint Pid,Tid;}
    [DllImport("advapi32.dll",SetLastError=true)]static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,out nint linked,uint size,out uint returned);
    [DllImport("advapi32.dll",EntryPoint="CreateProcessWithTokenW",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool CreateProcessWithToken(SafeAccessTokenHandle token,uint logonFlags,string application,string arguments,uint flags,nint environment,string? directory,ref StartupInfo startup,out ProcessInformation process);
    [DllImport("kernel32.dll")]static extern uint WaitForSingleObject(nint handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(nint process,out uint code);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(nint handle);
    public static int Request()
    {
        WindowsInputPrivilege.EnsureOrdinaryProcess();
        try
        {
            using var process=Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",Arguments="--version",WindowStyle=ProcessWindowStyle.Hidden});
            process?.WaitForExit();return 0;
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223){return 77;}
    }
    public static int Trigger(string output)
    {
        try
        {
            WindowsInputPrivilege.EnsureElevatedProcess();
            using var identity=WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if(!GetTokenInformation(identity.AccessToken,19,out nint linked,(uint)IntPtr.Size,out uint length)||length!=IntPtr.Size||linked==0)
                throw new Win32Exception(Marshal.GetLastWin32Error(),"Administrator has no linked medium token; no UAC policy changes are made");
            using var token=new SafeAccessTokenHandle(linked);
            string application=Environment.ProcessPath!;
            var startup=new StartupInfo{Size=(uint)Marshal.SizeOf<StartupInfo>(),Desktop=@"winsta0\default"};
            if(!CreateProcessWithToken(token,0,application,"\""+application+"\" --uac-consent-request",0x08000000,0,Path.GetDirectoryName(application),ref startup,out var child))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            uint exit;
            try
            {
                if(WaitForSingleObject(child.Process,40000)!=0)throw new IOException("Timed out waiting for opt-in UAC cancellation");
                if(!GetExitCodeProcess(child.Process,out exit))throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally{CloseHandle(child.Thread);CloseHandle(child.Process);}
            File.WriteAllText(output,JsonSerializer.Serialize(new{cancelled=exit==77,childExit=exit,scope="linked medium administrator token requesting harmless --version"}));
            return exit==77?0:2;
        }
        catch(Exception error){File.WriteAllText(output,JsonSerializer.Serialize(new{status="FAIL",error=error.ToString()}));return 1;}
    }
}
