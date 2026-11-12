using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using Windows.Data.Pdf;

namespace LiveSlides.App;

public enum DeckState { Waiting, Converting, Ready, Error }

/// <summary>One file of the running order: a PDF, a presentation (converted to PDF), or a single image.</summary>
public class Deck : ObservableObject
{
    public static readonly string[] PdfExtensions = { ".pdf" };
    public static readonly string[] PresentationExtensions = { ".pptx", ".ppt", ".ppsx", ".pps", ".pptm", ".odp" };
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    public Deck(string filePath)
    {
        FilePath = filePath;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        IsImage = ImageExtensions.Contains(ext);
        IsPresentation = PresentationExtensions.Contains(ext);
        TypeLabel = IsImage ? "IMG" : IsPresentation ? "PPT" : "PDF";
        var info = new FileInfo(filePath);
        Stamp = info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
    }

    public static bool IsSupported(string path)
    {
        var name = Path.GetFileName(path);
        // "~$name.pptx" (Office) and ".~lock.name#" (LibreOffice) are lock files of open documents.
        if (name.StartsWith("~$") || name.StartsWith(".~")) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return PdfExtensions.Contains(ext) || PresentationExtensions.Contains(ext) || ImageExtensions.Contains(ext);
    }

    public string FilePath { get; }
    public string Name => Path.GetFileName(FilePath);
    public string TypeLabel { get; }
    public bool IsImage { get; }
    public bool IsPresentation { get; }
    /// <summary>Size + modified date at load time: a changed file is reloaded on "Aggiorna".</summary>
    public string Stamp { get; }

    /// <summary>The loaded PDF (the file itself, or the conversion of a presentation). Null for images.</summary>
    public PdfDocument? Pdf { get; set; }

    public ObservableCollection<SlideThumb> Thumbs { get; } = new();

    /// <summary>Raised when the operator ticks/unticks the deck (the view model rebuilds the sequence).</summary>
    public event Action? EnabledChanged;

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (Set(ref _isEnabled, value)) EnabledChanged?.Invoke(); }
    }

    private DeckState _state = DeckState.Waiting;
    public DeckState State
    {
        get => _state;
        set { if (Set(ref _state, value)) Raise(nameof(StatusText)); }
    }

    private int _pageCount;
    public int PageCount
    {
        get => _pageCount;
        set { if (Set(ref _pageCount, value)) Raise(nameof(StatusText)); }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) Raise(nameof(StatusText)); }
    }

    public string StatusText => State switch
    {
        DeckState.Waiting => "in attesa…",
        DeckState.Converting => "conversione in corso…",
        DeckState.Ready => PageCount == 1 ? "1 slide" : $"{PageCount} slide",
        _ => "ERRORE: " + Error
    };

    private bool _isLive;
    /// <summary>True while one of this deck's slides is on air.</summary>
    public bool IsLive { get => _isLive; set => Set(ref _isLive, value); }

    /// <summary>Slides this deck contributes to the show right now.</summary>
    public int ActivePageCount => IsEnabled && State == DeckState.Ready ? PageCount : 0;
}

public class SlideThumb : ObservableObject
{
    public SlideThumb(Deck deck, int index) { Deck = deck; Index = index; }

    public Deck Deck { get; }
    public int Index { get; }
    public int Number => Index + 1;

    private ImageSource? _image;
    public ImageSource? Image { get => _image; set => Set(ref _image, value); }

    private bool _isLive;
    public bool IsLive { get => _isLive; set => Set(ref _isLive, value); }
}
