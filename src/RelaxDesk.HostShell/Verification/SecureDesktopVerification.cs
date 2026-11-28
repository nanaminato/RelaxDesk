using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal static class SecureDesktopVerification
{
    public static void Run(Application app,string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new IOException("Use a fresh UAC probe output directory");
        Directory.CreateDirectory(output);
        var window=new Window{Title="RelaxDesk UAC acceptance",Width=430,Height=160,
            Content=new TextBlock{Text="Secure desktop acceptance is running. Only the test elevation prompt will be cancelled.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(20)}};
        window.Loaded+=async(_,_)=>
        {
            AdminInputConnection? worker=null;bool secureSeen=false,mouseVerified=false,returned=false;long secureEpoch=0;int frames=0;
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await Task.Run(async () =>
            {
            try
            {
                WindowsInputPrivilege.EnsureOrdinaryProcess();
                var selection=new WindowsDisplaySelection("all");
                worker=await AdminInputLauncher.StartService(Guid.NewGuid(),selection.Device,deadline.Token);
                File.WriteAllText(Path.Combine(output,"READY"),"service desktop capture ready");
                while(!returned)
                {
                    var frame=await worker.Desktop!.Capture(selection.Device,deadline.Token);
                    if(frame.Kind==ServiceDesktopKind.Secure)
                    {
                        if(secureSeen){frames++;await Task.Delay(50,deadline.Token);continue;}
                        secureSeen=true;secureEpoch=frame.Epoch;frames++;
                        if(frame.Pixels.All(b=>b==0))throw new IOException("Secure capture is entirely black");
                        var bitmap=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Pixels,frame.Width*4);
                        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                        using(var file=File.Create(Path.Combine(output,"uac-secure.png")))png.Save(file);
                        worker.Desktop.ConfirmDisplayed(frame.Epoch);
                        await worker.Send(new("move",X:50000,Y:10000),deadline.Token);
                        var bounds=selection.Bounds;
                        int expectedX=bounds.X+(int)((50000L*(bounds.Width-1)+32767)/65535);
                        int expectedY=bounds.Y+(int)((10000L*(bounds.Height-1)+32767)/65535);
                        using var movement=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        movement.CancelAfter(TimeSpan.FromSeconds(3));
                        while(!mouseVerified)
                        {
                            var moved=await worker.Desktop.Capture(selection.Device,movement.Token);
                            if(moved.Kind==ServiceDesktopKind.Default||moved.Epoch!=frame.Epoch)throw new IOException("UAC prompt disappeared before input verification");
                            if(moved.Kind==ServiceDesktopKind.Transition){await Task.Delay(50,movement.Token);continue;}
                            mouseVerified=Math.Abs(moved.PointerX-expectedX)<=1&&Math.Abs(moved.PointerY-expectedY)<=1;
                            if(!mouseVerified)
                            {
                                // Motion is a new explicit test event, not replay of a click/key.
                                await worker.Send(new("move",X:50000,Y:10000),movement.Token);
                                await Task.Delay(50,movement.Token);
                            }
                        }
                        await worker.Send(new("key",VirtualKey:27),deadline.Token);
                        await worker.Send(new("key",VirtualKey:27,Up:true),deadline.Token);
                    }
                    else if(secureSeen&&frame.Kind==ServiceDesktopKind.Default&&frame.Epoch>secureEpoch)
                    {worker.Desktop.ConfirmDisplayed(frame.Epoch);returned=true;}
                    await Task.Delay(50,deadline.Token);
                }
                await worker.End(CancellationToken.None);
                File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{status="PASS",secureSeen,mouseVerified,
                    escapeCancelledPrompt=returned,frames,secureEpoch,ordinaryCoordinator=WindowsInputPrivilege.ReadProcessToken(),
                    os=Environment.OSVersion.ToString(),scope="real SCM authenticated local capture and input; Viewer transport tested separately"}));
                Console.WriteLine("PASS real UAC Winlogon capture, verified secure mouse movement, Escape cancellation and Default recovery");
            }
            catch(Exception error)
            {
                if(worker?.Desktop is { } desktop)
                    try{var frame=await desktop.Capture("all",CancellationToken.None);if(frame.Kind==ServiceDesktopKind.Secure){desktop.ConfirmDisplayed(frame.Epoch);await worker.Send(new("key",VirtualKey:27),CancellationToken.None);await worker.Send(new("key",VirtualKey:27,Up:true),CancellationToken.None);}}catch(Exception){}
                File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{status="FAIL",secureSeen,mouseVerified,returned,error=error.ToString()}));
                Console.Error.WriteLine(error);Environment.ExitCode=1;
            }
            finally{if(worker is not null)await worker.DisposeAsync();}
            });
            window.Close();
        };
        app.Run(window);
    }
}
