using System.Globalization;

namespace CompressIt.Core.Services;

public static class FormatHelper
{
    private const double Kb = 1024.0;
    private const double Mb = 1024.0 * 1024.0;
    private const double Gb = 1024.0 * 1024.0 * 1024.0;

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0) bytes = 0;

        if (bytes >= Gb)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", bytes / Gb);
        }

        if (bytes >= Mb)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / Mb);
        }

        if (bytes >= Kb)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", bytes / Kb);
        }

        return $"{bytes} B";
    }

    public static string FormatTimecode(TimeSpan ts, bool includeMilliseconds = true)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;

        var totalHours = (int)ts.TotalHours;
        if (includeMilliseconds)
        {
            int centiseconds = ts.Milliseconds / 10;
            return totalHours > 0
                ? $"{totalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{centiseconds:00}"
                : $"{ts.Minutes:00}:{ts.Seconds:00}.{centiseconds:00}";
        }

        return totalHours > 0
            ? $"{totalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    public static string FormatFfmpegTimestamp(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00}.{3:000}",
            (int)ts.TotalHours,
            ts.Minutes,
            ts.Seconds,
            ts.Milliseconds);
    }

    public static string GetQualityTier(int qualityPercent)
    {
        int clamped = Math.Clamp(qualityPercent, 1, 100);
        return clamped switch
        {
            < 40 => "Low",
            < 75 => "Medium",
            _ => "High"
        };
    }
}
