using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelaxDesk.Core.Localization;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    internal static void VerifyLocalization(Application app,string output)
    {
        var manager=LocalizationManager.Current;string original=manager.CurrentLanguageCode;
        var window=new HostWindow { ShowActivated=false };int checks=0;
        void Check(bool value){if(!value)throw new IOException($"Host localization check {checks+1}");checks++;}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                Directory.CreateDirectory(output);window.ShowPage(true);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while(!window.DisplayEnumerationFinished)await Task.Delay(20,timeout.Token);
                foreach(string code in new[]{"en","ja","fr","es","zh-CN"})
                {
                    manager.SetLanguage(code,persist:false);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(window.Title==manager.Get("Host.WindowTitle"));
                    Check(window.PageTitle.Text==manager.Get("Host.PageTitleSettings"));
                    Check(Equals(window.serviceSettings.InstallService.Content,manager.Get("Ui.ServiceSettingsView.Text173")));
                    Check(Equals(window.trustedDeviceSettings.Forget.Content,manager.Get("Ui.HostTrustedDevices.Text245")));
                    Check(window.loginOverview.Text==(window.loginSettings.UserName.Text.Length==0
                        ?manager.Get("Ui.HostLoginUi.Text186"):manager.Format("Ui.HostLoginUi.Text383",window.loginSettings.UserName.Text)));
                    if(window.SelectedDisplay is null)Check(window.displayOverview.Text==manager.Get("Ui.DisplaySettings.Text081"));
                    var root=(FrameworkElement)window.Content;
                    var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file=File.Create(Path.Combine(output,$"settings-{code}.png"));encoder.Save(file);
                }
                Console.WriteLine($"PASS {checks} Host localization checks: live navigation/service/device controls in five languages; no sharing or service installation");
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
            finally{manager.SetLanguage(original,persist:false);window.Close();}
        };
        app.Run(window);
    }
}
