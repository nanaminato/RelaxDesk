using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Globalization;
using RelaxDesk.Core;
using RelaxDesk.Core.Localization;
using RelaxDesk.SharedUI;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifyLocalization(Application app, string output)
    {
        var manager = LocalizationManager.Current;
        string original = manager.CurrentLanguageCode;
        var window = new ViewerWindow { ShowActivated = false };
        int checks = 0;
        void Check(bool value) { if (!value) throw new IOException($"Viewer localization check {checks + 1}"); checks++; }
        IEnumerable<DependencyObject> Children(DependencyObject root)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Children(child))yield return nested;}
        }
        window.Loaded += async (_, _) =>
        {
            SessionToolbar? bar = null;
            try
            {
                Directory.CreateDirectory(output);
                window.SetOptionsExpanded(true); window.connectionOptions.SelectedIndex = 2;
                var dynamicLabel = LocalizedText.Bind(new TextBlock(), "Text", "Ui.SessionToolbar.Text443", "", 144);
                var userText = new TextBox { Text = "中文 user-defined filename / пароль" };
                bar = new SessionToolbar(window, () => { }, () => { });
                bar.SetVideoSettings(VideoSettingsProtocol.Default,true,false,true,(_,_)=>{},false,_=>{});
                foreach (string code in new[] { "en", "ja", "fr", "es", "zh-CN" })
                {
                    manager.SetLanguage(code, persist:false);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();await Task.Delay(100);
                    Check(window.Title == manager.Get("Viewer.WindowTitle"));
                    Check(Equals(window.autoConnectionQuality.Content,manager.Get("Viewer.AutomaticQuality")));
                    Check(Equals(((MenuItem)bar.VideoSettingsAction.Items[0]).Header,manager.Get("Viewer.FrameRateLimit")));
                    Check(Equals(((MenuItem)bar.VideoSettingsAction.Items[1]).Header,manager.Get("Ui.SessionToolbar.Text024")));
                    Check(Equals(bar.VideoSettingsAction.Header,manager.Format("Ui.SessionToolbar.Text443","",30)));
                    Check(bar.VideoSettingsAction.Items.Count == 3 && bar.VideoSettingsAction.Items.OfType<MenuItem>().All(item=>item.IsEnabled));
                    Check(dynamicLabel.Text == manager.Format("Ui.SessionToolbar.Text443","",144));
                    Check(userText.Text == "中文 user-defined filename / пароль");
                    foreach(var label in Children(window).OfType<Label>().Where(label=>label.IsVisible&&label.Content is string))
                    {
                        var text=new FormattedText((string)label.Content,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
                            new Typeface(label.FontFamily,label.FontStyle,label.FontWeight,label.FontStretch),label.FontSize,Brushes.Black,VisualTreeHelper.GetDpi(label).PixelsPerDip);
                        Check(text.WidthIncludingTrailingWhitespace<=label.ActualWidth-label.Padding.Left-label.Padding.Right+1);
                    }
                    var root=(FrameworkElement)window.Content;
                    var bitmap=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file=File.Create(Path.Combine(output,$"advanced-{code}.png"));encoder.Save(file);
                }
                Console.WriteLine($"PASS {checks} Viewer localization checks: live language switching, Advanced controls, localized video menu without description rows, formatted bindings and unchanged user data; five rendered languages");
            }
            catch(Exception error) { Console.Error.WriteLine(error); Environment.ExitCode=1; }
            finally { bar?.Dispose(); manager.SetLanguage(original,persist:false); window.Close(); }
        };
        app.Run(window);
    }
}
