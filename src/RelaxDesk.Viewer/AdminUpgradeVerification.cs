using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifyAdminUpgrade(Application app, string hostApplication, bool activate = false, bool workerLoss = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        string path = Path.GetFullPath(hostApplication);
        bool managed = !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managed ? "dotnet" : path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (managed) start.ArgumentList.Add(path);
        string backend = workerLoss ? "test-admin-worker-loss" : activate ? "test-admin-lifecycle" : "test-displays";
        foreach (var arg in new[] { "--shell", "127.0.0.1", port.ToString(), backend }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Host");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        int released = 0;

        async Task<JsonElement> Event(string kind)
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("test_admin_input_released", StringComparison.Ordinal)) released++;
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var data = JsonDocument.Parse(line[9..]);
                if (data.RootElement.GetProperty("type").GetString() == kind) return data.RootElement.Clone();
            }
            throw new IOException($"Host did not emit {kind}");
        }

        async Task Send(string command)
        {
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync(ct);
        }

        async Task WaitForLog(string prefix)
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return;
            throw new IOException($"Host did not record {prefix}");
        }

        async Task Until(Func<bool> ready)
        {
            while (!ready()) await Task.Delay(20, ct);
        }

        var viewer = new ViewerWindow { ShowActivated = false };
        viewer.host.Text = "127.0.0.1";
        viewer.port.Text = port.ToString();
        viewer.networkMode.SelectedIndex = 1;
        viewer.gpuRendering.IsChecked = false;
        viewer.Loaded += async (_, _) =>
        {
            Task? connection = null;
            try
            {
                await Event("listening");
                connection = viewer.ConnectVideo();
                var initial = await Event("approval-request");
                if (initial.GetProperty("mode").GetString() != "Work") throw new IOException("Default Viewer did not request Work");
                string? id = initial.GetProperty("sessionId").GetString();
                await Send($"APPROVE {id}");
                await Until(() => viewer.smokeFrames > 0);
                var menu = viewer.sessionToolbar?.AdminModeAction ?? throw new IOException("Administrator action missing");
                if (menu.Visibility != Visibility.Visible || !viewer.inputAuthorized) throw new IOException("Work controls are incorrect");
                if (activate)
                {
                    string? prior = null;
                    for (int attempt = 0; attempt < (workerLoss ? 1 : 2); attempt++)
                    {
                        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        var request = await Event("admin-approval-request");
                        string? requestId = request.GetProperty("requestId").GetString();
                        if (requestId is null || requestId == prior || request.GetProperty("sessionId").GetString() != id)
                            throw new IOException("Invalid fresh admin approval");
                        prior = requestId;
                        await Send($"ALLOW_ADMIN_INPUT {id} {requestId}");
                        await Event("admin-ready");
                        await Until(() => viewer.activeMode == SessionMode.Admin && viewer.inputAuthorized && viewer.displaySession?.Ready == true);
                        if (viewer.Toolbar?.EndpointText.StartsWith("Admin · ", StringComparison.Ordinal) != true)
                            throw new IOException("Toolbar did not show the granted Admin mode");
                        if (!viewer.outgoing.TryWrite(InputProtocol.Encode(new("move", X: 200, Y: 100), ++viewer.sequence, viewer.displaySession!.Generation)))
                            throw new IOException("Test input did not enter the Viewer control queue");
                        await WaitForLog("test_admin_input_routed");
                        if (workerLoss)
                        {
                            await Event("session-ended");
                            if (released != 1) throw new IOException("Lost test worker did not run release cleanup");
                            var fresh = await Event("approval-request");
                            string? newId = fresh.GetProperty("sessionId").GetString();
                            if (newId is null || newId == id || fresh.GetProperty("mode").GetString() != "Work")
                                throw new IOException("Worker loss did not require a fresh Work approval");
                            await Send($"APPROVE {newId}");
                            await Until(() => viewer.activeMode == SessionMode.Work && viewer.inputAuthorized && viewer.displaySession?.Ready == true);
                            break;
                        }
                        if (attempt == 0) menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        else await Send($"REVOKE_ADMIN_INPUT {id}");
                        await Event("admin-revoked");
                        if (released != attempt + 1) throw new IOException("Worker did not release before the downgrade notification");
                        await Until(() => viewer.activeMode == SessionMode.Work && viewer.inputAuthorized);
                        if (viewer.Toolbar?.EndpointText.StartsWith("Work · ", StringComparison.Ordinal) != true)
                            throw new IOException("Toolbar did not return to Work after revocation");
                        if ((menu.Visibility == Visibility.Visible) != (attempt == 0))
                            throw new IOException("Host revocation left the administrator request available");
                    }
                    int frames = viewer.smokeFrames;
                    await Until(() => viewer.smokeFrames > frames);
                    viewer.StopConnection();
                    await connection.WaitAsync(ct);
                    await Send("STOP");
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode != 0) throw new IOException("Host did not exit cleanly");
                    Console.WriteLine(workerLoss
                        ? "PASS WPF administrator worker pipe loss: original grant ended, Viewer required fresh approval and restored Work on a new TLS connection"
                        : "PASS WPF Viewer twice entered Admin and routed input to protected non-injecting pipe workers; both Viewer exit and Host revoke released before Work resumed on the same TLS connection");
                    app.Shutdown();
                    return;
                }
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (viewer.inputAuthorized || menu.Visibility == Visibility.Visible) throw new IOException("Viewer kept input enabled while approval was pending");
                var approval = await Event("admin-approval-request");
                if (approval.GetProperty("sessionId").GetString() != id) throw new IOException("Approval belongs to another session");
                await Send($"DENY_ADMIN_INPUT {id} {approval.GetProperty("requestId").GetString()}");
                await Until(() => viewer.inputAuthorized && viewer.activeMode == SessionMode.Work);
                if (menu.Visibility != Visibility.Visible) throw new IOException("Denied request did not restore the administrator action");
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var retry = await Event("admin-approval-request");
                string? retryId = retry.GetProperty("requestId").GetString();
                if (retryId == approval.GetProperty("requestId").GetString()) throw new IOException("Retry reused the old approval");
                await Send($"DENY_ADMIN_INPUT {id} {retryId}");
                await Until(() => viewer.inputAuthorized && viewer.activeMode == SessionMode.Work);
                int before = viewer.smokeFrames;
                await Until(() => viewer.smokeFrames > before);
                viewer.StopConnection();
                await connection.WaitAsync(ct);
                await Send("STOP");
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) throw new IOException("Host did not exit cleanly");
                Console.WriteLine("PASS WPF Viewer Work administrator request: action sends request, input pauses, denial restores Work and video on the same connection");
                app.Shutdown();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                viewer.StopConnection();
                if (connection is not null) try { await connection.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                app.Shutdown(1);
            }
        };
        try { app.Run(viewer); }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
