namespace RelaxDesk.Host;

internal static class HostSessionLog
{
    static readonly object sync = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "logs", $"host-{DateTime.Now:yyyyMMdd}.log");
    // Only structured pipeline errors/recovery call this. Startup credentials,
    // invitations, wire messages and input contents must never be passed here.
    public static void Write(string diagnostic)
    {
        Console.WriteLine(diagnostic);
        try { lock (sync) { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {diagnostic}{Environment.NewLine}"); } }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
