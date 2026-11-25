using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace RelaxDesk.Session;

public sealed record ServiceSetupResult(string Operation,bool Changed,string Root,string Owner,string? Version,uint Startup,bool Running,bool PendingDelete,bool RetainedFiles);
public sealed record MachineServiceStatus(bool Installed,uint State,uint Startup,bool PendingDelete,
    AdminWorkerDeployment? WorkerDeployment = null);

public static class MachineServiceSetup
{
    // Read-only bundle preflight, independent of protected installed receipts.
    public static string ValidatePackage(string package)=>ServicePayload.Read(ServiceFilePolicy.FullPath(package)).Version;
    public static MachineServiceStatus Inspect(string owner)=>Inspect(LocalAgentPipe.CanonicalUser(owner),new WindowsServiceRegistrationStore().Read());
    internal static MachineServiceStatus Inspect(string owner,ServiceRegistration? item)
    {
        owner=LocalAgentPipe.CanonicalUser(owner);
        if(item is null)return new(false,0,0,false);
        if(item.PendingDelete)return new(true,0,0,true);
        BrokerServiceSecurity.Validate(item.Security,owner);
        var match=System.Text.RegularExpressions.Regex.Match(item.Command,"app-([a-f0-9]{32})",System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        string expected=match.Success?"\""+Path.Combine(ServiceFilePolicy.MachineRoot,"app-"+match.Groups[1].Value,"RelaxDesk.Service.exe")+"\" --service --owner-sid "+owner:"";
        if(item.Type!=16||!item.Account.Equals(@"NT AUTHORITY\LocalService",StringComparison.OrdinalIgnoreCase)||item.Startup is not (2 or 3)||!item.Command.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new SecurityException("Service does not match this user's broker configuration");
        return new(true,item.State,item.Startup,false);
    }
    public static Task<ServiceSetupResult> Install(string package,string owner,bool? automatic,bool start,bool whatIf,CancellationToken ct)=>new ServiceSetupTransaction(new(ServiceFilePolicy.MachineRoot),new WindowsServiceRegistrationStore(),()=>WindowsServiceRegistrationStore.Administrator,inputTasks:new AdminInputTask()).Install(package,owner,automatic,start,whatIf,ct);
    public static Task<ServiceSetupResult> Uninstall(string owner,bool whatIf,CancellationToken ct)=>new ServiceSetupTransaction(new(ServiceFilePolicy.MachineRoot),new WindowsServiceRegistrationStore(),()=>WindowsServiceRegistrationStore.Administrator,inputTasks:new AdminInputTask()).Uninstall(owner,whatIf,ct);
}

internal sealed class ServiceSetupTransaction(ServiceFilePolicy files,IServiceRegistrationStore services,Func<bool> administrator,Action<string>? checkpoint=null,IAdminInputTaskRegistration? inputTasks=null)
{
    void Point(string name)=>checkpoint?.Invoke(name);
    static string Command(ServiceFilePolicy files,string release,string owner)=>"\""+Path.Combine(files.ReleasePath(release),"RelaxDesk.Service.exe")+"\" --service --owner-sid "+LocalAgentPipe.CanonicalUser(owner);
    string WorkerPath(ServiceRelease release) => Path.Combine(files.ReleasePath(release.Id), "ElevatedInput", "RelaxDesk.ElevatedInput.exe");
    void RemoveWorkerTask(ServiceRelease release, string owner)
    {
        if (release.Files.Any(entry => ServicePayload.IsWorkerFile(entry.Path))) inputTasks?.Remove(WorkerPath(release), owner);
    }
    void ValidateRegistration(ServiceRegistration registration,ServiceInstallState? state)
    {
        if(state is null)throw new SecurityException("Existing service has no owned machine installation");
        if(registration.PendingDelete){if(!state.PendingDelete)throw new SecurityException("Unowned pending service deletion");return;}
        BrokerServiceSecurity.Validate(registration.Security,state.Owner);
        if(registration.Type!=16||!registration.Account.Equals(@"NT AUTHORITY\LocalService",StringComparison.OrdinalIgnoreCase)||registration.Startup is not (2 or 3)||!state.Releases.Any(release=>registration.Command.Equals(Command(files,release.Id,state.Owner),StringComparison.OrdinalIgnoreCase)))throw new SecurityException("Existing service configuration is not owned");
    }
    void Preflight(ServiceInstallState? state,string owner)
    {
        if(state is null)return;if(state.Owner!=owner)throw new SecurityException("Approved service user is immutable; explicitly uninstall before changing it");
        foreach(var release in state.Releases)if(Directory.Exists(files.ReleasePath(release.Id))){files.Check(files.ReleasePath(release.Id));_=files.Tree(files.ReleasePath(release.Id)).Count();}
    }
    Mutex Lock()
    {
        string suffix=files.Fixture?Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(files.Root))):"Machine";
        var mutex=new Mutex(false,(files.Fixture?"Local":"Global")+@"\RelaxDesk.Broker.Setup."+suffix);
        try{if(!mutex.WaitOne(0))throw new IOException("Another service setup operation is running");}
        catch(AbandonedMutexException){return mutex;}catch{mutex.Dispose();throw;}return mutex;
    }
    void DemandAdmin()
    {
        if(!administrator())throw new UnauthorizedAccessException("Explicit administrator elevation is required; setup does not request elevation or change policies");
        if(!files.Fixture&&Environment.ProcessPath is {} process&&ServiceFilePolicy.Child(Path.GetFullPath(process),files.Root))throw new IOException("Run maintenance from the external complete bundle, not an installed service executable");
    }
    internal Task<ServiceSetupResult> Install(string package,string owner,bool? automatic,bool start,bool whatIf,CancellationToken ct)=>
        Task.Run(()=>InstallCore(package,owner,automatic,start,whatIf,ct),ct);

