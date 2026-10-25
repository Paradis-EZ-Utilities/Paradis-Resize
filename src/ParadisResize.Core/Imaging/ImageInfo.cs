namespace ParadisResize.Core.Imaging;

/// <summary>What the list shows about an image, read from its header without decoding the pixels.</summary>
public sealed record ImageInfo
{
    public required string Path { get; init; }
    public required ImageFormat Format { get; init; }

    /// <summary>Pixel size as stored in the file.</summary>
    public required int StoredWidth { get; init; }
    public required int StoredHeight { get; init; }

    /// <summary>EXIF orientation, 1–8 (1 = upright). 5–8 mean the picture is stored on its side.</summary>
    public int Orientation { get; init; } = 1;

    public required long FileSize { get; init; }

    /// <summary>The file has an alpha channel. It may still be fully opaque; see <see cref="ImageProcessor"/>.</summary>
    public bool HasAlphaChannel { get; init; }

    /// <summary>16 bits per channel (some PNGs). Copies are always 8 bits per channel; see DECISIONS.md.</summary>
    public bool IsHighBitDepth { get; init; }

    /// <summary>For WebP: stored losslessly (VP8L) rather than lossy (VP8).</summary>
    public bool IsLosslessWebP { get; init; }

    /// <summary>libvips' description of the colour data, for the log ("srgb", "b-w", "cmyk", "rgb16").</summary>
    public string ColourSpace { get; init; } = "";

    public int Bands { get; init; }

    public bool HasIccProfile { get; init; }

    /// <summary>Stored sideways, so the upright picture swaps width and height.</summary>
    public bool IsRotated90 => Orientation is >= 5 and <= 8;

    /// <summary>The size the picture is shown at (after EXIF orientation). This is what the user sees and sets.</summary>
    public int Width => IsRotated90 ? StoredHeight : StoredWidth;
    public int Height => IsRotated90 ? StoredWidth : StoredHeight;

    public long PixelCount => (long)StoredWidth * StoredHeight;

    public string Describe() =>
        $"{SupportedFormats.DisplayName(Format)}{(Format == ImageFormat.WebP ? (IsLosslessWebP ? " lossless" : " lossy") : "")} " +
        $"{StoredWidth}x{StoredHeight}{(Orientation != 1 ? $" orientation={Orientation} (shown {Width}x{Height})" : "")}, " +
        $"{Bands} bands {ColourSpace}{(IsHighBitDepth ? " 16-bit" : "")}{(HasAlphaChannel ? ", alpha" : "")}{(HasIccProfile ? ", ICC profile" : "")}, " +
        $"{FileSize:N0} bytes";
}
