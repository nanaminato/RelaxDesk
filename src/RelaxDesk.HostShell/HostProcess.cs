using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
namespace RelaxDesk.HostShell;

internal sealed class HostProcess
{
    Process? process;
    Task? completion;
    bool stopping;
    public bool Running => process is not null;
    public bool Pending { get; private set; }
    public bool ClipboardRequested { get; private set; }
    public event Action<string,string?>? Changed;
    public event Action<string>? Diagnostic;
    public readonly List<string> Lines = [];
    public string[] Snapshot(){lock(Lines)return Lines.ToArray();}
    public Task Finished => completion ?? Task.CompletedTask;
    public async Task Start(string application,string ip,int port,string backend="dxgi")
    {
        if(Running) throw new InvalidOperationException("共享已在运行");
        if(!IPAddress.TryParse(ip,out var address)||address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||port is <1 or >65535)throw new ArgumentException("请选择有效的本机 IP 和端口");
        if(backend!="dxgi"&&!((backend is "test-hardware" or "test-clipboard")&&IPAddress.IsLoopback(address)))throw new ArgumentException("测试后端只用于本机回归");
        var start=new ProcessStartInfo(Path.GetExtension(application).Equals(".exe",StringComparison.OrdinalIgnoreCase)?application:"dotnet") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!application.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(application);
        foreach(string value in new[]{"--shell",ip,port.ToString(),backend})start.ArgumentList.Add(value);
        stopping=false;Pending=false;Lines.Clear();
        var child=new Process{StartInfo=start};process=child;
        try { if(!child.Start())throw new IOException("无法启动 Host"); }
        catch {process=null;child.Dispose();throw;}
        completion=Run(child);
        await Task.Yield();
    }
    async Task Run(Process child)
    {
        async Task Read(StreamReader reader)
        {
            while(await reader.ReadLineAsync() is {} line)
            {
                if(line.StartsWith("SHA256 fingerprint:")||line.StartsWith("One-session pairing token:")||line.StartsWith("Connection code:"))continue;
                if(line.StartsWith("RD_EVENT "))
                {
                    using var data=JsonDocument.Parse(line[9..]);string type=data.RootElement.GetProperty("type").GetString()!;
                    string? address=data.RootElement.TryGetProperty("address",out var endpoint)?endpoint.GetString():null;
                    if(type=="approval-request") ClipboardRequested=data.RootElement.TryGetProperty("features",out var requested)&&requested.ValueKind==JsonValueKind.Array&&requested.EnumerateArray().Any(feature=>feature.GetString()==RelaxDesk.Core.SessionCapabilities.ClipboardText);
                    Pending=type=="approval-request";if(type!="ended")Changed?.Invoke(type,address);continue;
                }
                lock(Lines){if(Lines.Count>=200)Lines.RemoveAt(0);Lines.Add(line);}
                Diagnostic?.Invoke(line);
                if(line.StartsWith("backend="))Changed?.Invoke("streaming",null);
                if(line.StartsWith("capture_color "))Changed?.Invoke("color",line[14..]);
            }
        }
        try {await Task.WhenAll(Read(child.StandardOutput),Read(child.StandardError),child.WaitForExitAsync());if(child.ExitCode!=0)Diagnostic?.Invoke($"Host exit_code={child.ExitCode}");}
        catch(Exception e){Diagnostic?.Invoke(e.Message);}
        finally {Pending=false;process=null;child.Dispose();Changed?.Invoke(stopping?"stopped":"ended",null);}
    }
    public async Task Decide(bool allow, bool allowClipboard = false)
    {
        var child=process;if(child is null||!Pending)return;
        Pending=false;await child.StandardInput.WriteLineAsync(allow ? allowClipboard && ClipboardRequested ? "APPROVE_CLIPBOARD" : "APPROVE" : "DENY");await child.StandardInput.FlushAsync();
    }
    public async Task RevokeClipboard()
    {
        var child=process;if(child is null)return;
        await child.StandardInput.WriteLineAsync("REVOKE_CLIPBOARD");await child.StandardInput.FlushAsync();
    }
    public async Task Stop()
    {
        var child=process;if(child is null)return;stopping=true;Pending=false;
        try {await child.StandardInput.WriteLineAsync("STOP");await child.StandardInput.FlushAsync();child.StandardInput.Close();}
        catch(Exception e) when(e is IOException or ObjectDisposedException or InvalidOperationException){}
        try {await Finished.WaitAsync(TimeSpan.FromSeconds(5));}
        catch(TimeoutException){try{child.Kill(true);}catch(InvalidOperationException){}await Finished.WaitAsync(TimeSpan.FromSeconds(5));Diagnostic?.Invoke("Host graceful stop timed out; launched child tree terminated");}
    }
}
