using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    ComboBox displayChoice => displaySettings.Choice;
    Button refreshDisplays => displaySettings.Refresh;
    TextBlock displayHint => displaySettings.Hint;
    string? SelectedDisplay=>(displayChoice.SelectedItem as ComboBoxItem)?.Tag as string;
    bool enumeratingDisplays;
    internal bool DisplayConfigurationEnabled=>displayChoice.IsEnabled;
    internal int DisplayChoiceCount=>displayChoice.Items.Count;
    internal bool DisplayEnumerationFinished=>!enumeratingDisplays;
    void ConfigureDisplaySettings()
    {
        displayChoice.SelectionChanged += (_, _) =>
        {
            if (displayChoice.SelectedItem is ComboBoxItem { Content: string label }) displayOverview.Text = label;
        };
        refreshDisplays.Click+=async(_,_)=>await RefreshDisplays();Loaded+=async(_,_)=>await RefreshDisplays();
    }
    async Task RefreshDisplays()
    {
        if(Host.Running||RestartPending||enumeratingDisplays)return;enumeratingDisplays=true;refreshDisplays.IsEnabled=false;
        try
        {
            string? selected=SelectedDisplay;var displays=await Task.Run(()=>WindowsDisplays.Enumerate());if(Host.Running||RestartPending)return;
            displayChoice.Items.Clear();displayChoice.Items.Add(new ComboBoxItem{Content="主显示器（自动选择）",Tag=null});
            foreach(var display in displays)displayChoice.Items.Add(new ComboBoxItem{Content=display.ToString(),Tag=display.Id,ToolTip=$"坐标 {display.Bounds.X},{display.Bounds.Y} · 旋转 {display.Rotation}°"});
            try{var union=DisplayTopology.Select(displays,"all");displayChoice.Items.Add(new ComboBoxItem{Content=$"全部屏幕 · {union.Width}×{union.Height}（兼容捕获）",Tag="all"});}catch(System.IO.IOException){}
            displayChoice.SelectedItem=displayChoice.Items.Cast<ComboBoxItem>().FirstOrDefault(item=>Equals(item.Tag,selected))??displayChoice.Items[0];
            displayHint.Text="开始共享后固定此范围；更换屏幕请停止共享并重新选择。新增屏幕不会自动共享。全部屏幕和旋转屏使用 GDI 兼容路径，HDR 精度受系统转换限制。";
        }
        catch(Exception){displayHint.Text="无法枚举屏幕。可刷新后重试；开始共享时仍会验证实际显示器。";}
        finally{enumeratingDisplays=false;refreshDisplays.IsEnabled=!Host.Running&&!RestartPending;}
    }
    void SetDisplayConfigurationEnabled(bool enabled){displayChoice.IsEnabled=enabled;refreshDisplays.IsEnabled=enabled&&!enumeratingDisplays;}
}
