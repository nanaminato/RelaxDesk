using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Session;

namespace RelaxDesk.HostShell;

internal static class ServiceSetupVerification
{
    sealed class Backend(string owner):IServiceSetupBackend
    {
        internal int Prepared,Runs;
        internal Exception? PreparationError,RunError;
        internal int Code;
        internal TaskCompletionSource<int>? Pending;
        internal Func<Task<MachineServiceStatus>>? Query;
        internal ServiceSetupPlan? Last;
        internal MachineServiceStatus Current=new(false,0,0,false);
        internal Func<CancellationToken,Task<SessionAgentClient>>? Connector;
        internal int Connections;
        public Task<ServiceSetupPlan> Prepare(string host,bool uninstall,bool? automatic)
        {
            Prepared++;if(PreparationError is {} error)throw error;
            return Task.FromResult(new ServiceSetupPlan(@"C:\fixture 中文 & (UI)\bundle",owner,"fixture-version",uninstall,automatic));
        }
        public async Task<int> Run(ServiceSetupPlan plan)
        {Runs++;Last=plan;if(RunError is {} error)throw error;int result=Pending is {} pending?await pending.Task:Code;if(result==0)Current=new(!plan.Uninstall,4,plan.Automatic==true?2u:3u,false);return result;}
        public Task<MachineServiceStatus> Inspect()=>Query?.Invoke()??Task.FromResult(Current);
        public Task<SessionAgentClient> Connect(CancellationToken ct){Connections++;return Connector?.Invoke(ct)??Task.FromException<SessionAgentClient>(new IOException("Fixture connection unavailable"));}
    }
    static IEnumerable<T> Children<T>(DependencyObject parent)where T:DependencyObject
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T item)yield return item;foreach(var nested in Children<T>(child))yield return nested;}}
    static async Task OneClick(HostWindow window,string owner,string output,Action<bool> check,Func<Func<bool>,Task> until)
    {
        int released=0;string name="RelaxDesk.Test."+Guid.NewGuid().ToString("N");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop=new CancellationTokenSource();
        var server=new SessionBrokerServer(owner,name,line=>{if(line.StartsWith("agent_released "))Interlocked.Increment(ref released);});
        var serving=Task.Run(()=>server.Run(stop.Token,()=>ready.SetResult()));
        var backend=new Backend(owner){Pending=new(TaskCreationOptions.RunContinuationsAsynchronously),Connector=ct=>SessionAgentClient.ConnectTest(name,Environment.ProcessId,ct)};
        window.SetupBackend=backend;window.SetupConfirmation=_=>throw new IOException("Simple enable must not ask a second confirmation");
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(window.OwnedWindows.Count == 0);
            window.EnableServiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await until(()=>backend.Runs==1);
            check(window.ServiceSetupBusy&&!window.EnableServiceButton.IsEnabled&&backend.Connections==0);
            await window.EnableSystemService();check(backend.Runs==1);
            backend.Pending.SetResult(0);await until(()=>!window.ServiceSetupBusy&&window.ServiceReady);backend.Pending=null;
            check(window.ServiceRequired&&!window.Host.Running&&window.SetupNotice.Contains("已启用")&&!window.EnableServiceButton.IsEnabled);
            var plan=backend.Last!;check(plan.OnlyIssues&&plan.Automatic is null&&plan.StartInfo().ArgumentList.Contains("--show-issues")&&!plan.StartInfo().ArgumentList.Contains("--show-result"));
            await window.EnableSystemService();check(backend.Runs==1&&backend.Connections==1);
            await window.DisconnectService();window.ServiceRequired=false;await until(()=>Volatile.Read(ref released)>=1);
            await window.EnableSystemService();check(window.ServiceReady&&backend.Runs==1&&backend.Prepared==1&&backend.Connections==2);
            await window.DisconnectService();window.ServiceRequired=false;await until(()=>Volatile.Read(ref released)>=2);
            backend.Current=new(false,0,0,false);backend.RunError=new Win32Exception(1223);
            await window.EnableSystemService();check(!window.ServiceRequired&&!window.Host.Running&&window.SetupNotice.Contains("已取消 Windows")&&backend.Connections==2);backend.RunError=null;
            backend.Code=1;await window.EnableSystemService();check(!window.ServiceReady&&backend.Connections==2&&window.SetupNotice.Contains("退出码 1"));backend.Code=0;
            backend.Current=new(true,0,0,true);int runs=backend.Runs;await window.EnableSystemService();check(backend.Runs==runs&&backend.Connections==2&&window.SetupNotice.Contains("等待删除"));
            backend.Current=new(true,2,3,false);await window.EnableSystemService();check(backend.Runs==runs&&window.SetupNotice.Contains("正在启动或停止"));
            backend.Current=new(true,4,3,false);backend.Connector=_=>Task.FromException<SessionAgentClient>(new IOException("Injected connection failure"));
            await window.EnableSystemService();check(!window.ServiceReady&&window.ServiceRequired&&!window.Host.Running&&window.SetupNotice.Contains("连接失败")&&backend.Runs==runs);
            backend.Connector=ct=>SessionAgentClient.ConnectTest(name,Environment.ProcessId,ct);await window.EnableSystemService();
            check(window.ServiceReady&&backend.Runs==runs&&!window.Host.Running);
            Directory.CreateDirectory(output);
            window.ShowPage(true);
            foreach(var size in new[]{(1040d,780d),(840d,650d)})
            {
                window.Width=size.Item1;window.Height=size.Item2;window.UpdateLayout();Children<TextBlock>(window).Single(item=>item.Text==window.SetupNotice).BringIntoView();await Task.Delay(100);
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,$"one-click-{size.Item1:0}x{size.Item2:0}.png"));png.Save(file);
                check(window.OwnedWindows.Count == 0);
            }
        }
        finally{backend.Pending?.TrySetResult(1);await window.DisconnectService();window.ServiceRequired=false;server.RequestStop();await serving;}
    }
    public static void Run(Application app,string host,string output)
    {
        using var identity=WindowsIdentity.GetCurrent();var backend=new Backend(identity.User!.Value);
        var window=new HostWindow(Path.GetFullPath(host)){SetupBackend=backend,ShowActivated=false};
        int checks=0;void Check(bool good){if(!good)throw new IOException($"Service setup UI contract {checks+1} failed");checks++;}
        async Task Until(Func<bool> predicate){using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!predicate())await Task.Delay(20,stop.Token);}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                Check(!window.ServiceSetupBusy&&!window.Host.Running&&!window.ServiceRequired&&backend.Runs==0);
                Check(window.InstallServiceButton.IsEnabled&&window.UninstallServiceButton.IsEnabled&&window.ServiceStartupChoice.SelectedIndex==0);
                Check(await ServiceSetupBackend.OnSta(()=>Thread.CurrentThread.GetApartmentState())==ApartmentState.STA);
                bool propagated=false;try{await ServiceSetupBackend.OnSta<int>(()=>throw new Win32Exception(1223));}catch(Win32Exception error)when(error.NativeErrorCode==1223){propagated=true;}Check(propagated);
                int confirmations=0;window.SetupConfirmation=plan=>{confirmations++;return false;};
                await window.RunServiceSetup(false);Check(confirmations==1&&backend.Runs==0&&window.SetupNotice.Contains("已取消")&&!window.ServiceSetupBusy);
                backend.PreparationError=new IOException("private fixture text");await window.RunServiceSetup(false);
                Check(confirmations==1&&backend.Runs==0&&window.SetupNotice.Contains("IOException")&&!window.SetupNotice.Contains("private fixture"));backend.PreparationError=null;
                window.SetupConfirmation=_=>true;
                backend.RunError=new Win32Exception(1223);await window.RunServiceSetup(false);
                Check(window.SetupNotice.Contains("已取消 Windows")&&!window.ServiceSetupBusy&&!window.Host.Running);backend.RunError=null;
                backend.Code=1;await window.RunServiceSetup(false);Check(window.SetupNotice.Contains("退出码 1")&&window.SetupNotice.Contains("不要手动删除"));
                backend.Code=2;await window.RunServiceSetup(true);Check(window.SetupNotice.Contains("等待删除")&&window.SetupNotice.Contains("保留文件"));
                backend.Code=0;
                foreach(int index in new[]{0,1,2})
                {
                    window.ServiceStartupChoice.SelectedIndex=index;await window.RunServiceSetup(false);
                    var plan=backend.Last??throw new IOException("Setup launch missing");var info=plan.StartInfo();
                    Check(info.UseShellExecute&&info.Verb=="runas"&&info.WindowStyle==ProcessWindowStyle.Hidden&&!info.RedirectStandardOutput);
                    Check(info.FileName==Path.Combine(plan.Package,"Service","RelaxDesk.Service.exe")&&info.WorkingDirectory==plan.Package);
                    Check(info.ArgumentList.Contains("--start")&&info.ArgumentList.Contains("--show-result")&&info.ArgumentList.Contains(identity.User!.Value));
                    Check(index==0?!info.ArgumentList.Contains("--automatic")&&!info.ArgumentList.Contains("--manual"):info.ArgumentList.Contains(index==2?"--automatic":"--manual"));
                    Check(info.ArgumentList.Contains(plan.Package)&&plan.Owner==identity.User!.Value);
                    Check(!window.Host.Running&&!window.ServiceReady&&!window.ServiceRequired&&window.SetupNotice.Contains("开始共享"));
                }
                await window.RunServiceSetup(true);var remove=backend.Last!.StartInfo();
                Check(remove.ArgumentList[0]=="--uninstall-service"&&!remove.ArgumentList.Contains("--package")&&!remove.ArgumentList.Contains("--start")&&!remove.ArgumentList.Contains("--automatic"));
                backend.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);int previous=backend.Runs;
                window.InstallServiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>backend.Runs==previous+1);
                Check(window.ServiceSetupBusy&&!window.InstallServiceButton.IsEnabled&&!window.UninstallServiceButton.IsEnabled&&!window.ServiceStartupChoice.IsEnabled&&!window.SetupAllowsSharing);
                await window.RunServiceSetup(true);await window.RunServiceSetup(false);Check(backend.Runs==previous+1);
                await window.StartSharing("127.0.0.1",48200,"test-hardware");Check(!window.Host.Running);
                bool tick=false;_ = window.Dispatcher.BeginInvoke(()=>tick=true);await Until(()=>tick);Check(tick);
                window.Close();Check(window.IsVisible&&window.ServiceSetupBusy&&window.SetupNotice.Contains("等待完成后再关闭"));
                backend.Pending.SetResult(0);await Until(()=>!window.ServiceSetupBusy);backend.Pending=null;
                Check(window.InstallServiceButton.IsEnabled&&window.UninstallServiceButton.IsEnabled&&!window.Host.Running);
                // An earlier read-only query must not overwrite post-transaction state.
                var oldQuery=new TaskCompletionSource<MachineServiceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                backend.Query=()=>oldQuery.Task;var scanning=window.InspectServiceInstallation();
                await window.RunServiceSetup(false);backend.Query=()=>Task.FromResult(new MachineServiceStatus(true,4,2,false,AdminWorkerDeployment.AccessDenied));
                oldQuery.SetResult(new(false,0,0,false));await scanning;
                bool accessDeniedShown=window.InstallationStatus.Contains("运行中")&&window.InstallationStatus.Contains("开机自动")&&window.InstallationStatus.Contains("无法读取");
                backend.Query=()=>Task.FromResult(new MachineServiceStatus(true,4,2,false,AdminWorkerDeployment.Verified));
                await window.InspectServiceInstallation();
                Check(accessDeniedShown&&window.InstallationStatus.Contains("部署已验证"));
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");previous=backend.Prepared;
                backend.Query=()=>Task.FromResult(new MachineServiceStatus(true,4,2,false,AdminWorkerDeployment.AccessDenied));
                await window.InspectServiceInstallation();
                if (!window.AdminDeploymentNoticeVisible || !window.Host.Running)
                    throw new IOException("Deployment preflight did not show the administrator repair notice while preserving the listener");
                backend.Query=()=>Task.FromResult(new MachineServiceStatus(true,4,2,false,AdminWorkerDeployment.Verified));
                await window.InspectServiceInstallation();
                if (window.AdminDeploymentNoticeVisible)
                    throw new IOException("Verified deployment retained the administrator repair notice");
                Check(!window.InstallServiceButton.IsEnabled&&!window.UninstallServiceButton.IsEnabled);
                await window.RunServiceSetup(false);Check(backend.Prepared==previous&&window.Host.Running);
                await window.StopSharing();await window.Host.Finished;await Until(()=>window.InstallServiceButton.IsEnabled);
                await OneClick(window,identity.User!.Value,output,Check,Until);
                Directory.CreateDirectory(output);
                window.ShowPage(false);
                window.UpdateLayout();
                var link = Children<TextBlock>(window).SelectMany(item => item.Inlines.OfType<System.Windows.Documents.Hyperlink>())
                    .Single(item => item.Inlines.OfType<System.Windows.Documents.Run>().Any(run => run.Text == "设置系统服务"));
                link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
                await Until(() => window.InstallServiceButton.IsVisible && window.MainScroll.VerticalOffset > 0);
                Check(window.OwnedWindows.Count == 0);
                foreach(var size in new[]{(1040d,780d),(840d,650d)})
                {
                    window.Width=size.Item1;window.Height=size.Item2;window.UpdateLayout();await Task.Delay(100);
                    var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,$"service-setup-{size.Item1:0}x{size.Item2:0}.png"));png.Save(file);
                    Check(window.InstallServiceButton.ActualWidth>150&&window.UninstallServiceButton.ActualWidth>100);
                }
                window.ShowPage(false);
                Console.WriteLine($"PASS {checks} actual WPF service setup UI contracts: one-click enable and real test-pipe automatic connection, running-service fast path, issues-only helper results, original-user SID, advanced startup/removal, cancel/failure/pending/single flight, responsive UI and guarded close/sharing, stale queries and layouts. Fake privileged launcher and same-process test broker; no UAC prompt, native result dialog, installation or SCM mutation.");
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
            finally{backend.Pending?.TrySetResult(1);await window.StopSharing();await window.DisconnectService();window.Close();}
        };
        app.Run(window);
    }
}
