namespace RelaxDesk.Mobile.Media;

public sealed class NetworkTrafficRate
{
    (long Received, long Sent, long Milliseconds)? previous;

    public (double DownloadKbps, double UploadKbps)? Sample(long received, long sent, long milliseconds)
    {
        if (received < 0 || sent < 0)
        {
            Reset();
            return null;
        }

        var last = previous;
        previous = (received, sent, milliseconds);
        if (last is not { } baseline || received < baseline.Received || sent < baseline.Sent ||
            milliseconds <= baseline.Milliseconds)
            return null;

        double elapsed = milliseconds - baseline.Milliseconds;
        // Bytes per millisecond multiplied by eight gives decimal kilobits per second.
        return ((received - baseline.Received) * 8.0 / elapsed, (sent - baseline.Sent) * 8.0 / elapsed);
    }

    public void Reset() => previous = null;

    public static string Format(double kilobitsPerSecond, bool inBytes)
    {
        double rate = inBytes ? kilobitsPerSecond / 8 : kilobitsPerSecond;
        if (rate >= 1000)
            return $"{rate / 1000:0.##} {(inBytes ? "MB/s" : "Mb/s")}";
        return $"{rate:0.#} {(inBytes ? "KB/s" : "kb/s")}";
    }
}
