using System.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

public sealed class App:Application
{
    [STAThread] public static void Main(string[] args)
    {
        if(args is ["--version"]){Console.WriteLine(BuildIdentity.Version);return;}
        var app=new App();
        if(args is ["--service-contracts",var serviceHost,var service])ServiceVerification.Run(app,serviceHost,service);
        else if(args is ["--shell-contracts",var host,var output]) ShellVerification.Run(app,host,output);
        else if(args is ["--recovery-contracts",var recoveryHost])RecoveryVerification.Run(app,recoveryHost);
        else
        {
            bool logon=args.Contains("--logon"),serviceMode=args.Contains("--service-agent");
            if(args.Any(value=>value is not ("--logon" or "--service-agent"))||args.Distinct().Count()!=args.Length){Environment.ExitCode=2;return;}
            using var instance=new Mutex(false,$"Local\\RelaxDesk.HostShell.{System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}");
            bool acquired;
            try{acquired=instance.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            if(!acquired)return;
            try
            {
                var window=new HostWindow(){ShowActivated=!logon};
                if(serviceMode){window.ServiceRequired=true;window.Loaded+=async(_,_)=>await window.ConnectService();}
                app.Run(window);
            }
            finally{instance.ReleaseMutex();}
        }
    }
}
