using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

// Keeps Android's configured/presented acknowledgements on the same display
// state machine as the Windows Viewer. Receiving bytes does not imply rendering.
public sealed class AndroidDisplaySession
{
    readonly DisplayViewerSession state;
    readonly Action<Packet> send;

    public AndroidDisplaySession(DisplaySessionInfo initial, bool topologyAware, Action<Packet> send)
    {
        state = new(initial, topologyAware);
        this.send = send;
        Configure(initial);
    }

    public long InputGeneration => state.Ready ? state.Generation : 0;
    public bool AcceptFrame(MediaFrame frame) => state.AcceptFrame(frame);

    public void Receive(Packet packet)
    {
        var next = packet.DisplayState ?? throw new InvalidDataException("Missing display state");
        if (packet.Kind == "display-topology-v2") state.ReceiveTopology(next);
        else if (packet.Kind == "display-state-v2") state.Receive(next);
        else throw new InvalidDataException("Unknown display update");
        Configure(next);
    }

    void Configure(DisplaySessionInfo next)
    {
        if (!next.Unavailable)
            send(new("display-configured-v2", DisplayGeneration: next.Generation));
    }

    public void Presented(long generation)
    {
        if (state.Presented(generation))
            send(new("display-ready-v2", DisplayGeneration: generation));
    }
}
