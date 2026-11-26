using System.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostPanelWindow : Window
{
    internal HostPanelWindow(Window owner, string title, UIElement body)
    {
        InitializeComponent();
        if (owner.WindowState == WindowState.Minimized)
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        else
            Owner = owner;
        Title = title;
        Background = owner.Background;
        FontSize = owner.FontSize;
        FontFamily = owner.FontFamily;
        RelaxDesk.Ui.ModernTheme.Apply(this);
        PanelScroll.Content = body;
        Closed += (_, _) => PanelScroll.Content = null;
    }
}
