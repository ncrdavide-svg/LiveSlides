using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveSlides.Core;

namespace LiveSlides.App;

// Presentations played by their own program (PowerPoint / LibreOffice) instead of as still
// images. Their slide show window sits on the output monitor UNDER our output window:
//  - to put it on air, our window fades to a snapshot of it and is then hidden (cloaked);
//  - to leave it, our window reappears showing a snapshot of it, then fades to what comes next.
// Either way the picture never passes through black, and the tappo still works on top.
public partial class MainViewModel
{
    private IPresentationPlayer? _player;
    private bool _playerCreated;
    private Deck? _playerDeck;
    private PlayerState _playerState = PlayerState.Stopped;
    private bool _nativeOnAir;
    private bool _polling;
    private DispatcherTimer? _nativeTimer;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly HashSet<Deck> _nativeFailed = new();

    // "Next"/"previous" pressed while a presentation is still opening: carried out as soon as
    // it is on air, instead of being lost.
    private bool _nativeOpening;
    private int _queuedSteps;

    private bool PlaysNatively(Deck deck)
        => deck.IsPresentation && _player is not null && _output?.MonitorBounds is not null && !_nativeFailed.Contains(deck);

    private Deck? NativeDeckAt(int index)
        => index >= 0 && index < _steps.Count && _steps[index] is { Kind: StepKind.Slide } step
           && PlaysNatively(_stepDecks[step.DeckIndex]) ? _stepDecks[step.DeckIndex] : null;

    /// <summary>The operator chose another program: the next show starts with it.</summary>
    private void ResetPlayer()
    {
        _player?.Dispose();
        _player = null;
        _playerCreated = false;
    }

    /// <summary>Called when the output opens: picks the program that will play presentations.</summary>
    private void PreparePlayer(OutputTarget target)
    {
        if (!_playerCreated)
        {
            _playerCreated = true;
            try { _player = PresentationPlayers.Create(_settings); }
            catch (Exception ex) { App.LogError(ex); }
        }
        _nativeFailed.Clear();
        if (_nativeTimer is null)
        {
            _nativeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _nativeTimer.Tick += async (_, _) => await PollPlayerAsync();
        }
        _nativeTimer.Start();

        if (!Decks.Any(d => d.IsPresentation)) return;
        StatusMessage = target.IsWindow ? "Finestra di prova: le presentazioni vanno come immagini fisse (le animazioni solo su un monitor)"
            : _player is null && string.Equals(_settings.PresentationEngine, "static", StringComparison.OrdinalIgnoreCase) ? "Presentazioni come immagini fisse (scelta in «Presentazioni»)"
            : _player is null && _settings.PresentationEngine is { } engine ? $"{(engine == "powerpoint" ? "PowerPoint" : "LibreOffice")} non trovato: le presentazioni vanno come immagini fisse"
            : _player is null ? "Né PowerPoint né LibreOffice trovati: le presentazioni vanno come immagini fisse"
            : $"Le presentazioni sono riprodotte da {_player.EngineName}, con animazioni e filmati";

    }

    /// <summary>
    /// IN ONDA preview of a natively played slide: the still picture of the slide. Deliberately
    /// not a grab of the screen: reading the screen back several times a second makes the
    /// presentation's own animations stutter.
    /// </summary>
    private async Task UpdateNativePreviewAsync()
    {
        var index = _index;
        if (NativeDeckAt(index) is null) return;
        var image = await GetStepImageAsync(_steps[index]);
        if (index != _index || !_nativeOnAir || image is null) return;
        CurrentPreview = image;
        // No presenter view from the program: the presenter screen at least shows the slide.
        if (_output is { Mirror: { } mirror, MirrorHasConsole: false }) mirror.Show(image, 0);
    }

    private void StopPlayer()
    {
        _nativeTimer?.Stop();
        _nativeOnAir = false;
        _playerDeck = null;
        _playerState = PlayerState.Stopped;
        if (_player is { } player) _ = CloseQuietlyAsync(player);
    }

