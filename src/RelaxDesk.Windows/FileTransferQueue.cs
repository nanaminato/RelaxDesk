using System.IO;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public enum TransferJobState { Queued, Running, Paused, AwaitingOverwrite, Completed, Cancelled, Failed, Skipped, Retried }
public enum FileCollisionChoice { Overwrite, KeepBoth, Skip }
public sealed record TransferQueueStatus(long Version, int Count, int Completed, int Failed, int Pending, long CompletedBytes, long TotalBytes, TransferJobSnapshot? Current);
public sealed record TransferJobSnapshot(Guid Id, string Name, string Direction, string RemotePath, string LocalFolder,
    TransferJobState State, long CompletedBytes, long TotalBytes, double BytesPerSecond, double? RemainingSeconds, string? Error)
{
    public override string ToString() => $"TransferJob id={Id} state={State} (paths redacted)";
}

// Session-scoped queue: one data operation, <=10000 manifest/history entries.
// All enumeration and file IO run on workers. UI receives coalescible snapshots.
public sealed class FileTransferQueue : IAsyncDisposable
{
    sealed class Job
    {
        public Guid Id = Guid.NewGuid();
        public required WindowsFileRoot LocalRoot;
        public required string LocalPath, LocalFolder, RemotePath;
        public bool Download, Directory, Overwrite;
        public long Total, Completed;
        public double Speed; public double? Eta;
        public TransferJobState State;
        public string? Error;
        public TransferControl Control = new();
        public required CancellationTokenSource Stop;
        public TaskCompletionSource<FileCollisionChoice>? Collision;
    }
    readonly object sync = new();
    readonly FileTransferClient client;
    readonly CancellationTokenSource stop;
    readonly SemaphoreSlim available = new(0);
    readonly Queue<Job> pending = new();
    readonly List<Job> jobs = [];
    readonly Task worker;
    bool disabled; int disposed;
    long version;
    public event Action? Changed;
    public FileTransferQueue(FileTransferClient client, CancellationToken session)
    { this.client = client; stop = CancellationTokenSource.CreateLinkedTokenSource(session); client.Closed += DataClosed; worker = Task.Run(Run); }
    void DataClosed() => Disable("文件数据通道已断开，请重新连接并授权。");
    public bool Enabled { get { lock (sync) return !disabled && !stop.IsCancellationRequested; } }
    public bool Busy { get { lock (sync) return jobs.Any(job => job.State is TransferJobState.Running or TransferJobState.Paused); } }
    public TransferJobSnapshot[] Snapshot()
    {
        lock (sync) return jobs.Select(Copy).ToArray();
    }
    static TransferJobSnapshot Copy(Job job) => new(job.Id, job.Directory ? Path.GetFileName(job.RemotePath) + "/" : Path.GetFileName(job.RemotePath), job.Download ? "下载" : "上传", job.RemotePath, job.LocalFolder, job.State, job.Completed, job.Total, job.State == TransferJobState.Paused ? 0 : job.Speed, job.Eta, job.Error);
    public TransferQueueStatus Status()
    {
        lock (sync)
        {
            var current = jobs.FirstOrDefault(job => job.State is TransferJobState.Running or TransferJobState.Paused or TransferJobState.AwaitingOverwrite);
            return new(version, jobs.Count, jobs.Count(job => job.State == TransferJobState.Completed), jobs.Count(job => job.State == TransferJobState.Failed), jobs.Count(Active),
                jobs.Where(job => job.State != TransferJobState.Retried).Sum(job => job.State == TransferJobState.Completed ? job.Total : job.Completed), jobs.Where(job => job.State != TransferJobState.Retried).Sum(job => job.Total), current is null ? null : Copy(current));
        }
    }
    void Update(Job job, Action<Job> change) { lock (sync) { var before = job.State; change(job); if (before != job.State) version++; } Changed?.Invoke(); }
    static string Join(string folder, string name) => TransferPaths.Relative(folder.Length == 0 ? name : folder + "/" + name);
    void CheckPlan(List<Job> plan)
    { if (plan.Count >= TransferPaths.MaximumEntries) throw new IOException("每批最多10000个文件或目录"); stop.Token.ThrowIfCancellationRequested(); }
    Job NewJob(WindowsFileRoot root, string local, string localFolder, string remote, bool download, bool directory, long size)
        => new() { LocalRoot = root, LocalPath = local, LocalFolder = localFolder, RemotePath = remote, Download = download, Directory = directory, Total = size, Stop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token) };
    Guid[] Add(List<Job> plan)
    {
        lock (sync)
        {
            if (disabled || stop.IsCancellationRequested) throw new OperationCanceledException("文件权限已关闭");
            if (jobs.Count + plan.Count > TransferPaths.MaximumEntries) throw new IOException("任务列表已满，请先清除已结束任务");
            jobs.AddRange(plan); foreach (var job in plan) pending.Enqueue(job); version++;
            if (plan.Count > 0) available.Release(plan.Count);
        }
        Changed?.Invoke(); return plan.Select(job => job.Id).ToArray();
    }
    public async Task<Guid[]> EnqueueUploads(string[] selectedPaths, string remoteDirectory, CancellationToken ct)
    {
        _ = TransferPaths.Relative(remoteDirectory, true);
        if (selectedPaths.Length is 0 or > 128) throw new IOException("一次最多选择128项");
        var plan = new List<Job>();
        try
        {
            await Task.Run(() =>
            {
                void Visit(WindowsFileRoot root, string local, string localFolder, string remote, bool directory, long length)
                {
                    ct.ThrowIfCancellationRequested(); CheckPlan(plan); plan.Add(NewJob(root, local, localFolder, remote, false, directory, length));
                    if (!directory) return;
                    for (int offset = 0; ; offset += TransferLimits.ListPage)
                    {
                        var entries = root.List(local, offset);
                        foreach (var entry in entries) Visit(root, entry.Path, localFolder, Join(remote, Path.GetFileName(entry.Path)), entry.Directory, entry.Length);
                        if (entries.Length < TransferLimits.ListPage) break;
                    }
                }
                foreach (string selected in selectedPaths)
                {
                    string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
                    string parent = Path.GetDirectoryName(full) ?? throw new IOException("请选择文件或文件夹，不能上传整个驱动器");
                    var root = new WindowsFileRoot(parent); string name = Path.GetFileName(full); _ = TransferPaths.Relative(name);
                    var attributes = File.GetAttributes(full);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不传输链接或云占位符");
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    long length = directory ? 0 : new FileInfo(full).Length;
                    if (length > TransferLimits.MaximumFileBytes) throw new IOException("单文件不能超过50GiB");
                    Visit(root, name, parent, Join(remoteDirectory, name), directory, length);
                }
            }, ct);
            ct.ThrowIfCancellationRequested(); return Add(plan);
        }
        catch { foreach (var job in plan) job.Stop.Dispose(); throw; }
    }
    public async Task<Guid[]> EnqueueDownloads(TransferEntry[] selected, string localDirectory, CancellationToken ct)
    {
        if (selected.Length is 0 or > 128) throw new IOException("一次最多选择128项");
        var root = await Task.Run(() => new WindowsFileRoot(localDirectory), ct);
        var plan = new List<Job>();
        try
        {
            async Task Visit(TransferEntry entry, string local)
            {
                ct.ThrowIfCancellationRequested(); CheckPlan(plan); _ = TransferPaths.Relative(entry.Path);
                plan.Add(NewJob(root, local, Path.GetFullPath(localDirectory), entry.Path, true, entry.Directory, entry.Length));
                if (!entry.Directory) return;
                for (int offset = 0; ; offset += TransferLimits.ListPage)
                {
                    var entries = await client.List(entry.Path, offset, ct);
                    foreach (var child in entries) await Visit(child, Join(local, Path.GetFileName(child.Path)));
                    if (entries.Length < TransferLimits.ListPage) break;
                }
            }
            foreach (var entry in selected) await Visit(entry, Path.GetFileName(TransferPaths.Relative(entry.Path)));
            ct.ThrowIfCancellationRequested(); return Add(plan);
        }
        catch { foreach (var job in plan) job.Stop.Dispose(); throw; }
    }
    public void Pause(Guid id, bool pause)
    {
        lock (sync)
        {
            var job = jobs.FirstOrDefault(job => job.Id == id); if (job is null || job.State is not (TransferJobState.Running or TransferJobState.Paused)) return;
            job.Control.Paused = pause; job.State = pause ? TransferJobState.Paused : TransferJobState.Running; version++;
        }
        Changed?.Invoke();
    }
    static bool Active(Job job) => job.State is TransferJobState.Queued or TransferJobState.Running or TransferJobState.Paused or TransferJobState.AwaitingOverwrite;
    public bool CanCancel(Guid id)
    {
        lock (sync)
        {
            var selected = jobs.FirstOrDefault(job => job.Id == id); return selected is not null && jobs.Any(job => Active(job) && (job.Id == id || selected.Directory && job.Download == selected.Download && job.RemotePath.StartsWith(selected.RemotePath + "/", StringComparison.Ordinal)));
        }
    }
    static void CancelSources(IEnumerable<CancellationTokenSource> sources) { foreach (var source in sources) try { source.Cancel(); } catch (ObjectDisposedException) { } }
    public void Cancel(Guid id)
    {
        CancellationTokenSource[] sources;
        lock (sync)
        {
            var selected = jobs.FirstOrDefault(job => job.Id == id); if (selected is null) return;
            sources = jobs.Where(job => Active(job) && (job.Id == id || selected.Directory && job.Download == selected.Download && job.RemotePath.StartsWith(selected.RemotePath + "/", StringComparison.Ordinal))).Select(job => job.Stop).ToArray();
        }
        CancelSources(sources); Changed?.Invoke();
    }
    public void CancelAll() { CancellationTokenSource[] sources; lock (sync) sources = jobs.Where(Active).Select(job => job.Stop).ToArray(); CancelSources(sources); Changed?.Invoke(); }
    public void ChooseCollision(Guid id, FileCollisionChoice choice) { lock (sync) jobs.FirstOrDefault(job => job.Id == id)?.Collision?.TrySetResult(choice); }
    public Guid Retry(Guid id)
    {
        Job copy;
        lock (sync)
        {
            var source = jobs.Single(job => job.Id == id);
            if (source.State is not (TransferJobState.Failed or TransferJobState.Cancelled or TransferJobState.Skipped)) throw new InvalidOperationException("此任务不能重试");
            copy = NewJob(source.LocalRoot, source.LocalPath, source.LocalFolder, source.RemotePath, source.Download, source.Directory, source.Total);
        }
        Add([copy]); lock (sync) { jobs.Single(job => job.Id == id).State = TransferJobState.Retried; version++; }
        Changed?.Invoke(); return copy.Id;
    }
    public void ClearFinished()
    { lock (sync) { jobs.RemoveAll(job => job.State is TransferJobState.Completed or TransferJobState.Cancelled or TransferJobState.Failed or TransferJobState.Skipped or TransferJobState.Retried); version++; } Changed?.Invoke(); }
    public void Disable(string reason)
    {
        lock (sync) { disabled = true; foreach (var job in jobs.Where(job => job.State is TransferJobState.Queued or TransferJobState.Running or TransferJobState.Paused or TransferJobState.AwaitingOverwrite)) job.Error = reason; }
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        Changed?.Invoke();
    }
    async Task Run()
    {
        try
        {
            while (true)
            {
                await available.WaitAsync(stop.Token); Job job; lock (sync) job = pending.Dequeue();
                TransferJobState end = TransferJobState.Completed; string? error = null;
                try
                {
                    job.Stop.Token.ThrowIfCancellationRequested(); Update(job, value => value.State = TransferJobState.Running);
                    int alternate = 0;
                    string original = job.Download ? job.LocalPath : job.RemotePath;
                    while (true)
                    {
                        try
                        {
                            if (job.Directory)
                            {
                                if (job.Download) job.LocalRoot.CreateDirectory(job.LocalPath); else await client.CreateDirectory(job.RemotePath, job.Stop.Token);
                            }
                            else
                            {
                                void Progress(TransferProgress progress) => Update(job, value => { value.Completed = progress.Completed; value.Total = progress.Total; value.Speed = progress.BytesPerSecond; value.Eta = progress.RemainingSeconds; });
                                if (job.Download) await client.Download(job.RemotePath, size => job.LocalRoot.CreateUpload(job.LocalPath, size, job.Overwrite), job.Stop.Token, job.Control, Progress);
                                else { await using var source = job.LocalRoot.OpenRead(job.LocalPath); await client.Upload(job.RemotePath, source.Stream, job.Overwrite, job.Stop.Token, job.Control, Progress); }
                            }
                            break;
                        }
                        catch (TransferRejectedException collision) when (collision.Code == "exists")
                        {
                            var decision = new TaskCompletionSource<FileCollisionChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                            Update(job, value => { value.State = TransferJobState.AwaitingOverwrite; value.Error = "目标已存在：请选择覆盖、保留两份或跳过"; value.Collision = decision; });
                            FileCollisionChoice choice = await decision.Task.WaitAsync(job.Stop.Token);
                            Update(job, value => { value.State = TransferJobState.Running; value.Error = null; value.Collision = null; });
                            if (choice == FileCollisionChoice.Skip) { end = TransferJobState.Skipped; break; }
                            if (choice == FileCollisionChoice.Overwrite) job.Overwrite = true;
                            else
                            {
                                if (++alternate > 999) throw new IOException("无法为同名文件生成新名称");
                                string folder = original.Contains('/') ? original[..original.LastIndexOf('/')] : "";
                                string suffix = $" ({alternate})", extension = Path.GetExtension(original), stem = Path.GetFileNameWithoutExtension(original);
                                int maximum = Math.Min(255, TransferPaths.MaximumLength - (folder.Length == 0 ? 0 : folder.Length + 1)) - suffix.Length - extension.Length;
                                if (maximum < 1) throw new IOException("文件名或路径过长，无法自动重命名");
                                string filename = stem[..Math.Min(stem.Length, maximum)] + suffix + extension;
                                string renamed = Join(folder, filename); if (job.Download) job.LocalPath = renamed; else job.RemotePath = renamed;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { end = TransferJobState.Cancelled; }
                catch (Exception failure)
                {
                    end = TransferJobState.Failed; error = failure is TransferRejectedException rejected ? rejected.Code switch { "disk-full" => "目标磁盘空间不足", "access-denied" => "未获文件访问权限", "invalid-data" => "数据或SHA-256校验失败", _ => "文件不可访问、被占用或路径不受支持" } : failure is InvalidDataException ? "数据或SHA-256校验失败" : failure is IOException ? "文件不可访问、被占用或空间不足" : failure.GetType().Name;
                }
                finally { job.Stop.Dispose(); }
                Update(job, value => { value.State = end; value.Error = error ?? value.Error; value.Speed = 0; value.Collision = null; });
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            lock (sync)
            {
                while (pending.TryDequeue(out var job)) { job.Stop.Dispose(); job.State = TransferJobState.Cancelled; }
                version++;
            }
            Changed?.Invoke();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Closed -= DataClosed; Disable("会话已结束"); await worker; stop.Dispose(); available.Dispose();
    }
}
