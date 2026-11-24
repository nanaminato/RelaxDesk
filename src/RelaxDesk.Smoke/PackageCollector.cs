using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using System.Text;

// Self-contained entry: no SDK, dotnet CLI, Python, Git or .ps1 execution needed.
internal sealed partial class PackageCollector
{
    record Step(string name, string status, int exit_code, double elapsed_seconds);
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    readonly string root, output;
    readonly List<Step> steps = [];
    string App(string relative) => Path.Combine(root, relative);
    PackageCollector(string root, string? sourceOutput = null)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        output = sourceOutput is null ? App($"logs/home-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}") : Path.GetFullPath(sourceOutput);
    }
    async Task Save(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, JsonOptions));
    internal static async Task Verify(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        void Guard(string path)
        {
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Manifest path is redirected");
        }
        string manifestPath = Path.Combine(root, "SHA256SUMS.json"); Guard(manifestPath);
        await using var source = File.OpenRead(manifestPath);
        if (source.Length is < 2 or > 4 * 1024 * 1024) throw new IOException("Package manifest size invalid");
        using var manifest = await JsonDocument.ParseAsync(source, new JsonDocumentOptions { MaxDepth = 8 });
        if (manifest.RootElement.ValueKind != JsonValueKind.Array || manifest.RootElement.GetArrayLength() is < 1 or > 8192) throw new IOException("Package manifest entry count invalid");
        int count = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.RootElement.EnumerateArray())
        {
            string relative = item.GetProperty("path").GetString() ?? throw new IOException("Manifest path missing");
            string expected = item.GetProperty("sha256").GetString() ?? "";
            string normalized = relative.Replace('\\', '/');
            if (relative.Length is < 1 or > 512 || normalized.Contains(':') || normalized.Contains('\0') || normalized.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.TrimEnd(' ', '.') != part)
                || expected.Length != 64 || !expected.All(char.IsAsciiHexDigit) || !seen.Add(normalized)) throw new IOException("Invalid/duplicate package manifest entry");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Manifest path escapes package");
            Guard(path);
            await using var file = File.OpenRead(path);
            if (item.TryGetProperty("bytes", out var bytes) && (!bytes.TryGetInt64(out long length) || length < 0 || file.Length != length)) throw new IOException("Package byte length mismatch");
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Package hash mismatch: " + Path.GetRelativePath(root, path));
            ++count;
        }
        if (count == 0) throw new IOException("Empty package manifest");
        Console.WriteLine($"PASS package SHA256 verification: {count} files");
    }
    internal static async Task<(string Text, bool Truncated)> Capture(StreamReader reader)
    {
        const int limit = 1024 * 1024; char[] buffer = new char[4096]; var text = new StringBuilder(); bool truncated = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer); if (count == 0) break;
            int kept = Math.Min(count, limit - text.Length); if (kept > 0) text.Append(buffer, 0, kept);
            if (kept != count) truncated = true;
        }
        return (text.ToString(), truncated);
    }
    async Task<int> Exec(string name, string application, string[] arguments, int seconds = 120, int[]? blocked = null)
    {
        var clock = Stopwatch.StartNew(); int code = -1;
        var info = ChildProcess.Create(application, arguments); info.WorkingDirectory = root;
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("Cannot start " + application);
            var stdout = Capture(process.StandardOutput); var stderr = Capture(process.StandardError);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try { await process.WaitForExitAsync(deadline.Token); code = process.ExitCode; }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            var capturedOutput = await stdout.WaitAsync(TimeSpan.FromSeconds(5)); var capturedError = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            if (capturedOutput.Truncated || capturedError.Truncated) code = -1;
            await File.WriteAllTextAsync(Path.Combine(output, name + ".stdout.txt"), capturedOutput.Text + (capturedOutput.Truncated ? "\nCOLLECTOR_OUTPUT_TRUNCATED" : ""));
            await File.WriteAllTextAsync(Path.Combine(output, name + ".stderr.txt"), capturedError.Text + (capturedError.Truncated ? "\nCOLLECTOR_OUTPUT_TRUNCATED" : "") + (code == -1 ? $"\nIncomplete output or timed out after {seconds}s" : ""));
        }
        catch (Exception error) { code = -1; await File.WriteAllTextAsync(Path.Combine(output, name + ".stderr.txt"), error.ToString()); }
        finally
        {
            try { if (process.Id > 0 && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { code = -1; }
        }
        string state = code == 0 ? "PASS" : blocked?.Contains(code) == true ? "BLOCKED" : "FAIL";
        steps.Add(new(name, state, code, clock.Elapsed.TotalSeconds)); Console.WriteLine($"{name} : {state} ({code})"); return code;
    }
    static bool Eligible(string? edition, JsonElement e, bool diagnostic) => !diagnostic && edition?.StartsWith("Core", StringComparison.OrdinalIgnoreCase) == true
        && e.GetProperty("environment").GetString() == "local-console" && e.GetProperty("session_id").GetInt32() == e.GetProperty("active_console_session").GetInt32()
        && e.GetProperty("station_visible").GetBoolean() && e.GetProperty("window_station").GetString() == "WinSta0"
        && e.GetProperty("input_desktop").GetString() == "Default" && e.GetProperty("thread_desktop").GetString() == "Default"
        && e.GetProperty("input_desktop_error").GetInt32() == 0 && e.GetProperty("connection_state").GetInt32() == 0;
    async Task Office(OfficeApplications applications)
    {
        var cases = OfficeAcceptanceCases.Create(applications, Path.Combine(output, "office-fixtures"));
        await Save("office-suite.json", new { source = "synthetic-contracts", count = cases.Length, cases,
            excluded = new[] { "physical capture/input/scanout", "real audio audibility", "actual SCM/startup mutation", "PowerShell installer contracts", ">1GiB file copy", "long soak", "matched office/MSTSC performance" } });
        foreach (var test in cases)
            await Exec(test.Name, test.Application, test.Arguments, test.Seconds);
    }
    public static async Task<int> SourceOffice(string artifacts, string destination)
    {
        artifacts = Path.GetFullPath(artifacts); destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Choose a fresh office result directory");
        string Dll(string role) => Path.Combine(artifacts, "bin", "RelaxDesk." + role, "release", "RelaxDesk." + role + ".dll");
        var applications = new OfficeApplications(Dll("Host"), Dll("Viewer"), Dll("HostShell"), Dll("Service"), Dll("Smoke"), Dll("Tests"));
        foreach (var file in new[] { applications.Host, applications.Viewer, applications.Shell, applications.Service, applications.Smoke, applications.Tests })
            if (!File.Exists(file)) throw new FileNotFoundException("Office application missing", file);
        var collector = new PackageCollector(Environment.CurrentDirectory, destination); Directory.CreateDirectory(destination);
        await collector.Office(applications);
        bool good = collector.steps.Count > 0 && collector.steps.All(step => step.status == "PASS");
        await collector.Save("result.json", new { status = good ? "PASS_SOURCE_AUTOMATION_ONLY" : "FAIL", steps = collector.steps, physical_scanout = "NOT_TESTED", input_to_photon = "NOT_TESTED", dual_pc_lan = "NOT_TESTED", packaged_runtime = "NOT_TESTED" });
        Console.WriteLine($"Source office checks: {collector.steps.Count}; result={destination}; no package or physical acceptance");
        return good ? 0 : 1;
    }
    public static async Task<int> Run(string packageRoot, string[] arguments)
    {
        bool diagnostic = false, verifyOnly = false, officeOnly = false; int frames = 300;
        try
        {
            for (int i = 0; i < arguments.Length; ++i)
            {
                switch (arguments[i].ToLowerInvariant())
                {
                    case "--diagnostic-only": case "-diagnosticonly": diagnostic = true; break;
                    case "--verify-only": case "-verifyonly": verifyOnly = true; break;
                    case "--office-only": officeOnly = true; break;
                    case "--frames": case "-frames": frames = int.Parse(arguments[++i]); if (frames is < 60 or > 1800) throw new ArgumentException("Frames must be 60..1800"); break;
                    default: throw new ArgumentException("Unknown collector argument " + arguments[i]);
                }
            }
            if (officeOnly && (diagnostic || verifyOnly)) throw new ArgumentException("Office-only cannot combine with diagnostic/verify-only");
            var collector = new PackageCollector(packageRoot, officeOnly ? Path.Combine(Path.GetFullPath(packageRoot), "logs", "office-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]) : null);
            await Verify(collector.root);
            if (verifyOnly) return 0;
            if (officeOnly)
            {
                Directory.CreateDirectory(collector.output);
                await collector.Office(new(collector.App("Host/RelaxDesk.Host.exe"),collector.App("Viewer/RelaxDesk.Viewer.exe"),collector.App("HostUi/RelaxDesk.HostShell.exe"),collector.App("Service/RelaxDesk.Service.exe"),collector.App("Tools/Smoke/RelaxDesk.Smoke.exe"),collector.App("Tools/Tests/RelaxDesk.Tests.exe")));
                bool good = collector.steps.All(step => step.status == "PASS");
                await collector.Save("result.json", new { status = good ? "PASS_PACKAGED_SYNTHETIC_OFFICE_ONLY" : "FAIL", steps = collector.steps, home_console_eligible = "NOT_EVALUATED", physical_scanout = "NOT_TESTED", input_to_photon = "NOT_TESTED", dual_pc_lan = "NOT_TESTED" });
                Console.WriteLine($"Office checks: {collector.steps.Count}; logs={collector.output}; no physical/Home acceptance");
                return good ? 0 : 1;
            }
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
            await Save("machine.json", new
            {
                collected_at = DateTimeOffset.Now, edition, product = os?.GetValue("ProductName"),
                build = os?.GetValue("CurrentBuildNumber"), ubr = os?.GetValue("UBR"),
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                diagnostic_only = diagnostic, manual_input = "NOT_TESTED", physical_scanout = "NOT_TESTED",
                dual_pc_lan = "NOT_TESTED", admin_window_input = "NOT_TESTED", uac_secure_desktop = "NOT_TESTED",
                lock_unlock = "NOT_TESTED", sign_in = "NOT_TESTED", console_user_switch = "NOT_TESTED"
            });
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
            await Exec("capture-contracts", probe, ["--capture-contracts"]);
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
                string shell = App("HostUi/RelaxDesk.HostShell.exe");
                if (await Exec("host-ui-version", shell, ["--version"], 30) != 0 || (await File.ReadAllTextAsync(Path.Combine(output,"host-ui-version.stdout.txt"))).Trim() != version.GetString()) throw new IOException("Host UI version does not match package manifest");
                await Exec("host-ui-contracts", shell, ["--shell-contracts",host,Path.Combine(output,"host-ui")], 75);
            }
            await Exec("wpf-hwnd-contracts", viewer, ["--hwnd-contracts"], 30);
            await Exec("viewer-ui-contracts", viewer, ["--ui-contracts"], 30);
            await Exec("viewer-toolbar-contracts", viewer, ["--toolbar-contracts"], 30);
            await Office(new(host, viewer, App("HostUi/RelaxDesk.HostShell.exe"), App("Service/RelaxDesk.Service.exe"), smoke, App("Tools/Tests/RelaxDesk.Tests.exe")));
            if (!eligible && !diagnostic) { status = "NOT_ACCEPTED_ENVIRONMENT"; await File.WriteAllTextAsync(Path.Combine(output, "stop-reason.txt"), "Not an unlocked Windows Home local console. No repeated GPU attempts."); }
            else
            {
                int comparison = await Exec("present-comparison", probe, ["--present-diagnose"], 45, [2]);
                if (diagnostic) status = "NOT_ACCEPTED_ENVIRONMENT";
                else if (comparison != 0) status = comparison == 2 ? "BLOCKED" : "FAIL";
                else
                {
                    bool good = await Exec("viewer-keyboard-contracts", viewer, ["--keyboard-contracts"], 30) == 0;
                    foreach (string mode in new[] { "--video-software", "--video-hardware", "--video-recovery", "--video-stalled", "--video-producer-failure", "--video-runtime-fallback", "--video-capture-recovery", "--video-capture-exhausted", "--video-control-timeout", "--video-idle-control" }) { if (!good || await Exec(mode[2..], smoke, [host, mode]) != 0) { good = false; break; } }
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
