namespace ParadisResize.Core.Imaging;

/// <summary>The still-image formats Paradis Resize reads and writes.</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    WebP,
}

public static class SupportedFormats
{
    /// <summary>File extensions picked up from dropped folders and offered in Add Files.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".webp"];

    /// <summary>For the Add Files dialog.</summary>
    public const string FileDialogFilter =
        "Images (PNG, JPEG, WebP)|*.png;*.jpg;*.jpeg;*.jpe;*.jfif;*.webp|All files (*.*)|*.*";

    public static bool HasSupportedExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The format a file's extension claims. The real format is read from the file itself (<see cref="ImageProbe"/>).</summary>
    public static ImageFormat? FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png,
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.WebP,
        _ => null,
    };

    /// <summary>The usual extension for a format: ".png", ".jpg", ".webp".</summary>
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        _ => ".webp",
    };

    public static string DisplayName(ImageFormat format) => format switch
    {
        ImageFormat.Png => "PNG",
        ImageFormat.Jpeg => "JPEG",
        _ => "WebP",
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
