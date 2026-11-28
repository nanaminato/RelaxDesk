using Android.Content;
using Android.Graphics;
using Android.Views;
using RelaxDesk.Mobile.Media;

namespace RelaxDesk.Mobile;

/// <summary>
/// Independent native Android cursor overlay decoupled from the SurfaceView video pipeline.
/// Local pointer prediction updates immediately without waiting for remote video frames.
/// </summary>
internal sealed class RemoteCursorView : View
{
    readonly Paint fallbackFill = new(PaintFlags.AntiAlias) { Color = Color.White };
    readonly Paint fallbackStroke = new(PaintFlags.AntiAlias) { Color = Color.Black, StrokeWidth = 2.5f };
    readonly Paint bitmapPaint = new(PaintFlags.FilterBitmap);
    readonly global::Android.Graphics.Path arrowPath = new();

    Bitmap? remoteBitmap;
    long currentShapeVersion = -1;
    int hotspotX;
    int hotspotY;
    float viewX;
    float viewY;
    bool cursorVisible = true;
    bool hasPosition;

    public RemoteCursorView(Context context) : base(context)
    {
        fallbackStroke.SetStyle(Paint.Style.Stroke);
        fallbackFill.SetStyle(Paint.Style.Fill);
        Clickable = false;
        Focusable = false;
        BuildDefaultArrow();
    }

    void BuildDefaultArrow()
    {
        arrowPath.Reset();
        arrowPath.MoveTo(0, 0);
        arrowPath.LineTo(0, 26);
        arrowPath.LineTo(7, 20);
        arrowPath.LineTo(12, 30);
        arrowPath.LineTo(16, 28);
        arrowPath.LineTo(11, 18);
        arrowPath.LineTo(20, 18);
        arrowPath.Close();
    }

    public void UpdateLocalPosition(float x, float y)
    {
        viewX = x;
        viewY = y;
        hasPosition = true;
        Invalidate();
    }

    public void ApplyRemoteState(RemotePointerState state)
    {
        cursorVisible = state.Visible || !state.UsesHostPointer;
        hotspotX = state.HotspotX;
        hotspotY = state.HotspotY;

        if (state.ShapeVersion != currentShapeVersion)
        {
            currentShapeVersion = state.ShapeVersion;
            RebuildBitmap(state);
        }
        Invalidate();
    }

    void RebuildBitmap(RemotePointerState state)
    {
        remoteBitmap?.Recycle();
        remoteBitmap?.Dispose();
        remoteBitmap = null;

        if (state.Shape.IsEmpty || state.Width <= 0 || state.Height <= 0 || state.Pitch <= 0)
            return;

        if (state.Type == RemotePointerType.Color && state.Pitch >= state.Width * 4)
        {
            var span = state.Shape.Span;
            int needed = (state.Height - 1) * state.Pitch + state.Width * 4;
            if (span.Length < needed) return;

            int[] argb = new int[state.Width * state.Height];
            for (int row = 0; row < state.Height; row++)
            {
                int srcRow = row * state.Pitch;
                int dstRow = row * state.Width;
                for (int col = 0; col < state.Width; col++)
                {
                    int idx = srcRow + col * 4;
                    byte b = span[idx];
                    byte g = span[idx + 1];
                    byte r = span[idx + 2];
                    byte a = span[idx + 3];
                    argb[dstRow + col] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
            remoteBitmap = Bitmap.CreateBitmap(argb, state.Width, state.Height, Bitmap.Config.Argb8888!);
        }
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (!cursorVisible || !hasPosition) return;

        if (remoteBitmap is { IsRecycled: false } bmp)
        {
            canvas.DrawBitmap(bmp, viewX - hotspotX, viewY - hotspotY, bitmapPaint);
        }
        else
        {
            canvas.Save();
            canvas.Translate(viewX, viewY);
            canvas.DrawPath(arrowPath, fallbackFill);
            canvas.DrawPath(arrowPath, fallbackStroke);
            canvas.Restore();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            remoteBitmap?.Recycle();
            remoteBitmap?.Dispose();
            remoteBitmap = null;
            fallbackFill.Dispose();
            fallbackStroke.Dispose();
            bitmapPaint.Dispose();
            arrowPath.Dispose();
        }
        base.Dispose(disposing);
    }
}
