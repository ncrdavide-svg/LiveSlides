using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiveSlides.App;

/// <summary>
/// A bare Win32 window that shows one picture, full screen, with adjustable transparency: used
/// to cross-fade between our output and another program's slide show running underneath.
/// WPF cannot do this itself: it strips the layered style from its own windows unless
/// AllowsTransparency is on, and that means software rendering. This window is composed by the
/// GPU and exists only for the few hundred milliseconds of a fade.
/// </summary>
internal sealed class FadeOverlay : IDisposable
{
    private IntPtr _hwnd;

    /// <param name="image">Opaque picture of exactly the bounds' pixel size.</param>
    public FadeOverlay(ScreenBounds bounds, BitmapSource image, byte alpha)
    {
        var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = pixels.PixelWidth, height = pixels.PixelHeight;
        var buffer = new byte[width * height * 4];
        pixels.CopyPixels(buffer, width * 4, 0);

        _hwnd = CreateWindowEx(WsExLayered | WsExTopmost | WsExNoActivate | WsExToolWindow | WsExTransparent,
            "Static", "", WsPopup, bounds.X, bounds.Y, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) return;
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            var previous = SelectObject(memory, bitmap);
            var position = new POINT { X = bounds.X, Y = bounds.Y };
            var size = new SIZE { Cx = width, Cy = height };
            var origin = new POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = alpha };
            // No AC_SRC_ALPHA: the picture is opaque, only the window as a whole fades.
            UpdateLayeredWindow(_hwnd, screen, ref position, ref size, memory, ref origin, 0, ref blend, UlwAlpha);
            SelectObject(memory, previous);
            ShowWindow(_hwnd, SwShowNoActivate);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void SetAlpha(byte alpha)
    {
        if (_hwnd == IntPtr.Zero) return;
        var blend = new BLENDFUNCTION { SourceConstantAlpha = alpha };
        UpdateLayeredWindowAlpha(_hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, UlwAlpha);
    }

    public async Task FadeAsync(int from, int to, int durationMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var progress = durationMs <= 0 ? 1.0 : Math.Min(1.0, clock.ElapsedMilliseconds / (double)durationMs);
            SetAlpha((byte)Math.Round(from + (to - from) * progress));
            if (progress >= 1) return;
            await Task.Delay(8);
        }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private const int WsExLayered = 0x80000, WsExTopmost = 0x8, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTransparent = 0x20;
    private const uint WsPopup = 0x80000000;
    private const int UlwAlpha = 2, SwShowNoActivate = 4;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dcDst, ref POINT dst, ref SIZE size, IntPtr dcSrc, ref POINT src, uint key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] private static extern bool UpdateLayeredWindowAlpha(IntPtr hwnd, IntPtr dcDst, IntPtr dst, IntPtr size, IntPtr dcSrc, IntPtr src, uint key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
