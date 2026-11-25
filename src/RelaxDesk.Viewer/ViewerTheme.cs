using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace RelaxDesk.Viewer;

// Viewer-local styling: keep HostShell and shared dialogs independent.
internal static class ViewerTheme
{
    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <Style TargetType="Button"><Setter Property="Padding" Value="12,4"/><Setter Property="Margin" Value="3,0"/><Setter Property="MinHeight" Value="27"/></Style>
 <Style TargetType="TextBox"><Setter Property="Padding" Value="5,3"/><Setter Property="MinHeight" Value="27"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
 <Style TargetType="PasswordBox"><Setter Property="Padding" Value="5,3"/><Setter Property="MinHeight" Value="27"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
 <Style TargetType="ComboBox"><Setter Property="Padding" Value="5,3"/><Setter Property="MinHeight" Value="27"/></Style>
 <Style TargetType="CheckBox"><Setter Property="Margin" Value="0,5"/></Style>
 <Style TargetType="TabItem"><Setter Property="Padding" Value="14,5"/></Style>
 <Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}"/>
 <Style x:Key="DangerButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}"/>
</ResourceDictionary>
"""));
    }
}
