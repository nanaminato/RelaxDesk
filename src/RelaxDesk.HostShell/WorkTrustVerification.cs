using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using RelaxDesk.Core;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    internal static void VerifyWorkTrust(Application app, string host, string output)
    {
        string file = Path.Combine(Path.GetFullPath(output), $"work-trust-{Guid.NewGuid():N}.json");
        using var storeGate = new SemaphoreSlim(1);
        var window = new HostWindow(Path.GetFullPath(host), new WorkTrustStore(file, storeGate)) { ShowActivated = false };
        window.Loaded += async (_, _) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = timeout.Token;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string fingerprint = DeviceIdentity.Fingerprint(key);

            async Task WaitFor(Func<bool> ready)
            {
                while (!ready()) await Task.Delay(20, ct);
            }
            SessionClient Viewer(params string[] features) => new()
            {
                RequestedMode = SessionMode.Work,
                OpenDeviceKey = () => ECDsa.Create(key.ExportParameters(true)),
                RequestedFeatures = features
            };

            try
            {
                await WaitFor(() => window.trustLoaded);
                var reserve = new TcpListener(IPAddress.Loopback, 0);
                reserve.Start();
                int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
                reserve.Stop();
                await window.StartSharing("127.0.0.1", port, "test-clipboard");
                await WaitFor(() => window.Status == "等待连接");

                using (var first = Viewer(SessionCapabilities.ClipboardText))
                {
                    var connection = first.ConnectByApproval("127.0.0.1", port, ct);
                    await WaitFor(() => window.RequestVisible);
                    if (window.ClipboardAllowed || window.rememberWorkDevice.Visibility != Visibility.Visible)
                        throw new IOException("Work trust is not opt-in");
                    window.ClipboardAllowed = true;
                    window.rememberWorkDevice.IsChecked = true;
                    await window.Decide(true);
                    await connection;
                    var saved = await new WorkTrustStore(file).Load();
                    if (saved is not [{ Features: [SessionCapabilities.ClipboardText] } entry] ||
                        entry.Fingerprint != fingerprint)
                        throw new IOException("Confirmed device and exact permissions were not persisted");
                    await window.DisconnectSession();
                }
                await WaitFor(() => window.Status == "等待连接");

                async Task MustAsk(SessionClient viewer, string reason)
                {
                    var pending = viewer.ConnectByApproval("127.0.0.1", port, ct);
                    await WaitFor(() => window.RequestVisible || pending.IsCompleted);
                    if (pending.IsCompleted) throw new IOException($"Work consent was skipped: {reason}");
                    await window.Decide(false);
                    try { await pending; throw new IOException("Manual rejection did not stop Work"); }
                    catch (HostConsentRejectedException) { }
                    await WaitFor(() => window.Status == "等待连接");
                }

                using (var remembered = Viewer()) await MustAsk(remembered, "saved device identity");

                using (var expanded = Viewer(SessionCapabilities.ClipboardText, SessionCapabilities.ClipboardImage))
                {
                    var connection = expanded.ConnectByApproval("127.0.0.1", port, ct);
                    await WaitFor(() => window.RequestVisible);
                    if (window.ClipboardAllowed || connection.IsCompleted)
                        throw new IOException("A new clipboard format inherited approval");
                    await window.Decide(false);
                    try { await connection; throw new IOException("Deny did not stop Work device"); }
                    catch (HostConsentRejectedException) { }
                }
                await WaitFor(() => window.Status == "等待连接");

                var priorTrust = (await new WorkTrustStore(file).Load()).Single();
                using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await window.RevokeWorkDevice(fingerprint);
                    if (!window.trustNotice.Text.Contains("未能删除磁盘记录"))
                        throw new IOException("Storage removal failure was not reported");
                    using var afterFailedRevoke = Viewer(SessionCapabilities.ClipboardText);
                    var pending = afterFailedRevoke.ConnectByApproval("127.0.0.1", port, ct);
                    await WaitFor(() => window.RequestVisible);
                    if (window.ClipboardAllowed || pending.IsCompleted)
                        throw new IOException("Failed storage removal left saved Work permissions active");
                    await window.Decide(false);
                    try { await pending; throw new IOException("Work request after failed revoke was not denied"); }
                    catch (HostConsentRejectedException) { }
                }
                await window.SaveWorkTrust(priorTrust);
                await WaitFor(() => window.Status == "等待连接");
                using (var trustedViewer = Viewer(SessionCapabilities.ClipboardText))
                {
                    Task connection = trustedViewer.ConnectByApproval("127.0.0.1", port, ct);
                    await WaitFor(() => window.RequestVisible);
                    if (!window.ClipboardAllowed || connection.IsCompleted)
                        throw new IOException("Matching rights did not prefill, or consent was skipped");
                    await window.Decide(true);
                    await connection;
                    await storeGate.WaitAsync(ct);
                    Task revoking = window.RevokeWorkDevice(fingerprint);
                    try
                    {
                        var ended = await Wire.Read(trustedViewer.Control, ct);
                        if (!ConnectionRecovery.IsExplicitEnd(ended)) throw new IOException("Revocation did not end the live session");
                        await WaitFor(() => window.Status == "等待连接");
                        using var duringRevoke = Viewer(SessionCapabilities.ClipboardText);
                        var pending = duringRevoke.ConnectByApproval("127.0.0.1", port, ct);
                        await WaitFor(() => window.RequestVisible);
                        if (revoking.IsCompleted || window.ClipboardAllowed || pending.IsCompleted)
                            throw new IOException("A connection arriving during revocation inherited old rights");
                        await window.Decide(false);
                        try { await pending; throw new IOException("Concurrent Work request was not denied"); }
                        catch (HostConsentRejectedException) { }
                    }
                    finally { storeGate.Release(); await revoking; }
                    if ((await new WorkTrustStore(file).Load()).Length != 0)
                        throw new IOException("Revocation did not close the matching live session and remove the record");
                }
                await WaitFor(() => window.Status == "等待连接");

                using var revoked = Viewer(SessionCapabilities.ClipboardText);
                var reconnect = revoked.ConnectByApproval("127.0.0.1", port, ct);
                await WaitFor(() => window.RequestVisible);
                if (window.ClipboardAllowed || reconnect.IsCompleted)
                    throw new IOException("Revoked permission was retained");
                await window.Decide(false);
                try { await reconnect; throw new IOException("Deny did not stop a revoked Work device"); }
                catch (HostConsentRejectedException) { }
                Console.WriteLine("PASS WPF Work device trust: opt-in persistence, exact permission prefill, no capability expansion, live revocation and fresh consent over production TLS");
                Console.WriteLine("PASS WPF saved Work device requires fresh Host consent");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { await window.StopSharing(); window.Close(); }
        };
        app.Run(window);
    }
}
