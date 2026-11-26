using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class DesktopPauseSmoke
{
    public static async Task Run(string application)
    {
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        int port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();
        using var host = Process.Start(ChildProcess.Create(application, "127.0.0.1", port.ToString(), "test-desktop-pause"))
            ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var viewer = new SessionClient();
        var ct = timeout.Token;
        try
        {
            for (int i = 0; i < 3; i++) _ = await host.StandardOutput.ReadLineAsync(ct);
            await Task.Delay(100, ct);
            var connect = viewer.ConnectByApproval("127.0.0.1", port, ct);
            if (!(await host.StandardOutput.ReadLineAsync(ct) ?? "").Contains("APPROVE"))
                throw new IOException("Host consent prompt absent");
            await host.StandardInput.WriteLineAsync("APPROVE");
            await host.StandardInput.FlushAsync(ct);
            await connect;
            var before = await viewer.Video.Read(ct);
            if (await Wire.Read(viewer.Control, ct) != new Packet("desktop-paused-v2"))
                throw new IOException("Desktop pause barrier missing");
            var next = viewer.Video.Read(ct);
            await Wire.Write(viewer.Control, new("ping-v2", 1), ct);
            if (await Wire.Read(viewer.Control, ct) != new Packet("pong-v2", 1))
                throw new IOException("Control connection failed during opaque desktop");
            if (await Task.WhenAny(next, Task.Delay(500, ct)) == next)
                throw new IOException("Media was delivered during the paused interval");
            if (await Wire.Read(viewer.Control, ct) != new Packet("desktop-resumed-v2"))
                throw new IOException("Desktop did not resume");
            var recovered = await next;
            using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
            {
                if (recovered.Sequence <= before.Sequence || !recovered.KeyFrame || decoder.Decode(recovered) is null)
                    throw new IOException("Desktop resume did not deliver a decodable keyframe");
            }
            viewer.Dispose();
            await host.WaitForExitAsync(ct);
            if (host.ExitCode != 0) throw new IOException("Host exit failed");
            Console.WriteLine("PASS production Host synthetic opaque desktop: video suspended, TLS heartbeat continued and a key frame decoded after resume");
        }
        finally { if (!host.HasExited) host.Kill(true); }
    }
}
