using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;
public sealed partial class ViewerWindow
{
    FileTransferClient? fileClient;
    FileTransferQueue? fileQueue;
    string[] RequestedFeatures()=>SessionCapabilities.All;
    internal void SelectFileRateForVerification(int index)=>fileRateLimit.SelectedIndex=index;
    Task StartFileSession(SessionClient connection,System.IO.Stream traffic,CancellationToken ct)
    {
        if(connection.Data is null)throw new System.IO.InvalidDataException("Current session requires the clipboard file channel");
        int rateKiB=fileRateLimit.SelectedIndex switch{1=>4096,2=>1024,3=>256,4=>50*1024,5=>100*1024,6=>250*1024,7=>0,_=>20*1024};
        fileClient=new(traffic,ct,maximumBytesPerSecond:rateKiB*1024,supportsResume:connection.SupportsFileResume,supportsClipboardFiles:true);
        fileClient.SetLinkRateCeiling(preferences.AutomaticVideoQuality ? FileTransferClient.LinkRateForVideoBitrate(AutomaticVideoQuality.Initial(preferences.FramesPerSecond,preferences.Bitrate,VideoSettingsProtocol.Default.Generation).Bitrate) : 0);
        fileQueue=new(fileClient,ct);return Task.CompletedTask;
    }
    async Task EndFileSession()
    {
        if(fileQueue is not null){await fileQueue.DisposeAsync();fileQueue=null;}
        if(fileClient is not null){await fileClient.DisposeAsync();fileClient=null;}
    }
}
