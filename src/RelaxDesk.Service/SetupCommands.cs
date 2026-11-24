using System.Text.Json;
using RelaxDesk.Session;

namespace RelaxDesk.Service;

internal static class SetupCommands
{
    internal static async Task Run(string[] args)
    {
        bool install=args[0]=="--install-service";string? package=null,owner=null;bool? automatic=null;bool start=false,whatIf=false,showResult=false,showIssues=false;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int i=1;i<args.Length;i++)
        {
            string option=args[i];if(!seen.Add(option))throw new ArgumentException("Duplicate setup argument");
            switch(option)
            {
                case "--package":if(!install||++i>=args.Length)throw new ArgumentException("Install package directory required");package=args[i];break;
                case "--owner-sid":if(++i>=args.Length)throw new ArgumentException("Explicit user SID required");owner=args[i];break;
                case "--automatic":case "--manual":if(!install||automatic is not null)throw new ArgumentException("Choose one startup policy");automatic=option=="--automatic";break;
                case "--start":if(!install)throw new ArgumentException("Start is an installation option");start=true;break;
                case "--what-if":whatIf=true;break;
                case "--show-result":showResult=true;break;
                case "--show-issues":showIssues=true;break;
                default:throw new ArgumentException("Unknown service setup argument");
            }
        }
        if(owner is null||install&&package is null)throw new ArgumentException("Explicit --owner-sid and installation --package are required");
        if(showResult&&showIssues)throw new ArgumentException("Choose one result display mode");
        using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,eventArgs)=>{eventArgs.Cancel=true;stop.Cancel();};
        var result=install?await MachineServiceSetup.Install(package!,owner,automatic,start,whatIf,stop.Token):await MachineServiceSetup.Uninstall(owner,whatIf,stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(result));if(result.PendingDelete)Environment.ExitCode=2;
        if(showResult||showIssues&&(result.PendingDelete||result.RetainedFiles))SetupResultDialog.Show(result,whatIf);
    }
}
