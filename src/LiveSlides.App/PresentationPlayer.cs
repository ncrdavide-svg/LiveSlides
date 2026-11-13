using System.IO;

namespace LiveSlides.App;

/// <summary>
/// Where a presentation being played by its own program is.
/// AtStart/AtEnd: one more "previous"/"next" would leave the presentation, so LiveSlides must
/// move to the neighbouring item of the running order instead of passing the key on.
/// </summary>
public readonly record struct PlayerState(bool Running, int Slide, int SlideCount, bool AtStart, bool AtEnd)
{
    public static readonly PlayerState Stopped = new(false, 0, 0, true, true);
}

/// <summary>
/// Plays a presentation with the program it was made for, full screen on the output monitor,
/// so animations, transitions and embedded media run exactly as designed. LiveSlides keeps its
/// own output window on top of it for the tappo and for everything that is not a presentation.
/// </summary>
public interface IPresentationPlayer : IDisposable
{
    /// <summary>Shown to the operator ("PowerPoint", "LibreOffice").</summary>
    string EngineName { get; }

    /// <summary>The slide show window (for grabbing its picture), or zero.</summary>
    IntPtr Window { get; }

    /// <summary>The program's presenter view, moved onto the presenter screen; zero if there is none.</summary>
    IntPtr PresenterWindow { get; }

    /// <summary>
    /// Loads the file in the background, without showing anything, so that putting it on air
    /// later is immediate. Called for the NEXT presentation only: players keep just a couple of
    /// files open, however long the running order. Failures surface in OpenAsync.
    /// </summary>
    Task LoadAsync(string path, ScreenBounds bounds);

    /// <summary>
    /// Opens the file and starts its show on the given screen at <paramref name="startSlide"/>,
    /// hidden behind our window. Whatever was open before is closed. Throws if the file cannot be played.
    /// With <paramref name="presenter">, the program's own presenter view is switched on and moved
    /// onto that screen (under LiveSlides' window there, which then steps aside).
    /// </summary>
    Task<PlayerState> OpenAsync(string path, ScreenBounds bounds, int startSlide, ScreenBounds? presenter);

    /// <summary>
    /// Called just before the show is uncovered: automatic animations and timings of the slide
    /// must start now, in front of the audience, not while it was waiting behind the tappo.
    /// </summary>
    Task BeforeRevealAsync();

    Task<PlayerState> NextAsync();
    Task<PlayerState> PreviousAsync();
    Task<PlayerState> GoToAsync(int slide);
    Task<PlayerState> GetStateAsync();
    Task CloseAsync();
}

public static class PresentationPlayers
{
    /// <summary>
    /// PowerPoint when it is installed (the only program that plays a PowerPoint file exactly as
    /// its author saw it), otherwise LibreOffice Impress, otherwise null (slides as still images).
    /// </summary>
    public static IPresentationPlayer? Create(AppSettings settings)
    {
        var wanted = (settings.PresentationEngine ?? "auto").ToLowerInvariant();
        if (wanted == "static") return null;

        if (wanted != "libreoffice" && Type.GetTypeFromProgID("PowerPoint.Application") is { } powerPoint)
            return new PowerPointPlayer(powerPoint);

        if (wanted != "powerpoint" && PresentationConverter.FindSoffice(settings.SofficePath) is { } soffice)
        {
            var folder = Path.GetDirectoryName(soffice)!;
            var python = Path.Combine(folder, "python.exe");
            var binary = Path.Combine(folder, "soffice.bin");
            if (File.Exists(python) && File.Exists(binary)) return new LibreOfficePlayer(python, binary, settings);
        }
        return null;
    }
}
