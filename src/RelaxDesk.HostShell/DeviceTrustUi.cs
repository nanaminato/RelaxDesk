using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly DeviceTrustStore deviceTrust;
    readonly CheckBox rememberDevice = new()
    {
        Content = "信任此设备",
        Visibility = Visibility.Collapsed
    };
    readonly ListBox trustedDevices = new() { MaxHeight = 125 };
    readonly Button forgetDevice = new() { Content = "撤销所选设备", IsEnabled = false };
    readonly Button rememberConnectedDevice = new() { Content = "信任当前连接的设备", IsEnabled = false };
    readonly TextBlock trustNotice = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock deviceOverview = Text("正在读取设备…", foreground: System.Windows.Media.Brushes.SlateGray);
    string[] trusted = [];
    readonly HashSet<string> revokedDuringRun = new(StringComparer.Ordinal);
    string? activeDeviceFingerprint;
    bool trustBusy, trustLoaded;

    void BuildDeviceTrust(StackPanel settings)
    {
        var panel = new StackPanel();
        panel.Children.Add(trustedDevices);
        panel.Children.Add(rememberConnectedDevice);
        panel.Children.Add(forgetDevice);
        panel.Children.Add(trustNotice);
        settings.Children.Add(panel);
        trustedDevices.SelectionChanged += (_, _) => forgetDevice.IsEnabled = !trustBusy && trustedDevices.SelectedItem is ListBoxItem;
        forgetDevice.Click += async (_, _) =>
        {
            if (trustedDevices.SelectedItem is ListBoxItem { Tag: string fingerprint }) await RevokeDevice(fingerprint);
        };
        rememberConnectedDevice.Click += async (_, _) =>
        {
            if (rememberConnectedDevice.IsEnabled && activeDeviceFingerprint is { } fingerprint)
            {
                rememberConnectedDevice.IsEnabled = false;
                await SaveDeviceTrust(fingerprint);
                UpdateConnectedDeviceTrust();
            }
        };
        Loaded += async (_, _) => await LoadDeviceTrust();
    }

    void UpdateConnectedDeviceTrust() => rememberConnectedDevice.IsEnabled = nativeApproval && activeInput && trustLoaded &&
        !trustBusy && displayedSessionId is not null && activeDeviceFingerprint is { } fingerprint && !trusted.Contains(fingerprint);

    async Task LoadDeviceTrust()
    {
        try { SetTrusted(await deviceTrust.Load()); }
        catch (Exception error) when (IsTrustStorageError(error)) { deviceOverview.Text = trustNotice.Text = "无法读取可信设备记录。"; }
        finally { trustLoaded = true; }
    }

    void SetTrusted(string[] entries)
    {
        trusted = entries;
        deviceOverview.Text = $"已信任 {entries.Length} 台设备";
        trustedDevices.Items.Clear();
        foreach (var entry in entries)
            trustedDevices.Items.Add(new ListBoxItem
            {
                Tag = entry,
                Content = new TextBlock { Text = entry, TextWrapping = TextWrapping.Wrap }
            });
    }

    void ApplyDeviceTrust(HostEvent requestEvent)
    {
        activeDeviceFingerprint = (requestEvent.FullControl || requestEvent.PasswordLogin) && DeviceTrustStore.IsFingerprint(requestEvent.DeviceFingerprint)
            ? requestEvent.DeviceFingerprint : null;
        rememberDevice.IsChecked = false;
        bool known = activeDeviceFingerprint is { } fingerprint && KnownDevice(fingerprint);
        rememberDevice.Visibility = !requestEvent.PasswordLogin && activeDeviceFingerprint is not null && !known ? Visibility.Visible : Visibility.Collapsed;
        if (known && !requestEvent.PasswordLogin) peer.Text += Environment.NewLine + "已信任此设备，请确认本次连接。";
    }

    bool KnownDevice(string fingerprint) => trustLoaded && !trustBusy &&
        !revokedDuringRun.Contains(fingerprint) && trusted.Contains(fingerprint);

    static RelaxDesk.Windows.PasswordLoginSettings.DeviceGrant ReadDeviceGrant(string fingerprint)
    {
        try { return RelaxDesk.Windows.PasswordLoginSettings.ReadDeviceGrant(fingerprint); }
        catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
        { return new([], false, null); }
    }

    void UpdateSavedGrant(Func<RelaxDesk.Windows.PasswordLoginSettings.DeviceGrant, RelaxDesk.Windows.PasswordLoginSettings.DeviceGrant> update)
    {
        if (!passwordLoginRequest || activeDeviceFingerprint is not { } fingerprint || loginVerifierForTests is not null) return;
        try { RelaxDesk.Windows.PasswordLoginSettings.SaveDeviceGrant(fingerprint, update(ReadDeviceGrant(fingerprint))); }
        catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
        {
            revokedDuringRun.Add(fingerprint);
            trustNotice.Text = "本次权限已停止，但无法保存撤销。请在受信任设备列表撤销该设备。";
        }
    }

    string? CaptureDeviceTrust(bool accepted) =>
        accepted && trustLoaded && !trustBusy && rememberDevice.IsChecked == true ? activeDeviceFingerprint : null;

    async Task SaveDeviceTrust(string fingerprint)
    {
        trustBusy = true;
        forgetDevice.IsEnabled = false;
        try
        {
            SetTrusted(await deviceTrust.Save(fingerprint));
            revokedDuringRun.Remove(fingerprint);
            trustNotice.Text = "已保存可信设备。";
        }
        catch (Exception error) when (IsTrustStorageError(error)) { trustNotice.Text = "本次连接已处理，设备记录未能保存。"; }
        finally { trustBusy = false; forgetDevice.IsEnabled = trustedDevices.SelectedItem is ListBoxItem; }
    }

    async Task RevokeDevice(string fingerprint)
    {
        if (trustBusy) return;
        revokedDuringRun.Add(fingerprint);
        trustBusy = true;
        forgetDevice.IsEnabled = false;
        try
        {
            if (fingerprint == activeDeviceFingerprint && displayedSessionId is { } active)
            {
                rememberDevice.IsChecked = false;
                allow.IsEnabled = deny.IsEnabled = false;
                await Host.Disconnect(active);
            }
            SetTrusted(await deviceTrust.Remove(fingerprint));
            revokedDuringRun.Remove(fingerprint);
            trustNotice.Text = "已撤销所选设备。";
        }
        catch (Exception error) when (IsTrustStorageError(error)) { trustNotice.Text = "本次运行已暂停该设备信任，但未能删除磁盘记录。请在关闭 Host 前重试撤销。"; }
        finally { trustBusy = false; forgetDevice.IsEnabled = trustedDevices.SelectedItem is ListBoxItem; }
    }

    static bool IsTrustStorageError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException;
}
