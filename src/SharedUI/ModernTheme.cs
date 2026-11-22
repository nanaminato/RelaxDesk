using System.Windows;
using System.Windows.Markup;
namespace RelaxDesk.Ui;

internal static class ModernTheme
{
    public static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <Style TargetType="Button">
  <Setter Property="Background" Value="#E8EEF6"/><Setter Property="Foreground" Value="#243248"/>
  <Setter Property="BorderBrush" Value="Transparent"/><Setter Property="BorderThickness" Value="1"/>
  <Setter Property="Padding" Value="18,10"/><Setter Property="Margin" Value="4"/><Setter Property="Cursor" Value="Hand"/>
  <Setter Property="FontWeight" Value="SemiBold"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
   <Border x:Name="Chrome" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}">
    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
   </Border>
   <ControlTemplate.Triggers>
    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Opacity" Value="0.87"/></Trigger>
    <Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Opacity" Value="0.66"/></Trigger>
    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#60A5FA"/></Trigger>
    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.4"/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key="PrimaryButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}"><Setter Property="Background" Value="#2563EB"/><Setter Property="Foreground" Value="White"/></Style>
 <Style x:Key="DangerButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}"><Setter Property="Background" Value="#FEE2E2"/><Setter Property="Foreground" Value="#B91C1C"/></Style>
 <Style TargetType="TextBox">
  <Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#17243A"/><Setter Property="BorderBrush" Value="#D9E2EE"/><Setter Property="BorderThickness" Value="1"/>
  <Setter Property="Padding" Value="12,10"/><Setter Property="Margin" Value="0,5,0,12"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox"><Border x:Name="Chrome" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"><ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/></Border><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#3B82F6"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType="PasswordBox">
  <Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#D9E2EE"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12,10"/><Setter Property="Margin" Value="0,5,0,12"/>
  <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="PasswordBox"><Border x:Name="Chrome" CornerRadius="8" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"><ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/></Border><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#3B82F6"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>
"""));
    }
}
