using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Session;

internal static class ServiceSetupSmoke
{
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle OpenDirectory(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(SafeFileHandle file,uint code,byte[] input,uint length,nint output,uint bytes,out uint returned,nint overlapped);
    internal static void Junction(string path,string target)
    {
        Directory.CreateDirectory(path);using var handle=OpenDirectory(@"\\?\"+Path.GetFullPath(path),0x40000000,0,0,3,0x02200000,0);
        if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Fixture junction directory unavailable");
        byte[] substitute=Encoding.Unicode.GetBytes(@"\??\"+Path.GetFullPath(target)),printable=Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer=new byte[16+substitute.Length+2+printable.Length+2];BinaryPrimitives.WriteUInt32LittleEndian(buffer,0xA0000003);BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4),checked((ushort)(buffer.Length-8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10),checked((ushort)substitute.Length));BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12),checked((ushort)(substitute.Length+2)));BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14),checked((ushort)printable.Length));
        substitute.CopyTo(buffer,16);printable.CopyTo(buffer,16+substitute.Length+2);
        if(!DeviceIoControl(handle,0x900A4,buffer,(uint)buffer.Length,0,0,out _,0))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Fixture junction unavailable");
    }
    sealed class FakeServices:IServiceRegistrationStore
    {
        internal ServiceRegistration? Current;
        internal int Writes,Stops,Starts,Creates;
        internal bool HoldDeletion,FailNextStart;
        public ServiceRegistration? Read()=>Current;
        public void Create(string command,string owner){if(Current is not null)throw new IOException("Collision");Creates++;Writes++;Current=new(command,@"NT AUTHORITY\LocalService",16,3,1,BrokerServiceSecurity.Descriptor(owner));}
        void Expected(ServiceRegistration expected){if(Current is null||Current.Command!=expected.Command||Current.Startup!=expected.Startup)throw new SecurityException("Concurrent fixture modification");}
        public void Configure(ServiceRegistration expected,string command,uint startup){Expected(expected);Writes++;Current=Current! with{Command=command,Startup=startup};}
        public Task Stop(ServiceRegistration expected,CancellationToken ct){Expected(expected);ct.ThrowIfCancellationRequested();Writes++;Stops++;Current=Current! with{State=1};return Task.CompletedTask;}
        public Task Start(ServiceRegistration expected,CancellationToken ct){Expected(expected);ct.ThrowIfCancellationRequested();Writes++;Starts++;if(FailNextStart){FailNextStart=false;throw new IOException("Injected startup failure");}Current=Current! with{State=4};return Task.CompletedTask;}
        public void Delete(ServiceRegistration expected){Expected(expected);Writes++;Current=Current! with{PendingDelete=true,State=1};}
        public Task<bool> WaitDeleted(CancellationToken ct){ct.ThrowIfCancellationRequested();if(HoldDeletion)return Task.FromResult(false);Current=null;return Task.FromResult(true);}
    }
    internal static async Task Run(string application,string output)
    {
        string parent=ServiceFilePolicy.FullPath(output);if(!parent.Contains("\\build\\",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Setup fixture must be in workspace build directory");
        // Exercise native file creation beyond MAX_PATH even in short source
        // build folders, not only when a user extracts a deeply nested bundle.
        string fixture=Path.Combine(parent,"long-path-"+new string('p',120),"machine-setup-"+Guid.NewGuid().ToString("N"));
        using var identity=WindowsIdentity.GetCurrent();string own=identity.User!.Value;
        var acl=new DirectorySecurity();acl.SetOwner(identity.User);acl.SetAccessRuleProtection(true,false);
        foreach(string sid in new[]{own,ServiceFilePolicy.SystemSid,ServiceFilePolicy.AdminsSid})acl.AddAccessRule(new(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        Directory.CreateDirectory(parent);new DirectoryInfo(fixture).Create(acl);
        int count=0;void Check(bool good){if(!good)throw new IOException($"Machine setup contract {count+1} failed");count++;}
        async Task Reject(Func<Task> action){bool rejected=false;try{await action();}catch(Exception error)when(error is IOException or InvalidDataException or SecurityException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException){rejected=true;}Check(rejected);}
        void RejectPolicy(Action action){bool rejected=false;try{action();}catch(Exception error)when(error is SecurityException or InvalidDataException){rejected=true;}Check(rejected);}
        string serviceFolder=Path.GetDirectoryName(Path.GetFullPath(application))!;
        string Payload(string name,string version)
        {
            string source=Path.Combine(fixture,name);Directory.CreateDirectory(Path.Combine(source,"Service"));Directory.CreateDirectory(Path.Combine(source,"ThirdParty"));
            foreach(string file in new[]{"RelaxDesk.Service.exe","RelaxDesk.Service.dll","RelaxDesk.Service.deps.json","RelaxDesk.Service.runtimeconfig.json","RelaxDesk.Session.dll","RelaxDesk.Core.dll"})File.Copy(Path.Combine(serviceFolder,file),Path.Combine(source,"Service",file));
            // Runtime components are inert IO fixtures, never executed by the simulated SCM.
            foreach(string file in new[]{"coreclr.dll","hostfxr.dll","hostpolicy.dll","System.Private.CoreLib.dll"})File.WriteAllText(Path.Combine(source,"Service",file),"generated runtime stand-in "+file);
            File.WriteAllText(Path.Combine(source,"Service","RelaxDesk.Service.runtimeconfig.json"),JsonSerializer.Serialize(new{runtimeOptions=new{tfm="net11.0",includedFrameworks=new[]{new{name="Microsoft.NETCore.App",version="11.0.0"}}}}));
            File.WriteAllText(Path.Combine(source,"ThirdParty","NOTICE.txt"),"generated notice fixture");
            File.WriteAllText(Path.Combine(source,"package.json"),JsonSerializer.Serialize(new{format=1,version,rid="win-x64",self_contained=true}));
            Manifest(source);return source;
        }
        void Manifest(string source)
        {
            var entries=Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories).Where(file=>Path.GetFileName(file)!="SHA256SUMS.json").Select(file=>new{path=Path.GetRelativePath(source,file).Replace('\\','/'),sha256=ServiceFilePolicy.Hash(file),bytes=new FileInfo(file).Length}).ToArray();
            File.WriteAllText(Path.Combine(source,"SHA256SUMS.json"),JsonSerializer.Serialize(entries));
        }
        string first=Payload("source-one","0.6.0-contract.1"),second=Payload("source-two","0.6.0-contract.2");
        var files=new ServiceFilePolicy(Path.Combine(fixture,"broker 中文 & (Service)"),true);var services=new FakeServices();string fault="";
        var setup=new ServiceSetupTransaction(files,services,()=>true,point=>{if(point==fault)throw new IOException("Injected "+point);});
        var plan=await setup.Install(first,own,null,false,true,CancellationToken.None);
        Check(!plan.Changed&&plan.Startup==3&&!plan.Running&&!Directory.Exists(files.Root)&&services.Writes==0);
        await Reject(async()=>await new ServiceSetupTransaction(files,services,()=>false).Install(first,own,null,false,false,CancellationToken.None));Check(services.Writes==0&&!Directory.Exists(files.Root));
        RejectPolicy(()=>ServiceDeploymentTrust.RequireRuntime(fixture,own));
        var allowed=BrokerServiceSecurity.Descriptor(own);BrokerServiceSecurity.Validate(allowed,own);Check(true);
        RejectPolicy(()=>BrokerServiceSecurity.Validate(new($"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00020025;;;{own})"),own));
        RejectPolicy(()=>BrokerServiceSecurity.Validate(new($"O:{own}G:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)"),own));
        var production=new ServiceFilePolicy(ServiceFilePolicy.MachineRoot);
        var unsafeFile=new FileSecurity();unsafeFile.SetOwner(identity.User);unsafeFile.SetAccessRuleProtection(true,false);unsafeFile.AddAccessRule(new(identity.User,FileSystemRights.FullControl,AccessControlType.Allow));
        RejectPolicy(()=>production.ValidateSecurity(unsafeFile));
        var unsafeRead=new FileSecurity();unsafeRead.SetOwner(new SecurityIdentifier(ServiceFilePolicy.AdminsSid));unsafeRead.SetAccessRuleProtection(true,false);unsafeRead.AddAccessRule(new(new SecurityIdentifier(ServiceFilePolicy.LocalServiceSid),FileSystemRights.FullControl,AccessControlType.Allow));RejectPolicy(()=>production.ValidateSecurity(unsafeRead));
        var safeRead=new FileSecurity();safeRead.SetOwner(new SecurityIdentifier(ServiceFilePolicy.AdminsSid));safeRead.SetAccessRuleProtection(true,false);safeRead.AddAccessRule(new(new SecurityIdentifier(ServiceFilePolicy.LocalServiceSid),FileSystemRights.ReadAndExecute,AccessControlType.Allow));production.ValidateSecurity(safeRead);Check(true);
        var nullDacl=new FileSecurity();nullDacl.SetSecurityDescriptorSddlForm("O:BAG:BAD:NO_ACCESS_CONTROL");RejectPolicy(()=>production.ValidateSecurity(nullDacl));
        var genericWrite=new FileSecurity();genericWrite.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;;GW;;;BU)");RejectPolicy(()=>production.ValidateSecurity(genericWrite));
        foreach(string failure in new[]{"mid-copy","after-copy","after-state","after-configure","after-commit"})
        {
            fault=failure;await Reject(async()=>await setup.Install(first,own,null,false,false,CancellationToken.None));
            Check(services.Current is null&&!Directory.Exists(files.Root));
        }
        fault="";var installed=await setup.Install(first,own,true,true,false,CancellationToken.None);
        Check(installed.Changed&&installed.Startup==2&&installed.Running&&services.Current?.State==4&&services.Current.Startup==2);
        var initial=files.ReadState()!;files.ValidateRelease(initial.Releases.Single(),true);Check(true);
        Check(files.ReleasePath(initial.Current).Length>260);
        ServiceDeploymentTrust.RequireRuntime(files,files.ReleasePath(initial.Current),own);Check(true);
        RejectPolicy(()=>ServiceDeploymentTrust.RequireRuntime(files,files.ReleasePath(initial.Current),"S-1-5-21-1-2-3-99999"));
        files.WriteState(initial with{Ready=false});RejectPolicy(()=>ServiceDeploymentTrust.RequireRuntime(files,files.ReleasePath(initial.Current),own));files.WriteState(initial);
        string initialCommand=services.Current!.Command;Check(initialCommand.StartsWith('"')&&initialCommand.EndsWith("--owner-sid "+own,StringComparison.Ordinal));
        Check(initialCommand.Contains(" 中文 & (Service)",StringComparison.Ordinal));
        foreach(string failure in new[]{"mid-copy","after-copy","after-state","after-stop","after-configure","after-commit"})
        {
            int stops=services.Stops,starts=services.Starts;fault=failure;
            await Reject(async()=>await setup.Install(second,own,null,false,false,CancellationToken.None));
            Check(services.Current?.Command==initialCommand&&services.Current.State==4&&files.ReadState()!.Current==initial.Current);
            Check(Directory.EnumerateDirectories(files.Root).Count()==1);
            if(failure is "mid-copy" or "after-copy" or "after-state")Check(services.Stops==stops&&services.Starts==starts);
        }
        fault="";services.FailNextStart=true;await Reject(async()=>await setup.Install(second,own,null,true,false,CancellationToken.None));
        Check(services.Current?.Command==initialCommand&&services.Current.State==4&&files.ReadState()!.Current==initial.Current);
        int before=services.Writes;
        await Reject(async()=>await setup.Install(second,"S-1-5-21-1-2-3-99999",null,false,false,CancellationToken.None));Check(services.Writes==before);
        var owned=services.Current!;services.Current=owned with{Account="LocalSystem"};await Reject(async()=>await setup.Uninstall(own,false,CancellationToken.None));Check(services.Writes==before);services.Current=owned;
        var updated=await setup.Install(second,own,null,false,false,CancellationToken.None);Check(updated.Startup==2&&updated.Running&&files.ReadState()!.Current!=initial.Current&&!Directory.Exists(files.ReleasePath(initial.Current)));
        var manual=await setup.Install(first,own,false,false,false,CancellationToken.None);Check(manual.Startup==3&&services.Current?.Startup==3&&manual.Running);
        var removePlan=await setup.Uninstall(own,true,CancellationToken.None);Check(!removePlan.Changed&&Directory.Exists(files.Root)&&services.Current is not null);
        string junction=Path.Combine(files.ReleasePath(files.ReadState()!.Current),"unowned-junction");
        Junction(junction,first);
        try{before=services.Writes;await Reject(async()=>await setup.Uninstall(own,false,CancellationToken.None));Check(services.Writes==before&&services.Current is not null);}finally{Directory.Delete(junction);}
        services.HoldDeletion=true;var pending=await setup.Uninstall(own,false,CancellationToken.None);
        Check(pending.PendingDelete&&!pending.Changed&&Directory.Exists(files.Root)&&files.ReadState()!.PendingDelete);
        await Reject(async()=>await setup.Install(second,own,null,false,false,CancellationToken.None));
        services.HoldDeletion=false;var removed=await setup.Uninstall(own,false,CancellationToken.None);Check(removed.Changed&&!removed.RetainedFiles&&!Directory.Exists(files.Root)&&services.Current is null);
        await setup.Install(first,own,null,false,false,CancellationToken.None);var retainedState=files.ReadState()!;string active=files.ReleasePath(retainedState.Current);
        string modified=Path.Combine(active,"ThirdParty","NOTICE.txt");File.WriteAllText(modified,"operator edit");string unknown=Path.Combine(active,"operator-empty-folder");Directory.CreateDirectory(unknown);
        RejectPolicy(()=>ServiceDeploymentTrust.RequireRuntime(files,active,own));
        var retained=await setup.Uninstall(own,false,CancellationToken.None);Check(retained.RetainedFiles&&File.ReadAllText(modified)=="operator edit"&&Directory.Exists(unknown)&&services.Current is null);
        File.WriteAllText(modified,"generated notice fixture");Directory.Delete(unknown);removed=await setup.Uninstall(own,false,CancellationToken.None);Check(!removed.RetainedFiles&&!Directory.Exists(files.Root));
        string bad=Payload("bad-source","0.6.0-bad");File.AppendAllText(Path.Combine(bad,"Service","RelaxDesk.Service.dll"),"tampered");
        await Reject(async()=>await setup.Install(bad,own,null,false,false,CancellationToken.None));Check(!Directory.Exists(files.Root));
        var escape=new[]{new{path="../outside",sha256=new string('A',64),bytes=0}};File.WriteAllText(Path.Combine(bad,"SHA256SUMS.json"),JsonSerializer.Serialize(escape));
        await Reject(async()=>await setup.Install(bad,own,null,false,false,CancellationToken.None));Check(services.Current is null);
        string shared=Payload("bad-runtime","0.6.0-bad-runtime");File.WriteAllText(Path.Combine(shared,"Service","RelaxDesk.Service.runtimeconfig.json"),"{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"11.0.0\"}}}");Manifest(shared);
        await Reject(async()=>await setup.Install(shared,own,null,false,false,CancellationToken.None));
        string collision=Payload("bad-mapping","0.6.0-bad-mapping");Directory.CreateDirectory(Path.Combine(collision,"Service","ThirdParty"));File.WriteAllText(Path.Combine(collision,"Service","ThirdParty","NOTICE.txt"),"collision");Manifest(collision);
        await Reject(async()=>await setup.Install(collision,own,null,false,false,CancellationToken.None));
        // Public commands never mutate SCM in these tests: WhatIf, rejected arguments and standard-token denial only.
        async Task<(int Code,string Text)> Cli(params string[] arguments)
        {
            using var child=System.Diagnostics.Process.Start(ChildProcess.Create(application,arguments))??throw new IOException("Service setup CLI launch failed");
            var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
            try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));return(child.ExitCode,await stdout+await stderr);}
            finally{if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();}
        }
        var invalid=await Cli("--install-service","--package",first,"--owner-sid",own,"--automatic","--manual","--what-if");Check(invalid.Code!=0&&invalid.Text.Contains("ArgumentException"));
        var overrideRoot=await Cli("--install-service","--package",first,"--owner-sid",own,"--install-root",fixture,"--what-if");Check(overrideRoot.Code!=0&&overrideRoot.Text.Contains("ArgumentException"));
        if(!Directory.Exists(ServiceFilePolicy.MachineRoot)&&new WindowsServiceRegistrationStore().Read() is null)
        {
            var preview=await Cli("--install-service","--package",first,"--owner-sid",own,"--what-if");Check(preview.Code==0&&preview.Text.Contains("\"Changed\":false")&&preview.Text.Contains("\"Startup\":3"));Check(!Directory.Exists(ServiceFilePolicy.MachineRoot));
        }
        else Console.WriteLine("SKIP public machine preview: an existing machine installation requires its own owner context");
        if(!WindowsServiceRegistrationStore.Administrator)
        {
            var denied=await Cli("--install-service","--package",first,"--owner-sid",own);Check(denied.Code!=0&&denied.Text.Contains("UnauthorizedAccessException"));
        }
        else Console.WriteLine("SKIP standard-token CLI denial: current process is elevated; no actual install attempted");
        Check(WindowsServiceRegistrationStore.AbiSizes.SequenceEqual(new[]{36,64,28}));
        var windowsProbe=new WindowsServiceRegistrationStore().ReadWindowsProbe();
        Check(windowsProbe is not null&&!windowsProbe.PendingDelete&&windowsProbe.Type is 16 or 32&&windowsProbe.State is >=1 and <=7&&windowsProbe.Command.Length>0&&windowsProbe.Account.Length>0&&windowsProbe.Security.Owner is not null);
        Console.WriteLine($"PASS {count} machine service setup/ACL/transaction contracts: real isolated files and ACLs, simulated SCM, defaults/opt-in, rollback, startup failure, ownership, pending deletion, integrity, retained content and complete cleanup; no service registration/elevation/package. fixture={fixture}");
    }
}