    // Keep the Windows mutex on its acquiring thread across SCM polling.
    ServiceSetupResult InstallCore(string package,string owner,bool? automatic,bool start,bool whatIf,CancellationToken ct)
    {
        owner=LocalAgentPipe.CanonicalUser(owner);
        if(!whatIf)DemandAdmin();
        string source=ServiceFilePolicy.FullPath(package);
        if(source.Equals(files.Root,StringComparison.OrdinalIgnoreCase)||ServiceFilePolicy.Child(source,files.Root)||ServiceFilePolicy.Child(files.Root,source))throw new SecurityException("Service source and machine destination overlap");
        var payload=ServicePayload.Read(source);
        var initial=files.ReadState();Preflight(initial,owner);var registration=services.Read();if(registration is not null)ValidateRegistration(registration,initial);
        if(initial?.PendingDelete==true||registration?.PendingDelete==true)throw new IOException("Complete pending service removal before installation");
        uint startup=automatic is {} choice?(choice?2u:3u):registration?.Startup??3u;
        if(whatIf)return new("install",false,files.Root,owner,payload.Version,startup,start||registration?.State==4,false,false);
        using var mutex=Lock();
        try
        {
            var previous=files.ReadState();Preflight(previous,owner);var before=services.Read();if(before is not null)ValidateRegistration(before,previous);
            if(previous?.PendingDelete==true||before?.PendingDelete==true)throw new IOException("Service deletion pending");
            if(previous?.Releases.Length>=4)throw new IOException("Retained release limit reached; review/uninstall retained content first");
            startup=automatic is {} selected?(selected?2u:3u):before?.Startup??3u;
            files.EnsureRoot();var release=payload.CreateRelease();bool committed=false;
            var staged=new ServiceInstallState(1,owner,previous?.Current??release.Id,[..previous?.Releases??[],release],false);
            bool touched=false,stoppedExisting=false;bool wasRunning=before?.State==4;
            bool includesWorker = release.Files.Any(entry => ServicePayload.IsWorkerFile(entry.Path));
            bool inputTaskRegistered = false;
            try
            {
                payload.CopyTo(files,release,owner,Point);
                if (includesWorker) files.AllowOwnerRead(files.Root, owner);
                files.WriteState(staged);Point("after-state");
                if(before is not null&&before.State!=1){stoppedExisting=true;services.Stop(before,ct).GetAwaiter().GetResult();before=services.Read()??throw new IOException("Service disappeared while updating");ValidateRegistration(before,staged);}
                Point("after-stop");
                if(before is null){touched=true;services.Create(Command(files,release.Id,owner),owner);}
                var current=services.Read()??throw new IOException("Service registration missing");ValidateRegistration(current,staged);
                touched=true;services.Configure(current,Command(files,release.Id,owner),startup);Point("after-configure");
                var ready=staged with{Current=release.Id,Ready=true};files.WriteState(ready);Point("after-commit");
                if(start||wasRunning){var configured=services.Read()??throw new IOException("Service registration missing");ValidateRegistration(configured,ready);services.Start(configured,ct).GetAwaiter().GetResult();}
                // Publish parent inspection only after the service update and an optional start have succeeded.
                if (includesWorker && !files.Fixture) files.AllowOwnerParentInspection(owner);
                if (includesWorker && inputTasks is not null)
                {
                    inputTasks.Register(WorkerPath(release), owner);
                    inputTaskRegistered = true;
                }
                Point("after-input-task");
                committed=true;
                var retained=new List<ServiceRelease>{release};
                foreach (var old in previous?.Releases ?? [])
                {
                    RemoveWorkerTask(old, owner);
                    if (!files.RemoveRelease(old)) retained.Add(old);
                }
                files.WriteState(ready with{Releases=retained.ToArray()});
                return new("install",true,files.Root,owner,payload.Version,startup,start||wasRunning,false,retained.Count>1);
            }
            catch
            {
                if(!committed)
                {
                    using var recovery=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try
                    {
                        if (inputTaskRegistered) RemoveWorkerTask(release, owner);
                        var current=services.Read();
                        if(current is not null&&(touched||stoppedExisting))
                        {
                            ValidateRegistration(current,staged);
                            if(current.State!=1&&!current.PendingDelete){services.Stop(current,recovery.Token).GetAwaiter().GetResult();current=services.Read()!;}
                            if(before is null)
                            {if(!current.PendingDelete)services.Delete(current);if(!services.WaitDeleted(recovery.Token).GetAwaiter().GetResult())throw new IOException("Rollback registration deletion pending");}
                            else if(touched)services.Configure(current,before.Command,before.Startup);
                        }
                        if(previous is not null)files.WriteState(previous);else{string marker=Path.Combine(files.Root,"broker-install.json");if(File.Exists(marker)){files.Check(marker);File.Delete(marker);}}
                        if(!files.RemoveRelease(release))
                        {
                            var retained=previous is null?staged with{Ready=false}:previous with{Releases=[..previous.Releases,release]};
                            files.WriteState(retained);
                            throw new IOException("Unverified or modified staging files retained; review the protected installation before retrying removal");
                        }
                        if(previous is null&&!Directory.EnumerateFileSystemEntries(files.Root).Any())Directory.Delete(files.Root);
                        if(wasRunning&&before is not null&&(touched||stoppedExisting)){var restored=services.Read()??throw new IOException("Previous service missing during recovery");ValidateRegistration(restored,previous);services.Start(restored,recovery.Token).GetAwaiter().GetResult();}
                    }
                    catch(Exception recoveryError){throw new IOException("Service setup failed and rollback is incomplete; protected state/files were retained for explicit recovery",recoveryError);}
                }
                throw;
            }
        }
        finally{mutex.ReleaseMutex();}
    }
    internal Task<ServiceSetupResult> Uninstall(string owner,bool whatIf,CancellationToken ct)=>
        Task.Run(()=>UninstallCore(owner,whatIf,ct),ct);

