using System.Text.Json;

namespace GlassesDesk;

/// <summary>User-tunable settings, saved to %LOCALAPPDATA%\ProjectGlasses\settings.json.</summary>
internal sealed class Settings
{
    /// <summary>Diagonal field of view of the glasses. Wrong values make panels drift as you turn.</summary>
    public float FovDegrees { get; set; } = 46f;

    /// <summary>Angular width of a 16:9 panel.</summary>
    public float PanelWidthDegrees { get; set; } = 38f;

    public float GapDegrees { get; set; } = 3f;

    /// <summary>How far ahead to extrapolate head motion to hide latency.</summary>
    public float PredictMilliseconds { get; set; } = 16f;

    /// <summary>Gyro bias from the last session (°/s), so tracking starts without drift.</summary>
    public float[]? GyroBiasDps { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectGlasses", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't read settings ({ex.Message}); using defaults.");
        }
        return new Settings();
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
            Console.WriteLine($"Couldn't save settings: {ex.Message}");
        }
    }

    public override string ToString() =>
        $"FOV {FovDegrees:F1}°, panel {PanelWidthDegrees:F1}°, gap {GapDegrees:F1}°, prediction {PredictMilliseconds:F0} ms";
}
