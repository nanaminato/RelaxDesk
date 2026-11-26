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
    PasswordVerifier? loginVerifierForTests;

    void ConfigureLoginSettings()
    {
        var name = loginSettings.UserName;
        var password = loginSettings.Password;
        Loaded += (_, _) =>
        {
            try
            {
                name.Text = PasswordLoginSettings.Read()?.UserName ?? "";
                loginOverview.Text = name.Text.Length == 0 ? "尚未设置登录信息" : $"用户名：{name.Text}";
            }
            catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
            {
                loginOverview.Text = "无法读取登录设置";
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
                loginOverview.Text = $"用户名：{verifier.UserName}";
                loginNotice.Text = "已保存。原设备授权已清除，设备需重新审批一次。";
            }
            catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
            { loginNotice.Text = error.Message; }
            finally { savingLogin = false; UpdateServiceControls(); }
        };
    }
}
