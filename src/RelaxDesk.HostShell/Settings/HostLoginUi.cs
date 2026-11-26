using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly HostLoginSettings loginSettings = new();
    TextBox loginFingerprint => loginSettings.Fingerprint;
    TextBlock loginNotice => loginSettings.Notice;
    Button saveLogin => loginSettings.Save;
    bool savingLogin, passwordLoginRequest;
    PasswordVerifier? loginVerifierForTests=null;

    void ConfigureLoginSettings()
    {
        var name = loginSettings.UserName;
        var password = loginSettings.Password;
        Loaded += (_, _) =>
        {
            try
            {
                name.Text = PasswordLoginSettings.Read()?.UserName ?? "";
                if(name.Text.Length==0) LocalizedText.Bind(loginOverview,"Text","Ui.HostLoginUi.Text186");
                else LocalizedText.Bind(loginOverview,"Text","Ui.HostLoginUi.Text383",name.Text);
            }
            catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
            {
                LocalizedText.Bind(loginOverview,"Text","Ui.HostLoginUi.Text293");
                loginNotice.Text = error.Message;
            }
        };
        saveLogin.Click += async (_, _) =>
        {
            if (Host.Running || RestartPending || savingLogin) return;
            savingLogin = true;
            UpdateServiceControls();
            string userName = name.Text.Trim(), secret = password.Password;
            password.Clear();
            try
            {
                var verifier = await Task.Run(() => PasswordVerifier.Create(userName, secret));
                SetTrusted(await deviceTrust.Clear());
                PasswordLoginSettings.Save(verifier);
                LocalizedText.Bind(loginOverview,"Text","Ui.HostLoginUi.Text383",verifier.UserName);
                LocalizedText.Bind(loginNotice,"Text","Ui.HostLoginUi.Text192");
            }
            catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
            { loginNotice.Text = error.Message; }
            finally { savingLogin = false; UpdateServiceControls(); }
        };
    }
}
