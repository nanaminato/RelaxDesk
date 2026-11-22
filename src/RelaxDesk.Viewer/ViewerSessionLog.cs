using System.IO;
namespace RelaxDesk.Viewer;

internal static class ViewerSessionLog
{
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "logs", $"viewer-{DateTime.Now:yyyyMMdd}.log");
    public static void Record(string operation, Exception? error = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Deliberately exclude textbox contents, credentials and exception
            // messages that might contain a complete user-supplied invitation.
            File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} operation={operation} error_type={error?.GetType().Name ?? "none"} hresult=0x{error?.HResult ?? 0:X8}{Environment.NewLine}");
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
