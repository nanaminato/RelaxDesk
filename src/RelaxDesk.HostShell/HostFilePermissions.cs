using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    string? selectedFileRoot;
    readonly TextBox fileRootLabel = new() { Text = "未选择目录（默认不开放文件访问）", IsReadOnly = true, Margin = new(0, 4, 0, 0) };
    readonly Button chooseFileRoot = new() { Content = "选择文件共享目录…" }, clearFileRoot = new() { Content = "清除" };
    readonly CheckBox filesPermission = new() { Content = "允许在选定目录内上传、下载、列出文件及短期续传", IsChecked = false, Visibility = Visibility.Collapsed, Margin = new(0, 8, 0, 0) };
    readonly Button revokeFiles = new() { Content = "停止文件传输", Visibility = Visibility.Collapsed };
    readonly TextBlock filesScopeLabel = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(22, 4, 0, 0), Visibility = Visibility.Collapsed, FontSize = 12 };
    internal bool FilesChoiceVisible => filesPermission.Visibility == Visibility.Visible;
    internal bool FilesChoiceEnabled => filesPermission.IsEnabled;
    internal bool FilesAllowed { get => filesPermission.IsChecked == true; set => filesPermission.IsChecked = value; }
    void AddFileSettings(StackPanel settings)
    {
        settings.Children.Add(new TextBlock { Text = "可选文件共享目录", FontWeight = FontWeights.SemiBold, Margin = new(0, 14, 0, 0) });
        settings.Children.Add(fileRootLabel);
        settings.Children.Add(new TextBlock { Text = "仅开放你选择的普通本地目录，收到请求后仍需单独允许。续传有效期5分钟；中断文件可能保持占用，到期或停止共享时清理。链接、云占位符及网络目录不支持。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 12 });
        var actions = new WrapPanel(); actions.Children.Add(chooseFileRoot); actions.Children.Add(clearFileRoot); settings.Children.Add(actions);
        chooseFileRoot.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "选择允许上传和下载的本地目录", Multiselect = false };
            if (dialog.ShowDialog(this) == true) { try { await SelectFileRoot(dialog.FolderName); } catch (Exception error) { detail.Text = "无法使用此目录：" + error.Message; } }
        };
        clearFileRoot.Click += (_, _) => { if (Host.Running) return; selectedFileRoot = null; fileRootLabel.Text = "未选择目录（默认不开放文件访问）"; };
        revokeFiles.Click += async (_, _) => await StopFiles();
    }
    internal async Task SelectFileRoot(string directory)
    {
        if (Host.Running) throw new InvalidOperationException("请先停止共享再更换目录");
        string full = Path.GetFullPath(directory);
        await Task.Run(() =>
        {
            if (full.StartsWith("\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) || !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请选择现有的普通本地目录");
        });
        if (Host.Running) throw new InvalidOperationException("共享启动后不能更换目录");
        selectedFileRoot = full; fileRootLabel.Text = full; fileRootLabel.ToolTip = full;
    }
    void SetFileConfigurationEnabled(bool enabled) { chooseFileRoot.IsEnabled = clearFileRoot.IsEnabled = enabled; }
    void ShowFileRequest(bool requested)
    {
        filesPermission.IsChecked = false; filesPermission.IsEnabled = selectedFileRoot is not null;
        filesPermission.Content = selectedFileRoot is null ? "文件访问未开放（请停止共享后先选择目录）" : "允许在选定目录内上传、下载、列出文件及短期续传";
        filesPermission.Visibility = requested ? Visibility.Visible : Visibility.Collapsed;
        filesScopeLabel.Text = selectedFileRoot is null ? "本次未开放任何文件目录。" : "仅此目录及其子目录：" + selectedFileRoot;
        filesScopeLabel.Visibility = filesPermission.Visibility;
    }
    internal async Task StopFiles()
    {
        string? id=displayedSessionId;if(id is null)return;
        await Host.RevokeFiles(id);if(displayedSessionId!=id)return;revokeFiles.Visibility = Visibility.Collapsed;
        detail.Text = "已停止文件传输；重新开启需要重新连接并授权。";
    }
}
