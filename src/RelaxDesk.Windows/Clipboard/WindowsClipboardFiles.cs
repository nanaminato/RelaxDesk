using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public interface ILocalFileClipboard
{
    Task<long> ClipboardVersion(CancellationToken ct);
    Task<LocalClipboardFiles?> ReadFiles(CancellationToken ct);
    Task<bool> PublishFiles(string[] paths,long expectedVersion,CancellationToken ct);
}
public interface IFileClipboardPublisher
{
    Task<long> ClipboardVersion(CancellationToken ct);
    Task<bool> PrepareFiles(ClipboardFiles files,long expectedVersion,CancellationToken ct);
}
public sealed partial class WindowsTextClipboard:ILocalFileClipboard,IFileClipboardPublisher
{
    readonly WindowsFileRoot? clipboardFileRoot;
    readonly Action<ClipboardFiles?>? fileOffer;
    readonly ClipboardFileSources? fileSources;
    readonly Action<LocalClipboardFiles>? localFilesChanged;
    readonly Action<ClipboardFiles, long>? remoteFilesReceived;
    long observedClipboardVersion, publishedFileVersion;
    readonly object fileCommandSync=new();
    Action<ClipboardFormats>? fileCommand;
    Action? cancelFileCommand;
    async Task<T> ScheduleFileCommand<T>(Func<ClipboardFormats,T> operation,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var done=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action cancel=()=>done.TrySetCanceled();
        lock(fileCommandSync)
        {
            if(window==0)throw new ObjectDisposedException(nameof(WindowsTextClipboard));
            if(fileCommand is not null)throw new IOException("Another clipboard file operation is pending");
            fileCommand=formats=>
            {
                if(ct.IsCancellationRequested){done.TrySetCanceled(ct);return;}
                try{if(!formats.Files)throw new IOException("Clipboard file permission is off");done.TrySetResult(operation(formats));}catch(Exception error){done.TrySetException(error);}
            };
            cancelFileCommand=cancel;
        }
        using var registration=ct.Register(()=>
        {
            lock(fileCommandSync)
            {
                if(!ReferenceEquals(cancelFileCommand,cancel))return;
                fileCommand=null;
                cancelFileCommand=null;
            }
            done.TrySetCanceled(ct);
        });
        return await done.Task.WaitAsync(ct);
    }
    void RunFileCommand(ClipboardFormats formats)
    {
        Action<ClipboardFormats>? command;lock(fileCommandSync){command=fileCommand;fileCommand=null;cancelFileCommand=null;}command?.Invoke(formats);
    }
    void CancelFileCommand(){lock(fileCommandSync){fileCommand=null;cancelFileCommand?.Invoke();cancelFileCommand=null;}}
    void ClearPublishedFiles()
    {
        if(publishedFileVersion==0||GetClipboardSequenceNumber()!=publishedFileVersion||!OpenClipboard(window))return;
        try
        {
            if(GetClipboardSequenceNumber()==publishedFileVersion)EmptyClipboard();
        }
        finally{CloseClipboard();}
    }
    public Task<long> ClipboardVersion(CancellationToken ct)=>ScheduleFileCommand<long>(_=>{Verify();Desktop.EnsureInteractiveDesktop();return GetClipboardSequenceNumber();},ct);
    public Task<bool> PrepareFiles(ClipboardFiles files,long expectedVersion,CancellationToken ct)
    {
        if(clipboardFileRoot is null||!ClipboardFiles.Valid(files))throw new InvalidDataException("Clipboard file scope unavailable");
        var owned=files.Own();return ScheduleFileCommand(formats=>TryWrite(new ClipboardContent(Files:owned),expectedVersion,out _),ct);
    }
    public Task<LocalClipboardFiles?> ReadFiles(CancellationToken ct)=>ScheduleFileCommand<LocalClipboardFiles?>(_=>
    {
        Verify();Desktop.EnsureInteractiveDesktop();if(!OpenClipboard(window))throw new IOException("Clipboard is busy; copy again and retry");
        try{if(!ReadMemory(15,ClipboardFileFormat.MaximumBytes,out var raw))throw new IOException("Clipboard is busy");return new(GetClipboardSequenceNumber(),raw is null?[]:ClipboardFileFormat.Decode(raw));}finally{CloseClipboard();}
    },ct);
    public Task<bool> PublishFiles(string[] paths,long expectedVersion,CancellationToken ct)
    {
        byte[] bytes=ClipboardFileFormat.Encode(paths);
        return ScheduleFileCommand(_=>
        {
            Verify();Desktop.EnsureInteractiveDesktop();nint memory=Allocate(bytes);
            try
            {
                if(!OpenClipboard(window))return false;
                try
                {
                    if(GetClipboardSequenceNumber()!=expectedVersion||!EmptyClipboard()||SetClipboardData(15,memory)==0)return false;
                    memory=0;publishedFileVersion=GetClipboardSequenceNumber();cached=null;richCached=null;return true;
                }
                finally{CloseClipboard();}
            }
            finally{if(memory!=0)GlobalFree(memory);}
        },ct);
    }
}
