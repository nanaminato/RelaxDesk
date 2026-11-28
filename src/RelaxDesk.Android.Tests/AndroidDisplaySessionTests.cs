using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;

internal static class AndroidDisplaySessionTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool pass) { if (!pass) throw new InvalidOperationException("Android display lifecycle"); checks++; }
        List<Packet> outgoing = [];
        var initial = new DisplaySessionInfo(
            [new("one", new(0, 0, 640, 360), 0, 96, 96, true, false, new string('A', 64))],
            ["one"], "one", 1);
        var display = new AndroidDisplaySession(initial, true, outgoing.Add);
        Check(outgoing.SequenceEqual([new Packet("display-configured-v2", DisplayGeneration: 1)]));
        Check(display.InputGeneration == 0);
        display.Presented(99);
        Check(display.InputGeneration == 0 && outgoing.Count == 1);
        display.Presented(1);
        display.Presented(1);
        Check(display.InputGeneration == 1 && outgoing.Count == 2 && outgoing[1] == new Packet("display-ready-v2", DisplayGeneration: 1));

        var unavailable = initial with { Generation = 2, Selected = "", Unavailable = true };
        display.Receive(new("display-topology-v2", DisplayState: unavailable));
        Check(display.InputGeneration == 0 && outgoing.Count == 2);
        display.Presented(1);
        display.Presented(2);
        Check(display.InputGeneration == 0 && outgoing.Count == 2);
        var restored = initial with { Generation = 3 };
        display.Receive(new("display-topology-v2", DisplayState: restored));
        Check(outgoing[^1] == new Packet("display-configured-v2", DisplayGeneration: 3) && display.InputGeneration == 0);
        display.Presented(3);
        Check(display.InputGeneration == 3 && outgoing[^1] == new Packet("display-ready-v2", DisplayGeneration: 3));
        try { display.Receive(new("display-topology-v2", DisplayState: initial)); throw new Exception("Replayed topology accepted"); }
        catch (InvalidDataException) { checks++; }
        return checks;
    }
}
