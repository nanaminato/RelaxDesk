using System.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

public sealed class App:Application
{
    [STAThread] public static void Main(string[] args)
    {
        if(args is ["--version"]){Console.WriteLine(BuildIdentity.Version);return;}
        if(args is ["--uac-consent-request"]){Environment.ExitCode=UacConsentTrigger.Request();return;}
        if(args is ["--uac-consent-trigger",var triggerOutput]){Environment.ExitCode=UacConsentTrigger.Trigger(triggerOutput);return;}
        if(args is ["--restricted-host-launch-probe",var restrictedHost,var restrictedOutput])
        {
            Environment.ExitCode=RestrictedHostLaunchVerification.Run(restrictedHost,restrictedOutput);
            return;
        }
        var app=new App();
        if(args is ["--secure-desktop-probe",var secureOutput])SecureDesktopVerification.Run(app,secureOutput);
        else if(args is ["--physical-taskbar-input-probe",var taskbarOutput])TaskbarInputVerification.Run(app,taskbarOutput);
        else if(args is ["--physical-input-elevated-target",var elevatedState])PhysicalInputSwitchVerification.RunElevatedTarget(app,elevatedState);
        else if(args is ["--physical-input-elevated-probe",var targetState,var elevatedOutput])PhysicalInputSwitchVerification.RunElevated(app,targetState,elevatedOutput);
        else if(args is ["--physical-input-switch-probe",var inputMode,var inputOutput])PhysicalInputSwitchVerification.Run(app,inputMode,inputOutput);
        else if(args is ["--password-login-contracts",var loginHost,var loginOutput])HostWindow.VerifyPasswordLogin(app,loginHost,loginOutput);
        else if(args is ["--localization-contracts",var languageOutput])HostWindow.VerifyLocalization(app,languageOutput);
        else if(args is ["--layout-contracts",var layoutOutput])HostWindow.VerifyLayout(app,layoutOutput);
        else if(args is ["--service-setup-ui-contracts",var setupHost,var setupOutput])ServiceSetupVerification.Run(app,setupHost,setupOutput);
        else if(args is ["--service-contracts",var serviceHost,var service])ServiceVerification.Run(app,serviceHost,service);
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
