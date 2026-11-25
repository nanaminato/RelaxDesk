using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly ComboBox networkMode = new() { ItemsSource = new[] { "Auto", "TCP", "UDP" }, SelectedIndex = 0 };
    readonly ComboBox fileRateLimit = new() { ItemsSource = new[] { "20 MiB/s（默认）", "4 MiB/s", "1 MiB/s", "256 KiB/s" }, SelectedIndex = 0 };
    readonly TextBox loginName = new() { MaxLength = 64 };
    string SessionEndpoint => host.Text;

    void BuildInterface()
    {
        Title = "RelaxDesk " + BuildIdentity.Display; Width = 1100; Height = 750; MinWidth = 680; MinHeight = 480;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 14;
        Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));
        RelaxDesk.Ui.ModernTheme.Apply(this);
        var root = new DockPanel();
        var toolbar = new DockPanel { Background = Brushes.White, LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var full = new Button { Content = "全屏模式", ToolTip = "切换全屏与窗口" }; full.Click += (_, _) => { ToggleFullScreen(); full.Content = fullScreen ? "返回窗口" : "全屏模式"; };
        disconnect.Style = (Style)Resources["DangerButton"];
        actions.Children.Add(full); actions.Children.Add(disconnect); DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions);
        toolbar.Children.Add(new TextBlock { Text = "RelaxDesk   " + BuildIdentity.Display, ToolTip = BuildIdentity.Version, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        windowToolbar = toolbar; DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var footer = new Border { Background = Brushes.White, Padding = new Thickness(18, 9, 18, 9) }; status.Text = "准备连接"; status.TextWrapping = TextWrapping.Wrap; status.Foreground = Brushes.DimGray;
        var footerContent = new StackPanel(); footerContent.Children.Add(status); BuildErrorDetails(footerContent); footer.Child = footerContent; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        connectionFooter = footer;
        renderPanel.Background = Background;
        image.Focusable = true; BuildViewport();
        var form = new StackPanel();
        form.Children.Add(new TextBlock { Text = "连接到远程电脑", FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        form.Children.Add(new TextBlock { Text = "使用受控端设置的用户名和密码登录。首次连接须批准设备，后续可自动重连。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = "远程电脑 IP", Margin = new Thickness(0, 16, 0, 0) }); form.Children.Add(host);
        form.Children.Add(new TextBlock { Text = "端口" }); form.Children.Add(port);
        form.Children.Add(new TextBlock { Text = "用户名" }); form.Children.Add(loginName);
        form.Children.Add(new TextBlock { Text = "密码" }); form.Children.Add(token);
        AddConnectionHistory(form);
        requestClipboard.Margin = new Thickness(0, 10, 0, 4); form.Children.Add(requestClipboard);
        form.Children.Add(requestClipboardImage);form.Children.Add(requestClipboardHtml);
        requestFiles.Margin=new Thickness(0,2,0,4);form.Children.Add(requestFiles);form.Children.Add(requestFileClipboard);
        form.Children.Add(requestAudio);
        var advanced = new StackPanel();
        void Field(string name, FrameworkElement control) { advanced.Children.Add(new TextBlock { Text = name }); advanced.Children.Add(control); }
        Field("媒体传输", networkMode);
        Field("文件传输限速（上传和下载）", fileRateLimit);
        var resetPin = new Button { Content = "清除当前地址的受控端证书记录" };
        resetPin.Click += (_, _) =>
        {
            if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out var address) ||
                !int.TryParse(port.Text, out int number) || number is < 1 or > 65535) return;
            if (MessageBox.Show(this, "清除后，下次登录前需要重新核对受控端证书指纹。", "清除证书记录", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try { PasswordLoginSettings.SaveServerPin(new System.Net.IPEndPoint(address, number).ToString(), null); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { status.Text = "无法清除证书记录。"; }
        };
        advanced.Children.Add(resetPin);
        advanced.Children.Add(gpuRendering);advanced.Children.Add(requestTopology);
        advanced.Children.Add(new TextBlock { Text = "HDR 桌面由 Host 转为 SDR 后传输；屏幕观感仍需真机确认。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 12 });
        gpuRendering.Content = "GPU 显示（不支持时回退）";
        form.Children.Add(new Expander { Header = "手动填写与显示选项", Content = advanced, Margin = new Thickness(0, 4, 0, 12) });
        connect.Content = "连接远程桌面"; connect.Style = (Style)Resources["PrimaryButton"];
        form.Children.Add(connect);
        form.Children.Add(new TextBlock { Text = "首次连接先核对受控端证书指纹，再发送密码。密码只在本次连接和自动重连期间保留。", FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        connectionCard.Child = form; connectionCard.Width = 490; connectionCard.Padding = new Thickness(28); connectionCard.Background = Brushes.White; connectionCard.CornerRadius = new CornerRadius(12); connectionCard.VerticalAlignment = VerticalAlignment.Center; connectionCard.HorizontalAlignment = HorizontalAlignment.Center;
        // Scroll when manual settings are expanded on smaller displays.
        var scroll = new ScrollViewer { Content = connectionCard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        connectionOverlay = scroll; renderPanel.Children.Add(scroll); root.Children.Add(renderPanel); Content = root;
    }
    FrameworkElement connectionOverlay = null!;
    FrameworkElement windowToolbar = null!, connectionFooter = null!;
    SessionToolbar? sessionToolbar;
    internal SessionToolbar? Toolbar => sessionToolbar;
    internal bool DesktopFillsClient => windowToolbar.Visibility == Visibility.Collapsed && connectionFooter.Visibility == Visibility.Collapsed;
    internal void AddToolbarTestSurface(NativeVideoHost surface) => renderPanel.Children.Add(surface);
    internal bool ConnectionPanelVisible => connectionOverlay.Visibility == Visibility.Visible;
    internal bool BasicConnectionReady => host.IsVisible && port.IsVisible && pinnedConnection.IsChecked != true;
    internal void SetConnectionText(string text) => invitation.Text = text;
    Task<bool> ConfirmPasswordServer(string pin, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        bool accepted = MessageBox.Show(this,
            $"请将下方指纹与受控端「登录设置」中的 TLS 指纹核对。\n\n{pin}\n\n仅在完全一致时继续，密码将在确认后发送。",
            "核对受控端身份", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(accepted);
    }
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
        windowToolbar.Visibility = connectionFooter.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (connected)
        {
            sessionToolbar ??= new SessionToolbar(this, ToggleFullScreen, () => disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                session is null ? null : SendShortcut,ReleaseHeldInputs);
            sessionToolbar.SetSession(SessionEndpoint, fullScreen);
            sessionToolbar.SetClipboard(sessionPermissions is{} clipboardGate&&ClipboardFormats.Granted(clipboardGate).Any, DisableClipboard);
            sessionToolbar.SetRemoteShortcuts(inputAuthorized);
            sessionToolbar.SetTextInput(inputAuthorized&&inputVersion==2?OpenTextInput:null);
            RefreshFileToolbar();RefreshFileClipboardToolbar();RefreshDisplayMenu();RefreshAudioToolbar();RefreshStatisticsToolbar();RefreshVideoSettingsMenu();sessionToolbar.SetViewport(CurrentViewport,SetViewportZoom);
        }
        else
        {
            localConnectionKeys.Clear();
            sessionToolbar?.Dispose(); sessionToolbar = null;
            if (fullScreen) ToggleFullScreen();
        }
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
            SetViewportVideoSize(960,540);
            status.Text = "本机预览 · 无网络连接 · 输入不会发往其他电脑";
        }
        Draw(); previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) }; previewTimer.Tick += (_, _) => Draw(); previewTimer.Start();
    }
    internal void StopLocalPreview()
    {
        if (!previewing) return;
        previewTimer?.Stop(); previewTimer = null; previewing = false;EndViewport(); image.Source = null; disconnect.IsEnabled = false; ShowConnectedView(false); status.Text = "预览已结束，准备连接";
    }
    internal void ToggleFullScreen()
    {
        ReleaseHeldInputs();
        if (!fullScreen) { previousWindowState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = previousWindowState; }
        fullScreen = !fullScreen;
        sessionToolbar?.SetFullScreen(fullScreen);
    }
}
