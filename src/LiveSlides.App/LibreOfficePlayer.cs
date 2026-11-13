using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LiveSlides.App;

/// <summary>
/// Plays presentations with LibreOffice Impress. LibreOffice has no .NET API, so a small Python
/// script (Assets/lo_bridge.py, run by LibreOffice's own Python) starts a private LibreOffice
/// and drives it; this class talks to that script over a local TCP port.
///
/// Two separate LibreOffice instances ("slots") take turns: one plays the presentation on air,
/// the other loads the presentation that comes next. Loading a file keeps LibreOffice's main
/// thread busy for a second or more, and in the instance on air that would freeze its
/// animations and embedded movies. Each instance holds at most one file: to load another, the
/// whole instance is thrown away and a fresh one started (closing a document after its show has
/// run crashes LibreOffice). So no more than two presentations are ever open, however long the
/// running order.
/// </summary>
public sealed class LibreOfficePlayer : IPresentationPlayer
{
    private sealed class Slot
    {
        public int Number;
        public Process? Bridge;
        public int Port;
        // One command at a time per instance; the two instances never wait for each other.
        public readonly SemaphoreSlim Gate = new(1, 1);
        public string? Path;       // the file this instance holds (or is loading)
        public Task<bool>? Loading;
        public int Doc;
        public int SlideCount;
        public int[] Clicks = Array.Empty<int>();
        public int OfficePid;
    }

    private readonly string _python;
    private readonly string _soffice;
    private readonly AppSettings _settings;
    private readonly Slot[] _slots = { new() { Number = 0 }, new() { Number = 1 } };
    private Slot? _live;
    private bool _disposed;

    private int _slide;
    // Clicks sent on the current slide: only a hint (a click during a long animation merely
    // finishes it), good enough to tell "nothing done yet on the first slide".
    private int _clicksDone;
    // The show itself reported that the current slide has no animation step left.
    private bool _animationsDone;

    public LibreOfficePlayer(string pythonPath, string sofficeBinPath, AppSettings settings)
    {
        _python = pythonPath;
        _soffice = sofficeBinPath;
        _settings = settings;
    }

    public string EngineName => "LibreOffice";
    public IntPtr Window { get; private set; }
    public IntPtr PresenterWindow { get; private set; }

    /// <summary>
    /// One line per LibreOffice operation in player.log (data folder): when a presentation does
    /// not go on air, this says which instance did what. Kept small: restarted at each run.
    /// </summary>
    private static void Trace(string message)
    {
        try
        {
            var file = Path.Combine(App.DataFolder, "player.log");
            if (!TraceStarted) { TraceStarted = true; File.WriteAllText(file, ""); }
            File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* diagnostics only */ }
    }
    private static bool TraceStarted;

    private Slot? Holding(string path)
        => _slots.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- loading

    public async Task LoadAsync(string path, ScreenBounds bounds)
    {
        if (_disposed) return;
        if (Holding(path) is { } holding)
        {
            if (holding.Loading is { } pending) await pending;
            return;
        }
        // Never the instance on air.
        var slot = _slots.First(s => !ReferenceEquals(s, _live));
        // Its previous load (if any) must finish first: it might be needed meanwhile.
        if (slot.Loading is { } previous) await previous;
        if (Holding(path) is not null || ReferenceEquals(slot, _live)) { await LoadAsync(path, bounds); return; }
        slot.Path = path;
        Trace($"slot {slot.Number}: load {Path.GetFileName(path)}");
        slot.Loading = LoadIntoAsync(slot, path);
        await slot.Loading;
    }

