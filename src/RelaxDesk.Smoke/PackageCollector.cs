using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

// Self-contained entry: no SDK, dotnet CLI, Python, Git or .ps1 execution needed.
internal sealed class PackageCollector
{
    record Step(string name, string status, int exit_code, double elapsed_seconds);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    readonly string root, output;
    readonly List<Step> steps = [];
    string App(string relative) => Path.Combine(root, relative);
    PackageCollector(string root)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        output = App($"logs/home-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
    }
    async Task Save(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, JsonOptions));
    static async Task Verify(string root)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "SHA256SUMS.json")));
        int count = 0;
        foreach (var item in manifest.RootElement.EnumerateArray())
        {
            string path = Path.GetFullPath(Path.Combine(root, item.GetProperty("path").GetString()!));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Manifest path escapes package");
            await using var file = File.OpenRead(path);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!hash.Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("Package hash mismatch: " + Path.GetRelativePath(root, path));
            ++count;
        }
        if (count == 0) throw new IOException("Empty package manifest");
        Console.WriteLine($"PASS package SHA256 verification: {count} files");
    }
    async Task<int> Exec(string name, string application, string[] arguments, int seconds = 120, int[]? blocked = null)
    {
        var clock = Stopwatch.StartNew(); int code = -1;
        using var process = new Process { StartInfo = new(application) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new IOException("Cannot start " + application);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try { await process.WaitForExitAsync(deadline.Token); code = process.ExitCode; }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            await File.WriteAllTextAsync(Path.Combine(output, name + ".stdout.txt"), await stdout.WaitAsync(TimeSpan.FromSeconds(5)));
            await File.WriteAllTextAsync(Path.Combine(output, name + ".stderr.txt"), await stderr.WaitAsync(TimeSpan.FromSeconds(5)) + (code == -1 ? $"\nTimed out after {seconds}s" : ""));
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(output, name + ".stderr.txt"), error.ToString()); }
        string state = code == 0 ? "PASS" : blocked?.Contains(code) == true ? "BLOCKED" : "FAIL";
        steps.Add(new(name, state, code, clock.Elapsed.TotalSeconds)); Console.WriteLine($"{name} : {state} ({code})"); return code;
    }
    static bool Eligible(string? edition, JsonElement e, bool diagnostic) => !diagnostic && edition?.StartsWith("Core", StringComparison.OrdinalIgnoreCase) == true
        && e.GetProperty("environment").GetString() == "local-console" && e.GetProperty("session_id").GetInt32() == e.GetProperty("active_console_session").GetInt32()
        && e.GetProperty("station_visible").GetBoolean() && e.GetProperty("window_station").GetString() == "WinSta0"
        && e.GetProperty("input_desktop").GetString() == "Default" && e.GetProperty("thread_desktop").GetString() == "Default"
        && e.GetProperty("input_desktop_error").GetInt32() == 0 && e.GetProperty("connection_state").GetInt32() == 0;
    public static async Task<int> Run(string packageRoot, string[] arguments)
    {
        bool diagnostic = false, verifyOnly = false; int frames = 300;
        try
        {
            for (int i = 0; i < arguments.Length; ++i)
            {
                switch (arguments[i].ToLowerInvariant())
                {
                    case "--diagnostic-only": case "-diagnosticonly": diagnostic = true; break;
                    case "--verify-only": case "-verifyonly": verifyOnly = true; break;
                    case "--frames": case "-frames": frames = int.Parse(arguments[++i]); if (frames is < 60 or > 1800) throw new ArgumentException("Frames must be 60..1800"); break;
                    default: throw new ArgumentException("Unknown collector argument " + arguments[i]);
                }
            }
            var collector = new PackageCollector(packageRoot);
            await Verify(collector.root);
            if (verifyOnly) return 0;
            return await collector.Collect(diagnostic, frames);
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    async Task<int> Collect(bool diagnostic, int frames)
    {
        Directory.CreateDirectory(output); bool eligible = false; string status = "FAIL";
        string host = App("Host/RelaxDesk.Host.exe"), viewer = App("Viewer/RelaxDesk.Viewer.exe"), smoke = App("Tools/Smoke/RelaxDesk.Smoke.exe"), probe = App("Tools/Native/RelaxDesk.NativeProbe.exe");
        try
        {
            File.Copy(App("package.json"), Path.Combine(output, "package.json"));
            File.Copy(App("SHA256SUMS.json"), Path.Combine(output, "package-hashes.json"));
            using var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string? edition = os?.GetValue("EditionID") as string;
            await Save("machine.json", new { collected_at = DateTimeOffset.Now, edition, product = os?.GetValue("ProductName"), build = os?.GetValue("CurrentBuildNumber"), ubr = os?.GetValue("UBR"), architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), diagnostic_only = diagnostic, manual_input = "NOT_TESTED", physical_scanout = "NOT_TESTED", dual_pc_lan = "NOT_TESTED" });
            // Read-only inventory command; no script file or execution-policy override.
            await Exec("display-drivers", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), ["-NoProfile", "-Command", "Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution | ConvertTo-Json"], 30);
            var hashes = new List<object>(); string? nativeHash = null;
            foreach (string relative in new[] { "Tools/Native/RelaxDesk.Native.dll", "Host/RelaxDesk.Native.dll", "Viewer/RelaxDesk.Native.dll", "Tools/Smoke/RelaxDesk.Native.dll" })
            {
                using var file = File.OpenRead(App(relative)); string hash = Convert.ToHexString(SHA256.HashData(file));
                nativeHash ??= hash; if (hash != nativeHash) throw new IOException("Native DLL mismatch"); hashes.Add(new { path = relative, sha256 = hash });
            }
            await Save("binary-hashes.json", hashes);
            await Exec("present-contracts", probe, ["--present-contracts"]);
            if (await Exec("capture-diagnostics", host, ["--capture-diagnose"]) != 0) throw new IOException("Cannot establish capture environment");
            using var environment = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "capture-diagnostics.stdout.txt")));
            eligible = Eligible(edition, environment.RootElement, diagnostic);
            await Save("eligibility.json", new { eligible, edition, environment = environment.RootElement });
            await Exec("core-tls", App("Tools/Tests/RelaxDesk.Tests.exe"), []);
            await Exec("windows-contracts", smoke, ["--windows-contracts"]);
            foreach (string mode in new[] { "--reject-token", "--reject-pin", "--deny-v2", "--reject-media" }) await Exec(mode[2..], smoke, [host, mode]);
            await Exec("deny-approval", smoke, [host, "--deny-approval"]);
            if (await Exec("deployment", host, ["--deployment-info"]) != 0) throw new IOException("Runtime launch failed");
            using var deployment = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "deployment.stdout.txt")));
            if (!deployment.RootElement.GetProperty("x64").GetBoolean() || !Path.GetFullPath(deployment.RootElement.GetProperty("runtime").GetString()!).StartsWith(App("Host") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Host did not load packaged x64 runtime");
            using var package = JsonDocument.Parse(await File.ReadAllTextAsync(App("package.json")));
            if (package.RootElement.TryGetProperty("version", out var version))
            {
                if (deployment.RootElement.GetProperty("version").GetString() != version.GetString()) throw new IOException("Host version does not match package manifest");
                if (await Exec("viewer-version", viewer, ["--version"], 30) != 0 || (await File.ReadAllTextAsync(Path.Combine(output,"viewer-version.stdout.txt"))).Trim() != version.GetString()) throw new IOException("Viewer version does not match package manifest");
            }
            await Exec("wpf-hwnd-contracts", viewer, ["--hwnd-contracts"], 30);
            await Exec("viewer-ui-contracts", viewer, ["--ui-contracts"], 30);
            if (!eligible && !diagnostic) { status = "NOT_ACCEPTED_ENVIRONMENT"; await File.WriteAllTextAsync(Path.Combine(output, "stop-reason.txt"), "Not an unlocked Windows Home local console. No repeated GPU attempts."); }
            else
            {
                int comparison = await Exec("present-comparison", probe, ["--present-diagnose"], 45, [2]);
                if (diagnostic) status = "NOT_ACCEPTED_ENVIRONMENT";
                else if (comparison != 0) status = comparison == 2 ? "BLOCKED" : "FAIL";
                else
                {
                    bool good = true;
                    foreach (string mode in new[] { "--video-software", "--video-hardware", "--video-recovery", "--video-stalled", "--video-producer-failure", "--video-runtime-fallback" }) { if (await Exec(mode[2..], smoke, [host, mode]) != 0) { good = false; break; } }
                    if (good) good = await Exec("native-render", probe, ["--render-test"], 45) == 0;
                    if (good)
                        foreach (string mode in new[] { "approval-cpu", "hardware-cpu", "gpu", "gpu-window", "gpu-idle", "gpu-resolution", "gpu-recovery", "gpu-device-fallback", "gpu-fallback", "gpu-close", "desktop-gpu" })
                            if (await Exec("wpf-" + mode, smoke, [host, "--wpf", viewer, mode]) != 0) { good = false; break; }
                    if (good)
                        foreach (string mode in new[] { "deflate", "software", "hardware-cpu", "gpu" })
                            if (await Exec("benchmark-" + mode, smoke, [host, "--wpf-budget", viewer, mode, frames.ToString()], 240) != 0) { good = false; break; }
                    status = good ? "PASS_LOCAL_AUTOMATION_ONLY" : "FAIL";
                }
            }
        }
        catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(output, "stop-reason.txt"), error.ToString()); status = "FAIL"; }
        if (steps.Any(s => s.status == "FAIL")) status = "FAIL";
        await Save("result.json", new { status, home_console_eligible = eligible, steps, physical_scanout = "NOT_TESTED", input_to_photon = "NOT_TESTED", dual_pc_lan = "NOT_TESTED", resource_sampling = "Per-stage application metrics; no external GPU counters in EXE collector" });
        ZipFile.CreateFromDirectory(output, output + ".zip", CompressionLevel.Optimal, includeBaseDirectory: true);
        Console.WriteLine($"Result: {status}; logs: {output}.zip");
        return status == "PASS_LOCAL_AUTOMATION_ONLY" ? 0 : 2;
    }
}
