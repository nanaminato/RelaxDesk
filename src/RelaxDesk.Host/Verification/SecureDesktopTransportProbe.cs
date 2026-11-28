using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

// Explicit local acceptance only: a single certificate-pinned loopback session,
// a fresh random password/device key, no persistent credentials or LAN listener.
internal static class SecureDesktopTransportProbe
{
    public static async Task Run(string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new IOException("Use a fresh transport probe output directory");
        Directory.CreateDirectory(output);
        WindowsInputPrivilege.EnsureOrdinaryProcess();WindowsSessionAccess.EnsureAllowed();
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var identity=new HostMediaIdentity();
        string password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        const string user="uac_probe_account";
        var verifier=PasswordVerifier.Create(user,password);
        string pin=identity.Certificate.GetCertHashString(HashAlgorithmName.SHA256);
        using var device=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start(8);
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        Environment.SetEnvironmentVariable("RELAXDESK_SERVICE_INPUT","1");
        Directory.CreateDirectory(Path.Combine(output,"transfers"));
        string selected=new WindowsDisplaySelection(null).Device;
        Task server=Serve();
        using var client=new SessionClient{Network=NetworkMode.Tcp,OpenDeviceKey=()=>ECDsa.Create(device.ExportParameters(true)),RequestFileResume=false};
        var writer=new SemaphoreSlim(1);long sequence=0;
        DisplayViewerSession? display=null;int decodedFrames=0;long secureGeneration=0;bool mouse=false,returned=false;
        try
        {
            await client.ConnectWithPassword("127.0.0.1",port,pin,user,password,(presented,_)=>Task.FromResult(presented==pin),lifetime.Token);
            display=new(client.DisplayState!,true);
            Task control=Controls();
            using var decoder=new ProbeDecoder(()=>{_ = WriteControl(new("keyframe-v2"));});
            while(!returned)
            {
                var reading=client.Video.Read(lifetime.Token);
                if(await Task.WhenAny(reading,control)==control)await control;
                var frame=await reading;
                if(!display.AcceptFrame(frame))continue;
                var decoded=decoder.Decode(frame);if(decoded is null)continue;
                decodedFrames++;
                if(display.Presented(frame.DisplayGeneration))await WriteControl(new("display-ready-v2",DisplayGeneration:frame.DisplayGeneration));
                if(decodedFrames==1)File.WriteAllText(Path.Combine(output,"READY"),"authenticated production media active");
                bool secure=decoded.Metadata.PointerType==CaptureMetadata.HostManagedPointer&&frame.DisplayGeneration>1;
                if(secure)
                {
                    secureGeneration=frame.DisplayGeneration;
                    File.WriteAllBytes(Path.Combine(output,"uac-through-tls.png"),ClipboardImageCodec.Encode(decoded.Pixels,frame.Width,frame.Height));
                    int expectedX=(int)((50000L*(frame.Width-1)+32767)/65535),expectedY=(int)((10000L*(frame.Height-1)+32767)/65535);
                    if(!mouse)
                    {
                        mouse=Math.Abs(decoded.Metadata.PointerX-expectedX)<=1&&Math.Abs(decoded.Metadata.PointerY-expectedY)<=1;
                        await WriteControl(InputProtocol.Encode(new("move",X:50000,Y:10000),1,frame.DisplayGeneration));
                        if(mouse)
                        {
                            await WriteControl(InputProtocol.Encode(new("key",VirtualKey:27),1,frame.DisplayGeneration));
                            await WriteControl(InputProtocol.Encode(new("key",VirtualKey:27,Up:true),1,frame.DisplayGeneration));
                        }
                    }
                }
                else if(secureGeneration>0&&frame.DisplayGeneration>secureGeneration)returned=true;
            }
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{status=mouse?"PASS":"INCOMPLETE",decodedFrames,secureGeneration,mouseVerified=mouse,
                escapeReturnedToDefault=mouse&&returned,defaultRecovered=returned,certificatePinned=true,passwordAndDeviceProof=true,network="TCP/TLS loopback",
                capture="production VideoHostSession + real SCM worker",decoder="production MediaDecoder",uacFrame="uac-through-tls.png"}));
            Console.WriteLine(mouse?"PASS production TLS UAC capture, mouse/Escape and Default recovery":"INCOMPLETE UAC capture and Default recovery passed; secure mouse/Escape not verified");
            if(!mouse)Environment.ExitCode=2;
            await lifetime.CancelAsync();client.Dispose();
            try{await control;}catch(Exception){}
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{status="FAIL",decodedFrames,secureGeneration,mouse,returned,error=error.ToString()}));
            throw;
        }
        finally
        {
            await lifetime.CancelAsync();client.Dispose();listener.Stop();
            try{await server;}catch(Exception error)when(lifetime.IsCancellationRequested){Console.Error.WriteLine("probe_server_end type="+error.GetType().Name);}
        }
        async Task WriteControl(Packet packet)
        {
            await writer.WaitAsync(lifetime.Token);
            try{await Wire.Write(client.Control,packet with{Sequence=++sequence},lifetime.Token);}
            finally{writer.Release();}
        }
        async Task Controls()
        {
            while(true)
            {
                var packet=await SessionControl.Read(client.Control,lifetime.Token);
                if(packet.Kind=="display-topology-v2")
                {
                    display!.ReceiveTopology(packet.DisplayState!);
                    await WriteControl(new("display-configured-v2",DisplayGeneration:packet.DisplayState!.Generation));
                }
                else if(packet.Kind=="component-unavailable-v5")continue;
                else if(packet.Kind=="session-ended-v2")throw new IOException("Production session ended during UAC probe");
                else throw new InvalidDataException("Unexpected production probe control message");
            }
        }
        async Task Serve()
        {
            using var socket=await listener.AcceptTcpClientAsync(lifetime.Token);
            using var tls=new SslStream(socket.GetStream(),false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=identity.Certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},lifetime.Token);
            var hello=await Wire.Read(tls,lifetime.Token,16*1024);
            _=HostConnection.ValidateRequest(hello);
            bool valid=hello.Kind==FullControlProtocol.PasswordRequest&&verifier.Verify(hello.Text,hello.Data);
            if(hello.Data is not null)CryptographicOperations.ZeroMemory(hello.Data);
            if(!valid||hello.DeviceKey is null)throw new AuthenticationException("Loopback probe password/device proof required");
            _=await HostConnection.VerifyDeviceIdentity(tls,identity.Certificate,hello.DeviceKey,lifetime.Token);
            // Invoking this local-only test explicitly approves its one generated device.
            await HostConnection.RunApproved(listener,socket,tls,identity,hello with{Data=null,Text=null},"dxgi",Path.Combine(output,"transfers"),null,
                lifetime.Token,null,selected);
        }
    }
    sealed class ProbeDecoder : IDisposable
    {
        readonly System.Collections.Concurrent.BlockingCollection<Action> work = new();
        readonly Thread thread;
        readonly MediaDecoder decoder;
        public ProbeDecoder(Action keyframe)
        {
            decoder = new(keyframe, preferHardware:false);
            thread = new Thread(() => { foreach(var action in work.GetConsumingEnumerable()) action(); }) { IsBackground=true, Name="UAC probe decoder" };
            thread.Start();
        }
        T Invoke<T>(Func<T> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Add(() => {try{result.SetResult(action());}catch(Exception error){result.SetException(error);}});
            return result.Task.GetAwaiter().GetResult();
        }
        public DecodedMedia? Decode(MediaFrame frame) => Invoke(() => decoder.Decode(frame));
        public void Dispose()
        {
            try{Invoke(() => {decoder.Dispose();return true;});}
            finally{work.CompleteAdding();thread.Join();work.Dispose();}
        }
    }

}
