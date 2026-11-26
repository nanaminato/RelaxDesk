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
        string message=preview?"只读预览已完成，没有更改服务或部署文件。":result.PendingDelete?"服务正在等待删除。请保留文件，关闭服务管理窗口或其他服务句柄后，再次点击卸载。":result.Operation=="install"?
            $"服务安装／更新已完成。\n服务：{(result.Running?"运行中":"已停止")}\n启动设置：{(result.Startup==2?"开机自动启动":"手动启动")}\n服务不会自动共享桌面。":"服务卸载操作已完成。";
        if(result.RetainedFiles)message+="\n存在未知、修改过或占用中的文件，已保留；请检查，不要直接递归删除服务目录。";
        MessageBox(0,message,"RelaxDesk 服务维护结果",0x10000|(result.PendingDelete||result.RetainedFiles?0x30u:0x40u));
    }
    internal static void Failure(Exception error)
    {
        string message="服务维护未成功完成。\n"+Describe(error);
        if(message.Length>1800)message=message[..1800];
        MessageBox(0,message+"\n\n可能保留了恢复记录或已更改部分状态。请刷新服务状态并检查，不要直接删除服务目录。","RelaxDesk 服务维护失败",0x10010);
    }
    static string Describe(Exception error)
    {
        string details = $"{error.GetType().Name} · 0x{error.HResult:X8}\n{error.Message}";
        if (error.InnerException is AggregateException failures)
            return details + "\n\n安装错误\n" + Describe(failures.InnerExceptions[0])
                + "\n\n回滚错误\n" + Describe(failures.InnerExceptions[1]);
        return error.InnerException is { } cause ? details + "\n" + Describe(cause) : details;
    }
}
