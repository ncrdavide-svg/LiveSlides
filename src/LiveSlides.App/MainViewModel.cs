using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiveSlides.Core;

namespace LiveSlides.App;

public partial class MainViewModel : ObservableObject
{
    private const int ThumbWidth = 256;
    private const int ThumbHeight = 144;
    private const int CacheCapacity = 8;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly RemoteApi _remote;

    // The flattened show (see ShowSequence) and the deck list it was built from.
    private List<ShowStep> _steps = new();
    private List<Deck> _stepDecks = new();
    private int _index = -1;
    private bool _tappoOverride;
    private int _showTicket;

    private OutputWindow? _output;
    private FocusGuard? _focusGuard;
    private int _renderWidth = 1920;
    private int _renderHeight = 1080;

    // Full-size slides, most recently used last. Tasks (not bitmaps) are cached so that a slide
    // being pre-rendered and then requested for air is rendered only once.
    private readonly Dictionary<(Deck Deck, int Page), Task<BitmapSource?>> _cache = new();
    private readonly List<(Deck Deck, int Page)> _cacheOrder = new();
    private BitmapSource? _tappoImage;

    private CancellationTokenSource _loadCts = new();
    private bool _loadingShow;
    private SlideThumb? _liveThumb;
    private Deck? _liveDeck;

    public MainViewModel()
    {
        _remote = new RemoteApi(HandleRemote);
        RefreshTargets();
        ApplyRemoteSettings();
    }

    // ---------------------------------------------------------------- bindable state

    public ObservableCollection<Deck> Decks { get; } = new();
    public ObservableCollection<OutputTarget> Targets { get; } = new();

