using System.Windows;
using System.Windows.Controls;
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
        Closed += (_, _) => Mirror?.Close();

        if (target.Bounds is { } bounds)
        {
            MonitorBounds = bounds;
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

    /// <summary>
    /// The presenter screen's window, when there is one: it repeats everything this window shows
    /// (PDF slides, images, the tappo). While a presentation is played by its own program, it
    /// steps aside like this window does, uncovering that program's presenter console, but only
    /// if <see cref="MirrorHasConsole"/>; otherwise it keeps showing what it is given.
    /// </summary>
    public OutputWindow? Mirror { get; set; }

    /// <summary>The program playing the presentation has put its presenter console under <see cref="Mirror"/>.</summary>
    public bool MirrorHasConsole { get; set; }

    /// <summary>The screen this window fills, or null for the resizable test window.</summary>
    public ScreenBounds? MonitorBounds { get; private set; }

    /// <summary>True while the window is hidden to let a presentation program's own show be seen.</summary>
    public bool IsCloaked { get; private set; }

    /// <summary>
    /// Hides this window (keeping it rendered) so the slide show running underneath is on air,
    /// or puts it back on top with whatever it currently shows.
    /// </summary>
    public void SetCloaked(bool cloaked)
    {
        if (Mirror is { } mirror && (!cloaked || MirrorHasConsole)) mirror.SetCloakedCore(cloaked);
        SetCloakedCore(cloaked);
    }

    /// <summary>
    /// Fades this whole window away, uncovering the slide show running underneath, then hides it.
    /// The show is live while it appears: its own animations are never missed or frozen.
    /// </summary>
    public Task FadeToCloakAsync(int fadeMs)
        => Mirror is { } mirror && MirrorHasConsole
            ? Task.WhenAll(FadeToCloakCoreAsync(fadeMs), mirror.FadeToCloakCoreAsync(fadeMs))
            : FadeToCloakCoreAsync(fadeMs);

    private async Task FadeToCloakCoreAsync(int fadeMs)
    {
        FinishFade();
        if (fadeMs > 0 && MonitorBounds is { } bounds && Snapshot(bounds) is { } picture)
        {
            // A stand-in showing the same picture takes our place and does the fading.
            using var overlay = new FadeOverlay(bounds, picture, 255);
            await Task.Delay(30); // on screen before we step aside
            SetCloakedCore(true);
            await overlay.FadeAsync(255, 0, fadeMs);
        }
        else SetCloakedCore(true);
    }

    private void SetCloakedCore(bool cloaked)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!cloaked) Win32WindowPlacement.BringToTop(hwnd);
        NativeWindows.SetCloaked(hwnd, cloaked);
        IsCloaked = cloaked;
    }

    /// <summary>The opposite: what this window now shows fades in over the running slide show.</summary>
    public Task FadeFromCloakAsync(int fadeMs)
        => Mirror is { IsCloaked: true } mirror
            ? Task.WhenAll(FadeFromCloakCoreAsync(fadeMs), mirror.FadeFromCloakCoreAsync(fadeMs))
            : FadeFromCloakCoreAsync(fadeMs);

    private async Task FadeFromCloakCoreAsync(int fadeMs)
    {
        if (fadeMs > 0 && MonitorBounds is { } bounds && Snapshot(bounds) is { } picture)
        {
            using var overlay = new FadeOverlay(bounds, picture, 0);
            await overlay.FadeAsync(0, 255, fadeMs);
            SetCloakedCore(false);
            await Task.Delay(60); // we are back on screen before the stand-in goes
        }
        else SetCloakedCore(false);
    }

    /// <summary>What this window is showing, as a picture of the monitor's pixel size.</summary>
    private System.Windows.Media.Imaging.BitmapSource? Snapshot(ScreenBounds bounds)
    {
        if (Content is not FrameworkElement root || root.ActualWidth <= 0 || root.ActualHeight <= 0) return null;
        root.UpdateLayout();
        var scale = bounds.Width / root.ActualWidth;
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(bounds.Width, bounds.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        target.Render(root);
        target.Freeze();
        return target;
    }

    /// <summary>Back above a slide show window that has just opened on the same monitor.</summary>
    public void BringToTop()
    {
        Win32WindowPlacement.BringToTop(new WindowInteropHelper(this).Handle);
        Mirror?.BringToTop();
    }

    public bool NothingOnAir => LayerA.Source is null;

    /// <summary>Puts an image on air, cutting (fadeMs = 0) or cross-fading from the current one.</summary>
    public void Show(ImageSource image, int fadeMs)
    {
        Mirror?.Show(image, fadeMs);
        // A fade still running is completed instantly, so rapid presses never queue up.
        FinishFade();

        if (fadeMs <= 0 || NothingOnAir)
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
