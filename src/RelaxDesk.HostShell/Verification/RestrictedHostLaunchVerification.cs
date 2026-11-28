using System.Diagnostics;
using System.IO;

namespace RelaxDesk.HostShell;

internal static class RestrictedHostLaunchVerification
{
    public static int Run(string executable, string result)
    {
        using var log = new StreamWriter(new FileStream(result, FileMode.CreateNew, FileAccess.Write));
        if (!RestrictedHostLaunch.Required())
        {
            log.WriteLine("SKIP: run this probe from the elevated local built-in Administrator session.");
            return 2;
        }

        try
        {
            var start = new ProcessStartInfo(Path.GetFullPath(executable));
            start.ArgumentList.Add("--input-token-diagnose");
            var launch = RestrictedHostLaunch.Start(start);
            using var child = launch.Process;
            using var input = launch.Input;
            using var output = launch.Output;
            using var error = launch.Error;
            var stdout = output.ReadToEndAsync();
            var stderr = error.ReadToEndAsync();
            if (!child.WaitForExit(10_000))
            {
                child.Kill();
                throw new IOException("Restricted Host token check timed out.");
            }

            string observed = stdout.GetAwaiter().GetResult();
            string diagnostics = stderr.GetAwaiter().GetResult();
            log.WriteLine(observed);
            log.WriteLine(diagnostics);
            using var current = Process.GetCurrentProcess();
            if (child.ExitCode != 0 ||
                !observed.Contains("ordinary_input_token=allowed", StringComparison.Ordinal) ||
                !observed.Split('\n').Any(line => line.TrimEnd() == $"input_token_session={current.SessionId}"))
                throw new IOException("Restricted Host did not use an ordinary token in the caller's session.");
            log.WriteLine("PASS: same logon session, ordinary Host token, and redirected output.");
            return 0;
        }
        catch (Exception error)
        {
            log.WriteLine(error);
            return 1;
        }
    }
}
