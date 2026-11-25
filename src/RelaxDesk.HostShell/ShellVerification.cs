using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    internal void VerifyAdminStatusEvents()
    {
        const string current = "current-session";
        const string previous = "previous-session";
        Changed(new("approval-request", SessionId: current, Mode: "Admin"));
        Changed(new("admin-ready", SessionId: previous));
        if (state.Text != "等待你的授权") throw new IOException("A stale readiness event modified the current session");
        Changed(new("admin-ready", SessionId: current));
        if (state.Text != "正在共享" || !detail.Text.Contains("管理员输入代理已连接"))
            throw new IOException("Full-control readiness status was missing");
        Changed(new("approval-request", SessionId: current, Mode: "Work"));
        activeInput = true;
        Changed(new("streaming", SessionId: current));
        Changed(new("ordinary-input-blocked", SessionId: current));
        Changed(new("ordinary-input-restored", SessionId: previous));
        if (state.Text != "正在共享 · Work" || !detail.Text.Contains("暂时拒绝键鼠"))
            throw new IOException("Input rejection notice lost the current Work session or accepted a stale event");
        Changed(new("ordinary-input-restored", SessionId: current));
        if (!detail.Text.Contains("键鼠操作已恢复")) throw new IOException("Ordinary input recovery notice missing");
        Changed(new("session-ended", SessionId: current));
        Changed(new("admin-ready", SessionId: current));
        if (state.Text != "会话已结束")
            throw new IOException("A delayed administrator event restored ended session controls");
        Changed(new("desktop-unavailable", DesktopReason: "expected-desktop-transition-expired"));
        if (DesktopStatusNotice?.Contains("已超时") != true)
            throw new IOException("The expired desktop transition had no actionable notice");
        RestartEnabled = true;
        restartBudget = new();
        Changed(new("desktop-unavailable", DesktopReason: "host-input-token-denied"));
        Changed(new("ended", ExitCode:2));
        if (RestartPending || !start.IsEnabled || DesktopStatusNotice?.Contains("普通 Windows 用户权限") != true)
            throw new IOException("Host token rejection lost its notice, retried, or disabled manual start");
        RestartEnabled = false;
        Changed(new("stopped"));
    }
}

