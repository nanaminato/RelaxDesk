using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly CheckBox identifyWorkDevice = new() { Content = "在工作模式下向 Host 提供此设备身份" };
    readonly ComboBox networkMode = new() { ItemsSource = new[] { "Auto", "TCP", "UDP" }, SelectedIndex = 0 };
    readonly ComboBox fileRateLimit = new() { ItemsSource = new[] { "20 MiB/s（默认）", "4 MiB/s", "1 MiB/s", "256 KiB/s" }, SelectedIndex = 0 };
    readonly ComboBox requestedMode = new() { ItemsSource = new[] { "Work（工作）", "Guest（访客）", "Admin（管理员）" }, SelectedIndex = 0 };
    SessionMode activeMode = SessionMode.Work;

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
        form.Children.Add(new TextBlock { Text = "输入 IP 和端口，Host 确认授权后进入桌面。意外断开后会有限重试，仍需 Host 重新允许。", Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        form.Children.Add(new TextBlock { Text = "远程电脑 IP", Margin = new Thickness(0, 16, 0, 0) }); form.Children.Add(host);
        form.Children.Add(new TextBlock { Text = "端口" }); form.Children.Add(port);
        AddConnectionHistory(form);
        form.Children.Add(new TextBlock { Text = "会话模式" });
        form.Children.Add(requestedMode);
        requestClipboard.Margin = new Thickness(0, 10, 0, 4); form.Children.Add(requestClipboard);
        form.Children.Add(requestClipboardImage);form.Children.Add(requestClipboardHtml);
        requestFiles.Margin=new Thickness(0,2,0,4);form.Children.Add(requestFiles);form.Children.Add(requestFileClipboard);
        form.Children.Add(requestAudio);
        var advanced = new StackPanel();
        void Field(string name, FrameworkElement control) { advanced.Children.Add(new TextBlock { Text = name }); advanced.Children.Add(control); }
        Field("媒体传输", networkMode);
        Field("文件传输限速（上传和下载）", fileRateLimit);
        advanced.Children.Add(identifyWorkDevice);
        advanced.Children.Add(pinnedConnection);
        Field("固定指纹连接信息（可选）", invitation); Field("SHA256 指纹", fingerprint); Field("一次性凭证", token);
        advanced.Children.Add(legacy); advanced.Children.Add(gpuRendering);advanced.Children.Add(requestTopology);
        advanced.Children.Add(new TextBlock { Text = "HDR 桌面由 Host 转为 SDR 后传输；屏幕观感仍需真机确认。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, FontSize = 12 });
        legacy.Content = "兼容模式（旧协议）"; gpuRendering.Content = "GPU 显示（不支持时回退）";
        form.Children.Add(new Expander { Header = "手动填写与显示选项", Content = advanced, Margin = new Thickness(0, 4, 0, 12) });
        connect.Content = "连接远程桌面"; connect.Style = (Style)Resources["PrimaryButton"];
        form.Children.Add(connect);
        form.Children.Add(new TextBlock { Text = "Host 必须明确批准，才会共享画面和输入。默认首次连接信任本次所见证书；请仅在可信局域网使用。", FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
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
    void ApplyInvitation()
    {
        if (string.IsNullOrWhiteSpace(invitation.Text)) return;
        var code = RelaxDesk.Core.ConnectionInvitation.Parse(invitation.Text);
        if (smoke is not null && (code.Host != smoke.Host || code.Port != smoke.Port)) throw new InvalidOperationException("Smoke invitation endpoint mismatch");
        host.Text = code.Host; port.Text = code.Port.ToString(); fingerprint.Text = code.Fingerprint; token.Password = code.Token;
        pinnedConnection.IsChecked = true;
    }
    bool adminUpgradeAvailable;
    int adminUpgradePending;

    void RequestAdminUpgrade()
    {
        if (!adminUpgradeAvailable || activeMode != SessionMode.Work ||
            session is not { IsCancellationRequested: false } current) return;
        adminUpgradeAvailable = false;
        ReleaseHeldInputs();
        inputAuthorized = false;
        CloseTextInput();
        sessionToolbar?.SetAdminDowngrade(null);
        sessionToolbar?.SetRemoteShortcuts(false);
        sessionToolbar?.SetTextInput(null);
        sessionToolbar?.SetEndpoint($"Work · 等待本机管理员授权 · {host.Text}");
        Volatile.Write(ref adminUpgradePending, 1);
        if (!outgoing.TryWrite(new("admin-input-request-v2", ++sequence))) current.Cancel();
    }

    void RequestAdminDowngrade()
    {
        if (activeMode != SessionMode.Admin || session is not { IsCancellationRequested: false } current) return;
        ReleaseHeldInputs();
        inputAuthorized = false;
        sessionToolbar?.SetAdminDowngrade(null);
        sessionToolbar?.SetRemoteShortcuts(false);
        sessionToolbar?.SetTextInput(null);
        sessionToolbar?.SetEndpoint($"Admin · 正在撤销高权限输入 · {host.Text}");
        if (!outgoing.TryWrite(new("admin-input-stop-v2", ++sequence))) current.Cancel();
    }

    internal void ShowConnectedView(bool connected)
    {
        connectionOverlay.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        windowToolbar.Visibility = connectionFooter.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (connected)
        {
            sessionToolbar ??= new SessionToolbar(this, ToggleFullScreen, () => disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                session is null ? null : SendShortcut,ReleaseHeldInputs);
            sessionToolbar.SetSession($"{activeMode} · {host.Text}", fullScreen);
            sessionToolbar.SetClipboard(sessionPermissions is{} clipboardGate&&ClipboardFormats.Granted(clipboardGate).Any, DisableClipboard);
            sessionToolbar.SetAdminDowngrade(activeMode == SessionMode.Admin ? RequestAdminDowngrade :
                adminUpgradeAvailable ? RequestAdminUpgrade : null, upgrade: adminUpgradeAvailable);
            sessionToolbar.SetRemoteShortcuts(inputAuthorized);
            sessionToolbar.SetTextInput(inputAuthorized&&inputVersion==2?OpenTextInput:null);
            RefreshFileToolbar();RefreshFileClipboardToolbar();RefreshDisplayMenu();RefreshAudioToolbar();RefreshStatisticsToolbar();RefreshVideoSettingsMenu();sessionToolbar.SetViewport(CurrentViewport,SetViewportZoom);
        }
        else
        {
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
