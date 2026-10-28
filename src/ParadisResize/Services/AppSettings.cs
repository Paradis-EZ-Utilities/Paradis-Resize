using System.Text.Json;
using ParadisResize.Core.Planning;

namespace ParadisResize.Services;

/// <summary>
/// The few choices remembered between launches: %LocalAppData%\ParadisResize\settings.json.
/// A custom output folder is deliberately not remembered: each session starts with copies going to a
/// "Resized"/"Optimized" folder next to the originals, so no one is surprised where files went.
/// </summary>
public sealed class AppSettings
{
    public JobMode Mode { get; set; } = JobMode.Resize;
    public ResizeBy ResizeBy { get; set; } = ResizeBy.Dimensions;
    public int? Width { get; set; } = 4096;
    public int? Height { get; set; }
    public double Percent { get; set; } = 50;
    public bool KeepProportions { get; set; } = true;
    public bool DontEnlarge { get; set; } = true;
    public OutputFormatChoice OutputFormat { get; set; } = OutputFormatChoice.KeepOriginal;
    public bool AllowFormatConversion { get; set; }
    public bool IncludeSubfolders { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParadisResize", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("Settings could not be read; using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("Settings could not be saved", ex);
        }
    }
}
