using System.Windows;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifySavedCredentials(Application app)
    {
        var store = new ViewerCredentialStore("RelaxDesk.Test." + Guid.NewGuid().ToString("N"));
        const string endpoint = "127.0.0.2:48200", otherEndpoint = "127.0.0.2:48201";
        const string password = "Synthetic-credential-测试-123";
        var window = new ViewerWindow { ShowActivated = false };
        int checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException($"Saved credential check {checks + 1} failed"); checks++; }
        window.Loaded += (_, _) =>
        {
            try
            {
                Check(store.Read(endpoint) is null);
                store.Save(endpoint, "fixture-user", password);
                Check(store.Read(endpoint) == new ViewerCredential("fixture-user", password));
                Check(store.Read(otherEndpoint) is null);
                Check(!store.Read(endpoint)!.ToString().Contains(password));
                window.credentialStore = store;
                window.host.Text = "127.0.0.2";
                Check(window.loginName.Text == "fixture-user" && window.token.Password == password && window.rememberCredentials.IsChecked == true);
                window.loginName.Text = "different-user";
                Check(window.token.Password.Length == 0 && window.rememberCredentials.IsChecked == false);
                Check(store.Read(endpoint)?.UserName == "fixture-user");
                window.loginName.Text = "fixture-user";
                Check(window.token.Password == password);
                window.rememberCredentials.IsChecked = false;
                Check(store.Read(endpoint) is null);
                var target = new ConnectionTarget("127.0.0.2", 48200, false, "", password, [], UserName: "fixture-user");
                window.SaveConnectedCredential(target, remember: false);
                Check(store.Read(endpoint) is null);
                Check(window.preferences.LoginNames.Single().UserName == "fixture-user");
                window.SaveConnectedCredential(target, remember: true);
                Check(store.Read(endpoint)?.Password == password);
                window.port.Text = "48201";
                Check(window.token.Password.Length == 0 && window.loginName.Text.Length == 0 && window.rememberCredentials.IsChecked == false);
                window.port.Text = "48200";
                Check(window.token.Password == password && window.loginName.Text == "fixture-user");
                window.SetHistory([new RelaxDesk.Core.ConnectionEndpoint("127.0.0.2", 48200)]);
                window.recentConnections.SelectedIndex = 0;
                Check(window.token.Password == password && window.rememberCredentials.IsChecked == true);
                store.Save(endpoint, "replacement-user", "Replacement-password-123");
                Check(store.Read(endpoint)?.UserName == "replacement-user");
                store.Delete(endpoint); store.Delete(endpoint);
                Check(store.Read(endpoint) is null);
                string fixture = System.IO.Path.Combine(Environment.CurrentDirectory, "build", "username-fixtures", Guid.NewGuid().ToString("N"), "viewer.json");
                window.preferencesStore = new ViewerPreferencesStore(fixture);
                window.SaveConnectedCredential(target with { UserName = "上次登录用户" }, remember: false);
                window.preferencesOperation.GetAwaiter().GetResult();
                Check(store.Read(endpoint) is null);
                var reloaded = window.preferencesStore.Load();
                Check(reloaded.LoginNames is [{ Endpoint:endpoint, UserName:"上次登录用户" }]);
                Check(!System.IO.File.ReadAllText(fixture).Contains(password));
                var next = new ViewerWindow { preferences = reloaded, credentialStore = store };
                try
                {
                    next.host.Text = "127.0.0.2";
                    Check(next.loginName.Text == "上次登录用户" && next.token.Password.Length == 0 && next.rememberCredentials.IsChecked == false);
                    next.port.Text = "48201";
                    Check(next.loginName.Text.Length == 0 && next.token.Password.Length == 0);
                    next.port.Text = "48200";
                    Check(next.loginName.Text == "上次登录用户" && next.token.Password.Length == 0);
                    next.loginName.Text = "未登录的新用户名";
                    Check(next.preferences.LoginNames.Single().UserName == "上次登录用户");
                }
                finally { next.Close(); }
                Console.WriteLine($"PASS {checks} Windows Credential Manager and WPF credential checks: Unicode roundtrip, endpoint isolation, username mismatch clears password, opt-in save, unchecked deletion, replacement and idempotent deletion; isolated synthetic credentials, no real login");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                store.Delete(endpoint); store.Delete(otherEndpoint);
                window.token.Clear(); window.Close();
            }
        };
        app.Run(window);
    }
}
