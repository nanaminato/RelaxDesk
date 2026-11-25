using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifyInputUi(Application app)
    {
        var window=new ViewerWindow{ShowActivated=false};int count=0;
        void Check(bool result){if(!result)throw new IOException($"Viewer input contract {count+1} failed");count++;}
        window.Loaded+=async(_,_)=>
        {
            TextInputWindow? dialog=null;
            try
            {
                Check(window.requestClipboard.IsChecked==true&&window.requestClipboardImage.IsChecked==true&&window.requestClipboardHtml.IsChecked==true&&window.requestFileClipboard.IsChecked==true&&window.requestFileClipboard.IsEnabled);
                using var lifetime=new CancellationTokenSource();window.session=lifetime;window.inputVersion=2;
                window.SendRawKey(65,(nint)((30L<<16)|1),false);Check(window.outgoing.Depth==0);
                window.inputAuthorized=true;
                window.SendRawKey(65,(nint)((30L<<16)|1),false);Check(window.outgoing.Depth==0);
                // Isolated non-injecting adapter fixture: authorize a submitted
                // viewport independently of the renderer tests.
                window.viewportInputReady=true;
                window.SendRawKey(0x11,(nint)((29L<<16)|(1L<<24)|1),false);
                Check(window.outgoing.TryRead(out var key)&&key?.Input is{VirtualKey:0xA3,ScanCode:29,Extended:true,Up:false});
                Check(window.richHeld.Count==1);
                window.SendRawKey(0x11,(nint)((29L<<16)|(1L<<24)|(1L<<30)|65),false);
                int repeats=0;while(window.outgoing.TryRead(out var repeat)){Check(repeat!.Input!.Repeat);repeats+=repeat.Input.RepeatCount;}
                Check(repeats==65&&window.richHeld.Count==1);
                window.SendRich(new("button",Button:5));window.ReleaseMouseButtons();
                Check(window.richHeld.Count==1);Check(window.outgoing.TryRead(out var button)&&button!.Input is{Button:5,Up:false});Check(window.outgoing.TryRead(out button)&&button!.Input is{Button:5,Up:true});
                window.ReleaseHeldInputs();Check(window.richHeld.Count==0);Check(window.outgoing.TryRead(out key)&&key!.Input is{VirtualKey:0xA3,ScanCode:29,Extended:true,Up:true});
                window.SendRich(new("wheel",WheelX:-7,WheelY:19));Check(window.outgoing.TryRead(out var wheel)&&wheel!.Input is{WheelX:-7,WheelY:19});
                window.Send(0,false,17,21);window.Send(0,false,29,32);window.Send(256,false);window.Send(0,false,44,55);
                Check(window.outgoing.Depth==3);Check(window.outgoing.TryRead(out var move)&&move!.Input is{Kind:"move",X:29,Y:32});Check(window.outgoing.TryRead(out button)&&button!.Input is{Button:1,Up:false});Check(window.outgoing.TryRead(out move)&&move!.Input is{Kind:"move",X:44,Y:55});
                window.ResetInputState();Check(!window.inputAuthorized&&window.inputVersion==0&&window.richHeld.Count==0);
                window.Send(65,false);Check(window.outgoing.Depth==0);
                window.inputAuthorized=true;window.Send(65,false);Check(window.outgoing.TryRead(out key)&&key!.Kind=="input"&&key.Code==65);window.ReleaseHeldInputs();while(window.outgoing.TryRead(out _)){}
                window.inputVersion=2;
                var texts=new List<string>();dialog=new TextInputWindow(window,text=>{texts.Add(text);return true;});dialog.Show();
                Check(!dialog.Submit.IsEnabled);
                dialog.Editor.Text="draft";Check(dialog.Submit.IsEnabled&&texts.Count==0);
                var composition=new TextComposition(InputManager.Current,dialog.Editor,"candidate");
                dialog.Editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,composition){RoutedEvent=TextCompositionManager.PreviewTextInputStartEvent});
                Check(dialog.Composing&&!dialog.Submit.IsEnabled);
                dialog.Submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(texts.Count==0);
                dialog.Editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,composition){RoutedEvent=TextCompositionManager.PreviewTextInputUpdateEvent});Check(dialog.Composing);
                dialog.Editor.Text="中文 日本語 😀\r\n";
                dialog.Editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,new TextComposition(InputManager.Current,dialog.Editor,"中文")){RoutedEvent=TextCompositionManager.PreviewTextInputEvent});
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!dialog.Composing&&dialog.Submit.IsEnabled&&texts.Count==0);
                dialog.Submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(texts.Count==1&&texts[0]=="中文 日本語 😀\r\n"&&dialog.Editor.Text.Length==0);
                dialog.Submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(texts.Count==1);
                dialog.Editor.Text="unsent";dialog.Close();Check(dialog.Editor.Text.Length==0&&texts.Count==1);dialog=null;
                window.OpenTextInput();Check(window.textInput is not null);window.textInput!.Editor.Text="do not replay";
                window.ResetInputState();Check(window.textInput is null&&!window.inputAuthorized);
                Check(KeyboardFocusVerification.RejectsBackgroundPointer(window));
                var probe = new DesktopInputProbe(new(false, false, 0x2000)) { Owner = window };
                try
                {
                    probe.Show();
                    var source = PresentationSource.FromVisual(probe) ?? throw new IOException("Input target did not create an HWND");
                    probe.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F7) { RoutedEvent = PreviewKeyDownEvent });
                    probe.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F8) { RoutedEvent = PreviewKeyDownEvent });
                    probe.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F8) { RoutedEvent = PreviewKeyUpEvent });
                    probe.Target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonDownEvent });
                    probe.Target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = PreviewMouseLeftButtonUpEvent });
                    Check(probe.Result == new DesktopInputProbeResult(false, 0x2000, 1, 1, 1, 1));
                }
                finally { probe.Close(); }
                foreach (int version in new[] { 0, InputProtocol.Version })
                {
                    using var overflowLifetime = new CancellationTokenSource();
                    window.session = overflowLifetime;
                    window.inputAuthorized = true;
                    window.inputVersion = version;
                    long dropsBefore = window.droppedPointerMoves;
                    for (int i = 0; i < 256; i++) window.Send(65, false);
                    Check(window.outgoing.Depth == 256 && !overflowLifetime.IsCancellationRequested);
                    window.SendPointerMove(90, 91);
                    Check(window.outgoing.Depth == 256 && !overflowLifetime.IsCancellationRequested && window.droppedPointerMoves == dropsBefore + 1);
                    Check(window.outgoing.TryRead(out key) && (version == 0
                        ? key is { Kind:"input", Code:65 } : key?.Input is { Kind:"key", VirtualKey:65 }));
                    window.Send(65, true);
                    Check(window.outgoing.Depth == 256 && !overflowLifetime.IsCancellationRequested);
                    window.Send(0, false, 17, 21);
                    Check(overflowLifetime.IsCancellationRequested && window.droppedPointerMoves == dropsBefore + 1);
                    Check(window.outgoing.TryRead(out key));
                    window.Send(256, false);
                    Check(window.outgoing.Depth == 255);
                    while (window.outgoing.TryRead(out _)) { }
                    window.session = null;
                }
                window.session = lifetime;
                window.inputAuthorized = true;
                window.inputVersion = InputProtocol.Version;
                window.ShowConnectedView(true);
                window.UpdateLayout();
                var toolbar = window.sessionToolbar!;
                var screenPoint = window.windowedSessionControls.PointToScreen(new Point(20, 10));
                var bitmapPoint = window.videoPanel.PointFromScreen(screenPoint);
                foreach (int version in new[] { 0, InputProtocol.Version })
                {
                    window.inputVersion = version;
                    window.viewportInputReady = true;
                    Check(!window.BitmapPosition(bitmapPoint, dragging: true));
                    window.SendBitmapWheel(bitmapPoint, 120);
                    Check(window.outgoing.Depth == 0);
                }
                window.ShowConnectedView(false);
                Console.WriteLine($"PASS {count} WPF input adapter/committed-text contracts: authorization gate, raw metadata/repeats, balanced held release, mouse barriers, legacy fallback, staged composition/no preedit/no double send, teardown and overflow; standalone motion drops without cancelling and a rejected click position prevents button submission; owned windows/non-injecting queue only, no foreground/physical IME acceptance");
                window.session=null;window.Close();app.Shutdown();
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;dialog?.Close();window.session=null;window.Close();app.Shutdown(1);}
        };
        app.Run(window);
    }
}
