using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal sealed class FileClipboardWindow:Window
{
    readonly CancellationTokenSource lifetime;
    CancellationTokenSource? operation;
    readonly Func<CancellationToken,Task<ILocalFileClipboard>> clipboard;
    readonly Func<CancellationToken,Task<FileClipboardTransfer>> transfer;
    readonly TextBlock localNames=new(){TextWrapping=TextWrapping.Wrap},remoteNames=new(){TextWrapping=TextWrapping.Wrap},feedback=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0)};
    internal readonly Button ReadLocal=new(){Content="读取本机已复制的文件"},ConfirmUpload=new(){Content="确认上传并准备远端粘贴",IsEnabled=false},ChooseFolder=new(){Content="选择本机接收文件夹",IsEnabled=false},ConfirmDownload=new(){Content="确认下载并准备本机粘贴",IsEnabled=false},RetryRemote=new(){Content="再次准备远端粘贴（不重传）",IsEnabled=false},RetryLocal=new(){Content="再次准备本机粘贴（不重传）",IsEnabled=false};
    internal Task CurrentOperation{get;private set;}=Task.CompletedTask;
    LocalClipboardFiles? localSelection;ClipboardFiles? remoteSelection;string? destination;bool closing;
    internal bool LastOperationSucceeded{get;private set;}
    public FileClipboardWindow(Window owner,Func<CancellationToken,Task<ILocalFileClipboard>> clipboard,Func<CancellationToken,Task<FileClipboardTransfer>> transfer,Action openQueue,CancellationToken ct)
    {
        Owner=owner;this.clipboard=clipboard;this.transfer=transfer;lifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);Title="文件剪贴板";Width=640;Height=600;MinWidth=480;MinHeight=420;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        RelaxDesk.Ui.ModernTheme.Apply(this);var panel=new StackPanel{Margin=new(22)};
        panel.Children.Add(new TextBlock{Text="文件复制粘贴",FontSize=22,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text="先在资源管理器复制文件，再在此确认传输。只有全部完成后才准备文件剪贴板；请在对应电脑的资源管理器中自行粘贴。传输使用可见的 RelaxDesk-Clipboard-* 子文件夹，保留文件供粘贴，不自动删除。",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,14)});
        foreach(var item in new UIElement[]{ReadLocal,localNames,ConfirmUpload,RetryRemote,new Separator(),remoteNames,ChooseFolder,ConfirmDownload,RetryLocal})panel.Children.Add(item);
        var queue=new Button{Content="打开传输进度 / 暂停 / 取消"};queue.Click+=(_,_)=>openQueue();panel.Children.Add(queue);
        var cancel=new Button{Content="取消当前剪贴板传输"};cancel.Click+=(_,_)=>operation?.Cancel();panel.Children.Add(cancel);panel.Children.Add(feedback);Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        ReadLocal.Click+=(_,_)=>Start(async token=>
        {
            var source=await clipboard(token);localSelection=await source.ReadFiles(token);
            localNames.Text=localSelection is{Paths.Length:>0}?string.Join("\n",localSelection.Paths.Select(Path.GetFileName)):"请先在本机资源管理器复制文件或文件夹。";
            ConfirmUpload.IsEnabled=localSelection is{Paths.Length:>0};
        });
        ConfirmUpload.Click+=(_,_)=>{if(localSelection is{} selection)Start(async token=>{var flow=await transfer(token);await flow.Upload(selection,token);feedback.Text="已准备远端文件剪贴板，请在远端资源管理器粘贴。";});};
        ChooseFolder.Click+=(_,_)=>{var picker=new OpenFolderDialog{Title="选择接收文件夹（会创建可见的剪贴板子文件夹）"};if(picker.ShowDialog(this)==true)SelectDestination(picker.FolderName);};
        ConfirmDownload.Click+=(_,_)=>{if(remoteSelection is{} selected&&destination is{} folder)Start(async token=>{var flow=await transfer(token);await flow.Download(selected,folder,token);feedback.Text="已准备本机文件剪贴板，请在本机资源管理器粘贴。";});};
        RetryRemote.Click+=(_,_)=>Start(async token=>{await (await transfer(token)).PrepareRemote(token);feedback.Text="已再次准备远端粘贴，无需重传。";});
        RetryLocal.Click+=(_,_)=>Start(async token=>{await (await transfer(token)).PrepareLocal(token);feedback.Text="已再次准备本机粘贴，无需重传。";});
        Closing+=(_,args)=>{if(!closing){args.Cancel=true;Hide();}};
    }
    internal void SetRemote(ClipboardFiles? files)
    {remoteSelection=files?.Own();remoteNames.Text=files is null?"远端尚未复制授权目录内的文件。":"远端已复制：\n"+string.Join("\n",files.Paths);ChooseFolder.IsEnabled=files is not null;ConfirmDownload.IsEnabled=files is not null&&destination is not null;}
    internal void SelectDestination(string folder){destination=folder;feedback.Text="接收到："+folder+"\n确认后创建新的剪贴板子文件夹。";ConfirmDownload.IsEnabled=remoteSelection is not null;}
    void Start(Func<CancellationToken,Task> run)
    {
        if(!CurrentOperation.IsCompleted||lifetime.IsCancellationRequested)return;CurrentOperation=Run();
        async Task Run()
        {
            using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);operation=current;LastOperationSucceeded=false;ReadLocal.IsEnabled=false;
            try{feedback.Text="正在处理；可在传输面板查看进度。";await run(current.Token);LastOperationSucceeded=true;}
            catch(OperationCanceledException){feedback.Text="已取消；未完成的文件不会发布为剪贴板。";}
            catch(Exception){feedback.Text="操作未完成。若剪贴板已改变，完整文件仍保留，可确认后重试准备粘贴；其他失败请查看传输面板。";}
            finally
            {
                operation=null;ReadLocal.IsEnabled=!lifetime.IsCancellationRequested;
                if(!lifetime.IsCancellationRequested)try{var flow=await transfer(lifetime.Token);RetryRemote.IsEnabled=flow.CanPrepareRemote;RetryLocal.IsEnabled=flow.CanPrepareLocal;}catch(Exception){RetryRemote.IsEnabled=RetryLocal.IsEnabled=false;}
            }
        }
    }
    internal async Task CloseForSession()
    {lifetime.Cancel();operation?.Cancel();try{await CurrentOperation;}catch(Exception){}closing=true;Close();lifetime.Dispose();}
    internal void DisablePermission(){lifetime.Cancel();foreach(var button in new[]{ReadLocal,ConfirmUpload,ChooseFolder,ConfirmDownload,RetryRemote,RetryLocal})button.IsEnabled=false;feedback.Text="文件剪贴板已关闭；重新开启需要新的连接授权。";}
}
