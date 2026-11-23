using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

[assembly:InternalsVisibleTo("RelaxDesk.Smoke")]
namespace RelaxDesk.Session;

internal sealed record ServiceFileEntry(string Path,string Sha256,long Bytes);
internal sealed record ServiceRelease(string Id,string Version,ServiceFileEntry[] Files);
internal sealed record ServiceInstallState(int Format,string Owner,string Current,ServiceRelease[] Releases,bool Ready,bool PendingDelete=false);

internal sealed class ServiceFilePolicy
{
    internal const string SystemSid="S-1-5-18",AdminsSid="S-1-5-32-544",LocalServiceSid="S-1-5-19";
    const FileSystemRights WriteRights=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
    readonly string owner;
    readonly HashSet<string> trusted;
    internal string Root{get;}
    internal bool Fixture{get;}
    internal static string MachineRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"RelaxDesk","Broker");
    internal ServiceFilePolicy(string root,bool fixture=false)
    {
        Root=FullPath(root);Fixture=fixture;
        using var identity=WindowsIdentity.GetCurrent();owner=fixture?identity.User!.Value:AdminsSid;
        trusted=[SystemSid,AdminsSid,owner];
        if(!fixture)trusted.Add(((SecurityIdentifier)new NTAccount(@"NT SERVICE\TrustedInstaller").Translate(typeof(SecurityIdentifier))).Value);
    }
    internal static string FullPath(string path)
    {
        if(string.IsNullOrWhiteSpace(path))throw new SecurityException("Local deployment directory required");
        string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if(full.StartsWith(@"\\",StringComparison.Ordinal)||full.Equals(Path.GetPathRoot(full)?.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||full.Contains('"'))throw new SecurityException("Invalid deployment directory");
        NoReparse(full);return full;
    }
    internal static bool Child(string path,string root)=>path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
    internal static void NoReparse(string path)
    {
        for(string? current=path;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new SecurityException("Reparse deployment path rejected");
    }
    internal static string Member(string root,string relative)
    {
        if(string.IsNullOrEmpty(relative)||relative.Length>240||relative.Contains('\\')||relative.Contains(':')||relative.StartsWith('/'))throw new InvalidDataException("Invalid deployment member");
        foreach(string part in relative.Split('/'))
        {
            if(part.Length==0||part is "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||part.IndexOfAny(Path.GetInvalidFileNameChars())>=0||System.Text.RegularExpressions.Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new InvalidDataException("Unsafe deployment member");
        }
        string full=Path.GetFullPath(Path.Combine(root,relative));if(!Child(full,root))throw new SecurityException("Deployment member escapes root");NoReparse(full);return full;
    }
    internal static byte[] ReadMetadata(string path)
    {
        NoReparse(path);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <2 or >4*1024*1024)throw new InvalidDataException("Deployment metadata size invalid");
        byte[] bytes=new byte[(int)file.Length];file.ReadExactly(bytes);
        return bytes.Length>=3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf?bytes[3..]:bytes;
    }
    internal static string Hash(string path)
    {NoReparse(path);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexString(SHA256.HashData(file));}
    internal static void ValidateEntry(ServiceFileEntry entry)
    {if(entry.Bytes is <0 or >2L*1024*1024*1024||entry.Sha256.Length!=64||!entry.Sha256.All(char.IsAsciiHexDigit))throw new InvalidDataException("Invalid deployment hash/size");}
    internal static bool Matches(string path,ServiceFileEntry entry)=>File.Exists(path)&&new FileInfo(path).Length==entry.Bytes&&Hash(path).Equals(entry.Sha256,StringComparison.OrdinalIgnoreCase);
    internal void ValidateSecurity(FileSystemSecurity security)
    {
        var descriptor=new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(),0);
        if(descriptor.Owner is not {} sid||!trusted.Contains(sid.Value)||descriptor.DiscretionaryAcl is null)throw new SecurityException("Deployment owner/DACL is not trusted");
        foreach(GenericAce generic in descriptor.DiscretionaryAcl)
        {
            if(generic is not CommonAce rule||rule.IsCallback)throw new SecurityException("Unsupported deployment ACE");
            if(rule.AceQualifier==AceQualifier.AccessAllowed&&(rule.AceFlags&AceFlags.InheritOnly)==0&&(rule.AccessMask&((int)WriteRights|0x50000000))!=0&&!trusted.Contains(rule.SecurityIdentifier.Value))throw new SecurityException("Untrusted deployment write permission");
        }
    }
    internal void Check(string path)
    {
        NoReparse(path);
        if(Directory.Exists(path))ValidateSecurity(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access));
        else if(File.Exists(path))ValidateSecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access));
        else throw new FileNotFoundException("Deployment path missing",path);
    }
    internal void CreateDirectory(string path)
    {
        NoReparse(path);
        if(Directory.Exists(path)){Check(path);return;}
        string parent=Path.GetDirectoryName(path)??throw new SecurityException("Deployment parent missing");Check(parent);
        var security=new DirectorySecurity();security.SetOwner(new SecurityIdentifier(owner));security.SetAccessRuleProtection(true,false);
        foreach(string sid in trusted.Where(value=>value==SystemSid||value==AdminsSid||value==owner))security.AddAccessRule(new(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(LocalServiceSid),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);Check(path);
    }
    internal void EnsureRoot()
    {
        CheckBoundary();
        string parent=Path.GetDirectoryName(Root)!;
        if(!Directory.Exists(parent))CreateDirectory(parent);else Check(parent);
        CreateDirectory(Root);
    }
    void CheckBoundary()
    {
        if(Fixture)return;
        if(!Root.Equals(MachineRoot,StringComparison.OrdinalIgnoreCase))throw new SecurityException("Unexpected machine service root");
        Check(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string parent=Path.GetDirectoryName(Root)!;if(Directory.Exists(parent))Check(parent);
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes{public int Length;public nint Descriptor;public int Inherit;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,ref SecurityAttributes security,uint disposition,uint flags,nint template);
    internal FileStream CreatePrivateFile(string path)
    {
        NoReparse(path);Check(Path.GetDirectoryName(path)!);
        string extra=Fixture?$"(A;;FA;;;{owner})":"";
        var descriptor=new RawSecurityDescriptor($"O:{owner}G:BAD:P(A;;FA;;;SY)(A;;FA;;;BA){extra}");byte[] bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);
        var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        try
        {
            var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=pinned.AddrOfPinnedObject()};
            var handle=CreateFile(path,0x40000000,0,ref attributes,1,128,0);
            if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);}
            try{return new FileStream(handle,FileAccess.Write);}catch{handle.Dispose();throw;}
        }
        finally{pinned.Free();}
    }
    internal void AllowServiceRead(string path)
    {
        Check(path);var security=new FileInfo(path).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access);
        security.AddAccessRule(new(new SecurityIdentifier(LocalServiceSid),FileSystemRights.ReadAndExecute,AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);Check(path);
    }
    internal IEnumerable<string> Tree(string directory,bool directories=false)
    {
        var pending=new Stack<string>();pending.Push(directory);int count=0;
        while(pending.TryPop(out var current))
            foreach(string path in Directory.EnumerateFileSystemEntries(current))
            {
                if(++count>20000)throw new InvalidDataException("Deployment tree entry cap exceeded");Check(path);
                if(Directory.Exists(path)){pending.Push(path);if(directories)yield return path;}else if(!directories)yield return path;
            }
    }
    internal string ReleasePath(string id)
    {if(!System.Text.RegularExpressions.Regex.IsMatch(id,"^app-[a-f0-9]{32}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant))throw new InvalidDataException("Invalid managed release");string path=Path.Combine(Root,id);NoReparse(path);return path;}
    internal ServiceInstallState? ReadState()
    {
        if(!Directory.Exists(Root))return null;CheckBoundary();Check(Root);string path=Path.Combine(Root,"broker-install.json");Check(path);
        var state=JsonSerializer.Deserialize<ServiceInstallState>(ReadMetadata(path))??throw new InvalidDataException("Deployment state missing");
        if(state.Format!=1||LocalAgentPipe.CanonicalUser(state.Owner)!=state.Owner||state.Releases is not {Length:>0 and <=4}||state.Releases.Select(item=>item.Id).Distinct(StringComparer.Ordinal).Count()!=state.Releases.Length||!state.Releases.Any(item=>item.Id==state.Current))throw new InvalidDataException("Invalid deployment state");
        foreach(var release in state.Releases)
        {
            _=ReleasePath(release.Id);if(release.Files is not {Length:>0 and <=10000}||string.IsNullOrWhiteSpace(release.Version)||release.Version.Length>128)throw new InvalidDataException("Invalid release state");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long bytes=0;
            foreach(var entry in release.Files){ValidateEntry(entry);_=Member(ReleasePath(release.Id),entry.Path);if(!names.Add(entry.Path)||(bytes+=entry.Bytes)>8L*1024*1024*1024)throw new InvalidDataException("Duplicate/oversized release state");}
        }
        return state;
    }
    internal void WriteState(ServiceInstallState state)
    {
        Check(Root);string target=Path.Combine(Root,"broker-install.json"),temporary=Path.Combine(Root,"state-"+Guid.NewGuid().ToString("N")+".tmp");NoReparse(target);
        try{using(var file=CreatePrivateFile(temporary)){JsonSerializer.Serialize(file,state);file.Flush(true);}AllowServiceRead(temporary);if(File.Exists(target)){Check(target);File.Replace(temporary,target,null);}else File.Move(temporary,target);}
        finally{if(File.Exists(temporary)){Check(temporary);File.Delete(temporary);}}
    }
    internal bool RemoveRelease(ServiceRelease release)
    {
        string directory=ReleasePath(release.Id);if(!Directory.Exists(directory))return true;Check(directory);
        var ownedDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in release.Files)
        {
            string path=Member(directory,entry.Path);
            for(string? parent=Path.GetDirectoryName(path);parent is not null&&Child(parent,directory);parent=Path.GetDirectoryName(parent))ownedDirectories.Add(parent);
            if(File.Exists(path)){Check(path);if(Matches(path,entry))try{File.Delete(path);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){/* Retain locked/read-only owned content. */}}
        }
        foreach(string path in ownedDirectories.OrderByDescending(item=>item.Length))if(Directory.Exists(path)){Check(path);if(!Directory.EnumerateFileSystemEntries(path).Any())Directory.Delete(path);}
        if(!Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);
        return !Directory.Exists(directory);
    }
    internal void ValidateRelease(ServiceRelease release,bool hashes)
    {
        string directory=ReleasePath(release.Id);Check(directory);var actual=Tree(directory).ToArray();
        if(hashes&&actual.Length!=release.Files.Length)throw new InvalidDataException("Unlisted or missing service deployment files");
        foreach(var entry in release.Files){string path=Member(directory,entry.Path);Check(path);if(hashes&&!Matches(path,entry))throw new InvalidDataException("Installed service hash mismatch");}
    }
}

public static class ServiceDeploymentTrust
{
    public static void RequireRuntime(string directory,string owner)=>RequireRuntime(new ServiceFilePolicy(ServiceFilePolicy.MachineRoot),directory,owner);
    internal static void RequireRuntime(ServiceFilePolicy files,string directory,string owner)
    {
        string binaryDirectory=ServiceFilePolicy.FullPath(directory);
        if(!string.Equals(Path.GetDirectoryName(binaryDirectory),files.Root,StringComparison.OrdinalIgnoreCase))throw new SecurityException("Service requires its protected machine deployment");
        files.Check(Path.GetDirectoryName(files.Root)!);var state=files.ReadState()??throw new SecurityException("Service installation state missing");
        if(!state.Ready||state.PendingDelete||state.Owner!=owner||!files.ReleasePath(state.Current).Equals(binaryDirectory,StringComparison.OrdinalIgnoreCase))throw new SecurityException("Service deployment is inactive or mismatched");
        files.ValidateRelease(state.Releases.Single(item=>item.Id==state.Current),true);
    }
}
