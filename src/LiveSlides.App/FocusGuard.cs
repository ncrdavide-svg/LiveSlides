using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LiveSlides.App;

/// <summary>
/// Keeps the keyboard on LiveSlides while a show is on air. LibreOffice and PowerPoint grab the
/// foreground whenever they load a file, start, or open their presenter view, often in the
/// background (the next presentation being prepared); from then on the arrow keys and a
/// presentation clicker would drive them, or nothing, instead of LiveSlides.
///
/// Watches every foreground change (a WinEvent hook, no polling). When the foreground passes from
/// LiveSlides to a window of LibreOffice or PowerPoint without the operator asking for it (no
/// mouse button and no Alt or Windows key held, i.e. not a click and not Alt+Tab), LiveSlides
/// takes it back at once.
/// </summary>
internal sealed class FocusGuard : IDisposable
{
    private readonly IntPtr _mainWindow;
    private readonly Dispatcher _dispatcher;
    private readonly WinEventProc _callback; // kept alive: the hook calls it from native code
    private readonly IntPtr _hook;
    private readonly int _ourProcess = Environment.ProcessId;
    private bool _oursWasForeground;

    public FocusGuard(IntPtr mainWindow, Dispatcher dispatcher)
    {
        _mainWindow = mainWindow;
        _dispatcher = dispatcher;
        _oursWasForeground = ProcessOf(GetForegroundWindow()) == _ourProcess;
        _callback = OnForegroundChanged;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);
        // Also asks Windows to refuse other programs' attempts to take the foreground (only
        // effective while LiveSlides has it; the hook covers the rest).
        if (_oursWasForeground) LockSetForegroundWindow(LsfwLock);
    }

    /// <summary>Number of times the keyboard was taken back (for the log).</summary>
    public int Restored { get; private set; }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var process = ProcessOf(hwnd);
        if (process == _ourProcess) { _oursWasForeground = true; return; }
        var wasOurs = _oursWasForeground;
        _oursWasForeground = false;
        if (!wasOurs || OperatorAsked() || !IsPresentationProgram(process)) return;

        Restored++;
        // Twice: a program that opens several windows in a row may grab the foreground again.
        _dispatcher.BeginInvoke(() => NativeWindows.ForceForeground(_mainWindow));
        _ = Task.Delay(150).ContinueWith(_ => _dispatcher.BeginInvoke(() =>
        {
            if (ProcessOf(GetForegroundWindow()) != _ourProcess && IsPresentationProgram(ProcessOf(GetForegroundWindow())) && !OperatorAsked())
                NativeWindows.ForceForeground(_mainWindow);
        }));
    }

    /// <summary>A click (mouse button down) or a keyboard switch (Alt, Windows key) means the operator chose that window.</summary>
    private static bool OperatorAsked()
        => Down(0x01) || Down(0x02) || Down(0x04) // mouse buttons
           || Down(0x12) || Down(0x5B) || Down(0x5C); // Alt, left/right Windows key

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool IsPresentationProgram(int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            return name.StartsWith("soffice", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; } // already gone
    }

    private static int ProcessOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        LockSetForegroundWindow(LsfwUnlock);
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint LsfwLock = 1, LsfwUnlock = 2;

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool LockSetForegroundWindow(uint code);
}
