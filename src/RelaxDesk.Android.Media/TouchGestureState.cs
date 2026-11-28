namespace RelaxDesk.Mobile.Media;

/// <summary>Tracks button ownership across single/multiple pointer transitions.</summary>
public sealed class TouchGestureState
{
    public bool DirectButtonDown { get; private set; }
    public bool MultiplePointers { get; private set; }
    public void BeginDirect() => DirectButtonDown = true;
    public bool EnterMultiplePointers()
    {
        MultiplePointers = true;
        return ReleaseDirect();
    }
    public bool ReleaseDirect()
    {
        bool release = DirectButtonDown;
        DirectButtonDown = false;
        return release;
    }
    public void Reset()
    {
        DirectButtonDown = false;
        MultiplePointers = false;
    }
}
