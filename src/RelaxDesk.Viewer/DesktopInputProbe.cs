using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

// Opt-in input target for physical acceptance on the Host console.
internal sealed class DesktopInputProbe : Window
{
    readonly WindowsInputToken token;
    int keyDowns, keyUps, buttonDowns, buttonUps;

    internal DesktopInputProbeResult Result => new(token.Elevated, token.IntegrityRid, keyDowns, keyUps, buttonDowns, buttonUps);
    internal Button Target { get; }

    public DesktopInputProbe(WindowsInputToken token)
    {
        this.token = token;
        Title = $"RelaxDesk 输入验收 · {(token.Elevated ? "管理员" : "普通")} · 0x{token.IntegrityRid:X4}";
        Width = 470;
        Height = 220;
        ShowActivated = false;

        var counts = new TextBlock { Margin = new Thickness(0, 12, 0, 0) };
        void UpdateCounts() => counts.Text = $"F8 按下／释放 {keyDowns}／{keyUps} · 鼠标左键 {buttonDowns}／{buttonUps}";

        Target = new Button { Content = "在这里测试鼠标左键", Height = 48 };
        Target.PreviewMouseLeftButtonDown += (_, _) => { buttonDowns++; UpdateCounts(); };
        Target.PreviewMouseLeftButtonUp += (_, _) => { buttonUps++; UpdateCounts(); };
        PreviewKeyDown += (_, input) =>
        {
            if (input.Key != Key.F8 || input.IsRepeat) return;
            keyDowns++;
            UpdateCounts();
            input.Handled = true;
        };
        PreviewKeyUp += (_, input) =>
        {
            if (input.Key != Key.F8) return;
            keyUps++;
            UpdateCounts();
            input.Handled = true;
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "将焦点放在此窗口，再按 F8。", Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(Target);
        panel.Children.Add(counts);
        Content = panel;
        UpdateCounts();
    }
}

internal sealed record DesktopInputProbeResult(bool Elevated, int IntegrityRid, int KeyDowns, int KeyUps, int ButtonDowns, int ButtonUps);
