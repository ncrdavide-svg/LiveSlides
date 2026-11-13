using System.IO;
using System.Text.Json;

namespace LiveSlides.App;

/// <summary>Per-PC settings (%LOCALAPPDATA%\LiveSlides\settings.json); the show itself lives in the show folder.</summary>
public class AppSettings
{
    public string? LastFolder { get; set; }
    public string? OutputTargetId { get; set; }
    /// <summary>Screen for the presenter view ("none" = no presenter screen; null = not chosen yet).</summary>
    public string? PresenterTargetId { get; set; }
    public bool RemoteEnabled { get; set; } = true;
    public int RemotePort { get; set; } = 8788;
    /// <summary>Manual override for soffice.exe, when LibreOffice is installed somewhere unusual.</summary>
    public string? SofficePath { get; set; }
    /// <summary>Who plays presentations: "auto" (PowerPoint if installed, else LibreOffice), "powerpoint", "libreoffice", or "static" (still images, no animations).</summary>
    public string? PresentationEngine { get; set; }
    /// <summary>LibreOffice's own number for each output screen ("x,y,w,h"), learned the first time.</summary>
    public Dictionary<string, int> LibreOfficeDisplays { get; set; } = new();

    private static string FilePath => Path.Combine(App.DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt settings: defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { App.LogError(ex); }
    }
}
