using RelaxDesk.Core.Localization;
using System.Runtime.InteropServices;
using RelaxDesk.Session;

namespace RelaxDesk.Service;

// Only the explicitly requested interactive setup command can reach this UI.
// The SCM service entry point, broker and Host core have no UI dependency.
internal static class SetupResultDialog
{
    [DllImport("user32.dll",EntryPoint="MessageBoxW",CharSet=CharSet.Unicode)] static extern int MessageBox(nint owner,string text,string title,uint flags);
    internal static void Show(ServiceSetupResult result,bool preview)
    {
        string message=preview?Strings.Get("Ui.SetupResultDialog.Text152"):result.PendingDelete?Strings.Get("Ui.SetupResultDialog.Text318"):result.Operation=="install"?
            Strings.Format("Ui.SetupResultDialog.Text313",(result.Running?Strings.Get("Ui.ServiceSetupUi.Text471"):Strings.Get("Ui.ServiceSetupUi.Text197")),(result.Startup==2?Strings.Get("Ui.ServiceSetupUi.Text231"):Strings.Get("Ui.ServiceSetupUi.Text234"))):Strings.Get("Ui.SetupResultDialog.Text312");
        if(result.RetainedFiles)message+=Strings.Get("Ui.SetupResultDialog.Text006");
        MessageBox(0,message,Strings.Get("Ui.SetupResultDialog.Text047"),0x10000|(result.PendingDelete||result.RetainedFiles?0x30u:0x40u));
    }
    internal static void Failure(Exception error)
    {
        string message=Strings.Get("Ui.SetupResultDialog.Text322")+Describe(error);
        if(message.Length>1800)message=message[..1800];
        MessageBox(0,message+Strings.Get("Ui.SetupResultDialog.Text000"),Strings.Get("Ui.SetupResultDialog.Text046"),0x10010);
    }
    static string Describe(Exception error)
    {
        string details = $"{error.GetType().Name} · 0x{error.HResult:X8}\n{error.Message}";
        if (error.InnerException is AggregateException failures)
            return details + Strings.Get("Ui.SetupResultDialog.Text002") + Describe(failures.InnerExceptions[0])
                + Strings.Get("Ui.SetupResultDialog.Text001") + Describe(failures.InnerExceptions[1]);
        return error.InnerException is { } cause ? details + "\n" + Describe(cause) : details;
    }
}
