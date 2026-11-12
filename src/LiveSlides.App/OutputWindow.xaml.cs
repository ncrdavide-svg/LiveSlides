using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LiveSlides.App;

/// <summary>
/// The show output. On a monitor it is a borderless, always-on-top window that can never take
/// keyboard focus (the operator keeps driving from the main window); as a "test window" it is an
/// ordinary resizable window, for trying the app on a single-screen PC.
/// </summary>
public partial class OutputWindow : Window
{
    private ImageSource? _pending;

    /// <summary>Keys pressed while the test window has focus are forwarded to the main window's handler.</summary>
    public Func<Key, bool>? KeyHandler { get; set; }

    public OutputWindow(OutputTarget target)
    {
        InitializeComponent();

        if (target.Bounds is { } bounds)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Focusable = false;
            Cursor = Cursors.None;

            // Rough initial placement only (WPF units); the exact pixel rect is applied once the
            // native window exists, and again after WPF finishes its own initial layout.
            Left = bounds.X;
            Top = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Win32WindowPlacement.SetNoActivate(hwnd);
                Win32WindowPlacement.ApplyPixelBounds(hwnd, bounds);
            };
            Loaded += (_, _) => Win32WindowPlacement.ApplyPixelBounds(new WindowInteropHelper(this).Handle, bounds);
        }
        else
        {
            Title = "LiveSlides — uscita (finestra di prova)";
            Width = 976;
            Height = 579;
            Left = 80;
            Top = 80;
            PreviewKeyDown += (_, e) =>
            {
                if (KeyHandler?.Invoke(e.Key) == true) e.Handled = true;
            };
        }
    }

    /// <summary>Puts an image on air, cutting (fadeMs = 0) or cross-fading from the current one.</summary>
    public void Show(ImageSource image, int fadeMs)
    {
        // A fade still running is completed instantly, so rapid presses never queue up.
        FinishFade();

        if (fadeMs <= 0 || LayerA.Source is null)
        {
            LayerA.Source = image;
            return;
        }

        _pending = image;
        LayerB.Source = image;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeMs)) { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            // Ignore the Completed of a fade that FinishFade already replaced.
            if (ReferenceEquals(_pending, image)) FinishFade();
        };
        LayerB.BeginAnimation(OpacityProperty, animation);
    }

    private void FinishFade()
    {
        if (_pending is null) return;
        LayerA.Source = _pending;
        _pending = null;
        LayerB.BeginAnimation(OpacityProperty, null);
        LayerB.Opacity = 0;
        LayerB.Source = null;
    }
}
