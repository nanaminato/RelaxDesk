using System.Windows.Controls;
using RelaxDesk.Core;

namespace RelaxDesk.HostShell;

internal sealed partial class HostLoginSettings : UserControl
{
    internal HostLoginSettings()
    {
        InitializeComponent();
        UserNameLabel.Text = $"连接端用户名（{PasswordVerifier.MinimumNewUserNameLength} 至 {PasswordVerifier.MaximumUserNameLength} 个字符）";
        PasswordLabel.Text = $"新密码（{PasswordVerifier.MinimumPasswordLength} 至 {PasswordVerifier.MaximumPasswordLength} 个字符）";
        Unloaded += (_, _) => Password.Clear();
    }
}
