namespace RelaxDesk.Mobile.Media;

public enum ViewportDisplayMode { Fit, Fill, NativePixels }

/// <summary>
/// Maps coordinates between Android view space (with zoom and pan) and
/// RelaxDesk normalized remote desktop space (0..65535), supporting both
/// direct touch and relative touchpad modes.
/// </summary>
public sealed class ViewportMapper
{
    public const float MinZoom = 1.0f;
    public const float MaxZoom = 4.0f;

    float viewWidth;
    float viewHeight;
    int videoWidth;
    int videoHeight;
    float zoom = 1.0f;
    float panX;
    float panY;
    bool locallyOwned;

    // Local predicted normalized pointer position (0..65535) for touchpad & cursor overlay.
    double cursorNormX = 32767.5;
    double cursorNormY = 32767.5;

    public ViewportDisplayMode DisplayMode { get; private set; }

    public void SetDisplayMode(ViewportDisplayMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        DisplayMode = mode;
        ResetTransform();
    }

    float BaseScale => DisplayMode switch
    {
        ViewportDisplayMode.Fill => Math.Max(viewWidth / videoWidth, viewHeight / videoHeight),
        ViewportDisplayMode.NativePixels => 1f,
        _ => Math.Min(viewWidth / videoWidth, viewHeight / videoHeight)
    };

    public float Zoom => zoom;
    public float PanX => panX;
    public float PanY => panY;
    public int VideoWidth => videoWidth;
    public int VideoHeight => videoHeight;
    public int CursorNormalizedX => (int)Math.Clamp(Math.Round(cursorNormX), 0, 65535);
    public int CursorNormalizedY => (int)Math.Clamp(Math.Round(cursorNormY), 0, 65535);

    public void SetViewSize(float width, float height)
    {
        if (width <= 0 || height <= 0 || !float.IsFinite(width) || !float.IsFinite(height)) return;
        viewWidth = width;
        viewHeight = height;
        ClampPan();
    }

