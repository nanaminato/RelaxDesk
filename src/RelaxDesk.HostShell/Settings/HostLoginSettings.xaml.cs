using RelaxDesk.SharedUI;
using System.Windows.Controls;
using RelaxDesk.Core;

namespace RelaxDesk.HostShell;

internal sealed partial class HostLoginSettings : UserControl
{
    internal HostLoginSettings()
    {
        InitializeComponent();
        LocalizedText.Bind(UserNameLabel,"Text","Ui.HostLoginSettings.Text487",PasswordVerifier.MinimumNewUserNameLength,PasswordVerifier.MaximumUserNameLength);
        LocalizedText.Bind(PasswordLabel,"Text","Ui.HostLoginSettings.Text279",PasswordVerifier.MinimumPasswordLength,PasswordVerifier.MaximumPasswordLength);
        Unloaded += (_, _) => Password.Clear();
    }
}
