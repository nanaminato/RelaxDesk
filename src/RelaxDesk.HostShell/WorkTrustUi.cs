using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RelaxDesk.Core;
using RelaxDesk.Windows;

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
    readonly Button authorizeNextWork = new() { Content = "预授权所选设备的下一次 Work 连接" };
    readonly Button cancelNextWork = new() { Content = "取消 Work 预授权", IsEnabled = false };
    readonly TextBlock trustNotice = new() { TextWrapping = TextWrapping.Wrap };
    WorkTrustEntry[] trusted = [];
    readonly HashSet<string> revokedDuringRun = new(StringComparer.Ordinal);
    string? workFingerprint;
    string[] workFeatures = [];
    bool trustBusy, trustLoaded;
    string? nextWorkFingerprint;
    long nextWorkStart;
    int nextWorkHostGeneration;
    uint? nextWorkConsole;
    DispatcherTimer? nextWorkWatch;
    int nextWorkSessionChanged;

    void BuildWorkTrust(StackPanel settings)
    {
        var panel = new StackPanel();
        panel.Children.Add(trustedDevices);
        panel.Children.Add(forgetDevice);
        panel.Children.Add(authorizeNextWork);
        panel.Children.Add(cancelNextWork);
        panel.Children.Add(trustNotice);
        settings.Children.Add(new Expander { Header = "受信任的工作设备", Content = panel });
        trustedDevices.SelectionChanged += (_, _) => forgetDevice.IsEnabled = !trustBusy && trustedDevices.SelectedItem is ListBoxItem;
        forgetDevice.Click += async (_, _) =>
        {
            if (trustedDevices.SelectedItem is ListBoxItem { Tag: string fingerprint }) await RevokeWorkDevice(fingerprint);
        };
        authorizeNextWork.Click += (_, _) =>
        {
            if (trustedDevices.SelectedItem is not ListBoxItem { Tag: string fingerprint } || !Host.Running || trustBusy)
            { trustNotice.Text = "请先开始共享，再选择一个已信任的设备。"; return; }
            if (MessageBox.Show(this, $"允许此设备在 15 分钟内发起一次 Work 连接？\n\n设备指纹：{fingerprint}\n\n此授权允许查看屏幕和操作普通桌面。", "确认一次性 Work 预授权",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                ArmNextWork(fingerprint);
        };
        cancelNextWork.Click += (_, _) => ClearNextWork("已取消 Work 预授权。");
        Loaded += async (_, _) => await LoadWorkTrust();
        Closed += (_, _) => ClearNextWork();
    }

    bool ArmNextWork(string fingerprint)
    {
        if (allowedModes.SelectedIndex == 2)
        { trustNotice.Text = "当前共享只允许访客模式。"; return false; }
        if (!Host.Running || !trustLoaded || trustBusy || revokedDuringRun.Contains(fingerprint) || !trusted.Any(entry => entry.Fingerprint == fingerprint)) return false;
        var desktop = WindowsSessionAccess.Read();
        if (desktop.Denial is not null)
        { trustNotice.Text = "请回到普通桌面后再预授权。"; return false; }
        if (nextWorkFingerprint is null)
        {
            Volatile.Write(ref nextWorkSessionChanged, 0);
            try { SystemEvents.SessionSwitch += NextWorkSessionChanged; }
            catch (Exception error) when (error is InvalidOperationException or ExternalException)
            { trustNotice.Text = "无法监听 Windows 会话变化，未开启预授权。"; return false; }
        }
        nextWorkFingerprint = fingerprint;
        nextWorkStart = Stopwatch.GetTimestamp();
        nextWorkHostGeneration = Host.Generation;
        nextWorkConsole = desktop.ConsoleSessionId;
        nextWorkWatch ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _ = NextWorkIsCurrent();
        }, Dispatcher);
        nextWorkWatch.Start();
        cancelNextWork.IsEnabled = true;
        trustNotice.Text = "一次性 Work 预授权已开启，15 分钟后失效。";
        return true;
    }

    void ClearNextWork(string? notice = null)
    {
        if (nextWorkFingerprint is null) return;
        nextWorkFingerprint = null;
        SystemEvents.SessionSwitch -= NextWorkSessionChanged;
        nextWorkWatch?.Stop();
        cancelNextWork.IsEnabled = false;
        trustNotice.Text = notice ?? "已结束 Work 预授权。";
    }

    bool NextWorkIsCurrent()
    {
        if (nextWorkFingerprint is null) return false;
        if (Stopwatch.GetElapsedTime(nextWorkStart) >= TimeSpan.FromMinutes(15))
        { ClearNextWork("Work 预授权已过期。"); return false; }
        var desktop = WindowsSessionAccess.Read();
        if (Volatile.Read(ref nextWorkSessionChanged) != 0 || !Host.Running || Host.Generation != nextWorkHostGeneration ||
            desktop.Denial is not null || desktop.ConsoleSessionId != nextWorkConsole)
        { ClearNextWork(); return false; }
        return true;
    }

    void NextWorkSessionChanged(object sender, SessionSwitchEventArgs args)
    {
        Volatile.Write(ref nextWorkSessionChanged, 1);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (Volatile.Read(ref nextWorkSessionChanged) != 0)
                ClearNextWork("Windows 会话已变化，已取消 Work 预授权。");
        }));
    }

    bool ConsumeNextWork()
    {
        if (!NextWorkIsCurrent()) return false;
        if (!trustLoaded || trustBusy || activeMode != "Work" || nextWorkFingerprint != workFingerprint ||
            !trusted.Any(entry => entry.Fingerprint == nextWorkFingerprint)) return false;
        ClearNextWork("所选设备已使用一次性 Work 预授权。");
        clipboardPermission.IsChecked = filesPermission.IsChecked = audioPermission.IsChecked = false;
        return true;
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
        if (nextWorkFingerprint == fingerprint) ClearNextWork();
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
