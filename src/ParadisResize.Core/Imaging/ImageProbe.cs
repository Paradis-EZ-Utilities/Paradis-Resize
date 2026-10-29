using NetVips;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Imaging;

/// <summary>
/// Reads an image's header: format, pixel size, orientation, alpha. Only the header is read, so this
/// takes milliseconds even for a 300 MB PNG and uses almost no memory. The format comes from the
/// file's contents, not its extension, so a JPEG renamed to ".png" (or ".tiff") is still treated as a JPEG.
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

        // libvips can open many more formats (TIFF, HEIC, SVG, ...). Refuse them before asking it, so nothing
        // outside PNG/JPEG/WebP/AVIF (and GIF, below) is ever handed to a decoder. BMP is read by our own
        // decoder, as the bundled libvips has none.
        // A GIF with a supported image name (common with downloaded pictures) is let through: its first frame
        // is recovered as a still image. A file that is recognisably another kind of image (a TIFF renamed to
        // .jpg) is "not supported"; a file whose contents aren't recognisable at all, although its name says
        // it's an image, is reported as unreadable, because it is most likely damaged.
        SourceKind? kind = Sniff(path, out string? otherFormat);
        if (kind == null)
        {
            if (otherFormat != null) throw FriendlyErrors.Unsupported($"{otherFormat} contents in {path}");
            throw FriendlyErrors.Unreadable($"unrecognised signature in {path}");
        }

        if (kind == SourceKind.Bmp) return ReadBmp(path, size);

        WebPInspector.Result webp = default;
        if (kind == SourceKind.WebP) webp = WebPInspector.Inspect(path);

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
            string expected = kind switch
            {
                SourceKind.Png => "png",
                SourceKind.Jpeg => "jpeg",
                SourceKind.WebP => "webp",
                SourceKind.Avif => "heif",
                _ => "gif",
            };
            if (!loader.StartsWith(expected, StringComparison.Ordinal))
                throw FriendlyErrors.Unreadable($"{SupportedFormats.DisplayName(kind.Value)} signature but libvips loader '{loader}'");
            if (kind == SourceKind.Avif && !(img.Contains("heif-compression") && img.Get("heif-compression") as string == "av1"))
                throw FriendlyErrors.Unsupported($"HEIF compression '{(img.Contains("heif-compression") ? img.Get("heif-compression") : "")}'");

            ImageFormat format = kind switch
            {
                SourceKind.Jpeg => ImageFormat.Jpeg,
                SourceKind.WebP => ImageFormat.WebP,
                SourceKind.Avif => ImageFormat.Avif,
                // Only the first frame of a GIF is ever decoded (libvips' default), and it is handled as a PNG.
                _ => ImageFormat.Png,
            };

            int frames = img.Contains("n-pages") && img.Get("n-pages") is int pages && pages > 1 ? pages : 1;
            // GIFs and WebPs give up a still image from their first frame; anything else with several
            // frames (an AVIF image collection) is left alone.
            if (frames > 1 && kind is not (SourceKind.Gif or SourceKind.WebP))
                throw FriendlyErrors.Animated();

            int orientation = img.Contains("orientation") && img.Get("orientation") is int o && o is >= 1 and <= 8 ? o : 1;
            var interpretation = img.Interpretation;
            return new ImageInfo
            {
                Path = path,
                Format = format,
                Source = kind.Value,
                StoredWidth = img.Width,
                StoredHeight = img.Height,
                Orientation = orientation,
                FileSize = size,
                HasAlphaChannel = img.HasAlpha(),
                IsHighBitDepth = img.Format is Enums.BandFormat.Ushort or Enums.BandFormat.Short or Enums.BandFormat.Uint
                    or Enums.BandFormat.Int or Enums.BandFormat.Float or Enums.BandFormat.Double,
                IsLosslessWebP = kind == SourceKind.WebP && webp.IsLossless,
                ColourSpace = interpretation.ToString().ToLowerInvariant(),
                Bands = img.Bands,
                HasIccProfile = img.Contains("icc-profile-data"),
                FrameCount = frames,
            };
        }
    }

    private static ImageInfo ReadBmp(string path, long size)
    {
        var h = BmpDecoder.ReadHeader(path);
        return new ImageInfo
        {
            Path = path,
            Format = ImageFormat.Png,
            Source = SourceKind.Bmp,
            StoredWidth = h.Width,
            StoredHeight = h.Height,
            FileSize = size,
            HasAlphaChannel = h.MayHaveAlpha,
            ColourSpace = $"srgb, {h.BitsPerPixel}-bit BMP{(h.Compression is 1 or 2 ? " RLE" : "")}",
            Bands = h.MayHaveAlpha ? 4 : 3,
        };
    }

    /// <summary>Recognises the image formats Paradis Resize reads by their first bytes.</summary>
    public static SourceKind? Sniff(string path) => Sniff(path, out _);

    /// <summary>How many bytes <see cref="Sniff(string, out string?)"/> reads: enough for an AVIF's brand list.</summary>
    public const int SniffLength = 64;

    /// <summary>
    /// Recognises PNG, JPEG, WebP, AVIF, BMP and GIF by their first bytes. When it's none of those but is
    /// another common image format, <paramref name="otherFormat"/> names it.
    /// </summary>
    public static SourceKind? Sniff(string path, out string? otherFormat)
    {
        Span<byte> b = stackalloc byte[SniffLength];
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
        return Sniff(b[..n], out otherFormat);
    }

    internal static SourceKind? Sniff(ReadOnlySpan<byte> b, out string? otherFormat)
    {
        otherFormat = null;
        int n = b.Length;
        if (n >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return SourceKind.Png;
        if (n >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return SourceKind.Jpeg;
        if (n >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return SourceKind.WebP;
        if (IsBmp(b)) return SourceKind.Bmp;
        if (b.StartsWith("GIF8"u8)) return SourceKind.Gif;
        if (n >= 12 && b[4..8].SequenceEqual("ftyp"u8))
        {
            if (HasAvifBrand(b)) return SourceKind.Avif;
            otherFormat = "HEIF";
            return null;
        }
        otherFormat = OtherImageFormat(b);
        return null;
    }

    /// <summary>"BM", then a DIB header size that a real bitmap has (so text starting "BM" isn't taken for one).</summary>
    private static bool IsBmp(ReadOnlySpan<byte> b) =>
        b.Length >= 18 && b[0] == 'B' && b[1] == 'M'
        && BitConverter.ToInt32(b[14..18]) is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124;

    /// <summary>An ISO media file ("ftyp" box) whose major or compatible brands include AVIF.</summary>
    private static bool HasAvifBrand(ReadOnlySpan<byte> b)
    {
        int boxSize = (int)Math.Min((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]), (uint)b.Length);
        for (int i = 8; i + 4 <= boxSize; i += 4)
        {
            if (i == 12) continue; // minor version, not a brand
            var brand = b.Slice(i, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return true;
        }
        return false;
    }

    /// <summary>Names other image formats people commonly have, so they can be told apart from damaged files.</summary>
    private static string? OtherImageFormat(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith("II*\0"u8) || b.StartsWith("MM\0*"u8)) return "TIFF";
        if (b.StartsWith(new byte[] { 0xFF, 0x0A }) || b.StartsWith(new byte[] { 0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20 })) return "JPEG XL";
        if (b.StartsWith(new byte[] { 0, 0, 1, 0 })) return "ICO";
        if (b.StartsWith("8BPS"u8)) return "PSD";
        return null;
    }
}
