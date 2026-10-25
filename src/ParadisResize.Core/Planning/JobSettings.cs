namespace ParadisResize.Core.Planning;

public enum JobMode
{
    /// <summary>Change the pixel dimensions.</summary>
    Resize,

    /// <summary>Keep the dimensions; make the file smaller.</summary>
    Optimize,
}

public enum ResizeBy
{
    Dimensions,
    Percentage,
}

/// <summary>The "Save as" choice. Keep Original is the default.</summary>
public enum OutputFormatChoice
{
    KeepOriginal,
    Png,
    Jpeg,
    WebP,
}

public sealed record ResizeSettings
{
    public ResizeBy By { get; init; } = ResizeBy.Dimensions;

    /// <summary>Wanted width in pixels; null = Auto.</summary>
    public int? Width { get; init; }

    /// <summary>Wanted height in pixels; null = Auto.</summary>
    public int? Height { get; init; }

    public double Percent { get; init; } = 50;

    public bool KeepProportions { get; init; } = true;

    public bool DontEnlarge { get; init; } = true;

    /// <summary>Largest width or height that can be typed. Far beyond any real need; stops typos like 400000.</summary>
    public const int MaxDimension = 65000;

    public const double MaxPercent = 1000;

    /// <summary>Null when the settings can be used, otherwise a short reason for the UI.</summary>
    public string? Validate()
    {
        if (By == ResizeBy.Percentage)
        {
            if (!(Percent > 0) || Percent > MaxPercent) return "Enter a percentage between 1 and 1000.";
            return null;
        }
        if (Width == null && Height == null) return "Enter a width, a height, or both.";
        if (Width is < 1 or > MaxDimension || Height is < 1 or > MaxDimension)
            return $"Width and height must be between 1 and {MaxDimension:N0} pixels.";
        return null;
    }

    public string Describe() => By == ResizeBy.Percentage
        ? $"{Percent:0.##}%{(DontEnlarge ? ", don't enlarge" : "")}"
        : $"width {(Width?.ToString() ?? "auto")}, height {(Height?.ToString() ?? "auto")}, " +
          $"keep proportions {(KeepProportions ? "on" : "off")}, don't enlarge {(DontEnlarge ? "on" : "off")}";
}

public sealed record JobSettings
{
    public JobMode Mode { get; init; } = JobMode.Resize;
    public ResizeSettings Resize { get; init; } = new();
    public OutputFormatChoice OutputFormat { get; init; } = OutputFormatChoice.KeepOriginal;

    /// <summary>Optimize only: a PNG with no transparency may become a JPEG when that is much smaller.</summary>
    public bool AllowFormatConversion { get; init; }

    /// <summary>Null = a "Resized"/"Optimized" folder next to each original.</summary>
    public string? CustomOutputFolder { get; init; }

    public string Describe() =>
        (Mode == JobMode.Resize ? $"Resize ({Resize.Describe()})" : $"Optimize (format conversion {(AllowFormatConversion ? "allowed" : "off")})") +
        $", save as {OutputFormat}, output {(CustomOutputFolder ?? "default folder next to each image")}";
}
