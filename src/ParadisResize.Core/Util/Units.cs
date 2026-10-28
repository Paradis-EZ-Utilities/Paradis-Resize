using System.Globalization;

namespace ParadisResize.Core.Util;

/// <summary>
/// Size and time formatting, in <b>decimal units</b> as in Paradis Compress:
/// <b>1 KB = 1,000 bytes, 1 MB = 1,000,000 bytes, 1 GB = 1,000,000,000 bytes</b>.
/// (Windows File Explorer counts in binary units of 1,048,576 bytes but also labels them "MB",
/// so it shows a slightly smaller number for the same file.)
/// </summary>
public static class Units
{
    public const long BytesPerKB = 1_000;
    public const long BytesPerMB = 1_000_000;
    public const long BytesPerGB = 1_000_000_000;

    /// <summary>"812 KB", "4.7 MB", "312 MB", "1.24 GB" (decimal units).</summary>
    public static string FormatSize(long bytes)
    {
        var c = CultureInfo.CurrentCulture;
        if (bytes < 0) bytes = 0;
        if (bytes < BytesPerMB) return string.Format(c, "{0:0} KB", Math.Max(bytes > 0 ? 1 : 0, Math.Round(bytes / (double)BytesPerKB)));
        double mb = bytes / (double)BytesPerMB;
        if (mb < 10) return string.Format(c, "{0:0.0} MB", mb);
        if (mb < 1000) return string.Format(c, "{0:0} MB", mb);
        return string.Format(c, "{0:0.00} GB", bytes / (double)BytesPerGB);
    }

    /// <summary>"503,362,712 bytes".</summary>
    public static string FormatBytes(long bytes) => string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", bytes);

    /// <summary>"12000 × 10000".</summary>
    public static string FormatDimensions(int width, int height) =>
        string.Format(CultureInfo.CurrentCulture, "{0} × {1}", width, height);

    /// <summary>
    /// How much smaller <paramref name="newBytes"/> is than <paramref name="originalBytes"/>, as a whole
    /// percentage: "75%". Negative when the new file is bigger. Never shows "100%" for a file that still has bytes.
    /// </summary>
    public static int PercentSaved(long originalBytes, long newBytes)
    {
        if (originalBytes <= 0) return 0;
        double saved = (originalBytes - newBytes) * 100.0 / originalBytes;
        int r = (int)Math.Round(saved);
        if (r >= 100 && newBytes > 0) r = 99;
        return r;
    }

    /// <summary>"1:23:45", "4:05" or "0:07".</summary>
    public static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}"
            : $"{d.Minutes}:{d.Seconds:00}";
    }
}
