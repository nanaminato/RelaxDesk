using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

// Opt-in input target for physical acceptance on the Host console.
internal sealed class DesktopInputProbe : Window
{
    internal const string MouseReadySuffix = " · 指针已就位";
    readonly WindowsInputToken token;
    int keyDowns, keyUps, buttonDowns, buttonUps;

    internal DesktopInputProbeResult Result => new(token.Elevated, token.IntegrityRid, keyDowns, keyUps, buttonDowns, buttonUps);
    internal Button Target { get; }

    public DesktopInputProbe(WindowsInputToken token, bool finishOnKey = false, bool finishOnMouse = false)
    {
        this.token = token;
        Title = $"RelaxDesk 输入验收 · {(token.Elevated ? "管理员" : "普通")} · 0x{token.IntegrityRid:X4}";
        Width = 470;
        Height = 220;
        ShowActivated = finishOnKey || finishOnMouse;
        Topmost = ShowActivated;

        var counts = new TextBlock { Margin = new Thickness(0, 12, 0, 0) };
        void UpdateCounts() => counts.Text = $"F8 按下／释放 {keyDowns}／{keyUps} · 鼠标左键 {buttonDowns}／{buttonUps}";

        Target = new Button { Content = "在这里测试鼠标左键", Height = 48 };
        Target.PreviewMouseLeftButtonDown += (_, _) => { buttonDowns++; UpdateCounts(); };
        Target.PreviewMouseLeftButtonUp += (_, _) =>
        {
            buttonUps++;
            UpdateCounts();
            if (finishOnMouse && buttonDowns == 1 && buttonUps == 1) Dispatcher.BeginInvoke(Close);
        };
        if (finishOnMouse)
        {
            string idleTitle = Title;
            Target.MouseEnter += (_, _) => Title = idleTitle + MouseReadySuffix;
            Target.MouseLeave += (_, _) => Title = idleTitle;
        }
        PreviewKeyDown += (_, input) =>
        {
            if (finishOnKey || input.Key != Key.F8 || input.IsRepeat) return;
            keyDowns++;
            UpdateCounts();
            input.Handled = true;
        };
        PreviewKeyUp += (_, input) =>
        {
            if (finishOnKey || input.Key != Key.F8) return;
            keyUps++;
            UpdateCounts();
            input.Handled = true;
        };

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = finishOnMouse ? "请将指针移到测试按钮上，不要点击。" : "将焦点放在此窗口，再按 F8。", Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(Target);
        panel.Children.Add(counts);
        Content = panel;
        UpdateCounts();
        if (finishOnKey)
        {
            Activated += (_, _) => Keyboard.Focus(Target);
            SourceInitialized += (_, _) =>
            {
                var source = (HwndSource)PresentationSource.FromVisual(this);
                source.AddHook(ObserveNativeKey);
            };
        }
        if (finishOnKey || finishOnMouse)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            timer.Tick += (_, _) => Close();
            Closed += (_, _) => timer.Stop();
            timer.Start();
        }

        nint ObserveNativeKey(nint window, int message, nint key, nint flags, ref bool handled)
        {
            if (key != 0x77) return 0;
            if (message == 0x100 && ((long)flags & (1L << 30)) == 0) keyDowns++;
            else if (message == 0x101) keyUps++;
            else return 0;
            UpdateCounts();
            handled = true;
            if (keyDowns == 1 && keyUps == 1) Dispatcher.BeginInvoke(Close);
            return 0;
        }
    }
}

internal sealed record DesktopInputProbeResult(bool Elevated, int IntegrityRid, int KeyDowns, int KeyUps, int ButtonDowns, int ButtonUps);
