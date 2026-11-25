using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    string? selectedFileRoot;
    readonly TextBox fileRootLabel = new() { Text = "使用 RelaxDesk 默认传输目录", IsReadOnly = true, Margin = new(0, 4, 0, 0) };
    readonly Button chooseFileRoot = new() { Content = "更改传输目录…" }, clearFileRoot = new() { Content = "恢复默认" };
    readonly CheckBox filesPermission = new() { Content = "允许文件传输及文件剪贴板", IsChecked = false, Visibility = Visibility.Collapsed, Margin = new(0, 8, 0, 0) };
    readonly Button revokeFiles = new() { Content = "停止文件传输", Visibility = Visibility.Collapsed };
    readonly TextBlock filesScopeLabel = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(22, 4, 0, 0), Visibility = Visibility.Collapsed, FontSize = 12 };
    internal bool FilesChoiceVisible => filesPermission.Visibility == Visibility.Visible;
    internal bool FilesChoiceEnabled => filesPermission.IsEnabled;
    internal bool FilesAllowed { get => filesPermission.IsChecked == true; set => filesPermission.IsChecked = value; }
    void AddFileSettings(StackPanel settings)
    {
        settings.Children.Add(new TextBlock { Text = "文件传输", FontWeight = FontWeights.SemiBold, Margin = new(0, 14, 0, 0) });
        settings.Children.Add(fileRootLabel);
        settings.Children.Add(new TextBlock { Text = "首次设备授权时可允许文件传输。默认使用独立传输目录，可用文件剪贴板复制粘贴，无需提前选择目录。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12 });
        var openRoot = new Button { Content = "打开传输目录" };
        var actions = new WrapPanel(); actions.Children.Add(openRoot); actions.Children.Add(chooseFileRoot); actions.Children.Add(clearFileRoot); settings.Children.Add(actions);
        openRoot.Click += (_, _) =>
        {
            EnsureDefaultFileRoot();
            if (selectedFileRoot is not { } directory) return;
            try { _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { fileRootLabel.Text = "无法打开传输目录。"; }
        };
        chooseFileRoot.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "选择允许上传和下载的本地目录", Multiselect = false };
            if (dialog.ShowDialog(this) == true) { try { await SelectFileRoot(dialog.FolderName); } catch (Exception error) { detail.Text = "无法使用此目录：" + error.Message; } }
        };
        clearFileRoot.Click += (_, _) => { if (Host.Running||RestartPending) return; selectedFileRoot = null; fileRootLabel.Text = "使用 RelaxDesk 默认传输目录"; };
        revokeFiles.Click += async (_, _) => await StopFiles();
    }
    void EnsureDefaultFileRoot()
    {
        if (selectedFileRoot is not null) return;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "Transfers");
        try
        {
            Directory.CreateDirectory(path);
            _ = new RelaxDesk.Windows.WindowsFileRoot(path);
            selectedFileRoot = path;
            fileRootLabel.Text = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { fileRootLabel.Text = "默认传输目录不可用。桌面共享仍可使用。"; }
    }
    internal async Task SelectFileRoot(string directory)
    {
        if (Host.Running||RestartPending) throw new InvalidOperationException("请先停止共享再更换目录");
        string full = Path.GetFullPath(directory);
        await Task.Run(() =>
        {
            if (full.StartsWith("\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) || !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请选择现有的普通本地目录");
        });
        if (Host.Running||RestartPending) throw new InvalidOperationException("共享启动后不能更换目录");
        selectedFileRoot = full; fileRootLabel.Text = full; fileRootLabel.ToolTip = full;
    }
    void SetFileConfigurationEnabled(bool enabled) { chooseFileRoot.IsEnabled = clearFileRoot.IsEnabled = enabled; }
    void ShowFileRequest(bool requested)
    {
        filesPermission.IsChecked = false; filesPermission.IsEnabled = selectedFileRoot is not null;
        filesPermission.Content = selectedFileRoot is null ? "传输目录不可用" : "允许文件传输（文件复制粘贴另需勾选剪贴板）";
        filesPermission.Visibility = requested ? Visibility.Visible : Visibility.Collapsed;
        filesScopeLabel.Text = selectedFileRoot is null ? "本次未开放任何文件目录。" : "仅此目录及其子目录：" + selectedFileRoot;
        filesScopeLabel.Visibility = filesPermission.Visibility;
    }
    internal async Task StopFiles()
    {
        string? id=displayedSessionId;if(id is null)return;
        await Host.RevokeFiles(id);if(displayedSessionId!=id)return;revokeFiles.Visibility = Visibility.Collapsed;
        UpdateSavedGrant(grant => grant with { FileRoot = null });
        detail.Text = "已撤销此设备的文件传输权限。";
    }
}
