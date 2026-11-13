using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiveSlides.App;

/// <summary>
/// Plays presentations with Microsoft PowerPoint itself, through COM automation: the file runs
/// exactly as its author designed it. All COM calls happen on one dedicated STA thread.
///
/// NOT YET TRIED ON A REAL POWERPOINT (the development PC has none): written from the
/// documented object model. See docs/STATO-PROGETTO.md.
/// </summary>
public sealed class PowerPointPlayer : IPresentationPlayer
{
    // MsoTriState / PowerPoint enums (late binding: no interop assembly to ship).
    private const int MsoTrue = -1, MsoFalse = 0;
    private const int PpShowTypeSpeaker = 1;
    private const int PpSlideShowRunning = 1, PpSlideShowPaused = 2, PpSlideShowDone = 5;

    private readonly Type _type;
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private dynamic? _app;
    private dynamic? _presentation;
    private dynamic? _showWindow;
    // Files stay loaded (without a window) for the whole show: opening is the slow part.
    private readonly Dictionary<string, object> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public PowerPointPlayer(Type powerPointType)
    {
        _type = powerPointType;
        _thread = new Thread(() => { foreach (var action in _work.GetConsumingEnumerable()) action(); })
        { IsBackground = true, Name = "PowerPoint show" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public string EngineName => "PowerPoint";
    public IntPtr Window { get; private set; }
    public IntPtr PresenterWindow { get; private set; }

    private Task<T> OnSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { done.SetResult(Retry(work)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    /// <summary>PowerPoint rejects calls while it is busy (opening a file, mid-transition): try again shortly.</summary>
    private static T Retry<T>(Func<T> work)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return work(); }
            catch (COMException ex) when (attempt < 20 && (uint)ex.HResult is 0x80010001 or 0x8001010A)
            {
                Thread.Sleep(100);
            }
        }
    }

    public async Task LoadAsync(string path, ScreenBounds bounds)
    {
        try { await OnSta(() => { Load(path, bounds); return true; }); }
        catch (Exception ex) { App.LogError(ex); }
    }

    private dynamic Load(string path, ScreenBounds bounds)
    {
        if (_app is null)
        {
            PreferMonitor(bounds);
            _app = Activator.CreateInstance(_type)!;
        }
        if (_loaded.TryGetValue(path, out var known)) return known;
        // ReadOnly, not Untitled, WithWindow: false -> no editing window on the operator's screen.
        dynamic presentation = _app.Presentations.Open(path, MsoTrue, MsoFalse, MsoFalse);
        _loaded[path] = presentation;
        // At most the one on air plus the one just loaded: a long running order must not keep
        // dozens of files open in PowerPoint.
        foreach (var other in _loaded.Keys.ToList())
        {
            if (string.Equals(other, path, StringComparison.OrdinalIgnoreCase)) continue;
            dynamic loaded = _loaded[other];
            if (_presentation is not null && ReferenceEquals(loaded, _presentation)) continue;
            try { loaded.Close(); } catch { /* already closed */ }
            _loaded.Remove(other);
        }
        return presentation;
    }

    public Task<PlayerState> OpenAsync(string path, ScreenBounds bounds, int startSlide, ScreenBounds? presenter) => OnSta(() =>
    {
        CloseCore();
        _presentation = Load(path, bounds);
        var settings = _presentation.SlideShowSettings;
        settings.ShowType = PpShowTypeSpeaker;
        // PowerPoint's presenter view only when LiveSlides has a screen for it: PowerPoint would
        // put it on a screen of its own choosing, often the operator's.
        try { settings.ShowPresenterView = presenter is null ? MsoFalse : MsoTrue; } catch { /* PowerPoint older than 2013 */ }
        _showWindow = settings.Run();

        Window = new IntPtr((int)_showWindow.HWND);
        if (!NativeWindows.Covers(Window, bounds)) NativeWindows.MoveTo(Window, bounds);

        PresenterWindow = IntPtr.Zero;
        if (presenter is { } presenterScreen)
        {
            // NOT TRIED with a real PowerPoint: the presenter view is taken to be PowerPoint's other
            // big visible window (not the slide show, not the editing window), moved onto the
            // presenter screen.
            var show = Window;
            PresenterWindow = NativeWindows.MoveNewWindowAsync(NativeWindows.ProcessIdOf(show), new[] { show },
                hwnd => NativeWindows.ClassName(hwnd) != "PPTFrameClass", presenterScreen, 3000).GetAwaiter().GetResult();
        }

        var view = _showWindow.View;
        if (startSlide > 0) view.GotoSlide(startSlide + 1, MsoTrue);
        // Frozen until LiveSlides uncovers it, so nothing plays unseen behind the tappo.
        try { view.State = PpSlideShowPaused; } catch { /* stays running: harmless */ }
        return ReadState();
    });

    /// <summary>
    /// PowerPoint has no API to choose the slide show monitor; it reads this per-user option,
    /// the same one its "Monitor" drop-down writes. Set before PowerPoint starts.
    /// </summary>
    private static void PreferMonitor(ScreenBounds bounds)
    {
        try
        {
            var device = Displays.GetTargets().FirstOrDefault(t => t.Bounds == bounds)?.Id;
            if (device is null) return;
            foreach (var version in new[] { "16.0", "15.0" })
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\{version}\PowerPoint\Options", true);
                if (key is null) continue;
                key.SetValue("DisplayMonitor", device, RegistryValueKind.String);
                key.SetValue("UseAutoMonSelection", 0, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    public Task BeforeRevealAsync() => OnSta(() =>
    {
        try { if (_showWindow is not null && (int)_showWindow.View.State == PpSlideShowPaused) _showWindow.View.State = PpSlideShowRunning; }
        catch { /* show already over */ }
        return true;
    });

    public Task<PlayerState> NextAsync() => OnSta(() => { TryView(view => view.Next()); return ReadState(); });
    public Task<PlayerState> PreviousAsync() => OnSta(() => { TryView(view => view.Previous()); return ReadState(); });
    public Task<PlayerState> GoToAsync(int slide) => OnSta(() => { TryView(view => view.GotoSlide(slide + 1, MsoTrue)); return ReadState(); });
    public Task<PlayerState> GetStateAsync() => OnSta(ReadState);

    private void TryView(Action<dynamic> action)
    {
        if (_showWindow is null) return;
        try { action(_showWindow.View); }
        catch (COMException) { /* the show window is gone: ReadState reports it */ }
    }

    private PlayerState ReadState()
    {
        if (_showWindow is null || _presentation is null) return PlayerState.Stopped;
        try
        {
            var view = _showWindow.View;
            if ((int)view.State == PpSlideShowDone) return PlayerState.Stopped;
            int count = _presentation.Slides.Count;
            int position = view.CurrentShowPosition; // 1-based
            int clickIndex = 0, clickCount = 0;
            try { clickIndex = view.GetClickIndex(); clickCount = view.GetClickCount(); }
            catch { /* PowerPoint older than 2010: slide-level precision only */ }
            return new PlayerState(true, position - 1, count,
                AtStart: position <= 1 && clickIndex <= 0,
                AtEnd: position >= count && clickIndex >= clickCount);
        }
        catch (COMException) { return PlayerState.Stopped; }
    }

    public Task CloseAsync() => OnSta(() => { CloseCore(); Unload(); return true; });

    private void CloseCore()
    {
        Window = IntPtr.Zero;
        PresenterWindow = IntPtr.Zero;
        try { _showWindow?.View.Exit(); } catch { /* already closed */ }
        _showWindow = null;
        _presentation = null;
    }

    /// <summary>End of the show: every file loaded for it is closed.</summary>
    private void Unload()
    {
        foreach (dynamic presentation in _loaded.Values)
        {
            try { presentation.Close(); } catch { /* already closed */ }
        }
        _loaded.Clear();
    }

    public void Dispose()
    {
        if (_work.IsAddingCompleted) return;
        _work.Add(() =>
        {
            CloseCore();
            Unload();
            // Quit only a PowerPoint that has nothing else open: it may be the operator's own.
            try { if (_app is not null && (int)_app.Presentations.Count == 0) _app.Quit(); } catch { /* best effort */ }
            _app = null;
        });
        _work.CompleteAdding();
        _thread.Join(5000);
    }
}
