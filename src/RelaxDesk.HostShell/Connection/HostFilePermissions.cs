using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    string? selectedFileRoot;
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
        { throw new IOException("Cannot create the file clipboard staging directory",error); }
    }
    internal async Task SelectFileRoot(string directory)
    {
        if (Host.Running||RestartPending) throw new InvalidOperationException(Strings.Get("Ui.HostFilePermissions.Text451"));
        string full = Path.GetFullPath(directory);
        await Task.Run(() =>
        {
            if (full.StartsWith("\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) || !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Strings.Get("Ui.HostFilePermissions.Text465"));
        });
        if (Host.Running||RestartPending) throw new InvalidOperationException(Strings.Get("Ui.HostFilePermissions.Text120"));
        selectedFileRoot = full;
    }
}
