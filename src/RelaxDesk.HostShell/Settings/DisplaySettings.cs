using RelaxDesk.SharedUI;
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
            if (displayChoice.SelectedItem is ComboBoxItem selected)
                displayOverview.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Content") { Source = selected });
        };
        refreshDisplays.Click+=async(_,_)=>await RefreshDisplays();Loaded+=async(_,_)=>await RefreshDisplays();
    }
    async Task RefreshDisplays()
    {
        if(Host.Running||RestartPending||enumeratingDisplays)return;enumeratingDisplays=true;refreshDisplays.IsEnabled=false;
        try
        {
            string? selected=SelectedDisplay;var displays=await Task.Run(()=>WindowsDisplays.Enumerate());if(Host.Running||RestartPending)return;
            displayChoice.Items.Clear();displayChoice.Items.Add(LocalizedText.Bind(new ComboBoxItem{Tag=null},"Content","Ui.DisplaySettings.Text081"));
            foreach(var display in displays)displayChoice.Items.Add(LocalizedText.Bind(LocalizedText.Bind(new ComboBoxItem{Tag=display.Id},"Content",()=>display.ToString()),"ToolTip","Ui.DisplaySettings.Text161",display.Bounds.X,display.Bounds.Y,display.Rotation));
            try{var union=DisplayTopology.Select(displays,"all");displayChoice.Items.Add(LocalizedText.Bind(new ComboBoxItem{Tag="all"},"Content","Ui.DisplaySettings.Text118",union.Width,union.Height));}catch(System.IO.IOException){}
            displayChoice.SelectedItem=displayChoice.Items.Cast<ComboBoxItem>().FirstOrDefault(item=>Equals(item.Tag,selected))??displayChoice.Items[0];
            LocalizedText.Bind(displayHint,"Text","Ui.DisplaySettings.Text229");
        }
        catch(Exception){LocalizedText.Bind(displayHint,"Text","Ui.DisplaySettings.Text288");}
        finally{enumeratingDisplays=false;refreshDisplays.IsEnabled=!Host.Running&&!RestartPending;}
    }
    void SetDisplayConfigurationEnabled(bool enabled){displayChoice.IsEnabled=enabled;refreshDisplays.IsEnabled=enabled&&!enumeratingDisplays;}
}
