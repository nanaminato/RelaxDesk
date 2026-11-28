using RelaxDesk.Core.Localization;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
namespace RelaxDesk.HostShell;

internal sealed record HostEvent(string Kind, string? Address = null, string? SessionId = null, string[]? Features = null,int? ExitCode=null,int Generation=0,bool FullControl=false,string? DeviceFingerprint=null,string? RequestId=null,string? DesktopReason=null, string? ServerFingerprint=null, bool PasswordLogin=false, bool ReconnectOnly=false);

internal sealed class HostProcess
{
    Process? process;
    Task? completion;
    StreamWriter? commandInput;
    bool stopping;
    readonly SemaphoreSlim commands = new(1);
    public string? SessionId { get; private set; }
    public bool Running => process is not null;
    public int Generation{get;private set;}
    internal void TerminateForVerification(){var child=process??throw new InvalidOperationException("No owned child");child.Kill(true);}
    public bool Pending { get; private set; }
    bool fileRootConfigured;
    public event Action<HostEvent>? Changed;
    public event Action<string>? Diagnostic;
    public readonly List<string> Lines = [];
    public string[] Snapshot(){lock(Lines)return Lines.ToArray();}
    public Task Finished => completion ?? Task.CompletedTask;
    public async Task Start(string application,string ip,int port,string backend="dxgi",string? fileRoot=null,string? display=null,RelaxDesk.Core.PasswordVerifier? testVerifier=null,bool serviceInput=false)
    {
        if(Running) throw new InvalidOperationException(Strings.Get("Ui.HostProcess.Text122"));
        if(!IPAddress.TryParse(ip,out var address)||address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||port is <1 or >65535)throw new ArgumentException(Strings.Get("Ui.HostProcess.Text464"));
        if(backend!="dxgi"&&!((backend is "test-hardware" or "test-displays" or "test-clipboard" or "test-clipboard-rich" or "test-clipboard-files" or "test-session-unavailable" or "test-session-locked" or "test-session-switched" or "test-session-desktop" or "test-audio" or "test-audio-failure")&&IPAddress.IsLoopback(address)))throw new ArgumentException(Strings.Get("Ui.HostProcess.Text375"));
        var start=new ProcessStartInfo(Path.GetExtension(application).Equals(".exe",StringComparison.OrdinalIgnoreCase)?application:"dotnet") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!application.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(application);
        start.ArgumentList.Add("--shell");
        if (serviceInput && !backend.StartsWith("test-", StringComparison.Ordinal)) start.Environment["RELAXDESK_SERVICE_INPUT"] = "1";
        else start.Environment.Remove("RELAXDESK_SERVICE_INPUT");
        if (testVerifier is not null && backend.StartsWith("test-", StringComparison.Ordinal))
            start.Environment["RELAXDESK_TEST_PASSWORD_VERIFIER"] = JsonSerializer.Serialize(testVerifier);
        fileRootConfigured=!string.IsNullOrWhiteSpace(fileRoot);
        if(fileRootConfigured){start.ArgumentList.Add("--file-root");start.ArgumentList.Add(Path.GetFullPath(fileRoot!));}
        if(display is not null){start.ArgumentList.Add("--display");start.ArgumentList.Add(display);}
        foreach(string value in new[]{ip,port.ToString(),backend})start.ArgumentList.Add(value);
        stopping=false;Pending=false;SessionId=null;Lines.Clear();
        RestrictedHostLaunch.Child child;
        if (!backend.StartsWith("test-",StringComparison.Ordinal) && RestrictedHostLaunch.Required())
            child=RestrictedHostLaunch.Start(start);
        else
        {
            var ordinary=new Process { StartInfo=start };
            try
            {
                if(!ordinary.Start())throw new IOException(Strings.Get("Ui.HostProcess.Text285"));
                child=new(ordinary,ordinary.StandardInput,ordinary.StandardOutput,ordinary.StandardError);
            }
            catch {ordinary.Dispose();throw;}
        }
        process=child.Process;
        commandInput=child.Input;
        completion=Run(child,++Generation);
        await Task.Yield();
    }
    async Task Run(RestrictedHostLaunch.Child launch,int generation)
    {
        using var input=launch.Input;
        using var output=launch.Output;
        using var error=launch.Error;
        var child=launch.Process;
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
                    if(type is "approval-request" or "native-approval-request")
                    {
                        if(id is not {Length:32}||!id.All(char.IsAsciiHexDigit))throw new IOException("Host request identity missing");
                        SessionId=id;
                    }
                    Pending=type=="approval-request";
                    if(type is "session-ended" or "listening") {SessionId=null;}
                    if(type!="ended")Emit(new(type,address,id,features,FullControl:data.RootElement.TryGetProperty("fullControl",out var fullControl)&&fullControl.ValueKind==JsonValueKind.True,DeviceFingerprint:data.RootElement.TryGetProperty("deviceFingerprint",out var device)?device.GetString():null,RequestId:data.RootElement.TryGetProperty("requestId",out var request)?request.GetString():null,DesktopReason:data.RootElement.TryGetProperty("desktopReason",out var desktop)?desktop.GetString():null,
                        ServerFingerprint:data.RootElement.TryGetProperty("serverFingerprint",out var server)?server.GetString():null,
                        PasswordLogin:data.RootElement.TryGetProperty("passwordLogin",out var login)&&login.ValueKind==JsonValueKind.True,
                        ReconnectOnly:data.RootElement.TryGetProperty("reconnectOnly",out var reconnect)&&reconnect.ValueKind==JsonValueKind.True));continue;
                }
                lock(Lines){if(Lines.Count>=200)Lines.RemoveAt(0);Lines.Add(line);}
                Diagnostic?.Invoke(line);
                if(line.StartsWith("backend="))Emit(new("streaming",SessionId:SessionId));
                if(line.StartsWith("capture_color "))Emit(new("color",line[14..],SessionId));
            }
        }
        try {await Task.WhenAll(Read(output),Read(error),child.WaitForExitAsync());if(child.ExitCode!=0)Diagnostic?.Invoke($"Host exit_code={child.ExitCode}");}
        catch(Exception e){Diagnostic?.Invoke(e.Message);}
        finally {int? exit=child.HasExited?child.ExitCode:null;Pending=false;SessionId=null;process=null;commandInput=null;child.Dispose();Emit(new(stopping?"stopped":"ended",ExitCode:exit));}
    }
    async Task Send(Process child,string command)
    {
        await commands.WaitAsync();
        try
        {
            if(ReferenceEquals(process,child)&&commandInput is {} input)
            {
                await input.WriteLineAsync(command);
                await input.FlushAsync();
            }
        }
        catch(Exception e) when(e is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally {commands.Release();}
    }
    public Task Decide(bool allow,string? expectedSessionId=null,bool newDevice=false)
    {
        var child=process;string? id=expectedSessionId??SessionId;
        if(child is null||!Pending||id is null||id!=SessionId)return Task.CompletedTask;
        Pending=false;return Send(child,(allow?(newDevice?"APPROVE_NEW":"APPROVE"):"DENY")+" "+id);
    }
    public Task RevokeDevice(string fingerprint) =>
        process is { } child ? Send(child, "REVOKE " + fingerprint) : Task.CompletedTask;
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
