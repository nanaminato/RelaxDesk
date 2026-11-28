using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class FileClipboardContracts
{
    sealed class BaselineClipboard:IContentClipboard
    {
        public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot)
        {
            snapshot=new(1,new(Clear:true));
            return true;
        }
        public bool TryWrite(ClipboardContent content,long expectedVersion,out long writtenVersion)
        {
            writtenVersion=expectedVersion;
            return false;
        }
        public void Dispose(){}
    }

    sealed class Local(string[] selected):ILocalFileClipboard
    {
        public int Published;public long Version=1;public string[] Paths=selected;
        public Task<long> ClipboardVersion(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(Version);}
        public Task<LocalClipboardFiles?> ReadFiles(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult<LocalClipboardFiles?>(new(Version,Paths.ToArray()));}
        public Task<bool> PublishFiles(string[] paths,long expected,CancellationToken ct){ct.ThrowIfCancellationRequested();if(expected!=Version)return Task.FromResult(false);Paths=ClipboardFileFormat.Decode(ClipboardFileFormat.Encode(paths));Published++;Version++;return Task.FromResult(true);}
    }
    sealed class Publisher(WindowsFileRoot root):IFileClipboardPublisher
    {
        public int Published;public long Version=3;public ClipboardFiles? Files;
        public Task<long> ClipboardVersion(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(Version);}
        public Task<bool> PrepareFiles(ClipboardFiles files,long expected,CancellationToken ct){ct.ThrowIfCancellationRequested();_=root.ResolveClipboard(files);if(Version!=expected)return Task.FromResult(false);Files=files.Own();Published++;Version++;return Task.FromResult(true);}
    }
    public static async Task Run(string directory)
    {
        int count=0;void Check(bool value){if(!value)throw new IOException($"Windows file clipboard contract {count+1} failed");count++;}
        void Reject(Action run){try{run();throw new InvalidOperationException("Unsafe clipboard input accepted");}catch(Exception error)when(error is IOException or InvalidDataException){count++;}}
        string fixtures=Path.Combine(Path.GetFullPath(directory),"contracts-"+Guid.NewGuid().ToString("N")),host=Path.Combine(fixtures,"host"),source=Path.Combine(fixtures,"source"),dest=Path.Combine(fixtures,"dest");
        Directory.CreateDirectory(host);Directory.CreateDirectory(Path.Combine(source,"空"));Directory.CreateDirectory(dest);File.WriteAllText(Path.Combine(host,"existing.txt"),"existing");File.WriteAllText(Path.Combine(source,"日本語.txt"),"file clipboard source");File.WriteAllBytes(Path.Combine(source,"empty"),[]);
        var root=new WindowsFileRoot(host);var paths=new[]{Path.Combine(source,"日本語.txt"),Path.Combine(source,"空")};byte[] encoded=ClipboardFileFormat.Encode(paths);
        Check(ClipboardFileFormat.Decode(encoded).SequenceEqual(paths));Check(BinaryPrimitives.ReadInt32LittleEndian(encoded)==20&&BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(16))==1);
        var wide=encoded.ToArray();BinaryPrimitives.WriteInt32LittleEndian(wide.AsSpan(16),-1);Check(ClipboardFileFormat.Decode(wide).SequenceEqual(paths));
        Reject(()=>ClipboardFileFormat.Decode(encoded[..^2]));var ansi=encoded.ToArray();BinaryPrimitives.WriteInt32LittleEndian(ansi.AsSpan(16),0);Reject(()=>ClipboardFileFormat.Decode(ansi));
        var offset=encoded.ToArray();BinaryPrimitives.WriteInt32LittleEndian(offset,21);Reject(()=>ClipboardFileFormat.Decode(offset));
        Reject(()=>ClipboardFileFormat.Encode(["C:\\bad:stream"]));Reject(()=>ClipboardFileFormat.Encode(["\\\\server\\share\\file"]));Reject(()=>ClipboardFileFormat.Encode([Path.GetPathRoot(source)!]));Reject(()=>ClipboardFileFormat.Encode(["relative.txt"]));Reject(()=>ClipboardFileFormat.Encode([paths[0],paths[0]]));
        var described=root.DescribeClipboard([Path.Combine(host,"existing.txt")]);Check(described.Paths is["existing.txt"]&&root.ResolveClipboard(described).Single()==Path.Combine(host,"existing.txt"));Reject(()=>root.DescribeClipboard(paths));Reject(()=>root.ResolveClipboard(new(["../escape"])));
        var sources=new ClipboardFileSources();
        var offered=sources.Describe(paths);
        string offerId=offered.Paths[0].Split('/')[0];
        Check(sources.List(offerId,0).Select(entry=>entry.Path).SequenceEqual(offered.Paths));
        await using(var selected=sources.OpenRead(offered.Paths[0]))Check(selected.Stream.Length>0);
        Reject(()=>sources.OpenRead(offerId+"/empty"));
        Reject(()=>sources.List(offerId+"/../source",0));
        var newer=sources.Describe(paths);
        await using(var retained=sources.OpenRead(offered.Paths[0]))Check(retained.Stream.Length>0);
        Check(sources.List(offerId,0).Length>0);
        sources.Clear();Reject(()=>sources.OpenRead(offered.Paths[0]));Reject(()=>sources.OpenRead(newer.Paths[0]));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));using var viewer=new TcpClient();var accepting=listener.AcceptTcpClientAsync(timeout.Token);await viewer.ConnectAsync((IPEndPoint)listener.LocalEndpoint,timeout.Token);using var server=await accepting;listener.Stop();
        var gate=new AccessGate();gate.Approve([SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardFiles]);var publisher=new Publisher(root);
        var worker=new FileTransferServer(root,gate,clipboardPublisher:_=>Task.FromResult<IFileClipboardPublisher?>(publisher)).Run(server.GetStream(),timeout.Token);
        await using var client=new FileTransferClient(viewer.GetStream(),timeout.Token,64*1024,supportsClipboardFiles:true);await using var queue=new FileTransferQueue(client,timeout.Token);
        try
        {
            long version=await client.ClipboardVersion(timeout.Token);Check(version==3);
            try{await client.PrepareClipboard(described,version+1,timeout.Token);throw new InvalidOperationException("Changed clipboard overwritten");}catch(TransferRejectedException error){Check(error.Code=="clipboard-changed"&&publisher.Published==0);}
            _=await client.List("",0,timeout.Token);await client.PrepareClipboard(described,version,timeout.Token);Check(publisher.Published==1);
            gate.RevokeFeature(SessionCapabilities.ClipboardFiles);try{await client.ClipboardVersion(timeout.Token);throw new InvalidOperationException("Unauthorized clipboard version read");}catch(TransferRejectedException error){Check(error.Code=="access-denied");}
            Check((await client.List("",0,timeout.Token)).Length==1);gate.Approve([SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardFiles]);
            var local=new Local([source]);var flow=new FileClipboardTransfer(client,queue,local,()=>ClipboardFormats.Granted(gate).Files);
            await flow.Upload(new(local.Version,[source]),timeout.Token);Check(publisher.Published==2&&publisher.Files is not null);Check(File.ReadAllText(Path.Combine(root.ResolveClipboard(publisher.Files!).Single(),"日本語.txt"))=="file clipboard source");
            await flow.Download(publisher.Files!,dest,timeout.Token);Check(local.Published==1&&File.ReadAllText(Path.Combine(local.Paths.Single(),"日本語.txt"))=="file clipboard source");Check(Directory.Exists(Path.Combine(local.Paths.Single(),"空")));
            long downloaded=client.DownloadedBytes;await flow.PrepareLocal(timeout.Token);Check(local.Published==2&&client.DownloadedBytes==downloaded);
            string large=Path.Combine(fixtures,"large.bin");File.WriteAllBytes(large,new byte[8*1024*1024]);local.Paths=[large];local.Version++;using var cancel=new CancellationTokenSource();int before=publisher.Published;
            var copying=flow.Upload(new(local.Version,[large]),cancel.Token);
            while(!queue.Snapshot().Any(job=>job.Name=="large.bin"&&job.CompletedBytes>0))await Task.Delay(10,timeout.Token);
            cancel.Cancel();try{await copying;throw new InvalidOperationException("Cancelled copy completed");}catch(OperationCanceledException){count++;}
            while(queue.Busy)await Task.Delay(10,timeout.Token);Check(publisher.Published==before);Check(queue.Snapshot().Any(job=>job.Name=="large.bin"&&job.State==TransferJobState.Cancelled));
        }
        finally{timeout.Cancel();viewer.Close();server.Close();try{await worker;}catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException){} }
        await VerifyAutomaticClipboard(root,source,dest,sources);
        string moved=host+"-moved";Directory.Move(host,moved);Directory.CreateDirectory(host);File.WriteAllText(Path.Combine(host,"existing.txt"),"replacement");Reject(()=>root.ResolveClipboard(described));
        Console.WriteLine($"PASS {count} actual Windows file clipboard contracts: Unicode DROPFILES/termination/bounds, scoped path/root identity, real TCP transfer/metadata acknowledgement, changed/revoked clipboard, recursive/empty files, local CF_HDROP buffer after commits, cancellation never publishes; generated fixtures={fixtures}; no system clipboard or Explorer paste");
    }

    static async Task VerifyAutomaticClipboard(WindowsFileRoot root,string source,string destination,ClipboardFileSources sources)
    {
        static async Task Reject(Func<Task> operation)
        {
            try{await operation();throw new InvalidOperationException("File outside the session scope was accessible");}
            catch(TransferRejectedException error)when(error.Code=="access-denied"){}
        }
        string file=Path.Combine(source,"日本語.txt");
        var offer=sources.Describe([source]);
        var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken ct=timeout.Token;
        using var viewer=new TcpClient();
        using var server=await Connect();
        async Task<TcpClient> Connect()
        {
            try
            {
                var accepting=listener.AcceptTcpClientAsync(ct);
                await viewer.ConnectAsync((IPEndPoint)listener.LocalEndpoint,ct);
                return await accepting;
            }
            finally{listener.Stop();}
        }
        var gate=new AccessGate();
        gate.Approve([SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardFiles]);
        var publisher=new Publisher(root);
        var worker=new FileTransferServer(root,gate,clipboardPublisher:_=>Task.FromResult<IFileClipboardPublisher?>(publisher),
            fileSources:sources).Run(server.GetStream(),ct);
        await using var client=new FileTransferClient(viewer.GetStream(),ct,supportsClipboardFiles:true);
        await using var queue=new FileTransferQueue(client,ct);
        try
        {
            await Reject(()=>client.List("",0,ct));
            await Reject(()=>client.CreateDirectory("outside-stage",ct));
            await Reject(()=>client.PrepareClipboard(new(["existing.txt"]),publisher.Version,ct));
            var local=new Local([file]);
            var flow=new FileClipboardTransfer(client,queue,local,()=>ClipboardFormats.Granted(gate).Files);
            await flow.Upload(new(local.Version,[file]),ct);
            if(publisher.Published!=1||publisher.Files is null||
                File.ReadAllText(root.ResolveClipboard(publisher.Files).Single())!=File.ReadAllText(file))
                throw new IOException("Automatic upload did not publish completed bytes");
            try
            {
                await flow.Download(offer,destination,ct,local.Version+1);
                throw new InvalidOperationException("A stale clipboard version started a download");
            }
            catch(IOException) when(client.DownloadedBytes==0&&local.Published==0){}
            await flow.Download(offer,destination,ct,local.Version);
            if(local.Published!=1||File.ReadAllText(Path.Combine(local.Paths.Single(),"日本語.txt"))!=File.ReadAllText(file))
                throw new IOException("Copied folder outside staging did not publish completed bytes");
            sources.ClearLatest();
            if(await client.LatestClipboardOffer(ct) is not null)
                throw new IOException("Cleared clipboard still advertises a selection");

            // The rich channel sees EOF before the Host copies another file.
            // Keep the same file client, queue and authorization for the download.
            await using var rich=new ClipboardContentSession(new MemoryStream(),()=>new BaselineClipboard(),gate,ct,receiveFiles:false);
            if(!await rich.FileFallback.WaitAsync(ct)||rich.Completion.IsCompleted)
                throw new IOException("Rich clipboard EOF did not leave the file clipboard available");
            var afterEof=sources.Describe([file]);
            string? offerId=await client.LatestClipboardOffer(ct);
            if(offerId is null||offerId==offer.Paths[0].Split('/')[0])
                throw new IOException("File channel did not discover the new Host selection after EOF");
            var recovered=await client.ReadClipboardOffer(offerId,ct);
            if(!recovered.Paths.SequenceEqual(afterEof.Paths))
                throw new IOException("File channel returned the wrong selection after EOF");
            await flow.Download(recovered,destination,ct,local.Version);
            if(local.Published!=2||File.ReadAllText(local.Paths.Single())!=File.ReadAllText(file))
                throw new IOException("File copied after rich clipboard EOF did not publish matching bytes");
            string[] many=Enumerable.Range(0,TransferLimits.ListPage+1)
                .Select(index=>Path.Combine(source,$"page-{index}.txt")).ToArray();
            foreach(string path in many)File.WriteAllBytes(path,[]);
            var multipage=sources.Describe(many);
            string? multipageId=await client.LatestClipboardOffer(ct);
            if(multipageId is null||!(await client.ReadClipboardOffer(multipageId,ct)).Paths.SequenceEqual(multipage.Paths))
                throw new IOException("Clipboard offer lost entries across pages");
            gate.RevokeFeature(SessionCapabilities.ClipboardFiles);
            await Reject(()=>client.LatestClipboardOffer(ct));
            await Reject(()=>client.ReadClipboardOffer(offerId,ct));
            await Reject(()=>client.List(offer.Paths.Single(),0,ct));
        }
        finally
        {
            timeout.Cancel();
            viewer.Close();server.Close();
            try{await worker;}catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException){}
        }
        Console.WriteLine("PASS clipboard-only server: staged upload, copied folder outside staging, session scope, Host file copy after rich-channel EOF, and live revocation");
    }
}
