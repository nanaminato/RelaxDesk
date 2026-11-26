using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal sealed partial class FileTransferWindow : Window
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
    internal Button DiscardRecoveryButton=>discardRecovery;
    Func<bool>? recoveryValid;
    readonly List<Button> commands = [];
    string directory = "", pendingDirectory = ""; string[] pendingPaths = [];
    string? downloadDirectory;
    int page, lastListedCompletion; long revision = -1; bool planning, allowClose, hasNext;
    Task commandFinished=Task.CompletedTask;
    internal int PendingUploadCount => pendingPaths.Length;
    internal int DisplayedJobs => rows.Count;
    internal int DisplayedRemoteEntries => entries.Count;
    internal bool VisibleConfirmation => confirmation.Visibility == Visibility.Visible;
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
        InitializeComponent();
        Owner = owner;
        this.client = client;
        this.queue = queue;
        stop = CancellationTokenSource.CreateLinkedTokenSource(session);
        StopFilesButton.Click += (_, _) => stopPermission();
        ConfigureCommand(parent, async () => { if (directory.Length > 0) { directory = ParentPath(directory); page = 0; await LoadRemote(); } });
        ConfigureCommand(reload, LoadRemote);
        ConfigureCommand(uploadFile, () =>
        {
            var dialog = new OpenFileDialog { Title = "选择要上传的本地文件", Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) PrepareUpload(dialog.FileNames);
            return Task.CompletedTask;
        });
        ConfigureCommand(uploadFolder, () =>
        {
            var dialog = new OpenFolderDialog { Title = "选择要上传的文件夹" };
            if (dialog.ShowDialog(this) == true) PrepareUpload([dialog.FolderName]);
            return Task.CompletedTask;
        });
        ConfigureCommand(download, async () =>
        {
            var selected = remote.SelectedItems.Cast<RemoteRow>().Select(row => row.Entry).ToArray();
            if (selected.Length == 0) { feedback.Text = "请先选择远端文件或文件夹。"; return; }
            if (downloadDirectory is null && !ChooseDownloadFolder()) return;
            await RequestDownload(selected, downloadDirectory!);
        });
        ConfigureCommand(chooseDestination, () => { ChooseDownloadFolder(); return Task.CompletedTask; });
        ConfigureCommand(previous, async () => { if (page > 0) { page--; await LoadRemote(); } });
        ConfigureCommand(next, async () => { page++; await LoadRemote(); });
        ConfirmUploadButton.Click += async (_, _) => await RunCommand(ConfirmPending);
        dismiss.Click += (_, _) => ClearPending();
        remote.ItemsSource = entries;
        remote.MouseDoubleClick += async (_, _) =>
        {
            if (remote.SelectedItem is RemoteRow row && row.Entry.Directory)
                await RunCommand(async () => { directory = row.Entry.Path; page = 0; await LoadRemote(); });
        };
        tasks.ItemsSource = rows;
        tasks.SelectionChanged += (_, _) => UpdateActions();
        clear.Click += (_, _) => queue.ClearFinished();
        cancelAll.Click += async (_, _) => { if (queue.Enabled) queue.CancelAll(); else await queue.DisposeAsync(); };
        pause.Click += (_, _) => { if (Selected is { } item) queue.Pause(item.Id, item.State != TransferJobState.Paused); }; cancel.Click += (_, _) => { if (Selected is { } item) queue.Cancel(item.Id); }; retry.Click += (_, _) => { try { if (Selected is { } item) queue.Retry(item.Id); } catch (Exception error) { feedback.Text = error.Message; } };
        overwrite.Click += (_, _) => Collision(FileCollisionChoice.Overwrite); keepBoth.Click += (_, _) => Collision(FileCollisionChoice.KeepBoth); skip.Click += (_, _) => Collision(FileCollisionChoice.Skip);
        openFolder.Click += (_, _) => { if (Selected is { } item) { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; start.ArgumentList.Add(item.LocalFolder); Process.Start(start); } };
        PreviewDragOver += (_, e) => { e.Effects = queue.Enabled && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }; Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) PrepareUpload(paths); e.Handled = true; };
        refresh.Tick += (_, _) => RefreshJobs(); IsVisibleChanged += (_, _) => { if (IsVisible) { RefreshJobs(); refresh.Start(); } else refresh.Stop(); };
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; ClearPending(); Hide(); Owner?.Activate(); } };
        Closed += (_, _) => { refresh.Stop(); stop.Cancel(); stop.Dispose(); }; UpdateActions();
    }
    internal void ConfigureRecovery(int count,Func<Task> restore,Func<Task> discard,Func<bool>? valid=null)
    {
        recoveryValid = valid;
        recoveryText.Text = $"有 {count} 项中断任务。已重新获得同一目录授权；确认后才会继续。续传有效期5分钟，过期自动清理；文件可能保持占用。";
        recoveryNotice.Visibility = Visibility.Visible;
        ContinueRecoveryButton.Click+=async(_,_)=>await RunCommand(async()=>{await restore();recoveryNotice.Visibility=Visibility.Collapsed;});
        discardRecovery.Click+=async(_,_)=>await RunCommand(async()=>{await discard();recoveryNotice.Visibility=Visibility.Collapsed;});
        commands.Add(ContinueRecoveryButton);commands.Add(discardRecovery);
    }
    void ConfigureCommand(Button button, Func<Task> action)
    {
        button.Click += async (_, _) => await RunCommand(action);
        commands.Add(button);
    }
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
