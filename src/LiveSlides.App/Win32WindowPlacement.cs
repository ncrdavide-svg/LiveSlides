using System.Runtime.InteropServices;


namespace LiveSlides.App;

/// <summary>
/// Shared Win32 helpers for the borderless overlay windows (video output, monitor identify):
/// exact pixel positioning regardless of per-monitor DPI scaling, and a guarantee that the
/// window can never steal keyboard focus from the main window. See VideoOutputWindow for why
/// both of these matter.
/// </summary>
internal static class Win32WindowPlacement
{
    public static void ApplyPixelBounds(IntPtr hwnd, ScreenBounds bounds)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoZOrder | SwpNoActivate);
    }

    public static void SetNoActivate(IntPtr hwnd)
    {
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle.ToInt64() | WsExNoActivate));
    }

    /// <summary>
    /// Re-asserts a window at the very top of the Z order, without activating it. WPF's own
    /// Topmost="True" only sets HWND_TOPMOST once at show time - among several topmost windows
    /// the most recently (re-)topmost-ed one wins visually, so an overlay window needs this
    /// called again whenever another output window opens afterward on the same monitor, or the
    /// newer window can end up drawn above it.
    /// </summary>
    public static void BringToTop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