    private OutputTarget? _selectedTarget;
    public OutputTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value) || value is null) return;
            _settings.OutputTargetId = value.Id;
            _settings.Save();
        }
    }

    /// <summary>Screens for the presenter view, plus "none".</summary>
    public ObservableCollection<OutputTarget> PresenterTargets { get; } = new();

    private OutputTarget? _selectedPresenterTarget;
    public OutputTarget? SelectedPresenterTarget
    {
        get => _selectedPresenterTarget;
        set
        {
            if (!Set(ref _selectedPresenterTarget, value) || value is null) return;
            _settings.PresenterTargetId = value.Id;
            _settings.Save();
        }
    }

    private const string NoPresenterId = "none";

    private string? _folder;
    public string? Folder
    {
        get => _folder;
        private set { if (Set(ref _folder, value)) { Raise(nameof(FolderLabel)); Raise(nameof(HasFolder)); } }
    }
    public string FolderLabel => Folder ?? "Nessuna cartella: premi «Cartella…» per scegliere dove sono le presentazioni";
    public bool HasFolder => Folder is not null;
    public string? LastFolder => _settings.LastFolder;

    private Deck? _selectedDeck;
    public Deck? SelectedDeck { get => _selectedDeck; set => Set(ref _selectedDeck, value); }

    private string? _tappoPath;
    public string? TappoPath
    {
        get => _tappoPath;
        private set { if (Set(ref _tappoPath, value)) Raise(nameof(TappoLabel)); }
    }
    public string TappoLabel => TappoPath is null ? "Nessun tappo" : Path.GetFileName(TappoPath);

    private ImageSource? _tappoPreview;
    public ImageSource? TappoPreview { get => _tappoPreview; private set => Set(ref _tappoPreview, value); }

    private bool _tappoAtStart = true;
    public bool TappoAtStart { get => _tappoAtStart; set { if (Set(ref _tappoAtStart, value)) OnShowOptionChanged(); } }

    private bool _tappoBetween = true;
    public bool TappoBetween { get => _tappoBetween; set { if (Set(ref _tappoBetween, value)) OnShowOptionChanged(); } }

    private bool _tappoAtEnd = true;
    public bool TappoAtEnd { get => _tappoAtEnd; set { if (Set(ref _tappoAtEnd, value)) OnShowOptionChanged(); } }

    private int _fadeMs = 300;
    public int FadeMs
    {
        get => _fadeMs;
        set { if (Set(ref _fadeMs, Math.Clamp(value, 0, 5000))) SaveShow(); }
    }

    private bool _isLive;
    public bool IsLive
    {
        get => _isLive;
        private set { if (Set(ref _isLive, value)) { Raise(nameof(IsNotLive)); Raise(nameof(LiveBadge)); } }
    }
    public bool IsNotLive => !IsLive;
    public string LiveBadge => IsLive ? "IN ONDA" : "FERMO";

    private ImageSource? _currentPreview;
    public ImageSource? CurrentPreview { get => _currentPreview; private set => Set(ref _currentPreview, value); }

    private ImageSource? _nextPreview;
    public ImageSource? NextPreview { get => _nextPreview; private set => Set(ref _nextPreview, value); }

    /// <summary>Thumbnail of the slide on air (null on a tappo or when stopped).</summary>
    public SlideThumb? LiveThumb => _liveThumb;

    private string _currentLabel = "—";
    public string CurrentLabel { get => _currentLabel; private set => Set(ref _currentLabel, value); }

    private string _nextLabel = "—";
    public string NextLabel { get => _nextLabel; private set => Set(ref _nextLabel, value); }

    private string _positionText = "";
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }

    private string _statusMessage = "Pronto";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public bool RemoteEnabled
    {
        get => _settings.RemoteEnabled;
        set { if (_settings.RemoteEnabled != value) { _settings.RemoteEnabled = value; Raise(); _settings.Save(); ApplyRemoteSettings(); } }
    }

    public int RemotePort
    {
        get => _settings.RemotePort;
        set
        {
            value = Math.Clamp(value, 1, 65535);
            if (_settings.RemotePort != value) { _settings.RemotePort = value; Raise(); _settings.Save(); ApplyRemoteSettings(); }
        }
    }

    private string _remoteInfo = "";
    public string RemoteInfo { get => _remoteInfo; private set => Set(ref _remoteInfo, value); }

    // ---------------------------------------------------------------- output targets

    public void RefreshTargets()
    {
        var wanted = _selectedTarget?.Id ?? _settings.OutputTargetId;
        var latest = Displays.GetTargets();
        Targets.Clear();
        foreach (var target in latest) Targets.Add(target);

        // Default: a secondary monitor if there is one; never silently the operator's own screen.
        _selectedTarget = Targets.FirstOrDefault(t => t.Id == wanted)
                          ?? Targets.FirstOrDefault(t => !t.IsWindow && !t.IsPrimary)
                          ?? Targets.First(t => t.IsWindow);
        Raise(nameof(SelectedTarget));

        var wantedPresenter = _selectedPresenterTarget?.Id ?? _settings.PresenterTargetId;
        PresenterTargets.Clear();
        PresenterTargets.Add(new OutputTarget(NoPresenterId, "Nessuno", null, false));
        foreach (var target in latest.Where(t => !t.IsWindow)) PresenterTargets.Add(target);
        // Default, only on a PC with three or more screens: the one that is neither the
        // operator's (primary) nor the output.
        _selectedPresenterTarget = PresenterTargets.FirstOrDefault(t => t.Id == wantedPresenter)
                                   ?? (wantedPresenter is null && latest.Count(t => !t.IsWindow) >= 3
                                       ? PresenterTargets.FirstOrDefault(t => !t.IsWindow && t.Bounds is not null && !t.IsPrimary && t.Id != _selectedTarget.Id)
                                       : null)
                                   ?? PresenterTargets[0];
        Raise(nameof(SelectedPresenterTarget));
    }

    // ---------------------------------------------------------------- folder loading

    public async Task LoadFolderAsync(string folder)
    {
        Stop();
        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();

        foreach (var deck in Decks) deck.EnabledChanged -= OnDeckEnabledChanged;
        Decks.Clear();
        ClearCache();
        _steps = new();
        _stepDecks = new();
        _index = -1;

        Folder = folder;
        _settings.LastFolder = folder;
        _settings.Save();

        var show = ShowFile.Load(folder);
        _loadingShow = true;
        TappoAtStart = show.TappoAtStart;
        TappoBetween = show.TappoBetween;
        TappoAtEnd = show.TappoAtEnd;
        FadeMs = show.FadeMs;
        _loadingShow = false;
        await LoadTappoAsync(ShowFile.ResolveTappoPath(folder, show.Tappo));

        foreach (var name in ShowFile.ApplyOrder(ScanFolder(folder, TappoNameInFolder()), show.Order))
        {
            var deck = new Deck(Path.Combine(folder, name));
            // Set before subscribing: no sequence rebuild per deck while loading.
            deck.IsEnabled = !show.Disabled.Contains(name, StringComparer.OrdinalIgnoreCase);
            deck.EnabledChanged += OnDeckEnabledChanged;
            Decks.Add(deck);
        }
        SelectedDeck = Decks.FirstOrDefault();
        UpdateLabels();

        if (Decks.Count == 0)
        {
            StatusMessage = "Nella cartella non ci sono presentazioni (PowerPoint, PDF o immagini)";
            return;
        }
        await PrepareDecksAsync(Decks.ToList(), _loadCts.Token);
    }

    /// <summary>Re-reads the folder: new files are added at the end, removed ones dropped, changed ones reloaded.</summary>
    public async Task RefreshFolderAsync()
    {
        if (Folder is null) return;
        var names = ScanFolder(Folder, TappoNameInFolder());
        var toPrepare = new List<Deck>();

        for (var i = Decks.Count - 1; i >= 0; i--)
        {
            var deck = Decks[i];
            var stillThere = names.Contains(deck.Name, StringComparer.OrdinalIgnoreCase);
            var fresh = stillThere ? new Deck(deck.FilePath) : null;
            if (fresh is not null && fresh.Stamp == deck.Stamp && deck.State != DeckState.Error) continue;

            deck.EnabledChanged -= OnDeckEnabledChanged;
            DropFromCache(deck);
            if (fresh is null) { Decks.RemoveAt(i); continue; }

            fresh.IsEnabled = deck.IsEnabled;
            fresh.EnabledChanged += OnDeckEnabledChanged;
            Decks[i] = fresh;
            toPrepare.Add(fresh);
        }
        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (Decks.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var deck = new Deck(Path.Combine(Folder, name));
            deck.EnabledChanged += OnDeckEnabledChanged;
            Decks.Add(deck);
            toPrepare.Add(deck);
        }

        RebuildSequence();
        SaveShow();
        StatusMessage = toPrepare.Count == 0 ? "Cartella aggiornata: nessuna novità" : $"Cartella aggiornata: {toPrepare.Count} file da caricare";
        await PrepareDecksAsync(toPrepare, _loadCts.Token);
    }

    /// <summary>Copies files dropped from Explorer into the show folder, then refreshes.</summary>
    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        if (Folder is null) { StatusMessage = "Scegli prima la cartella"; return; }
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !Deck.IsSupported(path)) continue;
            var destination = Path.Combine(Folder, Path.GetFileName(path));
            try
            {
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    await Task.Run(() => File.Copy(path, destination, true));
            }
            catch (Exception ex) { StatusMessage = $"Impossibile copiare {Path.GetFileName(path)}: {ex.Message}"; }
        }
        await RefreshFolderAsync();
    }

    /// <summary>The tappo's file name when it lives in the show folder (so it is not also listed as a slide).</summary>
    private string? TappoNameInFolder()
        => TappoPath is not null && Folder is not null
           && string.Equals(Path.GetDirectoryName(TappoPath)?.TrimEnd('\\', '/'), Folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(TappoPath) : null;

    private static List<string> ScanFolder(string folder, string? excludeName)
        => Directory.EnumerateFiles(folder)
            .Where(Deck.IsSupported)
            .Select(p => Path.GetFileName(p)!)
            .Where(n => !string.Equals(n, excludeName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private async Task PrepareDecksAsync(List<Deck> decks, CancellationToken token)
    {
        // Pass 1: make every deck ready (page counts), so the show can start as soon as possible.
        foreach (var deck in decks)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                if (deck.IsImage)
                {
                    deck.PageCount = 1;
                }
                else
                {
                    var pdfPath = deck.FilePath;
                    if (deck.IsPresentation)
                    {
                        deck.State = DeckState.Converting;
                        StatusMessage = $"Conversione di {deck.Name}…";
                        pdfPath = await PresentationConverter.ConvertAsync(deck.FilePath, _settings.SofficePath, token);
                    }
                    deck.Pdf = await SlideRenderer.OpenPdfAsync(pdfPath);
                    deck.PageCount = (int)deck.Pdf.PageCount;
                }
                if (token.IsCancellationRequested) return;
                for (var i = 0; i < deck.PageCount; i++) deck.Thumbs.Add(new SlideThumb(deck, i));
                deck.State = DeckState.Ready;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                App.LogError(ex);
                deck.Error = ex.Message;
                deck.State = DeckState.Error;
            }
            RebuildSequence();
        }

        var failed = Decks.Count(d => d.State == DeckState.Error);
        StatusMessage = failed == 0
            ? $"Pronto: {Decks.Count(d => d.State == DeckState.Ready)} file, {_steps.Count(s => s.Kind == StepKind.Slide)} slide in scaletta"
            : $"Attenzione: {failed} file non caricati (vedi la scaletta)";

        // Pass 2: thumbnails, in the background.
        foreach (var deck in decks)
        {
            if (deck.State != DeckState.Ready) continue;
            foreach (var thumb in deck.Thumbs.ToList())
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    thumb.Image = deck.IsImage ? await SlideRenderer.LoadImageAsync(deck.FilePath, ThumbWidth, ThumbHeight)
                        : await SlideRenderer.RenderPdfPageAsync(deck.Pdf!, thumb.Index, ThumbWidth, ThumbHeight);
                }
                catch (Exception ex) { App.LogError(ex); }
            }
        }
    }

    // ---------------------------------------------------------------- tappo

    public async Task SetTappoAsync(string? path)
    {
        await LoadTappoAsync(path);
        // An image chosen as tappo from inside the show folder must not also be a slide.
        if (TappoPath is not null && Decks.FirstOrDefault(d => string.Equals(d.FilePath, TappoPath, StringComparison.OrdinalIgnoreCase)) is { } deck)
        {
            deck.EnabledChanged -= OnDeckEnabledChanged;
            DropFromCache(deck);
            Decks.Remove(deck);
        }
        RebuildSequence();
        SaveShow();
        if (IsLive) await ShowCurrentAsync();
    }

    private async Task LoadTappoAsync(string? path)
    {
        _tappoImage = null;
        TappoPreview = null;
        TappoPath = null;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // Decoded once at up to 4K: the tappo must be instantly available at any moment.
            _tappoImage = await SlideRenderer.LoadImageAsync(path, 3840, 2160);
            TappoPreview = _tappoImage;
            TappoPath = path;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            StatusMessage = $"Tappo non caricato ({Path.GetFileName(path)}): {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- sequence

    private void OnDeckEnabledChanged()
    {
        RebuildSequence();
        SaveShow();
    }

    private void OnShowOptionChanged()
    {
        if (_loadingShow) return;
        RebuildSequence();
        SaveShow();
    }

    public void MoveDeck(Deck deck, int newIndex)
    {
        var oldIndex = Decks.IndexOf(deck);
        newIndex = Math.Clamp(newIndex, 0, Decks.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        Decks.Move(oldIndex, newIndex);
        SelectedDeck = deck;
        RebuildSequence();
    }

    /// <summary>
    /// Rebuilds the flattened show after any change (order, ticks, tappo options, a deck becoming
    /// ready), keeping the output on the slide it is showing.
    /// </summary>
    public void RebuildSequence()
    {
        Deck? currentDeck = null;
        var currentPage = -1;
        var currentKind = StepKind.Slide;
        var hadPosition = _index >= 0 && _index < _steps.Count;
        if (hadPosition)
        {
            var step = _steps[_index];
            currentKind = step.Kind;
            currentPage = step.PageIndex;
            currentDeck = step.DeckIndex >= 0 ? _stepDecks[step.DeckIndex] : null;
        }

        _stepDecks = Decks.ToList();
        _steps = ShowSequence.Build(_stepDecks.Select(d => d.ActivePageCount).ToList(), _tappoImage is not null,
            TappoAtStart, TappoBetween, TappoAtEnd);

        if (hadPosition)
        {
            var deckIndex = currentDeck is null ? -1 : _stepDecks.IndexOf(currentDeck);
            var found = ShowSequence.Find(_steps, currentKind, deckIndex, currentPage);
            // The slide on air is gone from the show (deck unticked/removed): stay near the same place.
            _index = found >= 0 ? found : Math.Min(_index, _steps.Count - 1);
        }

        UpdateLabels();
        if (IsLive && _index >= 0) _ = PrerenderAroundAsync(_showTicket);
    }

    public void SaveShow()
    {
        if (Folder is null || _loadingShow) return;
        try
        {
            new ShowFile
            {
                Order = Decks.Select(d => d.Name).ToList(),
                Disabled = Decks.Where(d => !d.IsEnabled).Select(d => d.Name).ToList(),
                Tappo = ShowFile.MakeTappoReference(Folder, TappoPath),
                TappoAtStart = TappoAtStart,
                TappoBetween = TappoBetween,
                TappoAtEnd = TappoAtEnd,
                FadeMs = FadeMs
            }.Save(Folder);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            StatusMessage = "Impossibile salvare la scaletta nella cartella: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- show control

    /// <summary>Opens the output and goes on air from the first step of the show.</summary>
    public Task StartAsync() => StartAtAsync(0);

    private async Task StartAtAsync(int index)
    {
        if (_steps.Count == 0)
        {
            StatusMessage = "Niente da mandare in onda: nessuna slide pronta";
            return;
        }
        if (!IsLive)
        {
            RefreshTargets();
            var target = SelectedTarget!;
            ClearCache();
            _renderWidth = target.Bounds?.Width ?? 1920;
            _renderHeight = target.Bounds?.Height ?? 1080;

            _output = new OutputWindow(target) { KeyHandler = HandleKey };
            var window = _output;
            // The presenter screen repeats the output (PDF, images, tappo); during a presentation
            // played by its own program it shows that program's presenter view instead.
            if (SelectedPresenterTarget is { Bounds: { } presenterBounds } presenterTarget && presenterBounds != target.Bounds)
            {
                window.Mirror = new OutputWindow(presenterTarget);
                window.Mirror.Show();
            }
            window.Closed += (_, _) =>
            {
                // The test window can be closed with its own X button.
                if (ReferenceEquals(_output, window)) { _output = null; Stop(); }
            };
            window.Show();
            IsLive = true;
            // LibreOffice / PowerPoint must never keep the keyboard: the operator's keys and clicker
            // drive LiveSlides for the whole show.
            if (Application.Current?.MainWindow is { } main)
                _focusGuard = new FocusGuard(new System.Windows.Interop.WindowInteropHelper(main).Handle, main.Dispatcher);
            PreparePlayer(target);
        }
        await GoToAsync(index);
    }

    public void Stop()
    {
        var window = _output;
        _output = null;
        window?.Close();
        _focusGuard?.Dispose();
        _focusGuard = null;
        StopPlayer();
        if (!IsLive) return;
        IsLive = false;
        _showTicket++;
        _index = -1;
        _tappoOverride = false;
        CurrentPreview = null;
        NextPreview = null;
        UpdateLabels();
        StatusMessage = "Uscita chiusa";
    }

    public Task NextAsync() => StepAsync(+1);
    public Task PreviousAsync() => StepAsync(-1);

    private Task StepAsync(int delta)
    {
        if (!IsLive) return Task.CompletedTask;
        // Like PowerPoint's "B" key: the first press after a manual tappo just returns to the slide.
        if (_tappoOverride) return GoToAsync(_index);
        // Inside a presentation played by its own program, the key goes to that program.
        if (NativeDeckAt(_index) is not null) return StepNativeAsync(delta);
        var target = _index + delta;
        if (target < 0 || target >= _steps.Count) return Task.CompletedTask;
        return GoToAsync(target);
    }

    public Task FirstAsync() => IsLive ? GoToAsync(0) : Task.CompletedTask;
    public Task LastAsync() => IsLive ? GoToAsync(_steps.Count - 1) : Task.CompletedTask;

    /// <summary>Jumps to a slide (double-click on a thumbnail, remote "goto"); goes on air if needed.</summary>
    public Task GoToSlideAsync(Deck deck, int pageIndex)
    {
        var index = ShowSequence.Find(_steps, StepKind.Slide, _stepDecks.IndexOf(deck), pageIndex);
        if (index < 0)
        {
            StatusMessage = "Questa slide non è in scaletta (presentazione disattivata?)";
            return Task.CompletedTask;
        }
        return StartAtAsync(index);
    }

    /// <summary>Shows the tappo right now, without losing the position; again to return to the slide.</summary>
    public async Task ToggleTappoAsync()
    {
        if (!IsLive) return;
        if (_tappoImage is null) { StatusMessage = "Nessuna immagine tappo scelta"; return; }
        if (_tappoOverride) { await GoToAsync(_index); return; }
        if (_index >= 0 && _steps[_index].Kind == StepKind.Tappo) return;

        _tappoOverride = true;
        _showTicket++;
        if (!await CoverNativeAsync(_tappoImage, FadeMs)) _output?.Show(_tappoImage, FadeMs);
        CurrentPreview = _tappoImage;
        UpdateLabels();
    }

    private async Task GoToAsync(int index)
    {
        if (_steps.Count == 0) return;
        _index = Math.Clamp(index, 0, _steps.Count - 1);
        _tappoOverride = false;
        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        if (_index < 0 || _index >= _steps.Count) return;
        var ticket = ++_showTicket;
        UpdateLabels();

        // Until the new slide is rendered the previous one simply stays on air: no black frame.
        var step = _steps[_index];
        if (NativeDeckAt(_index) is { } nativeDeck)
        {
            if (await ShowNativeAsync(nativeDeck, step.PageIndex, ticket))
            {
                if (ticket == _showTicket) await PrerenderAroundAsync(ticket);
                if (ticket == _showTicket) PreloadNative();
                return;
            }
            if (ticket != _showTicket) return;
        }
        var image = await GetStepImageAsync(step);
        if (ticket != _showTicket) return; // a newer command arrived meanwhile
        if (image is null)
        {
            StatusMessage = "Slide non leggibile: resta in onda la precedente";
        }
        else
        {
            // Leaving a presentation played by its own program: this image fades in over it.
            if (!await CoverNativeAsync(image, FadeMs)) _output?.Show(image, FadeMs);
            CurrentPreview = image;
        }
        await PrerenderAroundAsync(ticket);
        if (ticket == _showTicket) PreloadNative();
    }

    /// <summary>Renders the neighbours ahead of time so the next press is instant.</summary>
    private async Task PrerenderAroundAsync(int ticket)
    {
        if (_index < 0) return;
        var next = _index + 1 < _steps.Count ? await GetStepImageAsync(_steps[_index + 1]) : null;
        if (ticket != _showTicket) return;
        NextPreview = next;
        if (_index + 2 < _steps.Count) _ = GetStepImageAsync(_steps[_index + 2]);
        if (_index - 1 >= 0) _ = GetStepImageAsync(_steps[_index - 1]);
    }

    private Task<BitmapSource?> GetStepImageAsync(ShowStep step)
    {
        if (step.Kind == StepKind.Tappo) return Task.FromResult(_tappoImage);

        (Deck Deck, int Page) key = (_stepDecks[step.DeckIndex], step.PageIndex);
        if (_cache.TryGetValue(key, out var cached))
        {
            _cacheOrder.Remove(key);
            _cacheOrder.Add(key);
            return cached;
        }

        var task = RenderAsync(key.Deck, key.Page);
        _cache[key] = task;
        _cacheOrder.Add(key);
        while (_cacheOrder.Count > CacheCapacity)
        {
            _cache.Remove(_cacheOrder[0]);
            _cacheOrder.RemoveAt(0);
        }
        return task;
    }

    private async Task<BitmapSource?> RenderAsync(Deck deck, int page)
    {
        try
        {
            return deck.IsImage ? await SlideRenderer.LoadImageAsync(deck.FilePath, _renderWidth, _renderHeight)
                : await SlideRenderer.RenderPdfPageAsync(deck.Pdf!, page, _renderWidth, _renderHeight);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return null;
        }
    }

    private void ClearCache()
    {
        _cache.Clear();
        _cacheOrder.Clear();
    }

    private void DropFromCache(Deck deck)
    {
        foreach (var key in _cacheOrder.Where(k => ReferenceEquals(k.Deck, deck)).ToList())
        {
            _cache.Remove(key);
            _cacheOrder.Remove(key);
        }
    }

    // ---------------------------------------------------------------- labels / highlights

    private string Describe(int index)
    {
        if (index < 0 || index >= _steps.Count) return "—";
        var step = _steps[index];
        if (step.Kind == StepKind.Tappo) return "TAPPO";
        var deck = _stepDecks[step.DeckIndex];
        return $"{deck.Name} — slide {step.PageIndex + 1} di {deck.PageCount}";
    }

    private void UpdateLabels()
    {
        var onAir = IsLive && _index >= 0 && _index < _steps.Count;
        var step = onAir ? _steps[_index] : (ShowStep?)null;

        CurrentLabel = !onAir ? "—" : _tappoOverride ? "TAPPO (manuale)" : Describe(_index);
        NextLabel = !onAir ? "—"
            : _tappoOverride ? "torna a: " + Describe(_index)
            : _index + 1 < _steps.Count ? Describe(_index + 1) : "fine della scaletta";
        PositionText = _steps.Count == 0 ? "" : onAir ? $"{_index + 1} / {_steps.Count}" : $"{_steps.Count} passi in scaletta";

        Deck? liveDeck = null;
        SlideThumb? liveThumb = null;
        if (step is { Kind: StepKind.Slide } slide)
        {
            liveDeck = _stepDecks[slide.DeckIndex];
            if (slide.PageIndex < liveDeck.Thumbs.Count) liveThumb = liveDeck.Thumbs[slide.PageIndex];
        }

        if (!ReferenceEquals(_liveDeck, liveDeck))
        {
            if (_liveDeck is not null) _liveDeck.IsLive = false;
            if (liveDeck is not null)
            {
                liveDeck.IsLive = true;
                // Follow the show: the thumbnails panel always shows the deck on air.
                SelectedDeck = liveDeck;
            }
            _liveDeck = liveDeck;
        }
        // After the deck switch, so the thumbnails panel already shows the right deck when
        // the main window scrolls the slide on air into view.
        if (!ReferenceEquals(_liveThumb, liveThumb))
        {
            if (_liveThumb is not null) _liveThumb.IsLive = false;
            if (liveThumb is not null) liveThumb.IsLive = true;
            _liveThumb = liveThumb;
            Raise(nameof(LiveThumb));
        }
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>Show keys, shared by the main window and the test output window.</summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.F5:
                if (!IsLive) _ = StartAsync();
                return true;
            case Key.Escape:
                if (!IsLive) return false;
                Stop();
                return true;
        }
        if (!IsLive) return false;

        switch (key)
        {
            case Key.Right: case Key.Down: case Key.PageDown: case Key.Space: case Key.Enter:
                _ = NextAsync(); return true;
            case Key.Left: case Key.Up: case Key.PageUp: case Key.Back:
                _ = PreviousAsync(); return true;
            case Key.Home: _ = FirstAsync(); return true;
            case Key.End: _ = LastAsync(); return true;
            case Key.T: case Key.B: case Key.OemPeriod:
                _ = ToggleTappoAsync(); return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- remote API

    private void ApplyRemoteSettings()
    {
        _remote.Stop();
        if (!_settings.RemoteEnabled) { RemoteInfo = "Controllo remoto spento"; return; }
        if (!_remote.Start(_settings.RemotePort)) { RemoteInfo = _remote.Error ?? "Controllo remoto non disponibile"; return; }
        var addresses = RemoteApi.LocalAddresses();
        var host = addresses.Count > 0 ? addresses[0] : "127.0.0.1";
        RemoteInfo = $"http://{host}:{_settings.RemotePort}/api/next · prev · start · stop · tappo · goto?deck=1&slide=1 · status";
    }

    // Called on a server thread: everything that touches the show runs on the UI thread.
    private string? HandleRemote(string command, Dictionary<string, string> args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return null;
        return dispatcher.Invoke<string?>(() =>
        {
            switch (command)
            {
                case "start": _ = StartAsync(); break;
                case "stop": Stop(); break;
                case "next": _ = NextAsync(); break;
                case "prev": _ = PreviousAsync(); break;
                case "first": _ = FirstAsync(); break;
                case "last": _ = LastAsync(); break;
                case "tappo": _ = ToggleTappoAsync(); break;
                case "goto":
                    var active = Decks.Where(d => d.ActivePageCount > 0).ToList();
                    if (!args.TryGetValue("deck", out var d) || !int.TryParse(d, out var deckNumber) || deckNumber < 1 || deckNumber > active.Count)
                        return "{\"ok\":false,\"error\":\"deck non valido\"}";
                    var slideNumber = args.TryGetValue("slide", out var s) && int.TryParse(s, out var parsed) ? parsed : 1;
                    _ = GoToSlideAsync(active[deckNumber - 1], Math.Clamp(slideNumber, 1, active[deckNumber - 1].PageCount) - 1);
                    break;
                case "status": break;
                default: return null;
            }
            return StatusJson();
        });
    }

    private string StatusJson()
    {
        var onAir = IsLive && _index >= 0 && _index < _steps.Count;
        var step = onAir ? _steps[_index] : (ShowStep?)null;
        var isTappo = onAir && (_tappoOverride || step!.Value.Kind == StepKind.Tappo);
        var deck = step is { DeckIndex: >= 0 } st ? _stepDecks[st.DeckIndex] : null;
        return "{\"ok\":true"
               + $",\"live\":{(IsLive ? "true" : "false")}"
               + $",\"step\":{(onAir ? _index + 1 : 0)}"
               + $",\"steps\":{_steps.Count}"
               + $",\"tappo\":{(isTappo ? "true" : "false")}"
               + $",\"deck\":{RemoteApi.JsonString(deck?.Name)}"
               + $",\"slide\":{(step is { Kind: StepKind.Slide } sl ? sl.PageIndex + 1 : 0)}"
               + $",\"slides\":{deck?.PageCount ?? 0}"
               + $",\"current\":{RemoteApi.JsonString(CurrentLabel)}"
               + $",\"next\":{RemoteApi.JsonString(NextLabel)}"
               + "}";
    }

    public void Shutdown()
    {
        _loadCts.Cancel();
        _remote.Dispose();
        Stop();
        _player?.Dispose();
    }
}