    private static async Task CloseQuietlyAsync(IPresentationPlayer player)
    {
        try { await player.CloseAsync(); }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>Opens the deck in the player, behind our window. False if it cannot be played (then: still images).</summary>
    private async Task<bool> OpenDeckAsync(Deck deck, int slide)
    {
        await _openGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_playerDeck, deck) && _playerState.Running) return true;
            if (_player is not { } player || _output is not { MonitorBounds: { } bounds } output) return false;
            // The show underneath is about to change: nobody must see that.
            await CoverNativeAsync(null, 0);
            _playerDeck = null;
            try
            {
                _playerState = await player.OpenAsync(deck.FilePath, bounds, slide, output.Mirror?.MonitorBounds);
                output.MirrorHasConsole = player.PresenterWindow != IntPtr.Zero;
                _playerDeck = deck;
                if (ReferenceEquals(output, _output)) output.BringToTop();
                // The slide show took the keyboard: the operator's keys must keep reaching LiveSlides.
                if (Application.Current?.MainWindow is { } main)
                    NativeWindows.ForceForeground(new WindowInteropHelper(main).Handle);
                return true;
            }
            catch (Exception ex)
            {
                if (_output is null) return false; // the show was stopped meanwhile: not a failure
                App.LogError(ex);
                _nativeFailed.Add(deck);
                StatusMessage = $"{deck.Name}: {player.EngineName} non riesce a riprodurlo ({ex.Message}). Va in onda come immagini fisse";
                return false;
            }
        }
        finally { _openGate.Release(); }
    }

    /// <summary>Puts a slide of a natively played deck on air. False: fall back to the still image.</summary>
    private async Task<bool> ShowNativeAsync(Deck deck, int slide, int ticket)
    {
        _nativeOpening = true;
        _queuedSteps = 0;
        try
        {
            if (!await ShowNativeCoreAsync(deck, slide, ticket)) return false;
        }
        finally { _nativeOpening = false; }
        while (_queuedSteps != 0 && ticket == _showTicket && _nativeOnAir)
        {
            var delta = Math.Sign(_queuedSteps);
            _queuedSteps -= delta;
            await StepNativeAsync(delta);
        }
        _queuedSteps = 0;
        return true;
    }

    private async Task<bool> ShowNativeCoreAsync(Deck deck, int slide, int ticket)
    {
        if (!ReferenceEquals(_playerDeck, deck) || !_playerState.Running)
        {
            StatusMessage = $"Apertura di {deck.Name} in {_player!.EngineName}…";
            if (!await OpenDeckAsync(deck, slide)) return false;
            if (ticket != _showTicket) return true;
            StatusMessage = $"In onda con {_player.EngineName}: {deck.Name}";
        }
        if (_playerState.Slide != slide)
        {
            _playerState = await _player!.GoToAsync(slide);
            if (ticket != _showTicket) return true;
        }
        if (!_nativeOnAir) await RevealNativeAsync(ticket);
        await UpdateNativePreviewAsync();
        return true;
    }

    /// <summary>What the audience sees right now (only meaningful while the presentation is on air).</summary>
    private BitmapSource? CaptureNative()
        => _output?.MonitorBounds is { } bounds ? NativeWindows.CaptureScreen(bounds) : null;

    // One fade of the output window at a time: a tappo pressed in the middle of a reveal must
    // wait for it, not fight over the same window.
    private readonly SemaphoreSlim _coverGate = new(1, 1);

    /// <summary>Our window fades away and the presentation, already running underneath, is on air.</summary>
    private async Task RevealNativeAsync(int ticket)
    {
        await _coverGate.WaitAsync();
        try
        {
            if (_output is not { } output || _nativeOnAir) return;
            if (_player is { } player)
            {
                await player.BeforeRevealAsync();
                if (ticket != _showTicket || !ReferenceEquals(output, _output)) return;
            }
            _nativeOnAir = true;
            await output.FadeToCloakAsync(output.NothingOnAir ? 0 : FadeMs);
        }
        finally { _coverGate.Release(); }
    }

    /// <summary>
    /// Our window back over the presentation. With <paramref name="next"/>, that image fades in
    /// over the still-running show (returns true: it is on air). Without, the window comes back
    /// at once showing a snapshot of the show, to hold the picture while something else loads.
    /// </summary>
    private async Task<bool> CoverNativeAsync(System.Windows.Media.ImageSource? next, int fadeMs)
    {
        await _coverGate.WaitAsync();
        try
        {
            if (!_nativeOnAir) return false;
            _nativeOnAir = false;
            if (_output is not { } output) return false;
            if ((next ?? CaptureNative()) is { } image) output.Show(image, 0);
            // Let WPF draw it before the window becomes visible again.
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(40);
            await output.FadeFromCloakAsync(next is null ? 0 : fadeMs);
            return next is not null;
        }
        finally { _coverGate.Release(); }
    }

    /// <summary>"Next"/"previous" while a natively played deck is the current item.</summary>
    private async Task StepNativeAsync(int delta)
    {
        if (!_nativeOnAir && _nativeOpening)
        {
            _queuedSteps += delta;
            return;
        }
        if (!_nativeOnAir || _player is not { } player) { await GoToAsync(_index); return; }

        var deckIndex = _steps[_index].DeckIndex;
        var state = await player.GetStateAsync();
        if (!state.Running || (delta > 0 ? state.AtEnd : state.AtStart))
        {
            // The presentation is over (or at its very start): on to the neighbouring item.
            var target = _index;
            while (target >= 0 && target < _steps.Count && _steps[target].Kind == StepKind.Slide && _steps[target].DeckIndex == deckIndex)
                target += delta;
            if (target >= 0 && target < _steps.Count) await GoToAsync(target);
            return;
        }

        var ticket = _showTicket;
        var after = delta > 0 ? await player.NextAsync() : await player.PreviousAsync();
        if (ticket != _showTicket) return;
        if (!after.Running) { await NativeEndedAsync(); return; }
        ApplyPlayerState(after);
    }

    /// <summary>Keeps position and labels in step with the running presentation.</summary>
    private void ApplyPlayerState(PlayerState state)
    {
        _playerState = state;
        if (_index < 0 || _index >= _steps.Count || _steps[_index] is not { Kind: StepKind.Slide } step) return;
        var deck = _stepDecks[step.DeckIndex];
        if (!ReferenceEquals(deck, _playerDeck)) return;
        var found = ShowSequence.Find(_steps, StepKind.Slide, step.DeckIndex, Math.Clamp(state.Slide, 0, deck.PageCount - 1));
        if (found < 0 || found == _index) return;
        _index = found;
        UpdateLabels();
        _ = PrerenderAroundAsync(_showTicket);
        _ = UpdateNativePreviewAsync();
    }

    private async Task PollPlayerAsync()
    {
        if (!_nativeOnAir || _polling || _player is not { } player) return;
        _polling = true;
        try
        {
            var ticket = _showTicket;
            var state = await player.GetStateAsync();
            if (!_nativeOnAir || ticket != _showTicket) return;
            if (!state.Running) { await NativeEndedAsync(); return; }
            ApplyPlayerState(state);
        }
        catch (Exception ex) { App.LogError(ex); }
        finally { _polling = false; }
    }

    /// <summary>The slide show closed by itself (its own Esc, a crash): cover the hole at once and move on.</summary>
    private async Task NativeEndedAsync()
    {
        _nativeOnAir = false;
        _playerState = PlayerState.Stopped;
        var deck = _playerDeck;
        _playerDeck = null;
        _output?.SetCloaked(false);
        if (deck is null || _index < 0 || _index >= _steps.Count) return;
        StatusMessage = $"{deck.Name}: la presentazione si è chiusa da sola";
        var deckIndex = _steps[_index].DeckIndex;
        var target = _index;
        while (target < _steps.Count && _steps[target].Kind == StepKind.Slide && _steps[target].DeckIndex == deckIndex) target++;
        if (target < _steps.Count) await GoToAsync(target);
    }

    /// <summary>
    /// Loads (does not start) the next presentation of the running order, wherever it is, so
    /// that it goes on air at once. Only that one: loading every file of a long running order
    /// up front would be slow and would weigh on the presentation that is playing. And only
    /// loaded, never started: its show starts when it goes on air, or its movies would play
    /// unseen behind the tappo and then again.
    /// </summary>
    private async void PreloadNative()
    {
        // async void (fire and forget): nothing may escape, or it would take the app down.
        try { await PreloadNativeAsync(); }
        catch (Exception ex) { App.LogError(ex); }
    }

    private async Task PreloadNativeAsync()
    {
        if (_player is not { } player || _output?.MonitorBounds is not { } bounds || _index < 0) return;
        var currentDeck = _index < _steps.Count && _steps[_index].Kind == StepKind.Slide ? _stepDecks[_steps[_index].DeckIndex] : null;
        Deck? next = null;
        for (var i = _index + 1; i < _steps.Count && next is null; i++)
            if (NativeDeckAt(i) is { } deck && !ReferenceEquals(deck, currentDeck)) next = deck;
        if (next is null) return;

        // A presentation that has just gone on air gets the machine to itself for its first
        // moments (transition, first animations, a movie starting); the loading waits.
        if (_nativeOnAir)
        {
            var ticket = _showTicket;
            await Task.Delay(2000);
            if (ticket != _showTicket || _player is null || _output is null) return;
        }
        await player.LoadAsync(next.FilePath, bounds);
    }
}
