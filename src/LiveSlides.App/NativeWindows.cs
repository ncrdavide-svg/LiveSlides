using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiveSlides.App;

/// <summary>
/// Win32 helpers for running somebody else's slide show window (PowerPoint, LibreOffice)
/// underneath our own output window: hide/unhide ours without a flash, grab the other
/// window's picture, find it, and take the keyboard back.
/// </summary>
internal static class NativeWindows
{
    /// <summary>
    /// Hides or unhides a window at the compositor level. Unlike Hide()/Show(), a cloaked window
    /// keeps rendering, so when it is uncloaked its current content is on screen in the same
    /// frame: no black flash.
    /// </summary>
    public static void SetCloaked(IntPtr hwnd, bool cloaked)
    {
        if (hwnd == IntPtr.Zero) return;
        var value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
    }

    /// <summary>
    /// What is on that part of the screen right now. Taken from the screen, never from the slide
    /// show window itself: asking LibreOffice's show window for its picture (PrintWindow)
    /// freezes the show, which then ignores every "next".
    /// </summary>
    public static BitmapSource? CaptureScreen(ScreenBounds bounds)
    {
        int width = bounds.Width, height = bounds.Height;
        if (width <= 0 || height <= 0) return null;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        try
        {
            var previous = SelectObject(memory, bitmap);
            var ok = BitBlt(memory, 0, 0, width, height, screen, bounds.X, bounds.Y, SrcCopy);
            SelectObject(memory, previous);
            if (!ok) return null;

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32
            };
            var pixels = new byte[width * height * 4];
            if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
            // Bgr32, not Bgra32: the alpha bytes GDI leaves behind are meaningless (often 0).
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>The visible, titled top-level window of a process that fills the given screen rectangle.</summary>
    public static IntPtr FindFullScreenWindow(int processId, ScreenBounds bounds)
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != processId || !IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            if (!GetWindowRect(hwnd, out var r)) return true;
            // LibreOffice's show window is one pixel larger than the monitor.
            if (Math.Abs(r.Left - bounds.X) > 2 || Math.Abs(r.Top - bounds.Y) > 2) return true;
            if (r.Right - r.Left < bounds.Width || r.Right - r.Left > bounds.Width + 4) return true;
            if (r.Bottom - r.Top < bounds.Height || r.Bottom - r.Top > bounds.Height + 4) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Waits (up to <paramref name="timeoutMs"/>) for a visible, titled top-level window of the
    /// process that is not one of <paramref name="exclude"/> and is at least 640×480, and moves it
    /// to fill <paramref name="target"/>. Used for the presenter view, which the programs open on
    /// a screen of their own choosing (often the operator's). Polled every few milliseconds so it
    /// is moved before it has much chance to show up where it should not.
    /// </summary>
    public static async Task<IntPtr> MoveNewWindowAsync(int processId, IReadOnlyCollection<IntPtr> exclude,
        Func<IntPtr, bool>? accept, ScreenBounds target, int timeoutMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            var found = IntPtr.Zero;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != processId || exclude.Contains(hwnd) || !IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
                if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 640 || r.Bottom - r.Top < 480) return true;
                if (accept is not null && !accept(hwnd)) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                // A maximized window ignores a plain move: restore it first.
                if (IsZoomed(found)) ShowWindow(found, 9 /* SW_RESTORE */);
                SetWindowPos(found, IntPtr.Zero, target.X, target.Y, target.Width, target.Height, 0x0004 | 0x0010);
                return found;
            }
            await Task.Delay(5);
        }
        return IntPtr.Zero;
    }

    public static int ProcessIdOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    public static bool Covers(IntPtr hwnd, ScreenBounds bounds)
        => hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r)
           && Math.Abs(r.Left - bounds.X) <= 2 && Math.Abs(r.Top - bounds.Y) <= 2
           && r.Right - r.Left >= bounds.Width && r.Bottom - r.Top >= bounds.Height;

    public static void MoveTo(IntPtr hwnd, ScreenBounds bounds)
    {
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0004 | 0x0010);
    }

    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>
    /// Gives the keyboard back to our window after another program's slide show grabbed it.
    /// Windows refuses a plain SetForegroundWindow from a background process, hence the
    /// attach-to-the-foreground-thread dance.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return;
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var ourThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread && AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    private const int DwmwaCloak = 13;
    private const uint SrcCopy = 0x00CC0020;
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x80000;
    private const uint LwaAlpha = 2;

    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}
