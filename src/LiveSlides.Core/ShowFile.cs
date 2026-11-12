using System.Text.Json;

namespace LiveSlides.Core;

/// <summary>
/// Per-folder show settings, saved as "liveslides.json" inside the folder itself so the running
/// order travels with the files (copy the folder to another PC and the show is the same).
/// </summary>
public class ShowFile
{
    public const string FileName = "liveslides.json";

    /// <summary>File names (not paths) in running order.</summary>
    public List<string> Order { get; set; } = new();
    /// <summary>File names excluded from the show.</summary>
    public List<string> Disabled { get; set; } = new();
    /// <summary>Tappo image: a bare file name when it lives in the show folder, else a full path.</summary>
    public string? Tappo { get; set; }
    public bool TappoAtStart { get; set; } = true;
    public bool TappoBetween { get; set; } = true;
    public bool TappoAtEnd { get; set; } = true;
    public int FadeMs { get; set; } = 300;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ShowFile Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ShowFile>(File.ReadAllText(path)) ?? new ShowFile();
        }
        catch
        {
            // A corrupt settings file must not stop the folder from loading: start from defaults.
        }
        return new ShowFile();
    }

    public void Save(string folder)
        => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options));

    /// <summary>
    /// Running order for the files currently in the folder: the saved order first (dropping
    /// files that no longer exist), then any new file in alphabetical order at the end.
    /// </summary>
    public static List<string> ApplyOrder(IEnumerable<string> namesInFolder, IReadOnlyList<string> savedOrder)
    {
        var present = new HashSet<string>(namesInFolder, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in savedOrder)
            if (present.Remove(name)) result.Add(name);
        result.AddRange(present.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    public static string? ResolveTappoPath(string folder, string? tappo)
    {
        if (string.IsNullOrWhiteSpace(tappo)) return null;
        return Path.IsPathRooted(tappo) ? tappo : Path.Combine(folder, tappo);
    }

    public static string? MakeTappoReference(string folder, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;
        var dir = Path.GetDirectoryName(fullPath);
        return string.Equals(dir?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(fullPath)
            : fullPath;
    }
}
