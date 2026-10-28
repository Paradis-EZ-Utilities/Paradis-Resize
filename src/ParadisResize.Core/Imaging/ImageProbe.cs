using NetVips;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Imaging;

/// <summary>
/// Reads an image's header: format, pixel size, orientation, alpha. Only the header is read, so this
/// takes milliseconds even for a 300 MB PNG and uses almost no memory. The format comes from the
/// file's contents, not its extension, so a JPEG renamed to ".png" is still treated as a JPEG.
/// </summary>
public static class ImageProbe
{
    public static ImageInfo Read(string path)
    {
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Reading);
        }
        if (!File.Exists(path))
            throw FriendlyErrors.Classify(new FileNotFoundException("Image not found", path), FriendlyErrors.Stage.Reading);
        if (size == 0) throw FriendlyErrors.Damaged("empty file");

        // libvips can open many more formats (GIF, TIFF, HEIF, SVG, ...). Refuse them before asking it,
        // so nothing outside PNG/JPEG/WebP is ever handed to a decoder.
        // A file that is recognisably another kind of image (a GIF renamed to .png) is "not supported";
        // a file whose contents aren't recognisable at all, although its name says PNG/JPEG/WebP, is
        // reported as unreadable, because it is most likely damaged rather than the wrong type.
        ImageFormat? sniffed = Sniff(path, out string? otherFormat);
        if (sniffed == null)
        {
            if (otherFormat != null) throw FriendlyErrors.Unsupported($"{otherFormat} contents in {path}");
            throw FriendlyErrors.Unreadable($"unrecognised signature in {path}");
        }

        WebPInspector.Result webp = default;
        if (sniffed == ImageFormat.WebP)
        {
            webp = WebPInspector.Inspect(path);
            if (webp.IsAnimated) throw FriendlyErrors.Animated();
        }

        Image img;
        try
        {
            img = Image.NewFromFile(path, access: Enums.Access.Sequential);
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Reading);
        }

        using (img)
        {
            string loader = img.Contains("vips-loader") ? img.Get("vips-loader") as string ?? "" : "";
            ImageFormat format = loader switch
            {
                _ when loader.StartsWith("png", StringComparison.Ordinal) => ImageFormat.Png,
                _ when loader.StartsWith("jpeg", StringComparison.Ordinal) => ImageFormat.Jpeg,
                _ when loader.StartsWith("webp", StringComparison.Ordinal) => ImageFormat.WebP,
                _ => throw FriendlyErrors.Unsupported($"libvips loader '{loader}'"),
            };

            if (img.Contains("n-pages") && img.Get("n-pages") is int pages && pages > 1)
                throw FriendlyErrors.Animated();

            int orientation = img.Contains("orientation") && img.Get("orientation") is int o && o is >= 1 and <= 8 ? o : 1;
            var interpretation = img.Interpretation;
            return new ImageInfo
            {
                Path = path,
                Format = format,
                StoredWidth = img.Width,
                StoredHeight = img.Height,
                Orientation = orientation,
                FileSize = size,
                HasAlphaChannel = img.HasAlpha(),
                IsHighBitDepth = img.Format is Enums.BandFormat.Ushort or Enums.BandFormat.Short or Enums.BandFormat.Uint
                    or Enums.BandFormat.Int or Enums.BandFormat.Float or Enums.BandFormat.Double,
                IsLosslessWebP = format == ImageFormat.WebP && webp.IsLossless,
                ColourSpace = interpretation.ToString().ToLowerInvariant(),
                Bands = img.Bands,
                HasIccProfile = img.Contains("icc-profile-data"),
            };
        }
    }

    /// <summary>Recognises PNG, JPEG and WebP by their first bytes.</summary>
    internal static ImageFormat? Sniff(string path) => Sniff(path, out _);

    /// <summary>
    /// Recognises PNG, JPEG and WebP by their first bytes. When it's none of those but is another common
    /// image format, <paramref name="otherFormat"/> names it.
    /// </summary>
    internal static ImageFormat? Sniff(string path, out string? otherFormat)
    {
        otherFormat = null;
        Span<byte> b = stackalloc byte[12];
        int n;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64);
            n = fs.ReadAtLeast(b, b.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Reading);
        }
        if (n >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ImageFormat.Png;
        if (n >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ImageFormat.Jpeg;
        if (n >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return ImageFormat.WebP;
        otherFormat = OtherImageFormat(b[..n]);
        return null;
    }

    /// <summary>Names other image formats people commonly have, so they can be told apart from damaged files.</summary>
    private static string? OtherImageFormat(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith("GIF8"u8)) return "GIF";
        if (b.StartsWith("BM"u8) && b.Length >= 6) return "BMP";
        if (b.StartsWith("II*\0"u8) || b.StartsWith("MM\0*"u8)) return "TIFF";
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8)) return "HEIF/AVIF";
        if (b.StartsWith(new byte[] { 0xFF, 0x0A }) || b.StartsWith(new byte[] { 0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20 })) return "JPEG XL";
        if (b.StartsWith(new byte[] { 0, 0, 1, 0 })) return "ICO";
        if (b.StartsWith("8BPS"u8)) return "PSD";
        return null;
    }
}
