namespace RelaxDesk.Windows;

public static class PointerCompositor
{
    // Desktop Duplication positions are the shape's top-left, not its hotspot.
    // Monochrome height includes AND and XOR masks; masked-color alpha selects copy versus XOR.
    public static void Apply(byte[] bgra, int width, int height, CaptureMetadata pointer)
    {
        if (!pointer.PointerVisible || pointer.PointerShape.Length == 0) return;
        Validate(pointer);
        int w = checked((int)pointer.PointerWidth), h = checked((int)pointer.PointerHeight), pitch = checked((int)pointer.PointerPitch);
        bool mono = pointer.PointerType == 1;
        if (mono) h /= 2;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            long targetX = (long)pointer.PointerX + x, targetY = (long)pointer.PointerY + y;
            if (targetX < 0 || targetX >= width || targetY < 0 || targetY >= height) continue;
            int target = checked(((int)targetY * width + (int)targetX) * 4);
            if (mono)
            {
                int bit = 0x80 >> (x % 8);
                int and = (pointer.PointerShape[y * pitch + x / 8] & bit) != 0 ? 255 : 0;
                int xor = (pointer.PointerShape[(y + h) * pitch + x / 8] & bit) != 0 ? 255 : 0;
                if (and == 255 && xor == 255)
                {
                    InvertLuminance(bgra, target);
                    continue;
                }
                for (int channel = 0; channel < 3; channel++) bgra[target + channel] = (byte)((bgra[target + channel] & and) ^ xor);
            }
            else
            {
                int source = y * pitch + x * 4, alpha = pointer.PointerShape[source + 3];
                if (pointer.PointerType == 4 && alpha is not (0 or 255)) throw new InvalidDataException("Invalid masked pointer alpha");
                if (pointer.PointerType == 4 && alpha == 255 && (pointer.PointerShape[source] & pointer.PointerShape[source + 1] & pointer.PointerShape[source + 2]) == 255)
                {
                    InvertLuminance(bgra, target);
                    continue;
                }
                for (int channel = 0; channel < 3; channel++)
                    bgra[target + channel] = pointer.PointerType == 4
                        ? (alpha == 0 ? pointer.PointerShape[source + channel] : (byte)(bgra[target + channel] ^ pointer.PointerShape[source + channel]))
                        : (byte)((pointer.PointerShape[source + channel] * alpha + bgra[target + channel] * (255 - alpha) + 127) / 255);
            }
        }
    }
    // Keep reverse-video cursors neutral over ClearType and other colored edges.
    static void InvertLuminance(byte[] bgra, int target)
    {
        byte value = (byte)(255 - ((54 * bgra[target + 2] + 183 * bgra[target + 1] + 19 * bgra[target] + 128) >> 8));
        bgra[target] = bgra[target + 1] = bgra[target + 2] = value;
    }
    public static void Validate(CaptureMetadata pointer)
    {
        if (pointer.PointerShape.Length == 0) return;
        long w = pointer.PointerWidth, h = pointer.PointerHeight, pitch = pointer.PointerPitch;
        bool mono = pointer.PointerType == 1;
        if (w <= 0 || w > 4096 || h <= 0 || h > 8192 || pitch <= 0 || pitch * h > pointer.PointerShape.Length ||
            (mono ? pitch < (w + 7) / 8 || h % 2 != 0 : pitch < w * 4) || pointer.PointerType is not (1 or 2 or 4))
            throw new InvalidDataException("Invalid pointer bitmap");
        if (pointer.PointerType == 4)
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                if (pointer.PointerShape[y * (int)pitch + x * 4 + 3] is not (0 or 255)) throw new InvalidDataException("Invalid masked pointer alpha");
    }
}
