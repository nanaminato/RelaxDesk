namespace RelaxDesk.Mobile.Media;

/// <summary>Tracks button ownership across single/multiple pointer transitions.</summary>
public sealed class TouchGestureState
{
    public const int LongPressMilliseconds = 450;
    const int SecondTapMilliseconds = 250;
    (float X, float Y) firstStart, secondStart;
    long? secondTapStart;

    public bool DirectButtonDown { get; private set; }
    public bool MultiplePointers { get; private set; }
    public bool Pinching { get; private set; }
    public void BeginDirect() => DirectButtonDown = true;
    public bool EnterMultiplePointers()
    {
        MultiplePointers = true;
        return ReleaseDirect();
    }

    public void BeginPair(bool rightTapEligible, long eventTime,
        (float X, float Y) first, (float X, float Y) second)
    {
        firstStart = first;
        secondStart = second;
        secondTapStart = rightTapEligible ? eventTime : null;
        Pinching = !rightTapEligible;
    }

    public bool MovePair((float X, float Y) first, (float X, float Y) second, float slop)
    {
        if (secondTapStart.HasValue && PairMoved(first, second, slop))
        {
            secondTapStart = null;
            Pinching = true;
        }
        return Pinching;
    }

    public bool EndPair(bool secondLifted, long eventTime,
        (float X, float Y) first, (float X, float Y) second, float slop)
    {
        bool rightClick = secondLifted && secondTapStart is long start &&
            eventTime - start <= SecondTapMilliseconds && !PairMoved(first, second, slop);
        CancelPair();
        return rightClick;
    }

    public void CancelPair()
    {
        secondTapStart = null;
        Pinching = false;
    }

    bool PairMoved((float X, float Y) first, (float X, float Y) second, float slop) =>
        Moved(firstStart, first, slop) || Moved(secondStart, second, slop);

    public static bool Moved((float X, float Y) start, (float X, float Y) end, float slop)
    {
        float dx = end.X - start.X, dy = end.Y - start.Y;
        return dx * dx + dy * dy > slop * slop;
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
        CancelPair();
    }
}