    public void SetVideoSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (videoWidth != width || videoHeight != height) ResetPointer();
        videoWidth = width;
        videoHeight = height;
        ClampPan();
    }

    public void ResetTransform()
    {
        zoom = 1.0f;
        panX = 0f;
        panY = 0f;
    }

    public void ApplyScaleAndPan(float scaleFactor, float focusX, float focusY, float deltaX, float deltaY)
    {
        if (!float.IsFinite(scaleFactor) || scaleFactor <= 0) return;
        float oldZoom = zoom;
        float newZoom = Math.Clamp(oldZoom * scaleFactor, MinZoom, MaxZoom);

        if (Math.Abs(newZoom - oldZoom) > 1e-4f && viewWidth > 0 && viewHeight > 0)
        {
            float cx = viewWidth * 0.5f;
            float cy = viewHeight * 0.5f;
            float ratio = newZoom / oldZoom;
            panX = (focusX - cx) - ((focusX - cx) - panX) * ratio;
            panY = (focusY - cy) - ((focusY - cy) - panY) * ratio;
        }

        zoom = newZoom;
        if (float.IsFinite(deltaX)) panX += deltaX;
        if (float.IsFinite(deltaY)) panY += deltaY;
        ClampPan();
    }

    public bool TryGetFittedRect(out float left, out float top, out float width, out float height)
    {
        left = top = width = height = 0;
        if (viewWidth <= 0 || viewHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
            return false;

        float baseScale = BaseScale;
        width = videoWidth * baseScale * zoom;
        height = videoHeight * baseScale * zoom;
        left = (viewWidth - width) * 0.5f + panX;
        top = (viewHeight - height) * 0.5f + panY;
        return true;
    }

    /// <summary>
    /// Direct touch / external mouse mapping: converts view coordinates to 0..65535.
    /// </summary>
    public bool TryViewToNormalized(float x, float y, bool clampOutside, out int normalizedX, out int normalizedY)
    {
        normalizedX = normalizedY = 0;
        if (!TryGetFittedRect(out float left, out float top, out float width, out float height) || width <= 0 || height <= 0)
            return false;

        if (!clampOutside && (x < left || x > left + width || y < top || y > top + height))
            return false;

        double nx = (x - left) / Math.Max(1.0, width - 1.0);
        double ny = (y - top) / Math.Max(1.0, height - 1.0);
        cursorNormX = Math.Clamp(nx, 0.0, 1.0) * 65535.0;
        cursorNormY = Math.Clamp(ny, 0.0, 1.0) * 65535.0;
        normalizedX = CursorNormalizedX;
        normalizedY = CursorNormalizedY;
        locallyOwned = true;
        return true;
    }

    /// <summary>
    /// Relative touchpad mapping: applies finger delta in view pixels to the
    /// current predicted cursor position.
    /// </summary>
    public bool TryApplyRelativeDelta(float dx, float dy, float sensitivity, out int normalizedX, out int normalizedY)
    {
        normalizedX = CursorNormalizedX;
        normalizedY = CursorNormalizedY;
        if (!TryGetFittedRect(out _, out _, out float width, out float height) || width <= 0 || height <= 0)
            return false;
        if (!float.IsFinite(dx) || !float.IsFinite(dy) || !float.IsFinite(sensitivity) || sensitivity <= 0)
            return false;

        cursorNormX = Math.Clamp(cursorNormX + (dx * sensitivity / width) * 65535.0, 0.0, 65535.0);
        cursorNormY = Math.Clamp(cursorNormY + (dy * sensitivity / height) * 65535.0, 0.0, 65535.0);
        normalizedX = CursorNormalizedX;
        normalizedY = CursorNormalizedY;
        locallyOwned = true;
        return true;
    }

    /// <summary>
    /// Reconciles local cursor with an authoritative remote pointer position
    /// (in video pixels) when the user is not actively moving locally.
    /// </summary>
    public void SyncRemotePointer(int videoX, int videoY)
    {
        if (videoWidth <= 1 || videoHeight <= 1) return;
        cursorNormX = Math.Clamp((double)videoX / (videoWidth - 1), 0.0, 1.0) * 65535.0;
        cursorNormY = Math.Clamp((double)videoY / (videoHeight - 1), 0.0, 1.0) * 65535.0;
    }

    public void ResetPointer()
    {
        locallyOwned = false;
        cursorNormX = cursorNormY = 32767.5;
    }

    public bool TrySyncRemotePointer(RemotePointerState pointer)
    {
        // A frame can arrive after the finger lifts but describe an older
        // position. Local prediction owns position until a new display/session.
        if (locallyOwned || !pointer.Visible || pointer.Type is RemotePointerType.None or RemotePointerType.HostManaged ||
            pointer.X < 0 || pointer.Y < 0 || pointer.X >= videoWidth || pointer.Y >= videoHeight)
            return false;
        SyncRemotePointer(pointer.X, pointer.Y);
        return true;
    }

    /// <summary>
    /// Converts normalized remote coordinates (0..65535) to Android view coordinates
    /// for rendering the independent local cursor overlay.
    /// </summary>
    public bool TryNormalizedToView(int normalizedX, int normalizedY, out float viewX, out float viewY)
    {
        viewX = viewY = 0;
        if (!TryGetFittedRect(out float left, out float top, out float width, out float height))
            return false;

        viewX = left + (float)(Math.Clamp(normalizedX, 0, 65535) / 65535.0) * Math.Max(1f, width - 1f);
        viewY = top + (float)(Math.Clamp(normalizedY, 0, 65535) / 65535.0) * Math.Max(1f, height - 1f);
        return true;
    }

    void ClampPan()
    {
        if (viewWidth <= 0 || viewHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
        {
            panX = panY = 0;
            return;
        }
        float baseScale = BaseScale;
        float scaledW = videoWidth * baseScale * zoom;
        float scaledH = videoHeight * baseScale * zoom;
        float maxPanX = Math.Max(0f, (scaledW - viewWidth) * 0.5f);
        float maxPanY = Math.Max(0f, (scaledH - viewHeight) * 0.5f);
        panX = Math.Clamp(panX, -maxPanX, maxPanX);
        panY = Math.Clamp(panY, -maxPanY, maxPanY);
    }
}
