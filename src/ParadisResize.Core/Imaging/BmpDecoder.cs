using System.Buffers.Binary;
using NetVips;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Imaging;

/// <summary>
/// Reads Windows BMP files, which libvips can't (its BMP support comes through ImageMagick, which isn't in
/// the bundled libvips). Covers what Windows, Paint, scanners and other programs actually write:
/// 1, 4 and 8-bit palette images (uncompressed or RLE), 16-bit (5-5-5 or bit masks), 24-bit, and 32-bit
/// with or without an alpha channel, stored bottom-up or top-down. BMPs that embed a JPEG or PNG, and
/// OS/2-only variants, are reported as not supported.
/// <para>
/// Everything read from the file is checked against the file's real size before any memory is set aside,
/// so a damaged or hostile header can't ask for gigabytes. The decoded picture is handed to libvips as an
/// 8-bit sRGB image; from there it is processed like any other.
/// </para>
/// </summary>
public static class BmpDecoder
{
    public sealed record Header(int Width, int Height, int BitsPerPixel, int Compression, bool MayHaveAlpha, bool TopDown);

    private const int BiRgb = 0, BiRle8 = 1, BiRle4 = 2, BiBitfields = 3, BiJpeg = 4, BiPng = 5, BiAlphaBitfields = 6;

    /// <summary>Bigger than any real BMP; keeps a damaged header from asking for an absurd amount of memory.</summary>
    private const int MaxDimension = 65535;

    private sealed record Layout(Header Header, long PixelOffset, long FileLength, uint[] Masks, byte[][] Palette);

    /// <summary>Reads the headers only: size, depth and whether there can be transparency.</summary>
    public static Header ReadHeader(string path)
    {
        using var fs = Open(path);
        return ReadLayout(fs).Header;
    }

