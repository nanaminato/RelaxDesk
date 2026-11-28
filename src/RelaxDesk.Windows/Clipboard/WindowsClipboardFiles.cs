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
    // Explorer, the RDP clipboard service or a clipboard manager can hold the
    // clipboard for a few milliseconds. File commands are interactive, so retry
    // briefly instead of failing a whole copy/paste.
    bool TryOpenClipboard()
    {
        for(int attempt=0;attempt<10;attempt++)
        {
            if(OpenClipboard(window))return true;
            Thread.Sleep(10);
        }
        return false;
    }
    void ClearPublishedFiles()
    {
        if(publishedFileVersion==0&&publishedFileMarker is null)return;
        if(!TryOpenClipboard())return;
        try
        {
            // Only a CF_HDROP this adapter published is cleared. Text, HTML or
            // image content that replaced it must remain on the clipboard.
            bool ours=IsClipboardFormatAvailable(15)&&
                (GetClipboardSequenceNumber()==publishedFileVersion||
                 publishedFileMarker is not null&&ReadMarker()==publishedFileMarker);
            if(ours)EmptyClipboard();
            publishedFileVersion=0;publishedFileMarker=null;
        }
        finally{CloseClipboard();}
    }
    ClipboardContentSnapshot ReadFileBaseline(ClipboardFormats formats)
    {
        if(!ReadContent(formats,out var snapshot))throw new IOException("Clipboard is busy; copy again and retry");
        return snapshot;
    }

    public Task<long> ClipboardVersion(CancellationToken ct)=>ScheduleFileCommand(formats=>
    {
        ReadFileBaseline(formats);
        return fileClipboardVersion;
    },ct);
    public Task<bool> PrepareFiles(ClipboardFiles files,long expectedVersion,CancellationToken ct)
    {
        if(clipboardFileRoot is null||!ClipboardFiles.Valid(files))throw new InvalidDataException("Clipboard file scope unavailable");
        var owned=files.Own();
        return ScheduleFileCommand(formats=>
        {
            var snapshot=ReadFileBaseline(formats);
            return (expectedVersion==fileClipboardVersion||expectedVersion==snapshot.Version)&&
                TryWrite(new ClipboardContent(Files:owned),snapshot.Version,out _);
        },ct);
    }
    public Task<LocalClipboardFiles?> ReadFiles(CancellationToken ct)=>ScheduleFileCommand<LocalClipboardFiles?>(formats=>
    {
        ReadFileBaseline(formats);
        return new(fileClipboardVersion,observedFilePaths?.ToArray()??[]);
    },ct);
    public Task<bool> PublishFiles(string[] paths,long expectedVersion,CancellationToken ct)
    {
        byte[] bytes=ClipboardFileFormat.Encode(paths);
        string marker="publish:"+Guid.NewGuid().ToString("N");
        return ScheduleFileCommand(formats=>
        {
            var snapshot=ReadFileBaseline(formats);
            if(expectedVersion!=fileClipboardVersion&&expectedVersion!=snapshot.Version)return false;
            nint drop=0,tag=0;
            try
            {
                drop=Allocate(bytes);tag=Allocate(StrictUtf8.GetBytes(marker+"\0"));
                if(!TryOpenClipboard())return false;
                try
                {
                    if(GetClipboardSequenceNumber()!=snapshot.Version||!EmptyClipboard())return false;
                    if(SetClipboardData(15,drop)==0){EmptyClipboard();return false;}
                    drop=0;
                    if(SetClipboardData(Format(ref markerFormat,"RelaxDesk.ClipboardSnapshot"),tag)==0){EmptyClipboard();return false;}
                    tag=0;
                }
                finally{CloseClipboard();}
                // Final sequence only exists after CloseClipboard.
                publishedFileVersion=GetClipboardSequenceNumber();
                publishedMarker=marker;
                publishedFileMarker=marker;
                publishedText=false;publishedHtml=false;publishedPng=false;
                publishedTextDigest="";publishedHtmlDigest="";
                publishedFilePaths=paths.ToArray();
                cached=null;richCached=null;return true;
            }
            finally{if(drop!=0)GlobalFree(drop);if(tag!=0)GlobalFree(tag);}
        },ct);
    }
}
