using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Confirmed file clipboard copy uses the ordinary scoped transfer queue. A
// CF_HDROP publication occurs only after every job commits; no app paste/drag
// injection and no virtual OLE file object is involved.
public sealed class FileClipboardTransfer(FileTransferClient client,FileTransferQueue queue,ILocalFileClipboard local,Func<bool> allowed)
{
    ClipboardFiles? uploaded;
    string[]? downloaded;
    WindowsFileRoot? downloadRoot;
    public bool CanPrepareRemote=>uploaded is not null;
    public bool CanPrepareLocal=>downloaded is not null;
    void Check(CancellationToken ct){ct.ThrowIfCancellationRequested();if(!allowed()||!queue.Enabled||!client.Active)throw new IOException("文件剪贴板权限已关闭");}
    static string Stage()=>"RelaxDesk-Clipboard-"+Guid.NewGuid().ToString("N");
    async Task<TransferJobSnapshot[]> Complete(Guid[] ids,CancellationToken ct)
    {
        var selected=ids.ToHashSet();
        try
        {
            while(true)
            {
                Check(ct);var jobs=queue.Snapshot().Where(job=>selected.Contains(job.Id)).ToArray();
                if(jobs.Length!=ids.Length)throw new IOException("任务记录已改变；未发布剪贴板");
                if(jobs.Any(job=>job.State is TransferJobState.Failed or TransferJobState.Cancelled or TransferJobState.Skipped or TransferJobState.Interrupted or TransferJobState.Retried))throw new IOException("文件未全部完成；未发布剪贴板");
                if(jobs.All(job=>job.State==TransferJobState.Completed))return jobs;
                await Task.Delay(50,ct);
            }
        }
        catch{foreach(Guid id in ids)queue.Cancel(id);throw;}
    }
    public async Task Upload(LocalClipboardFiles selection,CancellationToken ct)
    {
        Check(ct);_=ClipboardFileFormat.Encode(selection.Paths);
        var fresh=await local.ReadFiles(ct);if(fresh is null||fresh.Version!=selection.Version||!fresh.Paths.SequenceEqual(selection.Paths))throw new IOException("本机剪贴板已改变，请重新读取并确认");
        long version=await client.ClipboardVersion(ct);Check(ct);string stage=Stage();await client.CreateDirectory(stage,ct);
        Guid[] jobs=await queue.EnqueueUploads(selection.Paths,stage,ct);var completed=await Complete(jobs,ct);Check(ct);
        string[] tops=selection.Paths.Select(path=>stage+"/"+Path.GetFileName(path)).ToArray();
        uploaded=new(completed.Where(job=>tops.Contains(job.RemotePath,StringComparer.OrdinalIgnoreCase)).Select(job=>job.RemotePath).ToArray());
        if(!ClipboardFiles.Valid(uploaded)||uploaded.Paths.Length!=selection.Paths.Length)throw new IOException("文件清单与完成任务不匹配");
        await client.PrepareClipboard(uploaded,version,ct);
    }
    async Task<TransferEntry> Describe(string path,CancellationToken ct)
    {
        int separator=path.LastIndexOf('/');string directory=separator<0?"":path[..separator];
        for(int offset=0;offset<=TransferPaths.MaximumEntries;offset+=TransferLimits.ListPage)
        {
            Check(ct);var entries=await client.List(directory,offset,ct);var found=entries.FirstOrDefault(entry=>entry.Path==path);if(found is not null)return found;if(entries.Length<TransferLimits.ListPage)break;
        }
        throw new IOException("复制的远端文件已改变或不在授权目录中");
    }
    public async Task Download(ClipboardFiles offer,string destination,CancellationToken ct)
    {
        Check(ct);if(!ClipboardFiles.Valid(offer))throw new InvalidDataException("Invalid clipboard file offer");
        long prior=await local.ClipboardVersion(ct);
        var entries=new List<TransferEntry>();foreach(string path in offer.Paths)entries.Add(await Describe(path,ct));
        string stage=Stage();var root=await Task.Run(()=>{var selected=new WindowsFileRoot(destination);selected.CreateDirectory(stage);return new WindowsFileRoot(Path.Combine(destination,stage));},ct);
        string folder=Path.Combine(destination,stage);Guid[] jobs=await queue.EnqueueDownloads(entries.ToArray(),folder,ct);var completed=await Complete(jobs,ct);Check(ct);
        downloaded=completed.Where(job=>offer.Paths.Contains(job.RemotePath,StringComparer.OrdinalIgnoreCase)).Select(job=>Path.Combine(folder,job.LocalRelativePath!.Replace('/',Path.DirectorySeparatorChar))).ToArray();downloadRoot=root;
        if(downloaded.Length!=offer.Paths.Length)throw new IOException("下载清单与完成任务不匹配");
        _=root.DescribeClipboard(downloaded);if(!await local.PublishFiles(downloaded,prior,ct))throw new IOException("本机剪贴板已改变；文件已下载，请确认后重试准备粘贴");
    }
    public async Task PrepareRemote(CancellationToken ct)
    {Check(ct);if(uploaded is null)throw new IOException("尚无完整上传文件");long version=await client.ClipboardVersion(ct);Check(ct);await client.PrepareClipboard(uploaded,version,ct);}
    public async Task PrepareLocal(CancellationToken ct)
    {
        Check(ct);if(downloaded is null||downloadRoot is null)throw new IOException("尚无完整下载文件");_=downloadRoot.DescribeClipboard(downloaded);
        long prior=await local.ClipboardVersion(ct);Check(ct);if(!await local.PublishFiles(downloaded,prior,ct))throw new IOException("剪贴板再次改变，请重试");
    }
}
