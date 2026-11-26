using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    string? selectedFileRoot;
    bool usesAutomaticFileClipboard;
    readonly CheckBox filesPermission = new() { Content = "允许文件传输及文件剪贴板", IsChecked = false, Visibility = Visibility.Collapsed, Margin = new(0, 8, 0, 0) };
    readonly Button revokeFiles = new() { Content = "停止文件传输", Visibility = Visibility.Collapsed };
    readonly TextBlock filesScopeLabel = new() { TextWrapping = TextWrapping.Wrap, Margin = new(22, 4, 0, 0), Visibility = Visibility.Collapsed, FontSize = 12 };
    internal bool FilesChoiceVisible => filesPermission.Visibility == Visibility.Visible;
    internal bool FilesChoiceEnabled => filesPermission.IsEnabled;
    internal bool FilesAllowed { get => filesPermission.IsChecked == true; set => filesPermission.IsChecked = value; }
    void EnsureDefaultFileRoot()
    {
        selectedFileRoot = null;
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "Transfers");
        try
        {
            Directory.CreateDirectory(path);
            _ = new RelaxDesk.Windows.WindowsFileRoot(path);
            selectedFileRoot = path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { detail.Text = "文件剪贴板缓存不可用。桌面共享仍可使用。"; }
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
        selectedFileRoot = full;
    }
    void ShowFileRequest(bool requested)
    {
        filesPermission.IsChecked = false; filesPermission.IsEnabled = selectedFileRoot is not null;
        filesPermission.Content = selectedFileRoot is null ? "传输目录不可用" : "允许文件传输（文件复制粘贴另需勾选剪贴板）";
        filesPermission.Visibility = requested && !usesAutomaticFileClipboard ? Visibility.Visible : Visibility.Collapsed;
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
