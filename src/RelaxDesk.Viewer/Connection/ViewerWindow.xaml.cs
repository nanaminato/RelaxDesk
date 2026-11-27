using RelaxDesk.SharedUI;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Windows;
using RelaxDesk.Core;
using RelaxDesk.Core.Localization;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    string SessionEndpoint => host.Text;

    bool optionsExpanded, desktopView;
    Rect connectionBounds;
    WindowState connectionWindowState;
    readonly ConnectionBarPreferences connectionBarPreferences = new();

    void ConfigureInterface()
    {
        InitializeComponent();
        optionsButton.Click += (_, _) => SetOptionsExpanded(!optionsExpanded);
        cancelConnection.Click += (_, _) =>
        {
            if (connectionRun is not null) StopConnection();
            else Close();
        };
        disconnect.IsEnabledChanged += (_, _) => disconnect.Visibility =
            disconnect.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ConfigureErrorDetails();
        ConfigureViewport();
        ConfigurePointerPresentation();
        ConfigureConnectionHistory();
        resetPin.Click += (_, _) =>
        {
            if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out var address) || !int.TryParse(port.Text, out int number) || number is < 1 or > 65535) return;
            if (MessageBox.Show(this, Strings.Get("Ui.ViewerWindow.Text376"), Strings.Get("Ui.ViewerWindow.Text378"), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try { PasswordLoginSettings.SaveServerPin(new System.Net.IPEndPoint(address, number).ToString(), null); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { LocalizedText.Bind(status,"Text","Ui.ViewerWindow.Text289"); }
        };
        ConfigureSavedCredentials();
        ConfigurePreferences();
        ConfigureLanguageSelector();
    }

    void ConfigureLanguageSelector()
    {
        var manager = LocalizationManager.Current;
        languageSelector.ItemsSource = manager.AvailableLanguages;
        languageSelector.SelectedValue = manager.CurrentLanguageCode;
        languageSelector.SelectionChanged += (_, _) =>
        {
            if (languageSelector.SelectedItem is LanguageOption option && !string.Equals(option.Code, manager.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                manager.SetLanguage(option.Code);
        };
        manager.LanguageChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                LocalizedText.Bind(optionsButton,"Content",optionsExpanded ? "Viewer.HideOptions" : "Viewer.ShowOptions");
                if (!string.Equals(languageSelector.SelectedValue as string, manager.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                    languageSelector.SelectedValue = manager.CurrentLanguageCode;
                videoMenuState=null;RefreshVideoSettingsMenu();RefreshDisplayMenu();
                UpdateViewTitle();
            });
        };
    }

    internal void SetOptionsExpanded(bool expanded)
    {
        if (expanded == optionsExpanded) return;
        if (expanded) { basicConnection.Child = null; generalTab.Content = signInForm; }
        else { generalTab.Content = null; basicConnection.Child = signInForm; }
        optionsExpanded = expanded;
        connectionOptions.SelectedIndex = 0;
        connectionOptions.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        basicConnection.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        LocalizedText.Bind(optionsButton,"Content",expanded ? "Viewer.HideOptions" : "Viewer.ShowOptions");
        Height = Math.Min(expanded ? 610 : 480, SystemParameters.WorkArea.Height);
    }
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
            Strings.Format("Ui.ViewerWindow.Text457",pin),
            Strings.Get("Ui.ViewerWindow.Text344"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(accepted);
    }
    Task<bool> ConfirmSessionTakeover(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = Strings.Get("Admission.TakeoverBody"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var proceed = new Button { Content = Strings.Get("Admission.TakeoverAction"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
        var cancel = new Button { Content = Strings.Get("Admission.Cancel"), IsCancel = true, IsDefault = true, Padding = new Thickness(12, 5, 12, 5) };
        actions.Children.Add(proceed); actions.Children.Add(cancel); panel.Children.Add(actions);
        var dialog = new Window { Owner = this, Title = Strings.Get("Admission.TakeoverTitle"), Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        proceed.Click += (_, _) => dialog.DialogResult = true;
        using var registration = ct.Register(() => _ = Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
        bool result = dialog.ShowDialog() == true;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
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
        if (connected && !desktopView)
        {
            connectionBounds = new Rect(Left, Top, Width, Height);
            connectionWindowState = WindowState;
            MinWidth = 680; MinHeight = 480;
            if (WindowState == WindowState.Normal) { Width = 1100; Height = 750; }
        }
        connectionOverlay.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        windowToolbar.Visibility = connectionFooter.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (connected)
        {
            sessionToolbar ??= new SessionToolbar(this, ToggleFullScreen, () => disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                session is null ? null : SendShortcut,ReleaseHeldInputs, connectionBarPreferences);
            sessionToolbar.SetSession(SessionEndpoint, fullScreen);
            sessionToolbar.SetRemoteShortcuts(inputAuthorized);
            sessionToolbar.SetTextInput(inputAuthorized&&inputVersion==2?OpenTextInput:null);
            RefreshDisplayMenu();RefreshAudioToolbar();RefreshStatisticsToolbar();RefreshVideoSettingsMenu();sessionToolbar.SetViewport(CurrentViewport,SetViewportZoom);
        }
        else
        {
            localConnectionKeys.Clear();
            sessionToolbar?.Dispose(); sessionToolbar = null;
            if (fullScreen) ToggleFullScreen();
            if (desktopView)
            {
                MinWidth = 540; MinHeight = 450;
                WindowState = connectionWindowState;
                if (WindowState == WindowState.Normal) { Width = connectionBounds.Width; Height = connectionBounds.Height; Left = connectionBounds.Left; Top = connectionBounds.Top; }
            }
        }
        desktopView = connected;
        SetPointerPresentation(false);
        if (sessionToolbar is not null) sessionToolbar.PointerRegionChanged = RefreshPointerPresentation;
        renderPanel.Background = connected ? Brushes.Black : Background;
        UpdateViewTitle();
        if (connected) { invitation.Clear(); token.Clear(); }
    }
    void UpdateViewTitle()
    {
        Title = desktopView ? previewing ? Strings.Get("Ui.ViewerWindow.Text044") : Strings.Format("Ui.ViewerWindow.Text058",host.Text) : Strings.Get("Viewer.WindowTitle");
        if (desktopView && !previewing && fileClipboardStatus.Length > 0)
            Title += " · " + fileClipboardStatus;
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
            LocalizedText.Bind(status,"Text","Ui.ViewerWindow.Text335");
        }
        Draw(); previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) }; previewTimer.Tick += (_, _) => Draw(); previewTimer.Start();
    }
    internal void StopLocalPreview()
    {
        if (!previewing) return;
        previewTimer?.Stop(); previewTimer = null; previewing = false;EndViewport(); image.Source = null; disconnect.IsEnabled = false; ShowConnectedView(false); LocalizedText.Bind(status,"Text","Ui.ViewerWindow.Text506");
    }
    bool IsLocalPointer(double x, double y) => sessionToolbar?.BlocksPointer(x, y) == true;
    internal void ToggleFullScreen()
    {
        ReleaseHeldInputs();
        SetFullScreenPresentation(!fullScreen);
        sessionToolbar?.SetFullScreen(fullScreen);
    }
}
