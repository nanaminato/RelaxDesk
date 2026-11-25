using System.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostPanelWindow : Window
{
    internal HostPanelWindow(Window owner, string title, UIElement body)
    {
        InitializeComponent();
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
