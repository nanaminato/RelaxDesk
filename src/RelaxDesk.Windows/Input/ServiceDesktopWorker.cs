using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class ServiceDesktopWorker
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct CursorPoint { public int X,Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetPhysicalCursorPos")]
    static extern bool GetCursorPos(out CursorPoint point);
    internal static void EnsureCaptureAllowed(WindowsSessionSnapshot snapshot)
    {
        snapshot.EnsureAllowed();
        if (snapshot.InputDesktop == "Winlogon" && snapshot.Locked != false)
            throw new WindowsSessionUnavailableException("secure-desktop-lock-state-unverified");
    }

    // Both channels have already authenticated the coordinator's SID/PID/session.
    // Input and GDI capture share one dedicated OS thread, never a UI/CLR pool thread.
    public static async Task Run(Stream inputChannel, Stream desktopChannel, Guid grant,
        Func<WindowsDisplaySelection> display, CancellationToken cancellation, Action<string>? diagnostic=null)
    {
        WindowsInputPrivilege.EnsureSystemInputProcess();
        WindowsSessionAccess.EnsureAllowed();
        using var thread = new InputDesktopThread();
        WindowsDisplaySelection selection = display();
        WindowsInputSession? input = null;
        var state = new ServiceDesktopEpoch();
        ServiceSecureCapture? secureCapture=null;
        string? lastInputGate=null;
        void InputGate(string value)
        {
            if(lastInputGate==value)return;
            lastInputGate=value;diagnostic?.Invoke($"service_input_gate={value} desktop_epoch={state.Epoch}");
        }
        thread.Invoke(() =>
        {
            input = new WindowsInputSession(() => selection);
            Observe();
        });
        WindowsSessionSnapshot Observe()
        {
            var before=WindowsSessionAccess.Read();
            if(before.InputDesktop!=before.ThreadDesktop){secureCapture?.Dispose();secureCapture=null;}
            WindowsDesktopSwitch.SyncThreadToInputDesktop();
            var snapshot = WindowsSessionAccess.Read();
            EnsureCaptureAllowed(snapshot);
            if (state.Observe(snapshot.InputDesktop!))
            { secureCapture?.Dispose();secureCapture=null;input!.ReleaseAll(); }
            return snapshot;
        }
        void Release() => thread.Invoke(() => input!.ReleaseAll());
        void Select(string device)
        {
            if (selection.Device == device) return;
            input!.ReleaseAll();secureCapture?.Dispose();secureCapture=null;
            selection = selection.Select(device);
            selection.EnsureCurrent();
        }
        void Inject(RemoteInput command, long epoch) => thread.Invoke(() =>
        {
            WindowsSessionSnapshot snapshot;
            try { snapshot = Observe(); }
            catch (WindowsSessionUnavailableException error) when (error.Reason == "input-desktop-unavailable")
            { state.BeginTransition(); return; }
            if (state.Secure && !WindowsUacDesktop.ConsentIsForeground()) {InputGate("consent-not-foreground");return;}
            if (!state.Accept(epoch)) {InputGate("stale-epoch");return;} // Never replay an event from the old visible desktop.
            WindowsDesktopSwitch.ExpectedInputDesktop = snapshot.InputDesktop;
            WindowsDesktopSwitch.ExpectedInputAuthorization = () =>
            {
                var current = WindowsSessionAccess.Read(); EnsureCaptureAllowed(current);
                if (current.InputDesktop == "Winlogon" && !WindowsUacDesktop.ConsentIsForeground()) throw new InputDesktopChangedException();
            };
            try { InputGate(input!.TryInject(command)?"accepted":$"temporarily-blocked win32={input.LastRejectedWin32Error}"); }
            catch (InputDesktopChangedException) { } // A racing switch is observed on the next capture/input.
            finally { WindowsDesktopSwitch.ExpectedInputDesktop = null; WindowsDesktopSwitch.ExpectedInputAuthorization = null; }
        });
        ServiceDesktopFrame Capture(long sequence, string device)
        {
            ServiceDesktopFrame? frame = null;
            thread.Invoke(() =>
            {
                var snapshot = Observe();
                Select(device);
                if (!state.Secure) { frame = new(sequence, state.Epoch, ServiceDesktopKind.Default, 0, 0, []); return; }
                if (!WindowsUacDesktop.ConsentIsForeground()) { frame = new(sequence,state.Epoch,ServiceDesktopKind.Transition,0,0,[]); return; }
                long epoch = state.Epoch;
                secureCapture??=new ServiceSecureCapture(selection,diagnostic);
                byte[] pixels;
                try{pixels=secureCapture.Capture();}
                catch(SecureFramePendingException){frame=new(sequence,state.Epoch,ServiceDesktopKind.Transition,0,0,[]);return;}
                var after = Observe();
                if (!GetCursorPos(out var pointer)) throw new IOException("Secure desktop cursor position unavailable");
                if (after.InputDesktop != snapshot.InputDesktop || state.Epoch != epoch || !WindowsUacDesktop.ConsentIsForeground())
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(pixels);
                    frame = new(sequence,state.Epoch,ServiceDesktopKind.Transition,0,0,[]);
                }
                else frame = new(sequence,epoch,ServiceDesktopKind.Secure,selection.Bounds.Width,selection.Bounds.Height,pixels,pointer.X,pointer.Y);
            });
            return frame!;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(2));
                await Wire.Write(inputChannel, new(AdminInputChannel.Ready, Origin: grant.ToString("N")), ready.Token);
                await Wire.Write(desktopChannel, new(ServiceDesktopProtocol.Ready, Origin: grant.ToString("N")), ready.Token);
            }
            Task commands = AdminInputChannel.Run(inputChannel, grant,
                _ => throw new InvalidDataException("Service input requires a desktop epoch"), Release, lifetime.Token,
                device => thread.Invoke(() => { Observe(); Select(device); }), Inject);
            Task capture = CaptureLoop();
            long unavailableSince = 0;
            Task watch = WindowsSessionAccess.Watch(lifetime.Token, authorize: snapshot =>
            {
                if (snapshot.Denial == "input-desktop-unavailable")
                {
                    unavailableSince = unavailableSince == 0 ? Environment.TickCount64 : unavailableSince;
                    if (Environment.TickCount64 - unavailableSince < 2000) return;
                }
                else unavailableSince = 0;
                snapshot.EnsureAllowed();
            });
            Task first = await Task.WhenAny(commands, capture, watch);
            try { await first; }
            finally
            {
                await lifetime.CancelAsync();
                desktopChannel.Dispose(); inputChannel.Dispose();
                foreach (Task task in new[] { commands, capture, watch })
                    if (task != first) try { await task; } catch (Exception) { }
            }
        }
        finally { thread.Invoke(() => {secureCapture?.Dispose();input!.Dispose();}); }

        async Task CaptureLoop()
        {
            long sequence = 0;
            while (true)
            {
                var request = await Wire.Read(desktopChannel, lifetime.Token, 2048);
                if (!AdminInputChannel.ValidDisplay(request.Text) || sequence == long.MaxValue ||
                    request != new Packet(ServiceDesktopProtocol.Request, sequence + 1, Text: request.Text, Origin: grant.ToString("N")))
                    throw new InvalidDataException("Invalid secure desktop capture binding or sequence");
                ServiceDesktopFrame frame;
                try { frame = Capture(++sequence, request.Text!); }
                catch (WindowsSessionUnavailableException error) when (error.Reason == "input-desktop-unavailable")
                {
                    long epoch = 0;
                    thread.Invoke(() => { state.BeginTransition(); epoch = state.Epoch; });
                    frame = new(sequence, epoch, ServiceDesktopKind.Transition, 0, 0, []);
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try { await ServiceDesktopProtocol.Write(desktopChannel, frame, deadline.Token); }
                finally { if(frame.Pixels.Length != 0) System.Security.Cryptography.CryptographicOperations.ZeroMemory(frame.Pixels); }
            }
        }
    }
}
