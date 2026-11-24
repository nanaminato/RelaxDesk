using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="IsWindowVisible")]static extern bool ViewportWindowVisible(nint hwnd);
    internal static void VerifyViewport(Application app)
    {
        var window=new ViewerWindow{Width=860,Height=640,ShowActivated=false};int count=0;NativeVideoHost? child=null;
        void Check(bool result){if(!result)throw new IOException($"WPF viewport check {count+1} failed");count++;}
        async Task Ready(){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!window.viewportInputReady)await Task.Delay(20,timeout.Token);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();}
        void Zoom(int percent)=>window.sessionToolbar!.ZoomAction.Items.OfType<MenuItem>().Single(item=>item.Tag is int value&&value==percent).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        void Pixel(int x,int y,int b,int g,int r)
        {
            var dpi=VisualTreeHelper.GetDpi(window.videoPanel);var bitmap=new RenderTargetBitmap((int)Math.Round(window.videoPanel.ActualWidth*dpi.DpiScaleX),(int)Math.Round(window.videoPanel.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);bitmap.Render(window.videoPanel);
            byte[] value=new byte[4];bitmap.CopyPixels(new Int32Rect(x,y,1,1),value,4,0);if(value[0]!=b||value[1]!=g||value[2]!=r)throw new IOException($"WPF viewport fixture pixel actual={value[0]},{value[1]},{value[2]} expected={b},{g},{r}");Check(true);
        }
        window.Loaded+=async(_,_)=>
        {
            try
            {
                window.StartLocalPreview();window.previewTimer!.Stop();byte[] pixels=new byte[1200*800*4];
                for(int y=0;y<800;y++)for(int x=0;x<1200;x++){int p=(y*1200+x)*4;pixels[p]=(byte)(x<600?40:180);pixels[p+1]=(byte)(y<400?30:160);pixels[p+2]=(byte)(x<600?160:40);pixels[p+3]=255;}
                window.image.Source=BitmapSource.Create(1200,800,96,96,PixelFormats.Bgr32,null,pixels,4800);window.SetViewportVideoSize(1200,800);await Ready();
                Zoom(100);Check(!window.viewportInputReady);await Ready();
                Check(window.horizontalPan.IsVisible&&window.verticalPan.IsVisible);window.horizontalPan.Value=0;window.verticalPan.Value=0;await Ready();
                Check(window.TryViewportLayout(out var view)&&view.ScaleX==1&&view.ScaleY==1&&view.SourceX==0&&view.SourceY==0);Pixel(20,20,40,30,160);
                Zoom(200);await Ready();window.horizontalPan.Value=window.horizontalPan.Maximum;window.verticalPan.Value=window.verticalPan.Maximum;await Ready();
                Check(window.TryViewportLayout(out view)&&view.SourceX>600&&view.SourceY>400);Pixel(20,20,180,160,40);
                Check(view.TryNormalize(0,0,false,out int nx,out int ny)&&nx>32767&&ny>32767);
                var custom=window.sessionToolbar!.ZoomAction.Items.OfType<MenuItem>().Single(item=>item.Header is StackPanel);var panel=(StackPanel)custom.Header;
                panel.Children.OfType<TextBox>().Single().Text="137";panel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Ready();Check(window.CurrentViewport.Zoom==137);
                Zoom(400);await Ready();Zoom(0);await Ready();Check(!window.horizontalPan.IsVisible&&!window.verticalPan.IsVisible);Check(window.TryViewportLayout(out view)&&view.SourceWidth==1200&&view.SourceHeight==800);
                if(view.DestinationY>2)Pixel(0,0,0,0,0);else if(view.DestinationX>2)Pixel(0,0,0,0,0);
                using (var inputLifetime = new CancellationTokenSource())
                {
                    window.session = inputLifetime;
                    window.inputAuthorized = true;
                    window.inputVersion = InputProtocol.Version;
                    var center = new Point(window.videoPanel.ActualWidth / 2, window.videoPanel.ActualHeight / 2);
                    window.SendBitmapWheel(new Point(-1, -1), 120);
                    window.SendBitmapWheel(center, 0);
                    Check(window.outgoing.Depth == 0);
                    window.SendBitmapWheel(center, -30);
                    Check(window.outgoing.TryRead(out var move) && move?.Input is { Kind:"move", X:>32000 and <33500, Y:>32000 and <33500 });
                    Check(window.outgoing.TryRead(out var wheel) && wheel?.Input is { Kind:"wheel", WheelY:-30 } && wheel.Sequence > move!.Sequence);
                    window.ResetInputState();
                    window.session = null;
                }
                window.Width=700;window.Height=500;await Task.Delay(100);await Ready();Check(window.TryViewportLayout(out view)&&view.SourceWidth==1200);
                Zoom(100);await Ready();window.ToggleFullScreen();await Task.Delay(100);await Ready();window.ToggleFullScreen();await Task.Delay(100);await Ready();Check(window.CurrentViewport.Zoom==100);
                // Real child HWND layout/input-adapter geometry, without foreground
                // keyboard/mouse injection or claiming a GPU Present.
                child=new NativeVideoHost();window.videoPanel.Children.Add(child);window.UpdateLayout();child.SetVideoSize(1200,800);child.SetViewport(new(200,1,1));Check(child.Handle!=0);
                var dpi=VisualTreeHelper.GetDpi(window.videoPanel);int width=(int)Math.Round(window.videoPanel.ActualWidth*dpi.DpiScaleX),height=(int)Math.Round(window.videoPanel.ActualHeight*dpi.DpiScaleY);
                Check(child.TryPosition(0,0,width,height,false,out nx,out ny)&&nx==(int)((1200-Math.Round(width/2d,MidpointRounding.AwayFromZero))/1199*65535)&&ny==(int)((800-Math.Round(height/2d,MidpointRounding.AwayFromZero))/799*65535));
                Check(!child.TryPosition(-1,-1,width,height,false,out _,out _));Check(child.TryPosition(-1,-1,width,height,true,out _,out _));
                var known=new DisplayInfo("fixture",new(0,0,1200,800),0,96,96,true,false,new string('A',64));var initial=new DisplaySessionInfo([known],[known.Id],known.Id,1);var scope=new DisplayTopologyAuthorization(initial);
                window.displaySession=new(initial,true);window.nativeView=child;window.gpuViewportActive=true;
                var paused=scope.Reconcile([],2);window.displaySession.ReceiveTopology(paused);window.ApplyDisplayTopology(paused,new CpuBitmapRenderer(window.image));window.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Check(child.Visibility==Visibility.Collapsed&&!ViewportWindowVisible(child.Handle)&&window.displayUnavailable.IsVisible);
                var available=scope.Reconcile([known],3);window.displaySession.ReceiveTopology(available);window.ApplyDisplayTopology(available,new CpuBitmapRenderer(window.image));window.UpdateLayout();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Check(child.Visibility==Visibility.Visible&&ViewportWindowVisible(child.Handle)&&!window.displayUnavailable.IsVisible);
                window.EndDisplaySession();window.nativeView=null;window.gpuViewportActive=false;
                window.videoPanel.Children.Remove(child);child.Dispose();child=null;
                window.StopLocalPreview();Check(window.viewportVideoWidth==0&&!window.viewportInputReady&&!window.horizontalPan.IsVisible&&!window.verticalPan.IsVisible);Check(GraphicsDiagnostics.Resources().Empty);
                Console.WriteLine($"PASS {count} actual WPF viewport menu/1:1/zoom/custom/pan/crop-pixel/fit/resize/fullscreen/HWND-map/teardown checks; synthetic bitmap and owned HWND only, no physical DPI/Present/input claim");
                window.Close();app.Shutdown();
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;if(child is not null){window.videoPanel.Children.Remove(child);child.Dispose();}window.StopLocalPreview();window.Close();app.Shutdown(1);}
        };
        app.Run(window);
    }
}
