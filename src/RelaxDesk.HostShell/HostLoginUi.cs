using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly TextBox loginFingerprint = new() { TextWrapping = TextWrapping.Wrap, IsReadOnly = true };
    readonly TextBlock loginNotice = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock loginOverview = Text("正在读取登录设置…", foreground: System.Windows.Media.Brushes.SlateGray);
    readonly Button saveLogin = new() { Content = "保存登录设置" };
    bool savingLogin, passwordLoginRequest;
    PasswordVerifier? loginVerifierForTests;

    StackPanel BuildLoginSettings()
    {
        var name = new TextBox { MaxLength = PasswordVerifier.MaximumUserNameLength };
        var password = new PasswordBox { MaxLength = PasswordVerifier.MaximumPasswordLength };
        var panel = new StackPanel();
        panel.Children.Add(Text($"连接端用户名（{PasswordVerifier.MinimumNewUserNameLength} 至 {PasswordVerifier.MaximumUserNameLength} 个字符）", 14, true));
        panel.Children.Add(name);
        panel.Children.Add(Text($"新密码（{PasswordVerifier.MinimumPasswordLength} 至 {PasswordVerifier.MaximumPasswordLength} 个字符）", 14, true));
        panel.Children.Add(password);
        panel.Children.Add(saveLogin);
        panel.Children.Add(loginNotice);
        panel.Children.Add(Text("受控端 TLS 指纹", 14, true));
        panel.Children.Add(loginFingerprint);
        panel.Unloaded += (_, _) => password.Clear();
        loginFingerprint.Text = "开始共享后显示。首次连接时请核对两端指纹。";
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
        return panel;
    }
}
