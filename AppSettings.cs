using System.IO;
using System.Text.Json;

namespace MCHOSE;

/// <summary>
/// User settings stored locally in %LOCALAPPDATA%\MchoseTray\settings.json.
/// The app never sends anything over the network.
/// </summary>
public sealed class AppSettings
{
    public int LowThreshold { get; set; } = 20;
    public int CriticalThreshold { get; set; } = 10;
    public int PollIntervalSeconds { get; set; } = 60;
    public bool Notifications { get; set; } = true;
    public bool AnimateCharging { get; set; } = true;

    public static readonly int[] LowThresholdOptions = [10, 15, 20, 25, 30, 40];
    public static readonly int[] CriticalThresholdOptions = [5, 10, 15];
    public static readonly int[] PollIntervalOptions = [30, 60, 120, 300, 600];

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MchoseTray", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            s = new(); // corrupted file - fall back to defaults
        }
        s.LowThreshold = Math.Clamp(s.LowThreshold, 5, 50);
        s.CriticalThreshold = Math.Clamp(s.CriticalThreshold, 1, s.LowThreshold);
        s.PollIntervalSeconds = Math.Clamp(s.PollIntervalSeconds, 15, 3600);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Failing to save must not crash the app.
        }
    }
}
