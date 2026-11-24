using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using RelaxDesk.Session;

namespace RelaxDesk.HostShell;

internal sealed record ServiceSetupPlan(string Package,string Owner,string Version,bool Uninstall,bool? Automatic,bool OnlyIssues=false)
{
    internal string Helper=>Path.Combine(Package,"Service","RelaxDesk.Service.exe");
    internal ProcessStartInfo StartInfo()
    {
        var info=new ProcessStartInfo(Helper){UseShellExecute=true,Verb="runas",WorkingDirectory=Package,WindowStyle=ProcessWindowStyle.Hidden};
        info.ArgumentList.Add(Uninstall?"--uninstall-service":"--install-service");
        if(!Uninstall){info.ArgumentList.Add("--package");info.ArgumentList.Add(Package);info.ArgumentList.Add("--start");if(Automatic is {} automatic)info.ArgumentList.Add(automatic?"--automatic":"--manual");}
        info.ArgumentList.Add("--owner-sid");info.ArgumentList.Add(Owner);info.ArgumentList.Add(OnlyIssues?"--show-issues":"--show-result");
        return info;
    }
}

internal interface IServiceSetupBackend
{
    Task<ServiceSetupPlan> Prepare(string host,bool uninstall,bool? automatic);
    Task<int> Run(ServiceSetupPlan plan);
    Task<MachineServiceStatus> Inspect();
    Task<SessionAgentClient> Connect(CancellationToken ct);
}

internal sealed class ServiceSetupBackend : IServiceSetupBackend
{
    // Capture the ordinary user's SID before ShellExecute can ask for another
    // administrator account. Never substitute the elevated helper's identity.
    readonly string owner=CurrentOwner();
    static string CurrentOwner(){using var identity=WindowsIdentity.GetCurrent();return identity.User!.Value;}
    public Task<ServiceSetupPlan> Prepare(string host,bool uninstall,bool? automatic)=>Task.Run(()=>
    {
        string package=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(host))!,".."));
        string version=MachineServiceSetup.ValidatePackage(package);
        return new ServiceSetupPlan(package,owner,version,uninstall,automatic);
    });
    public async Task<int> Run(ServiceSetupPlan plan)
    {
        using var child=await OnSta(()=>Process.Start(plan.StartInfo())??throw new IOException("Service setup process did not start"));
        // Do not kill a privileged transaction or abandon its process handle.
        // UAC can be cancelled by the operator before it starts.
        await child.WaitForExitAsync();return child.ExitCode;
    }
    internal static Task<T> OnSta<T>(Func<T> action)
    {
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult(action());}catch(Exception error){completion.SetException(error);}}){IsBackground=true,Name="RelaxDesk service setup UAC"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return completion.Task;
    }
    public Task<MachineServiceStatus> Inspect()=>Task.Run(()=>MachineServiceSetup.Inspect(owner));
    public Task<SessionAgentClient> Connect(CancellationToken ct)=>SessionAgentClient.Connect(ct);
}
