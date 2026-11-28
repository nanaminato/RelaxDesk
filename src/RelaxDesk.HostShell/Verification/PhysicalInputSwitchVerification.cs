using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

// Explicit, opt-in physical input test. It targets only these two owned windows.
internal static class PhysicalInputSwitchVerification
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }

    sealed class Target : Window
    {
        public readonly Button Button = new() { Content = "RelaxDesk physical input test", Margin = new Thickness(24), Height = 90 };
        public int Downs, Ups, KeyDowns, KeyUps;
        public Action? Updated;
        public Target(string label, double left)
        {
            Title = "RelaxDesk input switch probe " + label;
            Width = 340; Height = 220; Left = left; Top = 80;
            Content = Button;
            Button.PreviewMouseLeftButtonDown += (_, _) => { Downs++; Updated?.Invoke(); };
            Button.PreviewMouseLeftButtonUp += (_, _) => { Ups++; Updated?.Invoke(); };
            PreviewKeyDown += (_, e) => { if (e.Key == Key.F8 && !e.IsRepeat) { KeyDowns++; Updated?.Invoke(); e.Handled = true; } };
            PreviewKeyUp += (_, e) => { if (e.Key == Key.F8) { KeyUps++; Updated?.Invoke(); e.Handled = true; } };
        }
    }

    public static void RunElevatedTarget(Application app, string state)
    {
        state = Path.GetFullPath(state);
        if (File.Exists(state)) throw new IOException("Use a fresh elevated target state path");
        var token = WindowsInputPrivilege.ReadProcessToken();
        if (!token.Elevated || token.IntegrityRid < 0x3000) throw new IOException("Explicitly elevate the input target");
        var target = new Target("elevated acceptance target", 410);
        void Save()
        {
            var center = target.Button.PointToScreen(new Point(target.Button.ActualWidth / 2, target.Button.ActualHeight / 2));
            string json = JsonSerializer.Serialize(new { handle = (long)new WindowInteropHelper(target).Handle,
                processId = Environment.ProcessId, token, x = center.X, y = center.Y,
                target.Downs, target.Ups, target.KeyDowns, target.KeyUps });
            File.WriteAllText(state + ".tmp", json);
            File.Move(state + ".tmp", state, true);
        }
        target.Loaded += (_, _) => Save();
        target.Updated = Save;
        target.PreviewKeyUp += (_, e) => { if (e.Key == Key.F9) target.Close(); };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => target.Close();
        target.Closed += (_, _) => timer.Stop();
        timer.Start();
        app.Run(target);
    }

    public static void RunElevated(Application app, string state, string output)
    {
        if (File.Exists(output)) throw new IOException("Use a fresh elevated input probe result path");
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var ordinaryTarget = new Target("ordinary acceptance target", 50);
        ordinaryTarget.Loaded += async (_, _) =>
        {
            AdminInputConnection? worker = null;
            int switches = 0;
            GetCursorPos(out var cursor);
            try
            {
                WindowsInputPrivilege.EnsureOrdinaryProcess();
                using var highState = JsonDocument.Parse(File.ReadAllText(state));
                var high = highState.RootElement;
                if (!high.GetProperty("token").GetProperty("Elevated").GetBoolean() ||
                    high.GetProperty("token").GetProperty("IntegrityRid").GetInt32() < 0x3000)
                    throw new IOException("Target is not elevated");
                nint highWindow = (nint)high.GetProperty("handle").GetInt64();
                var display = new WindowsDisplaySelection("all");
                worker = await AdminInputLauncher.StartService(Guid.NewGuid(), display.Device, CancellationToken.None);
                async Task Click(double px, double py, nint expected)
                {
                    var bounds = display.Bounds;
                    int x = (int)Math.Round((px - bounds.X) * 65535 / (bounds.Width - 1));
                    int y = (int)Math.Round((py - bounds.Y) * 65535 / (bounds.Height - 1));
                    if (x is < 0 or > 65535 || y is < 0 or > 65535) throw new IOException("Elevated target outside selected desktop");
                    await worker.Send(new("move", X: x, Y: y), CancellationToken.None);
                    await Task.Delay(30);
                    await worker.Send(new("button", Button: 1), CancellationToken.None);
                    await worker.Send(new("button", Button: 1, Up: true), CancellationToken.None);
                    using var focus = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    while (GetForegroundWindow() != expected) await Task.Delay(20, focus.Token);
                }
                for (int index = 0; index < 12; index++)
                {
                    if (index % 2 == 0) await Click(high.GetProperty("x").GetDouble(), high.GetProperty("y").GetDouble(), highWindow);
                    else
                    {
                        var point = ordinaryTarget.Button.PointToScreen(new Point(ordinaryTarget.Button.ActualWidth / 2, ordinaryTarget.Button.ActualHeight / 2));
                        await Click(point.X, point.Y, new WindowInteropHelper(ordinaryTarget).Handle);
                    }
                    await worker.Send(new("key", VirtualKey: 0x77), CancellationToken.None);
                    await worker.Send(new("key", VirtualKey: 0x77, Up: true), CancellationToken.None);
                    await Task.Delay(70);
                    switches++;
                }
                using var received = JsonDocument.Parse(File.ReadAllText(state));
                foreach (string property in new[] { "Downs", "Ups", "KeyDowns", "KeyUps" })
                    if (received.RootElement.GetProperty(property).GetInt32() != 6) throw new IOException("Elevated target input missing: " + property);
                if (ordinaryTarget.Downs != 6 || ordinaryTarget.Ups != 6 || ordinaryTarget.KeyDowns != 6 || ordinaryTarget.KeyUps != 6)
                    throw new IOException("Ordinary target input missing after elevated window switching");
                // Close only the opt-in elevated target, using its explicit F9 handler.
                await Click(high.GetProperty("x").GetDouble(), high.GetProperty("y").GetDouble(), highWindow);
                await worker.Send(new("key", VirtualKey: 0x78), CancellationToken.None);
                await worker.Send(new("key", VirtualKey: 0x78, Up: true), CancellationToken.None);
                await worker.End(CancellationToken.None);
                File.WriteAllText(output, JsonSerializer.Serialize(new { status = "PASS", switches,
                    coordinator = WindowsInputPrivilege.ReadProcessToken(), elevatedTarget = high.GetProperty("token"),
                    os = Environment.OSVersion.ToString(), route = "SCM-SYSTEM-input", uac = "NOT_TESTED" }));
                Console.WriteLine($"PASS {switches} real service switches between ordinary and elevated windows, balanced mouse/F8 input");
            }
            catch (Exception error)
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new { status = "FAIL", switches, error = error.ToString() }));
                Console.Error.WriteLine(error); Environment.ExitCode = 1;
            }
            finally
            {
                if (worker is not null) await worker.DisposeAsync();
                SetCursorPos(cursor.X, cursor.Y);
                ordinaryTarget.Close(); app.Shutdown();
            }
        };
        app.Run(ordinaryTarget);
    }

    public static void Run(Application app, string mode, string output)
    {
        if (mode is not ("ordinary" or "service")) throw new ArgumentException("Input probe mode");
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException("Use a fresh physical probe result path");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var first = new Target("A", 50);
        var second = new Target("B", 410);
        first.Loaded += async (_, _) =>
        {
            AdminInputConnection? worker = null;
            WindowsInputSession? ordinary = null;
            GetCursorPos(out var cursor);
            int switches = 0;
            try
            {
                WindowsInputPrivilege.EnsureOrdinaryProcess();
                var display = new WindowsDisplaySelection("all");
                if (mode == "service") worker = await AdminInputLauncher.StartService(Guid.NewGuid(), display.Device, CancellationToken.None);
                else ordinary = new WindowsInputSession(display);
                second.Show();
                first.Activate();
                await Task.Delay(300);
                async Task Send(RemoteInput command)
                {
                    if (worker is not null) await worker.Send(command, CancellationToken.None);
                    else ordinary!.Inject(command);
                }
                foreach (var target in Enumerable.Range(0, 12).Select(index => index % 2 == 0 ? second : first))
                {
                    var point = target.Button.PointToScreen(new Point(target.Button.ActualWidth / 2, target.Button.ActualHeight / 2));
                    var bounds = display.Bounds;
                    int x = (int)Math.Round((point.X - bounds.X) * 65535 / (bounds.Width - 1));
                    int y = (int)Math.Round((point.Y - bounds.Y) * 65535 / (bounds.Height - 1));
                    if (x is < 0 or > 65535 || y is < 0 or > 65535) throw new IOException("Probe target outside selected desktop");
                    await Send(new("move", X: x, Y: y));
                    await Task.Delay(40);
                    await Send(new("button", Button: 1));
                    await Send(new("button", Button: 1, Up: true));
                    using var focusDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    nint expected = new WindowInteropHelper(target).Handle;
                    while (GetForegroundWindow() != expected) await Task.Delay(20, focusDeadline.Token);
                    await Send(new("key", VirtualKey: 0x77));
                    await Send(new("key", VirtualKey: 0x77, Up: true));
                    await Task.Delay(60);
                    switches++;
                }
                foreach (var target in new[] { first, second })
                    if (target.Downs != 6 || target.Ups != 6 || target.KeyDowns != 6 || target.KeyUps != 6)
                        throw new IOException($"Unbalanced or lost window input: {target.Downs}/{target.Ups}/{target.KeyDowns}/{target.KeyUps}");
                if (worker is not null) await worker.End(CancellationToken.None);
                ordinary?.ReleaseAll();
                File.WriteAllText(output, JsonSerializer.Serialize(new { status = "PASS", mode, switches,
                    windows = new[] { first, second }.Select(w => new { w.Downs, w.Ups, w.KeyDowns, w.KeyUps }),
                    token = WindowsInputPrivilege.ReadProcessToken(), os = Environment.OSVersion.ToString(),
                    systemService = mode == "service", elevatedTarget = "NOT_TESTED", uac = "NOT_TESTED" }));
                Console.WriteLine($"PASS {switches} real {mode} foreground switches with balanced mouse/F8 input to two ordinary probe windows");
            }
            catch (Exception error)
            {
                File.WriteAllText(output, JsonSerializer.Serialize(new { status = "FAIL", mode, switches, error = error.ToString() }));
                Console.Error.WriteLine(error);
                Environment.ExitCode = 1;
            }
            finally
            {
                ordinary?.Dispose();
                if (worker is not null) await worker.DisposeAsync();
                SetCursorPos(cursor.X, cursor.Y);
                first.Close(); second.Close(); app.Shutdown();
            }
        };
        app.Run(first);
    }
}
