using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly WorkTrustStore workTrust;
    readonly CheckBox rememberWorkDevice = new()
    {
        Content = "保存此设备及本次勾选的工作权限",
        Visibility = Visibility.Collapsed
    };
    readonly ListBox trustedDevices = new() { MaxHeight = 125 };
    readonly Button forgetDevice = new() { Content = "撤销所选设备", IsEnabled = false };
    readonly TextBlock trustNotice = new() { TextWrapping = TextWrapping.Wrap };
    WorkTrustEntry[] trusted = [];
    readonly HashSet<string> revokedDuringRun = new(StringComparer.Ordinal);
    string? workFingerprint;
    string[] workFeatures = [];
    bool trustBusy, trustLoaded;

    void BuildWorkTrust(StackPanel settings)
    {
        var panel = new StackPanel();
        panel.Children.Add(trustedDevices);
        panel.Children.Add(forgetDevice);
        panel.Children.Add(trustNotice);
        settings.Children.Add(new Expander { Header = "受信任的工作设备", Content = panel });
        trustedDevices.SelectionChanged += (_, _) => forgetDevice.IsEnabled = !trustBusy && trustedDevices.SelectedItem is ListBoxItem;
        forgetDevice.Click += async (_, _) =>
        {
            if (trustedDevices.SelectedItem is ListBoxItem { Tag: string fingerprint }) await RevokeWorkDevice(fingerprint);
        };
        Loaded += async (_, _) => await LoadWorkTrust();
    }

    async Task LoadWorkTrust()
    {
        try { SetTrusted(await workTrust.Load()); }
        catch (Exception error) when (IsTrustStorageError(error)) { trustNotice.Text = "无法读取工作设备记录。"; }
        finally { trustLoaded = true; }
    }

    void SetTrusted(WorkTrustEntry[] entries)
    {
        trusted = entries;
        trustedDevices.Items.Clear();
        foreach (var entry in entries)
            trustedDevices.Items.Add(new ListBoxItem
            {
                Tag = entry.Fingerprint,
                Content = new TextBlock { Text = entry.Fingerprint, TextWrapping = TextWrapping.Wrap }
            });
    }

    void ApplyWorkTrust(HostEvent requestEvent)
    {
        workFingerprint = activeMode == "Work" && WorkTrustStore.IsFingerprint(requestEvent.DeviceFingerprint)
            ? requestEvent.DeviceFingerprint : null;
        workFeatures = requestEvent.Features ?? [];
        rememberWorkDevice.IsChecked = false;
        rememberWorkDevice.Visibility = workFingerprint is not null ? Visibility.Visible : Visibility.Collapsed;
        // A reconnect can arrive before a revoke has finished writing the store.
        // In that interval require the operator to select every optional grant.
        if (!trustLoaded || trustBusy || workFingerprint is null || revokedDuringRun.Contains(workFingerprint)) return;
        var saved = trusted.FirstOrDefault(entry => entry.Fingerprint == workFingerprint);
        if (saved is null) return;
        peer.Text += Environment.NewLine + "已保存此设备的工作权限配置，请确认本次连接。";

        bool files = workFeatures.Contains(SessionCapabilities.FileTransfer) &&
            saved.Features.Contains(SessionCapabilities.FileTransfer) &&
            string.Equals(selectedFileRoot, saved.FileRoot, StringComparison.OrdinalIgnoreCase);
        var requestedClipboard = workFeatures.Where(SessionCapabilities.IsClipboard).ToArray();
        clipboardPermission.IsChecked = requestedClipboard.Length > 0 && requestedClipboard.All(saved.Features.Contains) &&
            (!requestedClipboard.Contains(SessionCapabilities.ClipboardFiles) || files);
        filesPermission.IsChecked = files;
        audioPermission.IsChecked = workFeatures.Contains(SessionCapabilities.SystemAudio) &&
            saved.Features.Contains(SessionCapabilities.SystemAudio);
    }

    WorkTrustEntry? CaptureWorkTrust(bool accepted, bool clipboard, bool files, bool audio)
    {
        if (!accepted || !trustLoaded || trustBusy || rememberWorkDevice.IsChecked != true || workFingerprint is null)
            return null;
        return new(workFingerprint, SessionCapabilities.Grant(workFeatures, clipboard, files, audio),
            files ? selectedFileRoot : null);
    }

    async Task SaveWorkTrust(WorkTrustEntry entry)
    {
        trustBusy = true;
        forgetDevice.IsEnabled = false;
        try
        {
            SetTrusted(await workTrust.Save(entry));
            revokedDuringRun.Remove(entry.Fingerprint);
            trustNotice.Text = "已保存设备及工作权限。";
        }
        catch (Exception error) when (IsTrustStorageError(error)) { trustNotice.Text = "本次权限已处理，设备记录未能保存。"; }
        finally { trustBusy = false; forgetDevice.IsEnabled = trustedDevices.SelectedItem is ListBoxItem; }
    }

    async Task RevokeWorkDevice(string fingerprint)
    {
        if (trustBusy) return;
        revokedDuringRun.Add(fingerprint);
        trustBusy = true;
        forgetDevice.IsEnabled = false;
        try
        {
            if (fingerprint == workFingerprint && displayedSessionId is { } active)
            {
                clipboardPermission.IsChecked = filesPermission.IsChecked = audioPermission.IsChecked = false;
                rememberWorkDevice.IsChecked = false;
                allow.IsEnabled = deny.IsEnabled = false;
                await Host.Disconnect(active);
            }
            SetTrusted(await workTrust.Remove(fingerprint));
            revokedDuringRun.Remove(fingerprint);
            trustNotice.Text = "已撤销所选设备。";
        }
        catch (Exception error) when (IsTrustStorageError(error)) { trustNotice.Text = "本次运行已暂停该设备信任，但未能删除磁盘记录。请在关闭 Host 前重试撤销。"; }
        finally { trustBusy = false; forgetDevice.IsEnabled = trustedDevices.SelectedItem is ListBoxItem; }
    }

    static bool IsTrustStorageError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException;
}
