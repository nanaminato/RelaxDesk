using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    // Explicit real-host probe. Requires an existing saved credential and pin;
    // stores bounded cursor-only metadata, never video, clipboard or credentials.
    internal static void VerifyRemotePointer(Application app, string address, int number, string userName, string output, bool gpu)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(address), number).ToString();
        var saved = new ViewerCredentialStore().Read(endpoint) ?? throw new IOException("Saved credential required");
        if (saved.UserName != userName || string.IsNullOrEmpty(PasswordLoginSettings.ReadServerPin(endpoint)))
            throw new IOException("Matching saved user and verified pin required");
        if (Directory.Exists(output)) throw new IOException("Use a fresh pointer probe output directory");
        Directory.CreateDirectory(output);
        var window = new ViewerWindow();
        window.host.Text = address; window.port.Text = number.ToString();
        window.loginName.Text = userName; window.token.Password = saved.Password;
        window.gpuRendering.IsChecked = gpu; window.startFullScreen.IsChecked = false;
        window.networkMode.SelectedIndex = 1;
        int frames = 0;
        var shapes = new Dictionary<string, object>();
        window.pointerFrameForVerification = frame =>
        {
            var pointer = CaptureMetadataCodec.Decode(frame.Metadata);
            PointerCompositor.Validate(pointer);
            Interlocked.Increment(ref frames);
            if (pointer.PointerShape.Length == 0 || shapes.Count >= 32) return;
            string key = Convert.ToHexString(SHA256.HashData(pointer.PointerShape));
            if (shapes.ContainsKey(key)) return;
            int index = shapes.Count;
            // Strip desktop timing/geometry and normalize the cursor origin.
            var cursorOnly = pointer with { Timestamp = 0, DirtyRectangles = [], MoveRectangles = [], PointerX = 0, PointerY = 0 };
            File.WriteAllBytes(Path.Combine(output, $"pointer-{index}.bin"), CaptureMetadataCodec.Encode(cursorOnly));
            shapes.Add(key, new { index, pointer.PointerType, pointer.PointerWidth, pointer.PointerHeight, pointer.PointerPitch, bytes = pointer.PointerShape.Length, sha256 = key });
        };
        window.Loaded += async (_, _) =>
        {
            Task? running = null;
            try
            {
                running = window.ConnectVideo();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (Volatile.Read(ref frames) == 0)
                {
                    if (running.IsCompleted) throw new IOException("Connection ended before pointer probe received video");
                    await Task.Delay(20, deadline.Token);
                }
                await Task.Delay(TimeSpan.FromSeconds(60));
                if (running.IsCompleted) throw new IOException("Connection ended during pointer observation");
                window.StopConnection(); await running;
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { frames, renderer = gpu ? "gpu" : "cpu", shapes = shapes.Values, videoSaved = false, physicalVisualAcceptance = "MANUAL" }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"PASS real-host pointer probe: frames={frames}, shapes={shapes.Count}; visual results require observation");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                window.StopConnection();
                if (running is not null) await running;
                window.pointerFrameForVerification = null;
                window.Close(); app.Shutdown(Environment.ExitCode);
            }
        };
        app.Run(window);
    }
}
