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
        public Target(string label, double left)
        {
            Title = "RelaxDesk input switch probe " + label;
            Width = 340; Height = 220; Left = left; Top = 80;
            Content = Button;
            Button.PreviewMouseLeftButtonDown += (_, _) => Downs++;
            Button.PreviewMouseLeftButtonUp += (_, _) => Ups++;
            PreviewKeyDown += (_, e) => { if (e.Key == Key.F8 && !e.IsRepeat) { KeyDowns++; e.Handled = true; } };
            PreviewKeyUp += (_, e) => { if (e.Key == Key.F8) { KeyUps++; e.Handled = true; } };
        }
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