    private async Task<bool> LoadIntoAsync(Slot slot, string path)
    {
        await slot.Gate.WaitAsync();
        try
        {
            if (slot.Doc != 0 || slot.OfficePid != 0)
            {
                // A fresh LibreOffice for the new file (see the class comment).
                slot.Doc = 0;
                try { await SendAsync(slot, "reset"); } catch (Exception ex) { App.LogError(ex); }
            }
            JsonElement opened;
            try { opened = await SendAsync(slot, "open", path); }
            catch (Exception) when (slot.Bridge is { HasExited: false } && !_disposed)
            {
                // LibreOffice now and then dies while loading: the bridge starts a new one.
                opened = await SendAsync(slot, "open", path);
            }
            slot.Doc = opened.GetProperty("id").GetInt32();
            slot.SlideCount = opened.GetProperty("slides").GetInt32();
            slot.Clicks = opened.GetProperty("clicks").EnumerateArray().Select(c => c.GetInt32()).ToArray();
            slot.OfficePid = (await SendAsync(slot, "ping")).GetProperty("pid").GetInt32();
            Trace($"slot {slot.Number}: loaded {Path.GetFileName(path)} (doc {slot.Doc}, pid {slot.OfficePid})");
            return true;
        }
        catch (Exception ex)
        {
            if (!_disposed) App.LogError(ex);
            Trace($"slot {slot.Number}: LOAD FAILED {Path.GetFileName(path)}: {ex.Message}");
            slot.Path = null;
            slot.Doc = 0;
            return false;
        }
        finally
        {
            slot.Gate.Release();
            slot.Loading = null;
        }
    }

    // ---------------------------------------------------------------- on air

    public async Task<PlayerState> OpenAsync(string path, ScreenBounds bounds, int startSlide, ScreenBounds? presenter)
    {
        try { return await OpenCoreAsync(path, bounds, startSlide, presenter); }
        catch (Exception ex) when (!_disposed)
        {
            // LibreOffice now and then dies around the end of a show: a fresh instance, once more.
            App.LogError(ex);
            if (Holding(path) is { } broken) { broken.Path = null; broken.Doc = 0; }
            return await OpenCoreAsync(path, bounds, startSlide, presenter);
        }
    }

