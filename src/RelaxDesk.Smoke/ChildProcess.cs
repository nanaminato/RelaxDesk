using System.Diagnostics;

internal static class ChildProcess
{
    public static void Report(Process process, string role) => Console.WriteLine("benchmark_process " + System.Text.Json.JsonSerializer.Serialize(new
    { Role = role, ProcessId = process.Id, StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks }));
    // DLL launch is retained for developer builds; acceptance packages use their
    // self-contained apphosts and never look for a machine-wide dotnet command.
    public static ProcessStartInfo Create(string application, params string[] arguments)
    {
        string path = Path.GetFullPath(application);
        bool executable = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(executable ? path : "dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        if (!executable) start.ArgumentList.Add(path);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
