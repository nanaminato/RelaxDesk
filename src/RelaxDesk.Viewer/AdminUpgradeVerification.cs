using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifyAdminUpgrade(Application app, string hostApplication, bool activate = false, bool workerLoss = false, bool realWorker = false, bool cancelElevation = false, bool physicalLock = false)
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
        string backend = realWorker ? "dxgi" : workerLoss ? "test-admin-worker-loss" : activate ? "test-admin-lifecycle" : "test-displays";
        foreach (var arg in new[] { "--shell", "127.0.0.1", port.ToString(), backend }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Host");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(physicalLock ? 120 : realWorker ? 90 : 25));
        var ct = timeout.Token;
        int released = 0;
        bool observedUacCancellation = false;
        bool observedNativeCleanup = false;

        async Task<JsonElement> Event(string kind, string? unexpected = null, string? unexpectedLog = null)
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("test_admin_input_released", StringComparison.Ordinal)) released++;
                if (cancelElevation && line.StartsWith("admin_uac_cancelled native_error=1223 ", StringComparison.Ordinal))
                    observedUacCancellation = true;
                if (physicalLock && line.StartsWith("session_cleanup native_live_resources=0 ", StringComparison.Ordinal))
                    observedNativeCleanup = true;
                if (unexpectedLog is not null && line.StartsWith(unexpectedLog, StringComparison.Ordinal))
                    throw new IOException($"Host emitted unexpected input callback {unexpectedLog}");
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var data = JsonDocument.Parse(line[9..]);
                string? observed = data.RootElement.GetProperty("type").GetString();
                if (observed == kind) return data.RootElement.Clone();
                if (unexpected is not null && observed == unexpected) throw new IOException($"Host emitted unexpected {observed}");
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
                    for (int attempt = 0; attempt < (workerLoss || realWorker ? 1 : 2); attempt++)
                    {
                        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        var request = await Event("admin-approval-request");
                        string? requestId = request.GetProperty("requestId").GetString();
                        if (requestId is null || requestId == prior || request.GetProperty("sessionId").GetString() != id)
                            throw new IOException("Invalid fresh admin approval");
                        prior = requestId;
                        long pendingSequence = viewer.sequence;
                        viewer.SendPointerMove(200, 100);
                        viewer.SendRich(new("key", VirtualKey:65));
                        if (viewer.inputAuthorized || viewer.sequence != pendingSequence || viewer.richHeld.Count != 0)
                            throw new IOException("Input entered the Viewer queue while local administrator approval was pending");
                        await Send($"ALLOW_ADMIN_INPUT {id} {requestId}");
                        if (realWorker)
                        {
                            await Event("admin-starting", unexpected: "admin-denied");
                            bool IsPaused() => viewer.Toolbar?.EndpointText.StartsWith("Work · Host 正在准备管理员输入 · ", StringComparison.Ordinal) == true;
                            await Until(IsPaused);
                            // A frame already queued before the pause can still be rendered.
                            await Task.Delay(250, ct);
                            int pausedFrames = Volatile.Read(ref viewer.smokeFrames);
                            long pausedSequence = viewer.sequence;
                            viewer.SendPointerMove(200, 100);
                            viewer.SendRich(new("key", VirtualKey: 0x77));
                            if (viewer.inputAuthorized || viewer.sequence != pausedSequence || viewer.richHeld.Count != 0)
                                throw new IOException("Input entered the queue while the administrator desktop was paused");
                            await Task.Delay(500, ct);
                            if (!IsPaused() || connection.IsCompleted || Volatile.Read(ref viewer.smokeFrames) != pausedFrames)
                                throw new IOException("Expected a sustained pause with no new video. Leave Windows UAC open for at least two seconds.");
                            Console.WriteLine("PASS administrator desktop pause suppressed new video and Viewer input while TLS remained open");
                        }
                        if (cancelElevation)
                        {
                            await Event("admin-denied", unexpected: "admin-ready");
                            if (!observedUacCancellation)
                                throw new IOException("Host denied the request without observing Windows UAC cancellation");
                            await Until(() => viewer.activeMode == SessionMode.Work && viewer.inputAuthorized && viewer.displaySession?.Ready == true);
                            if (connection.IsCompleted || viewer.Toolbar?.EndpointText.StartsWith("Work · ", StringComparison.Ordinal) != true)
                                throw new IOException("UAC cancellation did not restore Work on the existing connection");
                            break;
                        }
                        await Event("admin-ready", unexpected: "admin-denied");
                        await Until(() => viewer.activeMode == SessionMode.Admin && viewer.inputAuthorized && viewer.displaySession?.Ready == true);
                        if (viewer.Toolbar?.EndpointText.StartsWith("Admin · ", StringComparison.Ordinal) != true)
                            throw new IOException("Toolbar did not show the granted Admin mode");
                        if (physicalLock)
                        {
                            Console.WriteLine("READY Admin is active. Lock this Windows console, wait at least two seconds, then unlock locally.");
                            await connection.WaitAsync(ct);
                            await Event("session-ended", unexpected: "approval-request");
                            if (viewer.inputAuthorized || !observedNativeCleanup || !viewer.status.Text.StartsWith("Host 已锁屏", StringComparison.Ordinal))
                                throw new IOException("Physical lock did not close Admin with the WindowsLocked notice and clean resources");
                            Console.WriteLine("PASS physical Windows lock ended Admin and Viewer did not retry; Host native resources zero");
                            while (WindowsSessionAccess.Read().Denial is not null) await Task.Delay(250, ct);
                            int previousFrames = Volatile.Read(ref viewer.smokeFrames);
                            connection = viewer.ConnectVideo();
                            var fresh = await Event("approval-request", unexpected: "admin-ready");
                            string? freshId = fresh.GetProperty("sessionId").GetString();
                            if (freshId is null || freshId == id || fresh.GetProperty("mode").GetString() != "Work" ||
                                viewer.inputAuthorized || Volatile.Read(ref viewer.smokeFrames) != previousFrames)
                                throw new IOException("Unlock did not require a fresh Work approval before video and input");
                            await Send($"APPROVE {freshId}");
                            await Until(() => viewer.activeMode == SessionMode.Work && viewer.inputAuthorized && Volatile.Read(ref viewer.smokeFrames) > previousFrames);
                            viewer.StopConnection();
                            await connection.WaitAsync(ct);
                            await Event("session-ended");
                            await Send("STOP");
                            await process.WaitForExitAsync(ct);
                            if (process.ExitCode != 0) throw new IOException("Host did not exit cleanly");
                            Console.WriteLine("PASS physical unlock required fresh approval before starting a new Work grant and video");
                            app.Shutdown();
                            return;
                        }
                        if (realWorker)
                        {
                            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                            await Event("admin-revoked");
                            await Until(() => viewer.activeMode == SessionMode.Work && viewer.inputAuthorized);
                            if (connection.IsCompleted) throw new IOException("Downgrade ended the existing connection");
                            break;
                        }
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
                            var testKey = new RemoteInput("key", VirtualKey: 65);
                            if (!viewer.outgoing.TryWrite(InputProtocol.Encode(testKey, ++viewer.sequence, viewer.displaySession!.Generation)))
                                throw new IOException("Initial Work input was not queued");
                            await WaitForLog("test_display_input");
                            var freshMenu = viewer.sessionToolbar?.AdminModeAction ?? throw new IOException("Fresh Work administrator action missing");
                            freshMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                            var pending = await Event("admin-approval-request");
                            string? pendingId = pending.GetProperty("requestId").GetString();
                            if (pendingId is null || pending.GetProperty("sessionId").GetString() != newId)
                                throw new IOException("Pending administrator approval does not belong to the fresh Work session");
                            await Send($"REVOKE_ADMIN_INPUT {newId}");
                            var closed = await Event("admin-approval-closed");
                            if (closed.GetProperty("requestId").GetString() != pendingId)
                                throw new IOException("Host closed the wrong administrator approval");
                            await Send($"ALLOW_ADMIN_INPUT {newId} {pendingId}");
                            await Event("admin-denied");
                            await Until(() => viewer.activeMode == SessionMode.Work && viewer.inputAuthorized);
                            int framesAfterRevocation = viewer.smokeFrames;
                            await Until(() => viewer.smokeFrames > framesAfterRevocation);
                            if (freshMenu.Visibility == Visibility.Visible || connection.IsCompleted)
                                throw new IOException("Revocation did not preserve Work and disable administrator requests");
                            Console.WriteLine("PASS late local administrator approval ignored after revocation; Work continued on the same TLS connection");

                            // Simulate a request queued before the Viewer receives the revocation.
                            viewer.adminUpgradeAvailable = true;
                            viewer.RequestAdminUpgrade();
                            await Event("admin-denied", unexpected: "admin-approval-closed");
                            await Until(() => viewer.activeMode == SessionMode.Work && viewer.inputAuthorized);
                            if (freshMenu.Visibility == Visibility.Visible || connection.IsCompleted)
                                throw new IOException("A request after revocation did not stay in Work");
                            Console.WriteLine("PASS request after administrator revocation denied without creating a local approval");
                            await Send($"REVOKE_WORK_INPUT {newId}");
                            await Until(() => !viewer.inputAuthorized);
                            int framesAfterWorkRevoke = viewer.smokeFrames;
                            await Until(() => viewer.smokeFrames > framesAfterWorkRevoke);
                            if (viewer.activeMode != SessionMode.Work || connection.IsCompleted || freshMenu.Visibility == Visibility.Visible)
                                throw new IOException("Work input revocation did not preserve view-only sharing");
                            if (!viewer.outgoing.TryWrite(InputProtocol.Encode(testKey with { Up = true }, ++viewer.sequence, viewer.displaySession!.Generation)))
                                throw new IOException("Late Work input was not queued");
                            viewer.adminUpgradeAvailable = true;
                            viewer.RequestAdminUpgrade();
                            await Until(() => Volatile.Read(ref viewer.adminUpgradePending) == 0);
                            if (viewer.inputAuthorized || connection.IsCompleted || freshMenu.Visibility == Visibility.Visible)
                                throw new IOException("An in-flight administrator request re-enabled revoked Work input");
                            await Until(() => viewer.Toolbar?.EndpointText.StartsWith("Work · 仅查看 · ", StringComparison.Ordinal) == true);
                            Console.WriteLine("PASS Work input revoked in place with continued video and no Admin request action");
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
                    if (workerLoss) await Event("session-ended", unexpectedLog: "test_display_input");
                    await Send("STOP");
                    await process.WaitForExitAsync(ct);
                    if (process.ExitCode != 0) throw new IOException("Host did not exit cleanly");
                    Console.WriteLine(cancelElevation
                        ? "PASS UAC cancellation restored Work and video on the same TLS connection"
                        : realWorker
                        ? "PASS local UAC started the protected administrator worker; Viewer downgrade restored Work and video on the same TLS connection"
                        : workerLoss
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
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Event("admin-approval-request");
                viewer.StopConnection();
                await connection.WaitAsync(ct);
                await Event("session-ended");
                Console.WriteLine("PASS disconnect with local administrator approval pending ended the Host session");
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
