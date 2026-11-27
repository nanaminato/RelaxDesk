using RelaxDesk.Windows;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using RelaxDesk.Core;
using RelaxDesk.Core.Localization;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    internal static void VerifyPasswordLogin(Application app, string host, string output)
    {
        Directory.CreateDirectory(output);
        var store = new DeviceTrustStore(Path.Combine(output, $"{Guid.NewGuid():N}.json"));
        const string userName = "User06";
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var window = new HostWindow(host, store) { loginVerifierForTests = PasswordVerifier.Create(userName, secret), ShowActivated = false };
        window.Loaded += async (_, _) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var ct = timeout.Token;
            using var known = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var unknown = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();

            async Task WaitFor(Func<bool> ready)
            {
                while (!ready()) await Task.Delay(20, ct);
            }
            static void Check(bool valid, string message)
            {
                if (!valid) throw new IOException(message);
            }
            string pin = "";
            int confirmations = 0;
            Task<bool> Confirm(string fingerprint, CancellationToken cancellation)
            {
                confirmations++;
                return Task.FromResult(fingerprint == window.loginFingerprint.Text);
            }
            static SessionClient Client(ECDsa key) => new() { OpenDeviceKey = () => ECDsa.Create(key.ExportParameters(true)) };
            async Task Expect<T>(Task action) where T : Exception
            {
                try { await action; }
                catch (T) { return; }
                throw new IOException($"Expected {typeof(T).Name}");
            }
            Task Connect(SessionClient client, string password, bool reconnect = false) =>
                client.ConnectWithPassword("127.0.0.1", port, pin, userName, password, Confirm, ct, reconnect);
            async Task Listening()
            {
                await WaitFor(() => window.Status == Strings.Get("Ui.HostWindow.Text408"));
                Check(!window.RequestVisible, "Approval UI remained visible");
            }
            try
            {
                Check(!PasswordVerifier.ValidPasswordLength(7) && PasswordVerifier.ValidPasswordLength(8),
                    "Password length boundary is incorrect");
                try
                {
                    _ = PasswordVerifier.Create(userName[..5], secret);
                    throw new IOException("A five-character username was accepted for setup");
                }
                catch (ArgumentException) { }
                var existing = window.loginVerifierForTests! with { UserName = "user" };
                byte[] supplied = PasswordVerifier.EncodePassword(secret);
                try { Check(existing.Verify("user", supplied), "An existing account stopped verifying"); }
                finally { CryptographicOperations.ZeroMemory(supplied); }
                await window.StartSharing("127.0.0.1", port, "test-hardware");
                await Listening();
                using (var bad = Client(known))
                    await Expect<PasswordLoginRejectedException>(Connect(bad, secret + "wrong"));
                await Listening();
                using (var legacy = Client(known))
                    await Expect<System.Security.Authentication.AuthenticationException>(legacy.ConnectByApproval("127.0.0.1", port, ct));
                await Listening();
                using (var first = Client(known))
                {
                    Task login = Connect(first, secret);
                    await WaitFor(() => window.RequestVisible || login.IsCompleted);
                    Check(window.RequestVisible && !login.IsCompleted, "First valid device did not wait for approval");
                    await window.Decide(true);
                    await login;
                    Check(SessionCapabilities.All.All(first.Features.Contains), "Approval did not grant every capability");
                    pin = first.VerifiedFingerprint;
                }
                await Listening();
                Check(confirmations == 2 && (await store.Load()).SequenceEqual([DeviceIdentity.Fingerprint(known)]),
                    "Approved device identity was not persisted or certificate was not confirmed");
                using (var retry = Client(known)) await Connect(retry, secret, reconnect: true);
                await Listening();
                using (var returning = Client(known)) { await Connect(returning, secret); Check(SessionCapabilities.All.All(returning.Features.Contains), "Returning device lost capabilities"); }
                await Listening();
                using (var wrongDevice = Client(unknown))
                    await Expect<HostConsentRejectedException>(Connect(wrongDevice, secret, reconnect: true));
                await Listening();
                await window.RevokeDevice(DeviceIdentity.Fingerprint(known));
                using (var revoked = Client(known))
                    await Expect<HostConsentRejectedException>(Connect(revoked, secret, reconnect: true));
                await Listening();
                using (var again = Client(known))
                {
                    Task login = Connect(again, secret);
                    await WaitFor(() => window.RequestVisible || login.IsCompleted);
                    Check(window.RequestVisible && !login.IsCompleted, "Revoked device regained trust without approval");
                    await window.Decide(false);
                    await Expect<HostConsentRejectedException>(login);
                }
                Console.WriteLine("PASS password TLS, legacy rejection, first approval, unattended return, pinned reconnect, device mismatch, and revocation");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { await window.StopSharing(); window.Close(); }
        };
        app.Run(window);
    }
}
