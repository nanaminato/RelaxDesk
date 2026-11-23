using System.Reflection.PortableExecutable;
using System.Security;
using System.Text.Json;

namespace RelaxDesk.Session;

internal sealed class ServicePayload
{
    internal string Source{get;}
    internal string Version{get;}
    internal ServiceFileEntry[] Entries{get;}
    ServicePayload(string source,string version,ServiceFileEntry[] entries){Source=source;Version=version;Entries=entries;}
    internal static ServicePayload Read(string packageRoot)
    {
        string root=ServiceFilePolicy.FullPath(packageRoot);
        using var manifest=JsonDocument.Parse(ServiceFilePolicy.ReadMetadata(Path.Combine(root,"SHA256SUMS.json")),new(){MaxDepth=8});
        if(manifest.RootElement.ValueKind!=JsonValueKind.Array||manifest.RootElement.GetArrayLength() is <1 or >10000)throw new InvalidDataException("Invalid service package manifest");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var selected=new List<ServiceFileEntry>();long bytes=0;
        foreach(var item in manifest.RootElement.EnumerateArray())
        {
            string path=item.GetProperty("path").GetString()??throw new InvalidDataException("Missing manifest path");
            var entry=new ServiceFileEntry(path,item.GetProperty("sha256").GetString()??"",item.GetProperty("bytes").GetInt64());
            _=ServiceFilePolicy.Member(root,path);ServiceFilePolicy.ValidateEntry(entry);
            if(!names.Add(path)||(bytes+=entry.Bytes)>8L*1024*1024*1024||path.Equals("SHA256SUMS.json",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Duplicate/oversized service manifest");
            if(path.StartsWith("Service/",StringComparison.Ordinal)||path.StartsWith("ThirdParty/",StringComparison.Ordinal)||path=="package.json")selected.Add(entry);
        }
        foreach(string required in new[]{"package.json","Service/RelaxDesk.Service.exe","Service/RelaxDesk.Service.dll","Service/RelaxDesk.Service.deps.json","Service/RelaxDesk.Service.runtimeconfig.json","Service/RelaxDesk.Core.dll","Service/RelaxDesk.Session.dll","Service/coreclr.dll","Service/hostfxr.dll","Service/hostpolicy.dll","Service/System.Private.CoreLib.dll"})
            if(!selected.Any(entry=>entry.Path==required))throw new InvalidDataException("Required service package entry missing");
        if(selected.Select(entry=>entry.Path.StartsWith("Service/",StringComparison.Ordinal)?entry.Path[8..]:entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=selected.Count)throw new InvalidDataException("Service deployment paths collide");
        if(!selected.Any(entry=>entry.Path.StartsWith("ThirdParty/",StringComparison.Ordinal)))throw new InvalidDataException("Runtime redistribution notices missing");
        foreach(var entry in selected)
        {
            if(Path.GetFileName(entry.Path) is "RelaxDesk.Native.dll" or "RelaxDesk.Windows.dll" or "PresentationFramework.dll" or "PresentationCore.dll")throw new InvalidDataException("Graphics/UI payload is not a broker service");
            if(!ServiceFilePolicy.Matches(ServiceFilePolicy.Member(root,entry.Path),entry))throw new InvalidDataException("Service package integrity failed");
        }
        using var package=JsonDocument.Parse(ServiceFilePolicy.ReadMetadata(Path.Combine(root,"package.json")),new(){MaxDepth=8});var metadata=package.RootElement;
        string version=metadata.GetProperty("version").GetString()??"";
        if(metadata.GetProperty("format").GetInt32()!=1||metadata.GetProperty("rid").GetString()!="win-x64"||metadata.GetProperty("self_contained").ValueKind!=JsonValueKind.True||version.Length is <1 or >128||version.Any(char.IsControl))throw new InvalidDataException("Unsupported service package");
        using var runtime=JsonDocument.Parse(ServiceFilePolicy.ReadMetadata(Path.Combine(root,"Service","RelaxDesk.Service.runtimeconfig.json")),new(){MaxDepth=8});var options=runtime.RootElement.GetProperty("runtimeOptions");
        if(options.TryGetProperty("framework",out _)||options.TryGetProperty("frameworks",out _)||!options.TryGetProperty("includedFrameworks",out var included)||included.ValueKind!=JsonValueKind.Array||included.GetArrayLength()!=1)throw new InvalidDataException("Service must carry its own .NET runtime");
        var framework=included[0];if(framework.GetProperty("name").GetString()!="Microsoft.NETCore.App"||!System.Version.TryParse(framework.GetProperty("version").GetString(),out var runtimeVersion)||runtimeVersion.Major!=11)throw new InvalidDataException("Stable .NET 11 service runtime required");
        using(var executable=new FileStream(Path.Combine(root,"Service","RelaxDesk.Service.exe"),FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var pe=new PEReader(executable))if(pe.PEHeaders.CoffHeader.Machine!=Machine.Amd64||pe.PEHeaders.PEHeader is null)throw new InvalidDataException("Service apphost must be Windows x64 PE");
        return new(root,version,selected.ToArray());
    }
    internal ServiceRelease CreateRelease()=>new("app-"+Guid.NewGuid().ToString("N"),Version,Entries.Select(entry=>entry with{Path=entry.Path.StartsWith("Service/",StringComparison.Ordinal)?entry.Path[8..]:entry.Path}).ToArray());
    internal void CopyTo(ServiceFilePolicy files,ServiceRelease release,Action<string> checkpoint)
    {
        string directory=files.ReleasePath(release.Id);files.CreateDirectory(directory);int count=0;
        foreach(var original in Entries)
        {
            string relative=original.Path.StartsWith("Service/",StringComparison.Ordinal)?original.Path[8..]:original.Path;
            string target=ServiceFilePolicy.Member(directory,relative);string parent=Path.GetDirectoryName(target)!;
            var missing=new Stack<string>();for(string? current=parent;current is not null&&!Directory.Exists(current);current=Path.GetDirectoryName(current))missing.Push(current);
            while(missing.TryPop(out var next))files.CreateDirectory(next);
            files.Check(parent);string source=ServiceFilePolicy.Member(Source,original.Path);
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=files.CreatePrivateFile(target)){input.CopyTo(output);output.Flush(true);}
            files.Check(target);if(!ServiceFilePolicy.Matches(target,original))throw new InvalidDataException("Source changed while staging service");
            files.AllowServiceRead(target);
            if(++count==3)checkpoint("mid-copy");
        }
        files.ValidateRelease(release,true);checkpoint("after-copy");
    }
}
