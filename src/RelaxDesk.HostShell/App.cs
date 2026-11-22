using System.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

public sealed class App:Application
{
    [STAThread] public static void Main(string[] args)
    {
        if(args is ["--version"]){Console.WriteLine(BuildIdentity.Version);return;}
        var app=new App();
        if(args is ["--shell-contracts",var host,var output]) ShellVerification.Run(app,host,output);
        else app.Run(new HostWindow());
    }
}
