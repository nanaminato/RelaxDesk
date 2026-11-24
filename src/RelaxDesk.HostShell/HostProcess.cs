using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
namespace RelaxDesk.HostShell;

internal sealed record HostEvent(string Kind, string? Address = null, string? SessionId = null, string[]? Features = null,int? ExitCode=null,int Generation=0,string? Mode=null,string? DeviceFingerprint=null,string? RequestId=null);

internal sealed class HostProcess
{
    Process? process;
    Task? completion;
    bool stopping;
    readonly SemaphoreSlim commands = new(1);
    public string? SessionId { get; private set; }
    public bool Running => process is not null;
    public int Generation{get;private set;}
    internal void TerminateForVerification(){var child=process??throw new InvalidOperationException("No owned child");child.Kill(true);}
    public bool Pending { get; private set; }
    public bool ClipboardRequested { get; private set; }
    public bool FilesRequested { get; private set; }
    public bool AudioRequested{get;private set;}
    bool fileRootConfigured;
    public event Action<HostEvent>? Changed;
    public event Action<string>? Diagnostic;
    public readonly List<string> Lines = [];
    public string[] Snapshot(){lock(Lines)return Lines.ToArray();}
    public Task Finished => completion ?? Task.CompletedTask;
    public async Task Start(string application,string ip,int port,string backend="dxgi",string? fileRoot=null,string? display=null, bool guestOnly=false, bool adminDisabled=false)
    {
        if(Running) throw new InvalidOperationException("共享已在运行");
        if(!IPAddress.TryParse(ip,out var address)||address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||port is <1 or >65535)throw new ArgumentException("请选择有效的本机 IP 和端口");
        if(backend!="dxgi"&&!((backend is "test-hardware" or "test-displays" or "test-clipboard" or "test-clipboard-rich" or "test-clipboard-files" or "test-session-unavailable" or "test-audio" or "test-audio-failure")&&IPAddress.IsLoopback(address)))throw new ArgumentException("测试后端只用于本机回归");
        var start=new ProcessStartInfo(Path.GetExtension(application).Equals(".exe",StringComparison.OrdinalIgnoreCase)?application:"dotnet") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!application.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(application);
        start.ArgumentList.Add("--shell");
        if (guestOnly) start.ArgumentList.Add("--guest-only");
        if (adminDisabled) start.ArgumentList.Add("--no-admin");
        fileRootConfigured=!string.IsNullOrWhiteSpace(fileRoot);
        if(fileRootConfigured){start.ArgumentList.Add("--file-root");start.ArgumentList.Add(Path.GetFullPath(fileRoot!));}
        if(display is not null){start.ArgumentList.Add("--display");start.ArgumentList.Add(display);}
        foreach(string value in new[]{ip,port.ToString(),backend})start.ArgumentList.Add(value);
        stopping=false;Pending=false;SessionId=null;Lines.Clear();
        var child=new Process{StartInfo=start};process=child;
        try { if(!child.Start())throw new IOException("无法启动 Host"); }
        catch {process=null;child.Dispose();throw;}
        completion=Run(child,++Generation);
        await Task.Yield();
    }
    async Task Run(Process child,int generation)
    {
        void Emit(HostEvent value)=>Changed?.Invoke(value with{Generation=generation});
        async Task Read(StreamReader reader)
        {
            while(await reader.ReadLineAsync() is {} line)
            {
                if(line.StartsWith("SHA256 fingerprint:")||line.StartsWith("One-session pairing token:")||line.StartsWith("Connection code:"))continue;
                if(line.StartsWith("RD_EVENT "))
                {
                    using var data=JsonDocument.Parse(line[9..]);string type=data.RootElement.GetProperty("type").GetString()!;
                    string? address=data.RootElement.TryGetProperty("address",out var endpoint)?endpoint.GetString():null;
                    string? id=data.RootElement.TryGetProperty("sessionId",out var identity)?identity.GetString():null;
                    string[] features=data.RootElement.TryGetProperty("features",out var offered)&&offered.ValueKind==JsonValueKind.Array?offered.EnumerateArray().Select(feature=>feature.GetString()!).ToArray():[];
                    if(type=="approval-request")
                    {
                        if(id is not {Length:32}||!id.All(char.IsAsciiHexDigit))throw new IOException("Host request identity missing");
                        SessionId=id;ClipboardRequested=features.Any(RelaxDesk.Core.SessionCapabilities.IsClipboard);FilesRequested=features.Contains(RelaxDesk.Core.SessionCapabilities.FileTransfer);AudioRequested=features.Contains(RelaxDesk.Core.SessionCapabilities.SystemAudio);
                    }
                    Pending=type=="approval-request";
                    if(type is "session-ended" or "listening") {SessionId=null;ClipboardRequested=FilesRequested=AudioRequested=false;}
                    if(type!="ended")Emit(new(type,address,id,features,Mode:data.RootElement.TryGetProperty("mode",out var mode)?mode.GetString():null,DeviceFingerprint:data.RootElement.TryGetProperty("deviceFingerprint",out var device)?device.GetString():null,RequestId:data.RootElement.TryGetProperty("requestId",out var request)?request.GetString():null));continue;
                }
                lock(Lines){if(Lines.Count>=200)Lines.RemoveAt(0);Lines.Add(line);}
                Diagnostic?.Invoke(line);
                if(line.StartsWith("backend="))Emit(new("streaming",SessionId:SessionId));
                if(line.StartsWith("capture_color "))Emit(new("color",line[14..],SessionId));
            }
        }
        try {await Task.WhenAll(Read(child.StandardOutput),Read(child.StandardError),child.WaitForExitAsync());if(child.ExitCode!=0)Diagnostic?.Invoke($"Host exit_code={child.ExitCode}");}
        catch(Exception e){Diagnostic?.Invoke(e.Message);}
        finally {int? exit=child.HasExited?child.ExitCode:null;Pending=false;SessionId=null;ClipboardRequested=FilesRequested=AudioRequested=false;process=null;child.Dispose();Emit(new(stopping?"stopped":"ended",ExitCode:exit));}
    }
    async Task Send(Process child,string command)
    {
        await commands.WaitAsync();
        try {await child.StandardInput.WriteLineAsync(command);await child.StandardInput.FlushAsync();}
        catch(Exception e) when(e is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally {commands.Release();}
    }
    public Task Decide(bool allow, bool allowClipboard = false, bool allowFiles = false, string? expectedSessionId = null,bool allowAudio=false,bool guestInput=false,bool adminConfirmed=false)
    {
        var child=process;string? id=expectedSessionId??SessionId;if(child is null||!Pending||id is null||id!=SessionId)return Task.CompletedTask;
        bool clipboard=allowClipboard&&ClipboardRequested,files=allowFiles&&FilesRequested&&fileRootConfigured;
        string command=RelaxDesk.Core.AudioConsent.Command(allow,clipboard,files,allowAudio&&AudioRequested);
        Pending=false;return Send(child,command+(allow&&adminConfirmed?"_ADMIN":allow&&guestInput?"_INPUT":"")+" "+id);
    }
    public Task RevokeClipboard(string? expectedSessionId=null)=>SessionCommand("REVOKE_CLIPBOARD",expectedSessionId);
    public Task RevokeFiles(string? expectedSessionId=null)=>SessionCommand("REVOKE_FILES",expectedSessionId);
    public Task RevokeAdminInput(string? expectedSessionId=null)=>SessionCommand("REVOKE_ADMIN_INPUT",expectedSessionId);
    public Task DecideAdmin(bool allow, string sessionId, string requestId)
    {
        var child = process;
        if (child is null || sessionId != SessionId || !Guid.TryParseExact(requestId, "N", out _))
            return Task.CompletedTask;
        string command = allow ? "ALLOW_ADMIN_INPUT" : "DENY_ADMIN_INPUT";
        return Send(child, $"{command} {sessionId} {requestId}");
    }
    public Task RevokeGuestInput(string? expectedSessionId=null)=>SessionCommand("REVOKE_GUEST_INPUT",expectedSessionId);
    public Task RevokeAudio(string? expectedSessionId=null)=>SessionCommand("REVOKE_AUDIO",expectedSessionId);
    public Task Disconnect(string? expectedSessionId=null)=>SessionCommand("DISCONNECT",expectedSessionId);
    Task SessionCommand(string command,string? expectedSessionId)
    {
        var child=process;string? id=expectedSessionId??SessionId;
        return child is null||id is null||id!=SessionId?Task.CompletedTask:Send(child,command+" "+id);
    }
    public async Task Stop()
    {
        var child=process;if(child is null)return;stopping=true;Pending=false;
        try {await Send(child,"STOP");}
        catch(Exception e) when(e is IOException or ObjectDisposedException or InvalidOperationException){}
        try {await Finished.WaitAsync(TimeSpan.FromSeconds(5));}
        catch(TimeoutException){try{child.Kill(true);}catch(InvalidOperationException){}await Finished.WaitAsync(TimeSpan.FromSeconds(5));Diagnostic?.Invoke("Host graceful stop timed out; launched child tree terminated");}
    }
}
