using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

// Opt-in input target for physical acceptance on the Host console.
internal sealed class DesktopInputProbe : Window
{
    public DesktopInputProbe(WindowsInputToken token)
    {
        Title = $"RelaxDesk 输入验收 · {(token.Elevated ? "管理员" : "普通")} · 0x{token.IntegrityRid:X4}";
        Width = 470;
        Height = 220;
        ShowActivated = false;

        int keys = 0, clicks = 0;
        var counts = new TextBlock { Margin = new Thickness(0, 12, 0, 0) };
        void UpdateCounts() => counts.Text = $"F8 按下 {keys} 次 · 鼠标左键按下 {clicks} 次";

        var target = new Button { Content = "在这里测试鼠标左键", Height = 48 };
        target.PreviewMouseLeftButtonDown += (_, _) => { clicks++; UpdateCounts(); };
        PreviewKeyDown += (_, input) =>
        {
            if (input.Key != Key.F8 || input.IsRepeat) return;
            keys++;
            UpdateCounts();
            input.Handled = true;
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "将焦点放在此窗口，再按 F8。", Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(target);
        panel.Children.Add(counts);
        Content = panel;
        UpdateCounts();

        Closed += (_, _) => Console.WriteLine(JsonSerializer.Serialize(new { token.Elevated, token.IntegrityRid, keys, clicks }));
    }
}