internal static class ShellVerification
{
    public static void Run(Application app,string host,string output)
    {
        var window=new HostWindow(Path.GetFullPath(host)){ShowActivated=false};
        async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!condition())await Task.Delay(20,timeout.Token);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
        int Port(){var r=new TcpListener(IPAddress.Loopback,0);r.Start();int p=((IPEndPoint)r.LocalEndpoint).Port;r.Stop();return p;}
        void Snapshot(string name){Directory.CreateDirectory(output);window.UpdateLayout();var root=(FrameworkElement)window.Content;var image=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name));encoder.Save(file);}
        window.Loaded+=async(_,_)=>
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                window.VerifyAdminStatusEvents();
                Console.WriteLine("PASS WPF Host administrator status rejects stale session events and clears controls on session end");
                await Until(()=>window.DisplayEnumerationFinished);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Snapshot("host-idle.png");
                if(!window.DisplayConfigurationEnabled||window.DisplayChoiceCount<2)throw new IOException("Physical display metadata choices missing");
                int port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                if(window.DisplayConfigurationEnabled)throw new IOException("Running Host allowed scope selection change");
                using(var viewer=new SessionClient())
                {
                    var connection=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);
                    await Until(()=>window.RequestVisible);await Task.Delay(150,timeout.Token);
                    if(connection.IsCompleted)throw new IOException("Host shell advanced before operator approval");Snapshot("host-request.png");
                    await window.Decide(true);await connection;
                    var frame=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(frame.Data.Length==0||frame.Codec!=MediaCodec.H264||window.RequestVisible)throw new IOException("Shell-approved video missing");
                    await Until(()=>window.Status=="正在共享 · Work");Snapshot("host-sharing.png");
                    await window.StopSharing();await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                }
                if(window.Host.Running||window.Host.Pending)throw new IOException("Stop left child/session running");
                if(!window.DisplayConfigurationEnabled)throw new IOException("Stopped Host did not restore display choice");
                Console.WriteLine("PASS WPF Host display selection: real metadata choices, fixed scope while listening, settings restored after stop; synthetic video only, physical secondary capture pending");
                port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                using(var viewer=new SessionClient())
                {
                    var denied=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);await window.Decide(false);
                    bool rejected=false;try{await denied;}catch(Exception e)when(e is IOException or System.Security.Authentication.AuthenticationException){rejected=true;}
                    if(!rejected)throw new IOException("Shell denial accepted a session");await Until(()=>window.Status=="等待连接");
                    if(!window.Host.Running||window.Host.Pending)throw new IOException("Denial terminated persistent Host");
                    if(window.Host.Snapshot().Any(l=>l.StartsWith("backend=")||l.StartsWith("media frames=")))throw new IOException("Denied session started capture/media");
                }
                // Same process accepts another independently approved session after denial.
                using(var next=new SessionClient { RequestedMode = SessionMode.Work, RequestWorkInputRevoke = true })
                {
                    var connecting=next.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    await window.Decide(true);await connecting;var firstFrame=await MediaProtocol.Read(next.Media,timeout.Token);
                    if (!next.WorkInputRevoke) throw new IOException("Missing negotiated Work input revocation");
                    await window.StopSessionInput();
                    if (await Wire.Read(next.Control,timeout.Token) != new Packet("work-input-revoked-v2"))
                        throw new IOException("Work input revocation did not reach the Viewer");
                    if ((await MediaProtocol.Read(next.Media,timeout.Token)).Sequence <= firstFrame.Sequence)
                        throw new IOException("Work video stalled after input revocation");
                    await window.DisconnectSession();await Until(()=>window.Status=="等待连接");
                    if(!window.Host.Running)throw new IOException("Disconnect ended shared listener");
                }
                await window.StopSharing();await window.Host.Finished;
                foreach (var (backend, reason, notice) in new[]
                {
                    ("test-session-locked", SessionEndReason.WindowsLocked, "Windows 已锁屏"),
                    ("test-session-switched", SessionEndReason.WindowsSessionChanged, "Windows 已切换用户会话"),
                    ("test-session-desktop", SessionEndReason.WindowsDesktopUnavailable, "Host 无法读取当前输入桌面")
                })
                {
                    port = Port();
                    await window.StartSharing("127.0.0.1", port, backend);
                    await Until(()=>window.Status=="等待连接");
                    using (var lost = new SessionClient())
                    {
                        var connecting = lost.ConnectByApproval("127.0.0.1", port, timeout.Token);
                        await Until(() => window.RequestVisible);
                        await window.Decide(true);
                        await connecting;
                        _ = await MediaProtocol.Read(lost.Media, timeout.Token);
                        var terminal = await SessionControl.Read(lost.Control, timeout.Token);
                        if (!ConnectionRecovery.IsExplicitEnd(terminal) || terminal.EndReason != reason)
                            throw new IOException($"Missing terminal notice for {reason}");
                        await Until(() => window.Status == "等待连接" && window.DesktopStatusNotice?.Contains(notice) == true);
                        if (!window.Host.Running) throw new IOException("Session loss stopped persistent listener");
                    }
                    if (reason == SessionEndReason.WindowsLocked)
                    {
                        await window.StopSharing();
                        await window.Host.Finished;
                        port = Port();
                        await window.StartSharing("127.0.0.1", port, backend);
                        await Until(() => window.Status == "等待连接");
                        if (window.DesktopStatusNotice is not null)
                            throw new IOException("Restarted sharing retained the previous listener's lock notice");
                    }
                    using (var fresh = new SessionClient())
                    {
                        var connecting = fresh.ConnectByApproval("127.0.0.1", port, timeout.Token);
                        await Until(() => window.RequestVisible);
                        if (connecting.IsCompleted || window.DesktopStatusNotice is not null)
                            throw new IOException("Session recovery bypassed new consent or retained old notice");
                        await window.Decide(false);
                        try { await connecting; throw new IOException("Recovery denial ignored"); }
                        catch (HostConsentRejectedException) { }
                        await Until(() => window.Status == "等待连接");
                    }
                    await window.StopSharing();
                    await window.Host.Finished;
                }
                Console.WriteLine("PASS WPF persistent Host session-unavailable notice, media teardown, listener recovery and fresh consent; injected state only, no real lock/UAC");
                // Stop while still waiting, without a peer or an approval.
                port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");await window.StopSharing();await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                if(window.Host.Snapshot().Any(l=>l.Contains("pairing token:")||l.Contains("fingerprint:")||l.Contains("relaxdesk://")))throw new IOException("Shell diagnostic log exposed credentials");
                // Separate clipboard opt-in must never inherit the screen/input approval.
                foreach (bool allowClipboard in new[] { false, true })
                {
                    port=Port();await window.StartSharing("127.0.0.1",port,"test-clipboard");await Until(()=>window.Status=="等待连接");
                    using var viewer=new SessionClient{RequestedMode = SessionMode.Work, RequestWorkInputRevoke = true, RequestedFeatures=[SessionCapabilities.ClipboardText]};
                    var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    if(!window.ClipboardChoiceVisible||window.ClipboardAllowed)throw new IOException("Clipboard choice missing or pre-approved");
                    Snapshot("host-clipboard-request.png");window.ClipboardAllowed=allowClipboard;await window.Decide(true);await connecting;
                    if(viewer.Features.Contains(SessionCapabilities.ClipboardText)!=allowClipboard)throw new IOException("Shell clipboard permission was not honored");
                    _=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(allowClipboard)
                    {
                        var initialClipboard = ClipboardTextProtocol.Decode(await Wire.Read(viewer.Control,timeout.Token));
                        await window.StopSessionInput();
                        if (await Wire.Read(viewer.Control, timeout.Token) != new Packet("work-input-revoked-v2"))
                            throw new IOException("Work input revocation did not reach the clipboard-enabled session");
                        await Wire.Write(viewer.Control, ClipboardTextProtocol.Encode(new(Guid.NewGuid().ToString("N"), initialClipboard.Revision + 1, "Work clipboard after input revoke"), 1), timeout.Token);
                        await Wire.Write(viewer.Control,new("ping-v2",2),timeout.Token);
                        if((await Wire.Read(viewer.Control,timeout.Token)).Kind!="pong-v2")throw new IOException("Work input revocation stopped control");
                        await Until(() => window.Host.Snapshot().Any(line => line.Contains("test_clipboard_remote_applied=True")));
                        await window.StopClipboard();var revoked=await Wire.Read(viewer.Control,timeout.Token);
                        if(revoked.Kind!="capability-revoked-v2")throw new IOException("Host clipboard revoke not delivered");
                        await Wire.Write(viewer.Control,new("ping-v2",3),timeout.Token);
                        if((await Wire.Read(viewer.Control,timeout.Token)).Kind!="pong-v2")throw new IOException("Host clipboard revoke stopped control");
                        Console.WriteLine("PASS Work input revoked while independently granted clipboard and TLS control remained usable");
                    }
                    await window.StopSharing();await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                    if(!allowClipboard&&window.Host.Snapshot().Any(line=>line.StartsWith("test_clipboard_")))throw new IOException("Denied clipboard permission constructed provider");
                }
                Console.WriteLine("PASS WPF Host shell: real Host child/listen, explicit approval, H264 media, graceful stop, denial without capture, new approval after denial in same process, session-only disconnect returns to listening, stop while waiting, no credential logs; synthetic loopback source only");
                Console.WriteLine("PASS WPF clipboard permission: default off, request checkbox, screen-only grant denies clipboard, separate opt-in negotiates feature, Host IPC revocation preserves control; isolated test clipboard only");
                foreach(bool allowRich in new[]{false,true})
                {
                    port=Port();await window.StartSharing("127.0.0.1",port,"test-clipboard-rich");await Until(()=>window.Status=="等待连接");
                    using var viewer=new SessionClient{RequestedFeatures=[SessionCapabilities.ClipboardText,SessionCapabilities.ClipboardImage,SessionCapabilities.ClipboardHtml]};
                    var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    if(!window.ClipboardChoiceVisible||window.ClipboardAllowed||!window.ClipboardRequestDescription.Contains("图片")||!window.ClipboardRequestDescription.Contains("HTML"))throw new IOException("Rich clipboard default permission unsafe or formats hidden");
                    window.ClipboardAllowed=allowRich;await window.Decide(true);await connecting;
                    if((viewer.ClipboardData is not null)!=allowRich)throw new IOException("Rich clipboard channel consent mismatch");
                    _=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(allowRich)
                    {
                        var first=await ClipboardContentWire.Read(viewer.ClipboardData!,timeout.Token);
                        if(first.Update.Content.Png is null||first.Update.Content.Html!="<b>Host 中文</b>")throw new IOException("Rich Host provider did not publish");
                        await window.StopClipboard();var stopped=new HashSet<string>();while(stopped.Count<3){var packet=await Wire.Read(viewer.Control,timeout.Token);if(packet.Kind!="capability-revoked-v2"||packet.Features is not[var feature])throw new IOException("Rich revoke acknowledgement missing");stopped.Add(feature);}
                        await Wire.Write(viewer.Control,new("ping-v2",1),timeout.Token);if((await Wire.Read(viewer.Control,timeout.Token)).Kind!="pong-v2")throw new IOException("Rich clipboard revoke killed control");
                    }
                    await window.StopSharing();await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                    if(!allowRich&&window.Host.Snapshot().Any(line=>line.StartsWith("test_clipboard_rich_")))throw new IOException("Denied rich provider constructed");
                }
                Console.WriteLine("PASS WPF image/HTML clipboard consent: exact requested formats/default off, screen-only denies new channel/provider, explicit grant creates separate TLS, Host group revoke keeps control/media; isolated clipboard only");
                string fileRoot=Path.Combine(Path.GetFullPath(output),"file-scope-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fileRoot);
                // No directory means the file request cannot be approved.
                port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                using(var noRoot=new SessionClient{RequestedFeatures=[SessionCapabilities.FileTransfer]})
                {
                    var request=noRoot.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    if(!window.FilesChoiceVisible||window.FilesChoiceEnabled||window.FilesAllowed)throw new IOException("Unselected root exposed file permission");
                    await window.Decide(true);await request;if(noRoot.Data is not null)throw new IOException("Unselected root received data channel");
                    await window.StopSharing();await window.Host.Finished;
                }
                await window.SelectFileRoot(fileRoot);
                foreach(bool allowFiles in new[]{false,true})
                {
                    port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                    using var viewer=new SessionClient{RequestedFeatures=[SessionCapabilities.FileTransfer]};var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    if(!window.FilesChoiceVisible||!window.FilesChoiceEnabled||window.FilesAllowed)throw new IOException("File permission missing/pre-approved");
                    window.FilesAllowed=allowFiles;Snapshot("host-file-request.png");await window.Decide(true);await connecting;
                    if((viewer.Data is not null)!=allowFiles)throw new IOException("Separate file permission not honored");
                    _=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(allowFiles)
                    {
                        await using var files=new FileTransferClient(viewer.Data!,timeout.Token);using var source=new MemoryStream(new byte[]{12,34});await files.Upload("shell-approved.bin",source,false,timeout.Token);
                        if(!File.ReadAllBytes(Path.Combine(fileRoot,"shell-approved.bin")).SequenceEqual(new byte[]{12,34}))throw new IOException("Shell-selected file scope did not transfer");
                        await window.StopFiles();var revoked=await Wire.Read(viewer.Control,timeout.Token);
                        if(revoked.Kind!="capability-revoked-v2"||revoked.Features is not [SessionCapabilities.FileTransfer])throw new IOException("Host file revoke missing");
                        await Wire.Write(viewer.Control,new("ping-v2",1),timeout.Token);if((await Wire.Read(viewer.Control,timeout.Token)).Kind!="pong-v2")throw new IOException("File revoke stopped control");
                    }
                    else if(Directory.EnumerateFileSystemEntries(fileRoot).Any())throw new IOException("Unapproved file permission wrote data");
                    await window.StopSharing();await window.Host.Finished;
                }
                Console.WriteLine("PASS WPF file permission: selected local directory, disabled without root, default unchecked, separate allow/deny, real scoped TLS file upload and live Host file revocation; screen/control remain independent");
                Directory.CreateDirectory(Path.Combine(fileRoot,"host-copy"));
                foreach(var (clip,files) in new[]{(false,true),(true,false),(true,true)})
                {
                    port=Port();await window.StartSharing("127.0.0.1",port,"test-clipboard-files");await Until(()=>window.Status=="等待连接");
                    using var viewer=new SessionClient{RequestedFeatures=[SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardFiles]};var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    if(window.ClipboardAllowed||window.FilesAllowed||!window.ClipboardRequestDescription.Contains("文件清单"))throw new IOException("File clipboard consent dependency/description missing");
                    window.ClipboardAllowed=clip;window.FilesAllowed=files;await window.Decide(true);await connecting;
                    if(viewer.Features.Contains(SessionCapabilities.ClipboardFiles)!=(clip&&files)||(viewer.ClipboardData is not null)!=(clip&&files))throw new IOException("File clipboard bypassed independent consent");
                    _=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(clip&&files)
                    {
                        var offer=await ClipboardContentWire.Read(viewer.ClipboardData!,timeout.Token);if(offer.Update.Content.Files?.Paths is not["host-copy"])throw new IOException("File intent missing");
                        await window.StopFiles();var revoked=new HashSet<string>();while(revoked.Count<2){var packet=await Wire.Read(viewer.Control,timeout.Token);if(packet.Kind!="capability-revoked-v2"||packet.Features is not[var feature])throw new IOException("File intent revocation acknowledgement missing");revoked.Add(feature);}
                        if(!revoked.Contains(SessionCapabilities.FileTransfer)||!revoked.Contains(SessionCapabilities.ClipboardFiles))throw new IOException("File revoke retained clipboard intent");
                        await Wire.Write(viewer.Control,new("ping-v2",1),timeout.Token);if((await Wire.Read(viewer.Control,timeout.Token)).Kind!="pong-v2")throw new IOException("File clipboard revoke killed control");
                    }
                    await window.StopSharing();await window.Host.Finished;
                    if(!(clip&&files)&&window.Host.Snapshot().Any(line=>line.StartsWith("test_clipboard_rich_")))throw new IOException("Denied file clipboard constructed provider");
                }
                Console.WriteLine("PASS WPF file clipboard permission: exact file intent opt-in requires both clipboard and configured file grants, default off, live Host file revoke stops intent while control survives; isolated provider only");
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
            finally{await window.StopSharing();window.Close();}
        };
        app.Run(window);
    }
}
