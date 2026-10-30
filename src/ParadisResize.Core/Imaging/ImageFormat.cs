namespace ParadisResize.Core.Imaging;

/// <summary>The still-image formats Paradis Resize writes (and reads).</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    WebP,

    /// <summary>Kept as AVIF only for an AVIF original ("Same format as original"); never chosen otherwise.</summary>
    Avif,
}

/// <summary>
/// What a file really contains, read from its first bytes. Some of these are read but never written:
/// a GIF or a BMP is handled as a PNG from then on (<see cref="ImageInfo.Format"/>).
/// </summary>
public enum SourceKind
{
    Png,
    Jpeg,
    WebP,
    Avif,

    /// <summary>Only reached when a GIF has a PNG/JPEG/WebP/BMP/AVIF name; the first frame becomes a still image.</summary>
    Gif,

    Bmp,
}

public static class SupportedFormats
{
    /// <summary>File extensions picked up from dropped folders and offered in Add Files.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp", ".bmp", ".avif"];

    /// <summary>For the Add Files dialog.</summary>
    public const string FileDialogFilter =
        "Images (PNG, JPEG, WebP, BMP, AVIF)|*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.webp;*.bmp;*.avif|All files (*.*)|*.*";

    /// <summary>For messages: the formats that can be added.</summary>
    public const string ReadableNames = "PNG, JPEG, WebP, BMP or AVIF";

    public static bool HasSupportedExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The output format a file's extension claims (null for ".bmp", which is never written, and for other
    /// extensions). The real format is read from the file itself (<see cref="ImageProbe"/>).
    /// </summary>
    public static ImageFormat? FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.WebP,
        ".avif" => ImageFormat.Avif,
        _ => null,
    };

    /// <summary>The kind of file an extension claims, for the list before the header has been read.</summary>
    public static SourceKind? KindFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".bmp" => SourceKind.Bmp,
        _ => FromExtension(path) is ImageFormat f ? KindOf(f) : null,
    };

    /// <summary>The usual extension for a format: ".png", ".jpg", ".webp", ".avif".</summary>
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.WebP => ".webp",
        ImageFormat.Avif => ".avif",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string DisplayName(ImageFormat format) => format switch
    {
        ImageFormat.Png => "PNG",
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.WebP => "WebP",
        ImageFormat.Avif => "AVIF",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string DisplayName(SourceKind kind) => kind switch
    {
        SourceKind.Gif => "GIF",
        SourceKind.Bmp => "BMP",
        SourceKind.Png => "PNG",
        SourceKind.Jpeg => "JPEG",
        SourceKind.WebP => "WebP",
        SourceKind.Avif => "AVIF",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static SourceKind KindOf(ImageFormat format) => format switch
    {
        ImageFormat.Png => SourceKind.Png,
        ImageFormat.Jpeg => SourceKind.Jpeg,
        ImageFormat.WebP => SourceKind.WebP,
        _ => SourceKind.Avif,
    };

    /// <summary>
    /// The extension for a copy in <paramref name="format"/>: the original's own extension when it
    /// already matches (so "Photo.jpeg" stays ".jpeg"), otherwise the usual one.
    /// </summary>
    public static string ExtensionFor(string sourcePath, ImageFormat format)
    {
        string ext = Path.GetExtension(sourcePath);
        return FromExtension(sourcePath) == format ? ext : Extension(format);
    }

    /// <summary>The largest width or height a WebP image can have.</summary>
    public const int WebPMaxDimension = 16383;

    /// <summary>The largest width or height a JPEG image can have.</summary>
    public const int JpegMaxDimension = 65500;
}