    /// <summary>
    /// Decodes the whole picture into memory and wraps it as a libvips image (3 bands, or 4 when the file
    /// really has transparency). A 32-bit BMP whose alpha bytes are all zero is opaque: those bytes are just
    /// unused padding, as Windows and browsers treat them.
    /// </summary>
    public static Image Load(string path)
    {
        using var fs = Open(path);
        var layout = ReadLayout(fs);
        var h = layout.Header;
        int bands = h.MayHaveAlpha ? 4 : 3;
        long bytes = (long)h.Width * h.Height * bands;
        if (bytes > Array.MaxLength)
            throw FriendlyErrors.OutOfMemory($"BMP {h.Width}x{h.Height} needs {bytes:N0} bytes");

        byte[] pixels;
        try
        {
            pixels = GC.AllocateUninitializedArray<byte>((int)bytes);
        }
        catch (OutOfMemoryException ex)
        {
            throw FriendlyErrors.OutOfMemory(ex.Message, ex);
        }

        fs.Position = layout.PixelOffset;
        if (h.Compression is BiRle8 or BiRle4) DecodeRle(fs, layout, pixels);
        else DecodeRows(fs, layout, pixels, bands);

        if (bands == 4 && AlphaAllZero(pixels))
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var img = Image.NewFromMemory(pixels, h.Width, h.Height, bands, Enums.BandFormat.Uchar);
        using (img)
            return img.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static FileStream Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Reading);
        }
    }

    private static Layout ReadLayout(FileStream fs)
    {
        long length = fs.Length;
        Span<byte> fh = stackalloc byte[18];
        if (fs.ReadAtLeast(fh, 18, throwOnEndOfStream: false) < 18 || fh[0] != 'B' || fh[1] != 'M')
            throw FriendlyErrors.Unreadable("BMP file header too short");
        long pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(fh[10..]);
        int dibSize = BinaryPrimitives.ReadInt32LittleEndian(fh[14..]);
        if (dibSize is not (12 or 16 or 40 or 52 or 56 or 64 or 108 or 124))
            throw FriendlyErrors.Unreadable($"BMP header size {dibSize}");

        byte[] dib = new byte[Math.Max(dibSize, 40)];
        BinaryPrimitives.WriteInt32LittleEndian(dib, dibSize);
        if (fs.ReadAtLeast(dib.AsSpan(4, dibSize - 4), dibSize - 4, throwOnEndOfStream: false) < dibSize - 4)
            throw FriendlyErrors.Damaged("BMP header truncated");

        int width, height, bpp, compression = BiRgb, paletteEntrySize = 4;
        uint colours = 0;
        if (dibSize == 12)
        {
            // OS/2 1.x / Windows 2 "core" header: 16-bit sizes, 3-byte palette entries.
            width = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(4));
            height = BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(6));
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(10));
            paletteEntrySize = 3;
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
            height = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
            if (dibSize >= 20) compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
            if (dibSize >= 36) colours = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        }

        if (compression is BiJpeg or BiPng)
            throw FriendlyErrors.Unsupported($"BMP with embedded {(compression == BiJpeg ? "JPEG" : "PNG")}");
        // OS/2 2.x headers (16 and 64 bytes) reuse compression numbers for other schemes; only plain pixels are safe.
        if (dibSize is 16 or 64 && compression != BiRgb)
            throw FriendlyErrors.Unsupported($"OS/2 BMP compression {compression}");
        if (compression is not (BiRgb or BiRle8 or BiRle4 or BiBitfields or BiAlphaBitfields))
            throw FriendlyErrors.Unsupported($"BMP compression {compression}");

        bool topDown = height < 0;
        if (height == int.MinValue) throw FriendlyErrors.Unreadable("BMP height");
        height = Math.Abs(height);
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw FriendlyErrors.Unreadable($"BMP size {width}x{height}");

        bool valid = (bpp, compression) switch
        {
            (1 or 4 or 8 or 16 or 24 or 32, BiRgb) => true,
            (8, BiRle8) or (4, BiRle4) => true,
            (16 or 32, BiBitfields or BiAlphaBitfields) => true,
            _ => false,
        };
        if (!valid) throw FriendlyErrors.Unsupported($"BMP {bpp}-bit with compression {compression}");

        // Bit masks: inside a v2+ header, or right after a 40-byte header.
        uint[] masks = bpp == 16 ? [0x7C00, 0x03E0, 0x001F, 0] : [0x00FF0000, 0x0000FF00, 0x000000FF, 0];
        bool hasAlphaMask = false;
        if (compression is BiBitfields or BiAlphaBitfields)
        {
            int count = compression == BiAlphaBitfields ? 4 : 3;
            ReadOnlySpan<byte> m;
            if (dibSize >= 52)
            {
                m = dib.AsSpan(40, dibSize >= 56 ? 16 : 12);
                count = dibSize >= 56 ? 4 : 3;
            }
            else
            {
                byte[] extra = new byte[count * 4];
                if (fs.ReadAtLeast(extra, extra.Length, throwOnEndOfStream: false) < extra.Length)
                    throw FriendlyErrors.Damaged("BMP bit masks truncated");
                m = extra;
            }
            masks = new uint[4];
            for (int i = 0; i < count; i++) masks[i] = BinaryPrimitives.ReadUInt32LittleEndian(m[(i * 4)..]);
            hasAlphaMask = masks[3] != 0;
        }
        else if (bpp == 32 && dibSize >= 56)
        {
            // A v4/v5 header states an alpha mask even for plain 32-bit pixels.
            hasAlphaMask = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(52)) != 0;
            if (hasAlphaMask) masks[3] = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(52));
        }
        if (bpp == 32 && compression == BiRgb && !hasAlphaMask)
        {
            // The 4th byte of plain 32-bit pixels is officially unused but often holds real alpha.
            masks[3] = 0xFF000000;
            hasAlphaMask = true;
        }

        byte[][] palette = [];
        if (bpp <= 8)
        {
            int max = 1 << bpp;
            int n = colours == 0 || colours > max ? max : (int)colours;
            // The palette sits between the headers and the pixels; don't read past either.
            long available = (pixelOffset > fs.Position ? pixelOffset : length) - fs.Position;
            n = (int)Math.Min(n, Math.Max(0, available / paletteEntrySize));
            if (n == 0) throw FriendlyErrors.Damaged("BMP palette missing");
            var raw = new byte[n * paletteEntrySize];
            if (fs.ReadAtLeast(raw, raw.Length, throwOnEndOfStream: false) < raw.Length)
                throw FriendlyErrors.Damaged("BMP palette truncated");
            palette = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                int o = i * paletteEntrySize;
                palette[i] = [raw[o + 2], raw[o + 1], raw[o]];
            }
        }

        if (pixelOffset == 0) pixelOffset = fs.Position;
        if (pixelOffset >= length) throw FriendlyErrors.Damaged("BMP has no pixel data");

        if (compression is not (BiRle8 or BiRle4))
        {
            // All rows must be there (the last row's padding may be missing; some programs leave it out).
            long stride = Stride(width, bpp);
            long needed = pixelOffset + stride * (height - 1) + ((long)width * bpp + 7) / 8;
            if (needed > length) throw FriendlyErrors.Damaged($"BMP pixel data truncated ({length:N0} of {needed:N0} bytes)");
        }

        bool mayHaveAlpha = bpp is 16 or 32 && hasAlphaMask;
        return new Layout(new Header(width, height, bpp, compression, mayHaveAlpha, topDown), pixelOffset, length, masks, palette);
    }

    private static long Stride(int width, int bpp) => ((long)width * bpp + 31) / 32 * 4;

    private static void DecodeRows(FileStream fs, Layout layout, byte[] pixels, int bands)
    {
        var h = layout.Header;
        int stride = (int)Stride(h.Width, h.BitsPerPixel);
        byte[] row = new byte[stride];
        var shifts = layout.Masks.Select(MaskShift).ToArray();

        for (int y = 0; y < h.Height; y++)
        {
            // The last row may lack its padding; what's missing reads as zeros.
            int got = fs.ReadAtLeast(row, stride, throwOnEndOfStream: false);
            if (got < stride) Array.Clear(row, got, stride - got);
            int outY = h.TopDown ? y : h.Height - 1 - y;
            int o = outY * h.Width * bands;
            for (int x = 0; x < h.Width; x++, o += bands)
            {
                switch (h.BitsPerPixel)
                {
                    case 1 or 4 or 8:
                    {
                        int bit = x * h.BitsPerPixel;
                        int index = (row[bit >> 3] >> (8 - h.BitsPerPixel - (bit & 7))) & ((1 << h.BitsPerPixel) - 1);
                        SetIndexed(pixels, o, layout.Palette, index);
                        break;
                    }
                    case 24:
                        pixels[o] = row[x * 3 + 2];
                        pixels[o + 1] = row[x * 3 + 1];
                        pixels[o + 2] = row[x * 3];
                        break;
                    default:
                    {
                        uint v = h.BitsPerPixel == 16
                            ? BinaryPrimitives.ReadUInt16LittleEndian(row.AsSpan(x * 2))
                            : BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(x * 4));
                        for (int c = 0; c < bands; c++) pixels[o + c] = Extract(v, layout.Masks[c], shifts[c]);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Run-length encoded 4 and 8-bit images. Pixels the file skips over take palette colour 0.</summary>
    private static void DecodeRle(FileStream fs, Layout layout, byte[] pixels)
    {
        var h = layout.Header;
        long available = layout.FileLength - layout.PixelOffset;
        // Even the worst-case RLE is far smaller than this; anything bigger is not a real BMP.
        long limit = Math.Min(available, (long)h.Width * h.Height * 2 + 1024 * 1024);
        byte[] data = new byte[limit];
        int n = fs.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);

        byte[] index = new byte[h.Width * h.Height];
        bool four = h.Compression == BiRle4;
        int x = 0, y = 0, p = 0;

        void Put(int value)
        {
            if (x < h.Width && y < h.Height) index[(h.TopDown ? y : h.Height - 1 - y) * h.Width + x] = (byte)value;
            x++;
        }

        while (p + 1 < n && y < h.Height)
        {
            int count = data[p++], value = data[p++];
            if (count > 0)
            {
                for (int i = 0; i < count; i++) Put(four ? (i % 2 == 0 ? value >> 4 : value & 0x0F) : value);
                continue;
            }
            if (value == 0) { x = 0; y++; }          // end of line
            else if (value == 1) break;              // end of bitmap
            else if (value == 2)                     // move right and up
            {
                if (p + 1 >= n) break;
                x += data[p++];
                y += data[p++];
            }
            else                                     // a literal run of `value` pixels, padded to a whole word
            {
                int byteCount = four ? (value + 1) / 2 : value;
                if (p + byteCount > n) throw FriendlyErrors.Damaged("BMP RLE data truncated");
                for (int i = 0; i < value; i++) Put(four ? (i % 2 == 0 ? data[p + i / 2] >> 4 : data[p + i / 2] & 0x0F) : data[p + i]);
                p += byteCount + (byteCount & 1);
            }
        }

        for (int i = 0, o = 0; i < index.Length; i++, o += 3) SetIndexed(pixels, o, layout.Palette, index[i]);
    }

    private static void SetIndexed(byte[] pixels, int o, byte[][] palette, int index)
    {
        // An index outside a short palette is black, as Windows shows it.
        if (index < palette.Length)
        {
            var c = palette[index];
            pixels[o] = c[0];
            pixels[o + 1] = c[1];
            pixels[o + 2] = c[2];
        }
        else
        {
            pixels[o] = pixels[o + 1] = pixels[o + 2] = 0;
        }
    }

    private static int MaskShift(uint mask) => mask == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(mask);

    /// <summary>One channel of a 16 or 32-bit pixel, scaled to 0–255 whatever the mask's width.</summary>
    private static byte Extract(uint value, uint mask, int shift)
    {
        if (mask == 0) return 0;
        uint max = mask >> shift;
        uint v = (value & mask) >> shift;
        return max == 255 ? (byte)v : (byte)((v * 255 + max / 2) / max);
    }

    private static bool AlphaAllZero(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 0) return false;
        return true;
    }
}
