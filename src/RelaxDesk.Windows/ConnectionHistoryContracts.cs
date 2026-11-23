using RelaxDesk.Core;
namespace RelaxDesk.Windows;

public static class ConnectionHistoryContracts
{
    public static async Task Run(string directory)
    {
        string root=Path.Combine(Path.GetFullPath(directory),"history-"+Guid.NewGuid().ToString("N")),path=Path.Combine(root,"preferences","connections.json");
        var store=new ConnectionHistoryStore(path);int checks=0;void Check(bool value){if(!value)throw new IOException("History store contract");checks++;}
        async Task Reject(Func<Task> action){bool failed=false;try{await action();}catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException){failed=true;}Check(failed);}
        Check((await store.Load()).Length==0&&!Directory.Exists(root));
        await store.Remember("2001:db8::1",48200);Check(File.Exists(path));Check((await store.Load()) is [{Address:"2001:db8::1",Port:48200}]);
        await Task.WhenAll(store.Remember("192.0.2.1",1),store.Remember("192.0.2.2",2));Check((await store.Load()).Length==3);
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.tmp").Length==0);
        byte[] before=await File.ReadAllBytesAsync(path);using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        bool stopped=false;try{await store.Remember("192.0.2.3",3,cancelled.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped&&(await File.ReadAllBytesAsync(path)).SequenceEqual(before));
        await File.WriteAllTextAsync(path,"{\"Format\":1,\"Entries\":[],\"Token\":\"keep-private\"}");
        await Reject(async()=>{await store.Load();});await Reject(async()=>{await store.Remember("192.0.2.4",4);});Check((await File.ReadAllTextAsync(path)).Contains("keep-private"));
        await store.Clear();Check(!File.Exists(path));await store.Remember("127.0.0.1",1);Check((await store.Load()).Length==1);
        await File.WriteAllBytesAsync(path,new byte[ConnectionHistory.MaximumBytes+1]);await Reject(async()=>{await store.Load();});await store.Clear();
        Directory.CreateDirectory(path);await Reject(async()=>{await store.Clear();});Check(Directory.Exists(path));Directory.Delete(path);
        string target=Path.Combine(root,"target.json");await File.WriteAllBytesAsync(target,before);
        try
        {
            File.CreateSymbolicLink(path,target);await Reject(async()=>{await store.Load();});await Reject(async()=>{await store.Clear();});Check((await File.ReadAllBytesAsync(target)).SequenceEqual(before));File.Delete(path);
        }
        catch(Exception e)when(e is UnauthorizedAccessException||e is IOException&&unchecked((uint)e.HResult)==0x80070522){Console.WriteLine("SKIP history symbolic-link fixture privilege unavailable; junction tested independently");}
        string outside=Path.Combine(root,"junction-target"),junction=Path.Combine(root,"junction");Directory.CreateDirectory(outside);string sentinel=Path.Combine(outside,"connections.json");await File.WriteAllBytesAsync(sentinel,before);
        FileTransferContracts.CreateFixtureJunction(junction,outside);var redirected=new ConnectionHistoryStore(Path.Combine(junction,"connections.json"));
        await Reject(async()=>{await redirected.Load();});await Reject(async()=>{await redirected.Remember("192.0.2.9",9);});await Reject(async()=>{await redirected.Clear();});Check((await File.ReadAllBytesAsync(sentinel)).SequenceEqual(before));Directory.Delete(junction);
        Console.WriteLine($"PASS {checks} real history store checks: bounded records, canonical IPs, serialized updates, atomic replacement/no temps, cancellation, malformed preservation/explicit clear, directory and real junction refusal; fixture={root}; no actual user preferences");
    }
}
