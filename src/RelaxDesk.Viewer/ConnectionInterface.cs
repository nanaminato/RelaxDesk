using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    void BuildInterface()
    {
        Title = "RelaxDesk " + BuildIdentity.Display; Width = 1100; Height = 750; MinWidth = 680; MinHeight = 480;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 8, 18, 8)));
        buttonStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(4)));
        Resources.Add(typeof(Button), buttonStyle);
        var inputStyle = new Style(typeof(TextBox)); inputStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7))); inputStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 10))); Resources.Add(typeof(TextBox), inputStyle);
        var root = new DockPanel();
        var toolbar = new DockPanel { Background = Brushes.White, LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var full = new Button { Content = "全屏 / 窗口" }; full.Click += (_, _) => ToggleFullScreen();
        actions.Children.Add(full); actions.Children.Add(disconnect); DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        toolbar.Children.Add(new TextBlock { Text = "RelaxDesk   " + BuildIdentity.Display, ToolTip = BuildIdentity.Version, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var footer = new Border { Background = Brushes.White, Padding = new Thickness(18, 9, 18, 9) }; status.Text = "准备连接"; status.TextWrapping = TextWrapping.Wrap; status.Foreground = Brushes.DimGray; footer.Child = status; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        renderPanel.Background = Background;
        image.Focusable = true; renderPanel.Children.Add(image);
        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "连接到远程电脑", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        form.Children.Add(new TextBlock { Text = "输入 IP 和端口，Host 确认授权后进入桌面。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = "远程电脑 IP", Margin = new Thickness(0, 16, 0, 0) }); form.Children.Add(host);
        form.Children.Add(new TextBlock { Text = "端口" }); form.Children.Add(port);
        var advanced = new StackPanel();
        void Field(string name, FrameworkElement control) { advanced.Children.Add(new TextBlock { Text = name }); advanced.Children.Add(control); }
        advanced.Children.Add(pinnedConnection);
        Field("固定指纹连接信息（可选）", invitation); Field("SHA256 指纹", fingerprint); Field("一次性凭证", token);
        advanced.Children.Add(legacy); advanced.Children.Add(gpuRendering);
        advanced.Children.Add(new TextBlock { Text = "当前传输为 SDR；HDR 色调映射仍待实现与真机验证。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 12 });
        legacy.Content = "兼容模式（旧协议）"; gpuRendering.Content = "GPU 显示（不支持时回退）";
        form.Children.Add(new Expander { Header = "手动填写与显示选项", Content = advanced, Margin = new Thickness(0, 4, 0, 12) });
        connect.Content = "连接"; connect.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); connect.Foreground = Brushes.White;
        form.Children.Add(connect);
        form.Children.Add(new TextBlock { Text = "Host 必须明确批准，才会共享画面和输入。默认首次连接信任本次所见证书；请仅在可信局域网使用。", FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        connectionCard.Child = form; connectionCard.Width = 490; connectionCard.Padding = new Thickness(28); connectionCard.Background = Brushes.White; connectionCard.CornerRadius = new CornerRadius(12); connectionCard.VerticalAlignment = VerticalAlignment.Center; connectionCard.HorizontalAlignment = HorizontalAlignment.Center;
        // Scroll when manual settings are expanded on smaller displays.
        var scroll = new ScrollViewer { Content = connectionCard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        connectionOverlay = scroll; renderPanel.Children.Add(scroll); root.Children.Add(renderPanel); Content = root;
    }
    FrameworkElement connectionOverlay = null!;
    internal bool ConnectionPanelVisible => connectionOverlay.Visibility == Visibility.Visible;
    internal bool BasicConnectionReady => host.IsVisible && port.IsVisible && pinnedConnection.IsChecked != true;
    internal void SetConnectionText(string text) => invitation.Text = text;
    void ApplyInvitation()
    {
        if (string.IsNullOrWhiteSpace(invitation.Text)) return;
        var code = RelaxDesk.Core.ConnectionInvitation.Parse(invitation.Text);
        if (smoke is not null && (code.Host != smoke.Host || code.Port != smoke.Port)) throw new InvalidOperationException("Smoke invitation endpoint mismatch");
        host.Text = code.Host; port.Text = code.Port.ToString(); fingerprint.Text = code.Fingerprint; token.Password = code.Token;
        pinnedConnection.IsChecked = true;
    }
    internal void ShowConnectedView(bool connected)
    {
        connectionOverlay.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        renderPanel.Background = connected ? Brushes.Black : Background;
        Title = connected ? previewing ? "RelaxDesk — 本机预览" : $"RelaxDesk {BuildIdentity.Display} — {host.Text}" : "RelaxDesk " + BuildIdentity.Display;
        if (connected) { invitation.Clear(); token.Clear(); }
    }
    internal void StartLocalPreview()
    {
        if (session is not null) return;
        StopLocalPreview(); previewing = true; disconnect.IsEnabled = true; ShowConnectedView(true);
        int frame = 0;
        void Draw()
        {
            image.Source = BitmapSource.Create(960, 540, 96, 96, PixelFormats.Bgr32, null, CodecVerification.Pattern(960, 540, frame++ % 200), 960 * 4);
            status.Text = "本机预览 · 无网络连接 · 输入不会发往其他电脑";
        }
        Draw(); previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) }; previewTimer.Tick += (_, _) => Draw(); previewTimer.Start();
    }
    internal void StopLocalPreview()
    {
        if (!previewing) return;
        previewTimer?.Stop(); previewTimer = null; previewing = false; image.Source = null; disconnect.IsEnabled = false; ShowConnectedView(false); status.Text = "预览已结束，准备连接";
    }
    void ToggleFullScreen()
    {
        ReleaseHeldInputs();
        if (!fullScreen) { previousWindowState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = previousWindowState; }
        fullScreen = !fullScreen;
    }
}