    private async Task<PlayerState> OpenCoreAsync(string path, ScreenBounds bounds, int startSlide, ScreenBounds? presenter)
    {
        await LoadAsync(path, bounds);
        var slot = Holding(path);
        Trace($"show {Path.GetFileName(path)} at {startSlide}: slot {slot?.Number.ToString() ?? "none"}, live {_live?.Number.ToString() ?? "none"}, slots [{string.Join(" | ", _slots.Select(s => Path.GetFileName(s.Path ?? "-") + (s.Loading is null ? "" : " (loading)")))}]");
        if (slot is null) throw new InvalidOperationException("LibreOffice non riesce ad aprire il file");

        // The show that was on air (in the other instance) ends: our window covers the monitor.
        if (_live is { } old && !ReferenceEquals(old, slot)) await EndShowAsync(old);
        _live = slot;
        Window = IntPtr.Zero;
        PresenterWindow = IntPtr.Zero;

        await slot.Gate.WaitAsync();
        try
        {
            startSlide = Math.Clamp(startSlide, 0, Math.Max(0, slot.SlideCount - 1));
            // The number depends on the whole set of screens: the key says which screen of which set.
            // In Windows' device order, which is also the order LibreOffice numbers them in.
            var screens = Displays.GetTargets().Where(t => !t.IsWindow).Select(t => t.Bounds!).ToList();
            var key = $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height} of "
                      + string.Join(" ", screens.OrderBy(b => b.X).ThenBy(b => b.Y).Select(b => $"{b.X},{b.Y},{b.Width},{b.Height}"));
            var monitors = Math.Max(1, screens.Count);
            var candidates = Enumerable.Range(1, monitors).ToList();
            // First guess: LibreOffice's screen N is Windows' N-th screen in device order (seen with
            // 2 and 3 screens). A wrong guess briefly shows the presentation on another screen.
            var guess = screens.IndexOf(bounds) + 1;
            if (guess > 0 && candidates.Remove(guess)) candidates.Insert(0, guess);
            // LibreOffice numbers the screens its own way. The number that worked is remembered
            // (also across runs): a wrong try would start the show, movie and sound included,
            // on the operator's screen for a moment.
            if (_settings.LibreOfficeDisplays.TryGetValue(key, out var known) && candidates.Remove(known))
                candidates.Insert(0, known);

            foreach (var display in candidates)
            {
                // LibreOffice opens its presenter console on a screen of its own choosing (usually
                // the operator's): it is watched for from the very start and moved at once.
                using var stopWatching = new CancellationTokenSource();
                var console = presenter is { } presenterScreen
                    ? WatchForConsoleAsync(slot.OfficePid, bounds, presenterScreen, stopWatching.Token)
                    : Task.FromResult(IntPtr.Zero);
                await SendAsync(slot, "show", slot.Doc.ToString(), display.ToString(), startSlide.ToString(), presenter is null ? "0" : "1");
                for (var i = 0; i < 40 && Window == IntPtr.Zero; i++)
                {
                    Window = NativeWindows.FindFullScreenWindow(slot.OfficePid, bounds);
                    if (Window == IntPtr.Zero) await Task.Delay(25);
                }
                if (Window == IntPtr.Zero) { stopWatching.Cancel(); await console; continue; }
                PresenterWindow = await console;
                Trace($"slot {slot.Number}: presenter console {(presenter is null ? "off" : PresenterWindow == IntPtr.Zero ? "NOT FOUND" : "moved")}");
                if (!_settings.LibreOfficeDisplays.TryGetValue(key, out var saved) || saved != display)
                {
                    _settings.LibreOfficeDisplays[key] = display;
                    _settings.Save();
                }
                break;
            }
            Trace($"slot {slot.Number}: show window {(Window == IntPtr.Zero ? "NOT FOUND" : "ok")}");
            if (Window == IntPtr.Zero)
            {
                try { await SendAsync(slot, "end", slot.Doc.ToString()); } catch { /* best effort */ }
                throw new InvalidOperationException("LibreOffice non ha aperto la presentazione sul monitor di uscita");
            }
            _slide = startSlide;
            _clicksDone = 0;
            _animationsDone = false;
            return State();
        }
        finally { slot.Gate.Release(); }
    }

    /// <summary>
    /// The console is the program's other full-screen window. Nothing is moved until the slide
    /// show itself is seen on the output screen: if LibreOffice put the show on the wrong screen,
    /// the window found first could be the show itself.
    /// </summary>
    private async Task<IntPtr> WatchForConsoleAsync(int officePid, ScreenBounds output, ScreenBounds presenter, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var show = IntPtr.Zero;
        while (!token.IsCancellationRequested && clock.ElapsedMilliseconds < 4000)
        {
            if (show == IntPtr.Zero)
            {
                show = NativeWindows.FindFullScreenWindow(officePid, output);
                if (show == IntPtr.Zero) { await Task.Delay(5); continue; }
            }
            var found = await NativeWindows.MoveNewWindowAsync(officePid, new[] { show },
                hwnd => NativeWindows.ClassName(hwnd) == "SALTMPSUBFRAME", presenter, 200);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Nothing to do: the show is started only at the moment it goes on air, so its movies and
    /// timed animations start then, once. (Starting it earlier behind our window and restarting
    /// the slide when uncovering it played embedded movies twice.)
    /// </summary>
    public Task BeforeRevealAsync() => Task.CompletedTask;

    public Task<PlayerState> NextAsync() => CommandAsync(+1, "next");
    public Task<PlayerState> PreviousAsync() => CommandAsync(-1, "prev");
    public Task<PlayerState> GetStateAsync() => CommandAsync(0, "state");
    public Task<PlayerState> GoToAsync(int slide) => CommandAsync(0, "goto", slide);

    private async Task<PlayerState> CommandAsync(int clickDelta, string command, int? slide = null)
    {
        if (_live is not { } slot) return PlayerState.Stopped;
        await slot.Gate.WaitAsync();
        try
        {
            if (slot.Doc == 0 || !ReferenceEquals(slot, _live)) return PlayerState.Stopped;
            var reply = slide is { } target
                ? await SendAsync(slot, command, slot.Doc.ToString(), target.ToString())
                : await SendAsync(slot, command, slot.Doc.ToString());
            return Apply(slot, reply, clickDelta, slide);
        }
        catch (Exception ex) { if (!_disposed) App.LogError(ex); return PlayerState.Stopped; }
        finally { slot.Gate.Release(); }
    }

    private PlayerState Apply(Slot slot, JsonElement reply, int clickDelta, int? jumpedTo)
    {
        if (!reply.TryGetProperty("running", out var running) || !running.GetBoolean()) return PlayerState.Stopped;
        var slide = reply.GetProperty("slide").GetInt32();
        // -1 while a slide transition is running: the count restarts when the new slide reports in.
        if (slide < 0) slide = jumpedTo ?? _slide;
        if (slide != _slide || jumpedTo is not null)
        {
            _slide = slide;
            _clicksDone = 0;
        }
        else
        {
            _clicksDone = Math.Max(0, _clicksDone + clickDelta);
        }
        _animationsDone = reply.TryGetProperty("done", out var done) && done.GetBoolean();
        return State();
    }

    private PlayerState State()
    {
        if (_live is not { } slot) return PlayerState.Stopped;
        var clicksHere = _slide >= 0 && _slide < slot.Clicks.Length ? slot.Clicks[_slide] : 0;
        return new PlayerState(true, _slide, slot.SlideCount,
            AtStart: _slide <= 0 && _clicksDone == 0,
            // A last slide without click steps is over as soon as it is shown: a "next" passed on
            // to LibreOffice there would end its show on a black "click to exit" screen.
            AtEnd: _slide >= slot.SlideCount - 1 && (clicksHere == 0 || _animationsDone));
    }

    private async Task EndShowAsync(Slot slot)
    {
        await slot.Gate.WaitAsync();
        try { if (slot.Doc != 0) await SendAsync(slot, "end", slot.Doc.ToString()); }
        catch (Exception ex) { if (!_disposed) App.LogError(ex); }
        finally { slot.Gate.Release(); }
    }

    /// <summary>End of the show: both LibreOffice instances are thrown away.</summary>
    public async Task CloseAsync()
    {
        Window = IntPtr.Zero;
        _live = null;
        foreach (var slot in _slots)
        {
            if (slot.Loading is { } pending) await pending;
            await slot.Gate.WaitAsync();
            try
            {
                slot.Path = null;
                slot.Doc = 0;
                if (slot.Bridge is { HasExited: false }) await SendAsync(slot, "reset");
            }
            catch (Exception ex) { if (!_disposed) App.LogError(ex); }
            finally { slot.Gate.Release(); }
        }
    }

    // ---------------------------------------------------------------- bridge processes

    private async Task EnsureBridgeAsync(Slot slot)
    {
        if (slot.Bridge is { HasExited: false }) return;
        if (_disposed) throw new ObjectDisposedException(nameof(LibreOfficePlayer));

        var script = Path.Combine(App.DataFolder, "lo_bridge.py");
        Directory.CreateDirectory(App.DataFolder);
        // Both slots may start together: written once, never while the other bridge reads it.
        lock (_slots)
        {
            using var source = typeof(LibreOfficePlayer).Assembly.GetManifestResourceStream("LiveSlides.App.Assets.lo_bridge.py")
                               ?? throw new InvalidOperationException("lo_bridge.py mancante");
            using var memory = new MemoryStream();
            source.CopyTo(memory);
            var bytes = memory.ToArray();
            if (!File.Exists(script) || !File.ReadAllBytes(script).AsSpan().SequenceEqual(bytes))
                File.WriteAllBytes(script, bytes);
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        slot.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        // Profiles of their own: two LibreOffice runs on one profile merge into one process (and
        // the PDF conversion uses yet another one).
        var start = new ProcessStartInfo(_python) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(_soffice);
        start.ArgumentList.Add(Path.Combine(App.DataFolder, $"lo-show-profile-{slot.Number}"));
        start.ArgumentList.Add(slot.Port.ToString());
        slot.Bridge = Process.Start(start) ?? throw new InvalidOperationException("impossibile avviare LibreOffice");

        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, slot.Port);
                return;
            }
            catch (SocketException) { await Task.Delay(100); }
        }
        throw new InvalidOperationException("LibreOffice non risponde");
    }

    private async Task<JsonElement> SendAsync(Slot slot, params string[] fields)
    {
        await EnsureBridgeAsync(slot);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, slot.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Join('\t', fields) + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Generous: the very first start of LibreOffice also creates its profile.
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120))
                   ?? throw new InvalidOperationException("LibreOffice non risponde");
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement.Clone();
        if (!root.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("LibreOffice: " + root.GetProperty("error").GetString());
        return root;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var slot in _slots)
        {
            var bridge = slot.Bridge;
            slot.Bridge = null;
            if (bridge is null || bridge.HasExited) continue;
            try
            {
                // Synchronous on purpose: called while the app is closing.
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, slot.Port);
                client.GetStream().Write(Encoding.UTF8.GetBytes("quit\n"));
                if (!bridge.WaitForExit(8000)) bridge.Kill(true);
            }
            catch
            {
                try { bridge.Kill(true); } catch { /* already gone */ }
            }
        }
    }
}
