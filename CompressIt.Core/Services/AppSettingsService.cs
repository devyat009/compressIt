using System.Text.Json;
using CompressIt.Core.Models;

namespace CompressIt.Core.Services;

public sealed class AppSettings
{
    public bool UseGpuAcceleration { get; set; } = true;
    public string? CustomFfmpegDirectory { get; set; }
    public bool ShellIntegrationEnabled { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public VideoCodec DefaultCodec { get; set; } = VideoCodec.H264;
    public EncodingSpeed DefaultSpeed { get; set; } = EncodingSpeed.Balanced;
    public int DefaultQualityPercent { get; set; } = 85;
}

public static class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompressIt");

    public static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall back to default settings if corrupted.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }
}
