using System.Windows.Controls;
using System.Windows.Data;

namespace RelaxDesk.HostShell;

internal sealed partial class ServiceSettingsView : UserControl
{
    internal ServiceSettingsView(TextBlock notice)
    {
        InitializeComponent();
        Progress.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = notice });
    }
}
