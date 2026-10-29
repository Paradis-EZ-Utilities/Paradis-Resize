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

    /// <summary>What the file really contains, whatever its name says.</summary>
    public SourceKind Source { get; init; }

    /// <summary>
    /// The file is really a GIF, although its name says PNG, JPEG, WebP, BMP or AVIF. Only its first frame is
    /// used, as a still image, and it is handled like a PNG (<see cref="Format"/> is Png): lossless,
    /// transparency kept. Animation is never kept; see DECISIONS.md.
    /// </summary>
    public bool IsGif => Source == SourceKind.Gif;

    /// <summary>
    /// An animated WebP. Only its first frame is used, as a still image (still a WebP unless another format is
    /// chosen or converted to). Animation is never kept; see DECISIONS.md.
    /// </summary>
    public bool IsAnimatedWebP => Source == SourceKind.WebP && FrameCount > 1;

    /// <summary>
    /// A still image is recovered from the first frame of a GIF or an animated WebP. The copy is always made
    /// (never "copied unchanged", which would keep the animation), and it isn't counted as a saving: leaving
    /// out an animation's other frames isn't compression.
    /// </summary>
    public bool IsFrameRecovery => IsGif || IsAnimatedWebP;

    /// <summary>A Windows bitmap. Read only: handled like a PNG (<see cref="Format"/> is Png); see DECISIONS.md.</summary>
    public bool IsBmp => Source == SourceKind.Bmp;

    /// <summary>
    /// The original can't be copied unchanged: the copy would be a GIF, an animation or a BMP, none of which
    /// Paradis Resize makes.
    /// </summary>
    public bool MustReencode => IsFrameRecovery || IsBmp;

    /// <summary>Frames in the file (more than 1 for an animated GIF or WebP).</summary>
    public int FrameCount { get; init; } = 1;

    /// <summary>
    /// The extension isn't one Paradis Resize lists (".tiff", ".dat"...), but the contents are a supported image
    /// (found by <see cref="Scanning.InputScanner"/>'s signature check). Copies get the right extension.
    /// </summary>
    public bool HasForeignExtension => !SupportedFormats.HasSupportedExtension(Path);

    /// <summary>The name says one kind of image and the contents are another (a JPEG named ".tiff" or ".png").</summary>
    public bool ExtensionMismatch => SupportedFormats.KindFromExtension(Path) != Source;

    /// <summary>What the file really is, for the list: "PNG", "JPEG", "WebP", "AVIF", "BMP" or "GIF".</summary>
    public string SourceFormatName => SupportedFormats.DisplayName(Source);

    /// <summary>Stored sideways, so the upright picture swaps width and height.</summary>
    public bool IsRotated90 => Orientation is >= 5 and <= 8;

    /// <summary>The size the picture is shown at (after EXIF orientation). This is what the user sees and sets.</summary>
    public int Width => IsRotated90 ? StoredHeight : StoredWidth;
    public int Height => IsRotated90 ? StoredWidth : StoredHeight;

    public long PixelCount => (long)StoredWidth * StoredHeight;

    public string Describe() =>
        $"{SourceFormatName}{(Source == SourceKind.WebP ? (IsLosslessWebP ? " lossless" : " lossy") : "")}" +
        $"{(IsFrameRecovery ? $" ({FrameCount} frame{(FrameCount == 1 ? "" : "s")}, first frame used as a still image)" : "")}" +
        $"{(ExtensionMismatch ? $" (named {System.IO.Path.GetExtension(Path)})" : "")} " +
        $"{StoredWidth}x{StoredHeight}{(Orientation != 1 ? $" orientation={Orientation} (shown {Width}x{Height})" : "")}, " +
        $"{Bands} bands {ColourSpace}{(IsHighBitDepth ? " 16-bit" : "")}{(HasAlphaChannel ? ", alpha" : "")}{(HasIccProfile ? ", ICC profile" : "")}, " +
        $"{FileSize:N0} bytes";
}
