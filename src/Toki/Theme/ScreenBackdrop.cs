using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toki.Theme;

/// <summary>
/// REAL GLASS FOR THE NOTCH, BY PHOTOGRAPHING WHAT IS BEHIND IT.
///
/// The notch cannot use the OS backdrop. `WindowBackdrop` records why: it is a per-pixel LAYERED
/// window (AllowsTransparency=True), which bypasses the frame surface DWM paints into, and it is
/// never activated, and Microsoft documents that acrylic falls back to a flat colour on a
/// deactivated window. Real DWM acrylic on the Library was also tried and reverted, because
/// translucency lets the desktop tint a reading surface and a light theme turns grey.
///
/// So this is the remaining route: capture the screen region the card is about to cover, shrink it,
/// blur it, and paint it as the card's own backdrop. Not a live material - a photograph, refreshed
/// when it is worth refreshing.
///
/// GHOST MODE HELPS HERE. `WDA_EXCLUDEFROMCAPTURE` keeps the notch out of every screen capture,
/// including this one, so the window cannot photograph itself into its own background. When ghost
/// mode is OFF (it is, on the owner's machine) that protection is gone, which is why every capture
/// happens while the window is HIDDEN or before it is shown. Photographing a visible notch with
/// ghost off produces a feedback smear, and it looks exactly like a rendering bug.
///
/// COST. One BitBlt of a ~740x320 region, downscaled 8x in the same call by the GPU-backed blitter,
/// is a fraction of a millisecond; the blur then runs over ~92x40 pixels. That is affordable ON A
/// STATE CHANGE. It is NOT affordable per frame, and nothing here should ever be called from a
/// rendering callback.
/// </summary>
public static class ScreenBackdrop
{
    const int Downscale = 8;
    const int BlurPasses = 3;
    const int SrcCopy = 0x00CC0020;
    const int CaptureBlt = 0x40000000;   // include layered windows that are not excluded
    const int Halftone = 4;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int xd, int yd, int wd, int hd,
        IntPtr src, int xs, int ys, int ws, int hs, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines,
        byte[]? bits, ref BitmapInfo bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public int bmiColors;
    }

    /// <summary>
    /// Photograph the screen under <paramref name="area"/> (DEVICE pixels), shrunk and blurred, or
    /// null if anything about the capture failed. Never throws: a missing backdrop must degrade to
    /// the painted material, never to a crash on a window that is mid-reveal.
    /// </summary>
    public static ImageBrush? Capture(Int32Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return null;

        var smallW = Math.Max(1, area.Width / Downscale);
        var smallH = Math.Max(1, area.Height / Downscale);

        var screen = IntPtr.Zero;
        var mem = IntPtr.Zero;
        var bmp = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            mem = CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero) return null;
            bmp = CreateCompatibleBitmap(screen, smallW, smallH);
            if (bmp == IntPtr.Zero) return null;
            old = SelectObject(mem, bmp);

            // HALFTONE is the only stretch mode that averages rather than drops pixels. Point
            // sampling a 740px card down to 92px would alias hard edges into speckle, and the blur
            // below cannot undo speckle it was handed as input.
            SetStretchBltMode(mem, Halftone);
            if (!StretchBlt(mem, 0, 0, smallW, smallH,
                            screen, area.X, area.Y, area.Width, area.Height, SrcCopy | CaptureBlt))
                return null;

            var header = new BitmapInfo();
            header.bmiHeader.biSize = Marshal.SizeOf<BitmapInfoHeader>();
            header.bmiHeader.biWidth = smallW;
            header.bmiHeader.biHeight = -smallH;      // negative: top-down rows, as WPF wants them
            header.bmiHeader.biPlanes = 1;
            header.bmiHeader.biBitCount = 32;
            header.bmiHeader.biCompression = 0;

            var stride = smallW * 4;
            var pixels = new byte[stride * smallH];
            if (GetDIBits(mem, bmp, 0, (uint)smallH, pixels, ref header, 0) == 0) return null;

            BoxBlur(pixels, smallW, smallH, BlurPasses);

            var src = BitmapSource.Create(smallW, smallH, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            src.Freeze();
            // Fill, not Uniform: the brush paints a card whose aspect it already matches, and a
            // Uniform stretch would letterbox the material against its own rim.
            var brush = new ImageBrush(src) { Stretch = Stretch.Fill, Opacity = 1.0 };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            App.Log($"backdrop capture failed (falling back to the painted material): {ex.Message}");
            return null;
        }
        finally
        {
            if (mem != IntPtr.Zero && old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Separable box blur, in place, ignoring alpha. Three passes of a box approximate a Gaussian
    /// closely enough that nobody can tell at this size, and each pass is O(n) rather than O(n*r),
    /// which is why the radius can be generous without costing anything.
    ///
    /// The buffer is already ~92x40, so the whole thing is a few thousand operations.
    /// </summary>
    static void BoxBlur(byte[] px, int w, int h, int passes)
    {
        if (w < 3 || h < 3) return;
        var radius = Math.Max(1, w / 24);
        var tmp = new byte[px.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            BlurRows(px, tmp, w, h, radius);
            BlurCols(tmp, px, w, h, radius);
        }
    }

    static void BlurRows(byte[] src, byte[] dst, int w, int h, int r)
    {
        for (var y = 0; y < h; y++)
        {
            var row = y * w * 4;
            for (var c = 0; c < 3; c++)
            {
                int sum = 0, n = 0;
                for (var x = -r; x <= r; x++) { sum += src[row + Clamp(x, w) * 4 + c]; n++; }
                for (var x = 0; x < w; x++)
                {
                    dst[row + x * 4 + c] = (byte)(sum / n);
                    sum -= src[row + Clamp(x - r, w) * 4 + c];
                    sum += src[row + Clamp(x + r + 1, w) * 4 + c];
                }
            }
            for (var x = 0; x < w; x++) dst[row + x * 4 + 3] = 255;
        }
    }

    static void BlurCols(byte[] src, byte[] dst, int w, int h, int r)
    {
        for (var x = 0; x < w; x++)
        {
            for (var c = 0; c < 3; c++)
            {
                int sum = 0, n = 0;
                for (var y = -r; y <= r; y++) { sum += src[Clamp(y, h) * w * 4 + x * 4 + c]; n++; }
                for (var y = 0; y < h; y++)
                {
                    dst[y * w * 4 + x * 4 + c] = (byte)(sum / n);
                    sum -= src[Clamp(y - r, h) * w * 4 + x * 4 + c];
                    sum += src[Clamp(y + r + 1, h) * w * 4 + x * 4 + c];
                }
            }
            for (var y = 0; y < h; y++) dst[y * w * 4 + x * 4 + 3] = 255;
        }
    }

    /// <summary>Clamp to the edge, so the blur does not darken the border by averaging in nothing.</summary>
    static int Clamp(int i, int n) => i < 0 ? 0 : i >= n ? n - 1 : i;
}
