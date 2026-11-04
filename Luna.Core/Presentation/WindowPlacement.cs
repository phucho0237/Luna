namespace Luna.Core.Presentation;

/// <summary>A rectangle in physical pixels. Only integers, so the default ToString holds no data worth hiding.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// Pure placement math for the main window: scales a size given in device-independent pixels to physical pixels,
/// clamps it to the display work area and centers it there. It has no UI types so it can be tested without a window.
/// </summary>
public static class WindowPlacement
{
    /// <summary>The DPI that corresponds to a scale of 100 percent.</summary>
    public const int BaseDpi = 96;

    /// <summary>
    /// Scales the desired size (device-independent pixels) by <paramref name="dpi"/>, clamps it to the work area minus
    /// <paramref name="marginPixels"/> on every side, and centers it inside the work area using the work area origin
    /// (a display left of or above the primary one has a negative origin). The result never leaves the work area.
    /// A non-positive dpi counts as 96, a negative margin as 0. When the work area is too small for the margin, the
    /// margin is dropped and the whole work area is the limit.
    /// </summary>
    public static PixelRect Compute(
        int desiredWidthDips,
        int desiredHeightDips,
        int dpi,
        int workAreaX,
        int workAreaY,
        int workAreaWidth,
        int workAreaHeight,
        int marginPixels)
    {
        int effectiveDpi = dpi > 0 ? dpi : BaseDpi;
        int margin = Math.Max(marginPixels, 0);

        int width = FitAxis(desiredWidthDips, effectiveDpi, workAreaWidth, margin);
        int height = FitAxis(desiredHeightDips, effectiveDpi, workAreaHeight, margin);

        // width <= workAreaWidth and height <= workAreaHeight, so these offsets are never negative.
        int x = workAreaX + (Math.Max(workAreaWidth, 0) - width) / 2;
        int y = workAreaY + (Math.Max(workAreaHeight, 0) - height) / 2;
        return new PixelRect(x, y, width, height);
    }

    private static int FitAxis(int desiredDips, int dpi, int workAreaLength, int margin)
    {
        int length = Math.Max(workAreaLength, 0);
        long available = (long)length - 2L * margin;
        if (available < 1)
        {
            available = length;
        }

        long pixels = (long)Math.Round((double)desiredDips * dpi / BaseDpi, MidpointRounding.AwayFromZero);
        long lowest = Math.Min(1L, available);
        return (int)Math.Clamp(pixels, lowest, available);
    }
}