    ServiceSetupResult UninstallCore(string owner,bool whatIf,CancellationToken ct)
    {
        owner=LocalAgentPipe.CanonicalUser(owner);if(!whatIf)DemandAdmin();
        var initial=files.ReadState()??throw new IOException("Owned machine service installation not found");Preflight(initial,owner);
        var registration=services.Read();if(registration is not null)ValidateRegistration(registration,initial);
        if(whatIf)return new("uninstall",false,files.Root,owner,initial.Releases.Single(item=>item.Id==initial.Current).Version,registration?.Startup??3,registration?.State==4,registration?.PendingDelete==true,false);
        using var mutex=Lock();
        try
        {
            var state=files.ReadState()??throw new IOException("Machine installation disappeared");Preflight(state,owner);registration=services.Read();if(registration is not null)ValidateRegistration(registration,state);
            if(registration is not null)
            {
                if(!registration.PendingDelete&&registration.State!=1){services.Stop(registration,ct).GetAwaiter().GetResult();registration=services.Read()??throw new IOException("Service disappeared during removal");ValidateRegistration(registration,state);}
                state=state with{Ready=false,PendingDelete=true};files.WriteState(state);
                if(!registration.PendingDelete)services.Delete(registration);
                if(!services.WaitDeleted(ct).GetAwaiter().GetResult())return new("uninstall",false,files.Root,owner,null,registration.Startup,false,true,true);
            }
            var retained=new List<ServiceRelease>();
            foreach (var release in state.Releases)
            {
                RemoveWorkerTask(release, owner);
                if (!files.RemoveRelease(release)) retained.Add(release);
            }
            if(retained.Count>0)files.WriteState(state with{Ready=false,PendingDelete=false,Current=retained[0].Id,Releases=retained.ToArray()});
            else{string marker=Path.Combine(files.Root,"broker-install.json");files.Check(marker);File.Delete(marker);if(!Directory.EnumerateFileSystemEntries(files.Root).Any())Directory.Delete(files.Root);}
            return new("uninstall",true,files.Root,owner,null,3,false,false,Directory.Exists(files.Root));
        }
        finally{mutex.ReleaseMutex();}
    }
}
