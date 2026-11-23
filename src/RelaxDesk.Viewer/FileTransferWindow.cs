using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal sealed class FileTransferWindow : Window
{
    sealed class RemoteRow(TransferEntry entry)
    {
        public TransferEntry Entry { get; } = entry;
        public string Name => Path.GetFileName(Entry.Path);
        public string Type => Entry.Directory ? "文件夹" : "文件";
        public string Size => Entry.Directory ? "—" : Bytes(Entry.Length);
    }
    sealed class JobRow(TransferJobSnapshot value) : INotifyPropertyChanged
    {
        public TransferJobSnapshot Value { get; private set; } = value;
        public string Name => Value.Name;
        public string Direction => Value.Direction;
        public string State => Value.State == TransferJobState.Completed && Value.Name.EndsWith('/') ? "已创建目录" : StateText(Value.State);
        public string Progress => $"{Bytes(Value.CompletedBytes)} / {Bytes(Value.TotalBytes)}"+(Value.ResumedFrom>0?" · 续传":"");
        public string Rate => Value.BytesPerSecond > 0 ? Bytes((long)Value.BytesPerSecond) + "/s" : "—";
        public string Eta
        {
            get
            {
                if (Value.State == TransferJobState.Paused) return "已暂停";
                if (Value.RemainingSeconds is not { } seconds || !double.IsFinite(seconds)) return "—";
                var remaining = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 31536000));
                return remaining.TotalDays >= 1 ? $"{remaining.Days}天 {remaining.Hours:00}:{remaining.Minutes:00}" : remaining.ToString(@"hh\:mm\:ss");
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(TransferJobSnapshot next) { Value = next; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("")); }
    }
    readonly FileTransferClient client;
    readonly FileTransferQueue queue;
    readonly CancellationTokenSource stop;
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly ObservableCollection<RemoteRow> entries = [];
    readonly ObservableCollection<JobRow> rows = [];
    readonly Dictionary<Guid, JobRow> byId = [];
    readonly DataGrid remote = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, MinHeight = 130, EnableRowVirtualization = true, CanUserAddRows = false };
    readonly DataGrid tasks = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, EnableRowVirtualization = true, CanUserAddRows = false, MinHeight = 110 };
    readonly TextBlock breadcrumb = new() { FontWeight = FontWeights.SemiBold, Margin = new(4, 6, 4, 6) }, feedback = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray }, summary = new(), selectedDetail = new() { TextWrapping = TextWrapping.Wrap };
    readonly ProgressBar totalProgress = new() { Minimum = 0, Maximum = 100, Height = 6, Margin = new(0, 6, 0, 6) };
    readonly Border confirmation = new() { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(232, 242, 255)), Padding = new(12), CornerRadius = new(8), Margin = new(0, 5, 0, 6) };
    readonly TextBlock confirmationText = new() { TextWrapping = TextWrapping.Wrap };
    readonly Border recoveryNotice=new(){Visibility=Visibility.Collapsed,Background=new SolidColorBrush(Color.FromRgb(232,242,255)),Padding=new(12),CornerRadius=new(8),Margin=new(0,7,0,4)};
    internal Button ContinueRecoveryButton {get;}=new(){Content="继续中断任务"};
    readonly Button discardRecovery=new(){Content="放弃中断任务"};
    internal Button DiscardRecoveryButton=>discardRecovery;
    Func<bool>? recoveryValid;
    readonly StackPanel browser=new(){Margin=new(0,8,0,0)};
    readonly Button pause = new() { Content = "暂停" }, cancel = new() { Content = "取消" }, retry = new() { Content = "重试" }, overwrite = new() { Content = "覆盖" }, keepBoth = new() { Content = "保留两份" }, skip = new() { Content = "跳过" }, openFolder = new() { Content = "打开本地目录" };
    readonly List<Button> commands = [];
    readonly Button next;
    string directory = "", pendingDirectory = ""; string[] pendingPaths = [];
    string? downloadDirectory;
    int page, lastListedCompletion; long revision = -1; bool planning, allowClose, hasNext;
    Task commandFinished=Task.CompletedTask;
    internal int PendingUploadCount => pendingPaths.Length;
    internal int DisplayedJobs => rows.Count;
    internal int DisplayedRemoteEntries => entries.Count;
    internal bool VisibleConfirmation => confirmation.Visibility == Visibility.Visible;
    internal Button ConfirmUploadButton { get; }
    internal Button StopFilesButton { get; }
    internal FileTransferQueue Queue => queue;
    internal Button OverwriteButton => overwrite;
    internal Button KeepBothButton => keepBoth;
    internal Button SkipButton => skip;
    internal Button PauseButton => pause;
    internal Button CancelButton => cancel;
    internal Button RetryButton => retry;
    internal void SelectJob(Guid id) { RefreshJobs(); if (byId.TryGetValue(id, out var row)) tasks.SelectedItem = row; UpdateActions(); }
    internal FileTransferWindow(Window owner, FileTransferClient client, FileTransferQueue queue, CancellationToken session, Action stopPermission)
    {
        Owner = owner; this.client = client; this.queue = queue; stop = CancellationTokenSource.CreateLinkedTokenSource(session);
        Title = "RelaxDesk · 文件传输"; Width = 1080; Height = 800; MinWidth = 820; MinHeight = 620; FontSize = 13; FontFamily = new("Segoe UI, Microsoft YaHei UI"); Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));
        RelaxDesk.Ui.ModernTheme.Apply(this);
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new DockPanel(); StopFilesButton = new Button { Content = "关闭文件权限" }; StopFilesButton.Click += (_, _) => stopPermission(); DockPanel.SetDock(StopFilesButton, Dock.Right); heading.Children.Add(StopFilesButton);
        heading.Children.Add(new StackPanel { Children = { new TextBlock { Text = "文件传输", FontSize = 24, FontWeight = FontWeights.SemiBold }, new TextBlock { Text = "仅访问Host授权目录。拖入文件表示上传，不模拟远程应用内拖放；不传输链接或云占位符。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray } } }); root.Children.Add(heading);
        browser.Children.Add(breadcrumb); var actions = new WrapPanel();
        actions.Children.Add(Command("上级", async () => { if (directory.Length > 0) { directory = ParentPath(directory); page = 0; await LoadRemote(); } }));
        actions.Children.Add(Command("刷新", LoadRemote));
        actions.Children.Add(Command("上传文件…", () => { var dialog = new OpenFileDialog { Title = "选择要上传的本地文件", Multiselect = true, CheckFileExists = true }; if (dialog.ShowDialog(this) == true) PrepareUpload(dialog.FileNames); return Task.CompletedTask; }));
        actions.Children.Add(Command("上传文件夹…", () => { var dialog = new OpenFolderDialog { Title = "选择要上传的文件夹" }; if (dialog.ShowDialog(this) == true) PrepareUpload([dialog.FolderName]); return Task.CompletedTask; }));
        actions.Children.Add(Command("下载选中…", async () => { var selected = remote.SelectedItems.Cast<RemoteRow>().Select(row => row.Entry).ToArray(); if (selected.Length == 0) { feedback.Text = "请先选择远端文件或文件夹。"; return; } if (downloadDirectory is null && !ChooseDownloadFolder()) return; await RequestDownload(selected, downloadDirectory!); }));
        actions.Children.Add(Command("下载目录…", () => { ChooseDownloadFolder(); return Task.CompletedTask; }));
        actions.Children.Add(Command("上一页", async () => { if (page > 0) { page--; await LoadRemote(); } })); next = Command("下一页", async () => { page++; await LoadRemote(); }); actions.Children.Add(next); browser.Children.Add(actions);
        var confirmBody = new StackPanel(); confirmBody.Children.Add(confirmationText); var confirmActions = new WrapPanel(); ConfirmUploadButton = new() { Content = "确认上传" }; ConfirmUploadButton.Click += async (_, _) => await RunCommand(ConfirmPending); var dismiss = new Button { Content = "取消上传" }; dismiss.Click += (_, _) => ClearPending(); confirmActions.Children.Add(ConfirmUploadButton); confirmActions.Children.Add(dismiss); confirmBody.Children.Add(confirmActions); confirmation.Child = confirmBody; browser.Children.Add(confirmation); Grid.SetRow(browser, 1); root.Children.Add(browser);
        remote.Columns.Add(Column("名称", nameof(RemoteRow.Name), new(1, DataGridLengthUnitType.Star))); remote.Columns.Add(Column("类型", nameof(RemoteRow.Type), new(90))); remote.Columns.Add(Column("大小", nameof(RemoteRow.Size), new(120))); remote.ItemsSource = entries;
        remote.MouseDoubleClick += async (_, _) => { if (remote.SelectedItem is RemoteRow row && row.Entry.Directory) await RunCommand(async () => { directory = row.Entry.Path; page = 0; await LoadRemote(); }); }; Grid.SetRow(remote, 2); root.Children.Add(remote);
        var totals = new StackPanel { Margin = new(0, 10, 0, 6) }; totals.Children.Add(summary); totals.Children.Add(totalProgress); totals.Children.Add(feedback); Grid.SetRow(totals, 3); root.Children.Add(totals);
        tasks.Columns.Add(Column("任务", nameof(JobRow.Name), new(1, DataGridLengthUnitType.Star))); tasks.Columns.Add(Column("方向", nameof(JobRow.Direction), new(55))); tasks.Columns.Add(Column("状态", nameof(JobRow.State), new(130))); tasks.Columns.Add(Column("进度", nameof(JobRow.Progress), new(180))); tasks.Columns.Add(Column("速度", nameof(JobRow.Rate), new(100))); tasks.Columns.Add(Column("预计剩余", nameof(JobRow.Eta), new(100))); tasks.ItemsSource = rows; tasks.SelectionChanged += (_, _) => UpdateActions();
        var expander = new Expander { Header = "传输队列", IsExpanded = true, Content = tasks }; Grid.SetRow(expander, 4); root.Children.Add(expander);
        var footer = new StackPanel(); var taskActions = new WrapPanel(); foreach (var button in new[] { pause, cancel, retry, overwrite, keepBoth, skip, openFolder }) taskActions.Children.Add(button); var clear = new Button { Content = "清除已结束" }; clear.Click += (_, _) => queue.ClearFinished(); taskActions.Children.Add(clear); var cancelAll = new Button { Content = "取消全部" }; cancelAll.Click += async (_, _) => {if(queue.Enabled)queue.CancelAll();else await queue.DisposeAsync();}; taskActions.Children.Add(cancelAll); footer.Children.Add(taskActions); footer.Children.Add(selectedDetail); Grid.SetRow(footer, 5); root.Children.Add(footer);
        pause.Click += (_, _) => { if (Selected is { } item) queue.Pause(item.Id, item.State != TransferJobState.Paused); }; cancel.Click += (_, _) => { if (Selected is { } item) queue.Cancel(item.Id); }; retry.Click += (_, _) => { try { if (Selected is { } item) queue.Retry(item.Id); } catch (Exception error) { feedback.Text = error.Message; } };
        overwrite.Click += (_, _) => Collision(FileCollisionChoice.Overwrite); keepBoth.Click += (_, _) => Collision(FileCollisionChoice.KeepBoth); skip.Click += (_, _) => Collision(FileCollisionChoice.Skip);
        openFolder.Click += (_, _) => { if (Selected is { } item) { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; start.ArgumentList.Add(item.LocalFolder); Process.Start(start); } };
        Content = new Border { Background = Background, Padding = new(20), Child = root }; AllowDrop = true; PreviewDragOver += (_, e) => { e.Effects = queue.Enabled && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }; Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) PrepareUpload(paths); e.Handled = true; };
        foreach (var grid in new[] { remote, tasks }) { grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)); grid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(248, 250, 253)); grid.RowHeight = 25; grid.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)); }
        refresh.Tick += (_, _) => RefreshJobs(); IsVisibleChanged += (_, _) => { if (IsVisible) { RefreshJobs(); refresh.Start(); } else refresh.Stop(); };
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; ClearPending(); Hide(); Owner?.Activate(); } };
        Closed += (_, _) => { refresh.Stop(); stop.Cancel(); stop.Dispose(); }; UpdateActions();
    }
    static DataGridTextColumn Column(string title, string path, DataGridLength width) => new() { Header = title, Binding = new Binding(path), Width = width };
    internal void ConfigureRecovery(int count,Func<Task> restore,Func<Task> discard,Func<bool>? valid=null)
    {
        recoveryValid=valid;var body=new StackPanel();body.Children.Add(new TextBlock{Text=$"有 {count} 项中断任务。已重新获得同一目录授权；确认后才会继续。续传有效期5分钟，过期自动清理；文件可能保持占用。",TextWrapping=TextWrapping.Wrap});
        var actions=new WrapPanel();actions.Children.Add(ContinueRecoveryButton);actions.Children.Add(discardRecovery);body.Children.Add(actions);recoveryNotice.Child=body;recoveryNotice.Visibility=Visibility.Visible;
        ContinueRecoveryButton.Click+=async(_,_)=>await RunCommand(async()=>{await restore();recoveryNotice.Visibility=Visibility.Collapsed;});
        discardRecovery.Click+=async(_,_)=>await RunCommand(async()=>{await discard();recoveryNotice.Visibility=Visibility.Collapsed;});
        commands.Add(ContinueRecoveryButton);commands.Add(discardRecovery);
        browser.Children.Insert(0,recoveryNotice);
    }
    Button Command(string text, Func<Task> action) { var button = new Button { Content = text }; button.Click += async (_, _) => await RunCommand(action); commands.Add(button); return button; }
    async Task RunCommand(Func<Task> action)
    {
        if (planning || !queue.Enabled) return; planning = true; SetCommands();
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);commandFinished=completion.Task;
        try { await action(); }
        catch (OperationCanceledException) { feedback.Text = "操作已取消。"; }
        catch (Exception error) { feedback.Text = error is TransferRejectedException rejected ? "文件操作失败：" + rejected.Code : error.Message; }
        finally { planning = false; SetCommands(); RefreshJobs();completion.TrySetResult(); }
    }
    void SetCommands() { foreach (var command in commands) command.IsEnabled = !planning && queue.Enabled; ConfirmUploadButton.IsEnabled = !planning && queue.Enabled; next.IsEnabled = !planning && queue.Enabled && hasNext; }
    bool ChooseDownloadFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择接收下载的本地目录" }; if (dialog.ShowDialog(this) != true) return false;
        downloadDirectory = Path.GetFullPath(dialog.FolderName); feedback.Text = "下载到：" + downloadDirectory; return true;
    }
    internal void PrepareUpload(string[] paths)
    {
        if (!queue.Enabled) { feedback.Text = "文件权限未开放或已关闭。"; return; }
        if (paths.Length is 0 or > 128) { feedback.Text = "一次最多拖入或选择128项。"; return; }
        pendingPaths = paths.ToArray(); pendingDirectory = directory; confirmationText.Text = $"将上传 {paths.Length} 项到 授权目录/{directory}。文件夹会递归传输；同名文件需另行确认。"; confirmation.Visibility = Visibility.Visible;
    }
    void ClearPending() { pendingPaths = []; confirmation.Visibility = Visibility.Collapsed; }
    internal async Task ConfirmPending()
    {
        string[] paths = pendingPaths; string target = pendingDirectory; if (paths.Length == 0) return;
        feedback.Text = "正在扫描并加入传输队列…"; await queue.EnqueueUploads(paths, target, stop.Token); ClearPending(); feedback.Text = "已加入传输队列。";
    }
    internal async Task RequestDownload(TransferEntry[] selected, string localDirectory)
    { feedback.Text = "正在准备下载任务…"; await queue.EnqueueDownloads(selected, localDirectory, stop.Token); feedback.Text = "下载任务已加入队列。"; }
    internal Task RefreshRemote() => RunCommand(LoadRemote);
    async Task LoadRemote()
    {
        var list = await client.List(directory, page * TransferLimits.ListPage, stop.Token); if (allowClose) return;
        entries.Clear(); foreach (var entry in list) entries.Add(new(entry)); hasNext = list.Length == TransferLimits.ListPage; next.IsEnabled = hasNext;
        breadcrumb.Text = "授权目录 / " + directory + (page > 0 ? $"  · 第 {page + 1} 页" : "");
        lastListedCompletion = queue.Status().Completed;
    }
    TransferJobSnapshot? Selected => (tasks.SelectedItem as JobRow)?.Value;
    void Collision(FileCollisionChoice choice) { if (Selected is { } item) queue.ChooseCollision(item.Id, choice); }
    void UpdateActions()
    {
        var item = Selected; bool enabled = queue.Enabled;
        pause.IsEnabled = enabled && item?.State is TransferJobState.Running or TransferJobState.Paused; pause.Content = item?.State == TransferJobState.Paused ? "继续" : "暂停";
        cancel.IsEnabled = enabled && item is not null && queue.CanCancel(item.Id);
        retry.IsEnabled = enabled && item?.State is TransferJobState.Failed or TransferJobState.Cancelled or TransferJobState.Skipped;
        overwrite.IsEnabled = keepBoth.IsEnabled = skip.IsEnabled = enabled && item?.State == TransferJobState.AwaitingOverwrite;
        openFolder.IsEnabled = item?.Direction == "下载" && item.State == TransferJobState.Completed;
        selectedDetail.Text = item is null ? "选择任务可暂停、取消或查看详情。" : $"{item.Direction} · {item.RemotePath}" + (item.Error is null ? "" : "\n" + item.Error);
    }
    internal void RefreshJobs()
    {
        if(recoveryNotice.Visibility==Visibility.Visible&&recoveryValid?.Invoke()==false){recoveryNotice.Visibility=Visibility.Collapsed;feedback.Text="中断任务已过期或已处理，请重新选择文件。";}
        var state = queue.Status();
        if (state.Version != revision)
        {
            Guid? selected = Selected?.Id; var snapshots = queue.Snapshot(); var present = snapshots.Select(item => item.Id).ToHashSet();
            foreach (var removedId in byId.Keys.Where(id => !present.Contains(id)).ToArray()) { rows.Remove(byId[removedId]); byId.Remove(removedId); }
            foreach (var snapshot in snapshots) { if (byId.TryGetValue(snapshot.Id, out var row)) row.Set(snapshot); else { row = new(snapshot); byId.Add(snapshot.Id, row); rows.Add(row); } }
            if (selected is { } id && byId.TryGetValue(id, out var selectedRow)) tasks.SelectedItem = selectedRow;
            revision = state.Version;
        }
        if (state.Current is { } current && byId.TryGetValue(current.Id, out var active)) active.Set(current);
        summary.Text = $"已结束 {state.Count - state.Pending} / {state.Count} 项 · 成功 {state.Completed} · 失败 {state.Failed} · {Bytes(state.CompletedBytes)} / {Bytes(state.TotalBytes)}";
        totalProgress.Value = state.TotalBytes > 0 ? Math.Min(100, state.CompletedBytes * 100d / state.TotalBytes) : 0;
        if (!queue.Enabled) feedback.Text = "文件权限已关闭或会话已结束；重新开启需要重新连接并授权。";
        SetCommands(); UpdateActions();
        if (!planning && queue.Enabled && state.Pending == 0 && state.Completed != lastListedCompletion) { lastListedCompletion = state.Completed; _ = RefreshRemote(); }
    }
    internal void CloseForSession() { allowClose = true; Close(); }
    internal async Task CloseForSessionAsync(){CloseForSession();await commandFinished;}
    static string ParentPath(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    static string StateText(TransferJobState state) => state switch { TransferJobState.Queued => "等待", TransferJobState.Running => "传输中", TransferJobState.Paused => "已暂停", TransferJobState.AwaitingOverwrite => "等待重名处理", TransferJobState.Completed => "完成（已校验）", TransferJobState.Cancelled => "已取消", TransferJobState.Failed => "失败", TransferJobState.Skipped => "已跳过",TransferJobState.Interrupted=>"已中断，待重新授权", _ => "已重试" };
    static string Bytes(long bytes) { string[] units = ["B", "KiB", "MiB", "GiB", "TiB"]; double value = bytes; int unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return $"{value:F1} {units[unit]}"; }
}
