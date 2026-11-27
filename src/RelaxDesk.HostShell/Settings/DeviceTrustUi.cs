using RelaxDesk.Windows;
using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly DeviceTrustStore deviceTrust;
    CheckBox rememberDevice => consent.RememberDevice;
    readonly HostTrustedDevices trustedDeviceSettings = new();
    ListBox trustedDevices => trustedDeviceSettings.Devices;
    Button forgetDevice => trustedDeviceSettings.Forget;
    Button rememberConnectedDevice => trustedDeviceSettings.RememberConnected;
    TextBlock trustNotice => trustedDeviceSettings.Notice;
    string[] trusted = [];
    readonly HashSet<string> revokedDuringRun = new(StringComparer.Ordinal);
    string? activeDeviceFingerprint;
    bool trustBusy, trustLoaded;

    void ConfigureDeviceTrust()
    {
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
        catch (Exception error) when (IsTrustStorageError(error)) { deviceOverview.Text = trustNotice.Text = Strings.Get("Ui.DeviceTrustUi.Text291"); }
        finally { trustLoaded = true; }
    }

    void SetTrusted(string[] entries)
    {
        trusted = entries;
        LocalizedText.Bind(deviceOverview,"Text","Ui.DeviceTrustUi.Text194",entries.Length);
        trustedDevices.Items.Clear();
        foreach (var entry in entries)
            trustedDevices.Items.Add(new ListBoxItem
            {
                Tag = entry,                Content = new TextBlock { Text = entry,TextWrapping = TextWrapping.Wrap }
            });
    }

    void ApplyDeviceTrust(HostEvent requestEvent)
    {
        activeDeviceFingerprint = (requestEvent.FullControl || requestEvent.PasswordLogin) && DeviceTrustStore.IsFingerprint(requestEvent.DeviceFingerprint)
            ? requestEvent.DeviceFingerprint : null;
        rememberDevice.IsChecked = false;
        bool known = activeDeviceFingerprint is { } fingerprint && KnownDevice(fingerprint);
        rememberDevice.Visibility = !requestEvent.PasswordLogin && activeDeviceFingerprint is not null && !known ? Visibility.Visible : Visibility.Collapsed;
        if (known && !requestEvent.PasswordLogin) peer.Text += Environment.NewLine + Strings.Get("Ui.DeviceTrustUi.Text195");
    }

    bool KnownDevice(string fingerprint) => trustLoaded && !trustBusy &&
        !revokedDuringRun.Contains(fingerprint) && trusted.Contains(fingerprint);

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
            LocalizedText.Bind(trustNotice,"Text","Ui.DeviceTrustUi.Text193");
        }
        catch (Exception error) when (IsTrustStorageError(error)) { LocalizedText.Bind(trustNotice,"Text","Ui.DeviceTrustUi.Text341"); }
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
            await Host.RevokeDevice(fingerprint);
            if (fingerprint == activeDeviceFingerprint && displayedSessionId is { } active)
            {
                rememberDevice.IsChecked = false;
                allow.IsEnabled = deny.IsEnabled = false;
                await Host.Disconnect(active);
            }
            SetTrusted(await deviceTrust.Remove(fingerprint));
            revokedDuringRun.Remove(fingerprint);
            LocalizedText.Bind(trustNotice,"Text","Ui.DeviceTrustUi.Text219");
        }
        catch (Exception error) when (IsTrustStorageError(error)) { LocalizedText.Bind(trustNotice,"Text","Ui.DeviceTrustUi.Text339"); }
        finally { trustBusy = false; forgetDevice.IsEnabled = trustedDevices.SelectedItem is ListBoxItem; }
    }

    static bool IsTrustStorageError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException;
}
