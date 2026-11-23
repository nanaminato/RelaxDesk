using RelaxDesk.Core;
namespace RelaxDesk.Windows;

// Current-user preference only. No identity trust, credentials or permissions persist.
public sealed class ConnectionHistoryStore(string file)
{
    readonly string path=Path.GetFullPath(file);readonly SemaphoreSlim operations=new(1);
    void Guard()
    {
        if(Directory.Exists(path))throw new IOException("History location is a directory");
        for(string? current=path;current is not null;current=Path.GetDirectoryName(current))
        {
            try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("History path is redirected");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
    }
    ConnectionEndpoint[] Read()
    {
        Guard();if(!File.Exists(path))return [];
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>ConnectionHistory.MaximumBytes)throw new InvalidDataException("History file too large");
        byte[] bytes=new byte[(int)input.Length];input.ReadExactly(bytes);return ConnectionHistory.Decode(bytes);
    }
    async Task<T> Run<T>(Func<T> work,CancellationToken ct)
    {
        await operations.WaitAsync(ct);try{return await Task.Run(()=>{ct.ThrowIfCancellationRequested();return work();},ct);}finally{operations.Release();}
    }
    public Task<ConnectionEndpoint[]> Load(CancellationToken ct=default)=>Run(Read,ct);
    public Task<ConnectionEndpoint[]> Remember(string address,int port,CancellationToken ct=default)=>Run(()=>
    {
        var entries=ConnectionHistory.Remember(Read(),address,port);Guard();Directory.CreateDirectory(Path.GetDirectoryName(path)!);Guard();
        string temporary=Path.Combine(Path.GetDirectoryName(path)!,"history-"+Guid.NewGuid().ToString("N")+".tmp");
        try{File.WriteAllBytes(temporary,ConnectionHistory.Encode(entries));Guard();ct.ThrowIfCancellationRequested();File.Move(temporary,path,true);return entries;}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    },ct);
    // Explicit clear also removes malformed bounded documents; never follows links.
    public Task<bool> Clear(CancellationToken ct=default)=>Run(()=>{Guard();if(File.Exists(path))File.Delete(path);return true;},ct);
}
