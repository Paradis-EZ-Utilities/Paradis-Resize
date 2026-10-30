using System.Security.Cryptography;
using NetVips;
using ParadisResize.Core.Processing;

namespace ParadisResize.Core.Tests;

/// <summary>A scratch folder per test, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "paradis-resize-tests", Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative)
    {
        string p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        return p;
    }

    /// <summary>Files directly in a subfolder; empty if it doesn't exist.</summary>
    public string[] FilesIn(string relative)
    {
        string d = System.IO.Path.Combine(Path, relative);
        return Directory.Exists(d) ? Directory.GetFiles(d) : [];
    }

    public string[] AllFiles() => Directory.GetFiles(Path, "*", SearchOption.AllDirectories);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

/// <summary>Generates test images with libvips itself, so the tests need no binary fixtures.</summary>
public static class Fixtures
{
    static Fixtures() => VipsRuntime.Initialize();

    /// <summary>Photo-like: smooth random colour with fine grain. Compresses badly as PNG and well as JPEG.</summary>
    public static Image Photo(int width, int height, int seed = 1)
    {
        Image Band(int s)
        {
            using var noise = Image.Gaussnoise(width / 4 + 1, height / 4 + 1, mean: 128, sigma: 60, seed: s);
            using var blurred = noise.Gaussblur(2.0);
            using var big = blurred.Resize(width / (double)(width / 4 + 1), vscale: height / (double)(height / 4 + 1));
            using var grain = Image.Gaussnoise(big.Width, big.Height, mean: 0, sigma: 4, seed: s + 100);
            using var sum = big + grain;
            using var cropped = sum.Crop(0, 0, Math.Min(width, sum.Width), Math.Min(height, sum.Height));
            using var embedded = cropped.Embed(0, 0, width, height, extend: Enums.Extend.Copy);
            return embedded.Cast(Enums.BandFormat.Uchar);
        }
        using var r = Band(seed);
        using var g = Band(seed + 1);
        using var b = Band(seed + 2);
        return r.Bandjoin(g, b).Copy(interpretation: Enums.Interpretation.Srgb);
    }

    /// <summary>Screenshot/diagram-like: a few flat colours and hard edges. PNG's home ground.</summary>
    public static Image Graphic(int width, int height)
    {
        var img = Image.Black(width, height, bands: 3).NewFromImage(240, 240, 245).Copy(interpretation: Enums.Interpretation.Srgb);
        return img.Mutate(m =>
        {
            for (int i = 0; i < 40; i++)
            {
                int x = i * 37 % Math.Max(1, width - 40), y = i * 53 % Math.Max(1, height - 30);
                m.DrawRect(new double[] { i * 50 % 255, i * 90 % 255, i * 20 % 255 }, x, y, 40 + i % 60, 12 + i % 20, fill: true);
                m.DrawLine(new double[] { 20, 20, 20 }, 0, (i * 7) % height, width - 1, (i * 13) % height);
            }
        });
    }

    /// <summary>Adds an alpha channel; with <paramref name="transparentCorner"/> a 20 px corner is fully transparent.</summary>
    public static Image WithAlpha(Image rgb, bool transparentCorner)
    {
        using var alpha = Image.Black(rgb.Width, rgb.Height).NewFromImage(255).Cast(Enums.BandFormat.Uchar);
        using var a2 = transparentCorner
            ? alpha.Mutate(m => m.DrawRect(new double[] { 0 }, 0, 0, 20, 20, fill: true))
            : alpha.Copy();
        return rgb.Bandjoin(a2);
    }

    /// <summary>A deliberately wasteful PNG (no compression), like some scanners and screen tools write.</summary>
    public static string SavePngUncompressed(Image img, string path)
    {
        img.Pngsave(path, compression: 0, filter: Enums.ForeignPngFilter.None);
        return path;
    }

    public static string SaveJpeg(Image img, string path, int quality, int? orientation = null)
    {
        if (orientation is int o)
        {
            using var tagged = img.Mutate(m => m.Set(GValue.GIntType, "orientation", o));
            tagged.Jpegsave(path, q: quality, keep: Enums.ForeignKeep.All);
        }
        else
        {
            img.Jpegsave(path, q: quality);
        }
        return path;
    }

    /// <summary>
    /// An animated GIF whose frames are flat colours (frame 1 = <paramref name="colours"/>[0], ...), saved under
    /// whatever name is given, as browsers and download tools sometimes do (a GIF named ".jpg").
    /// </summary>
    public static string SaveAnimatedGif(string path, int width, int height, params double[][] colours)
    {
        var frames = colours.Select(c => Image.Black(width, height, bands: 3).NewFromImage(c)
            .Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb)).ToArray();
        try
        {
            using var strip = Image.Arrayjoin(frames, across: 1);
            using var anim = strip.Mutate(m =>
            {
                m.Set(GValue.GIntType, "page-height", height);
                m.Set(GValue.ArrayIntType, "delay", Enumerable.Repeat(100, colours.Length).ToArray());
            });
            anim.Gifsave(path);
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
        }
        return path;
    }

    /// <summary>
    /// An animated WebP whose frames are flat colours (frame 1 = <paramref name="colours"/>[0], ...). With
    /// <paramref name="transparentCorner"/> every frame has a fully transparent 10 px corner.
    /// </summary>
    public static string SaveAnimatedWebP(string path, int width, int height, bool transparentCorner, bool lossless, params double[][] colours)
    {
        var frames = colours.Select(c =>
        {
            using var rgb = Image.Black(width, height, bands: 3).NewFromImage(c).Cast(Enums.BandFormat.Uchar)
                .Copy(interpretation: Enums.Interpretation.Srgb);
            if (!transparentCorner) return rgb.Copy();
            using var alpha = Image.Black(width, height).NewFromImage(255).Cast(Enums.BandFormat.Uchar)
                .Mutate(m => m.DrawRect(new double[] { 0 }, 0, 0, 10, 10, fill: true));
            return rgb.Bandjoin(alpha);
        }).ToArray();
        try
        {
            using var strip = Image.Arrayjoin(frames, across: 1);
            using var anim = strip.Mutate(m =>
            {
                m.Set(GValue.GIntType, "page-height", height);
                m.Set(GValue.ArrayIntType, "delay", Enumerable.Repeat(100, colours.Length).ToArray());
            });
            if (lossless) anim.Webpsave(path, lossless: true);
            else anim.Webpsave(path, q: 90);
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
        }
        return path;
    }

    /// <summary>
    /// Writes a Windows BMP from a pixel function (x, y → R, G, B, A), the way Paint and other programs do.
    /// <paramref name="bpp"/> 24 or 32 (plain), 16 (5-6-5 bit masks), 8/4/1 (palette of the colours used).
    /// <paramref name="v5"/> writes a 124-byte header with an alpha mask (32-bit only).
    /// </summary>
    public static string SaveBmp(string path, int width, int height, int bpp, Func<int, int, (byte R, byte G, byte B, byte A)> pixel,
                                 bool topDown = false, bool v5 = false)
    {
        var palette = new List<(byte, byte, byte)>();
        int Index((byte R, byte G, byte B, byte A) p)
        {
            int i = palette.IndexOf((p.R, p.G, p.B));
            if (i < 0) { palette.Add((p.R, p.G, p.B)); i = palette.Count - 1; }
            if (i >= 1 << bpp) throw new ArgumentException("too many colours for the bit depth");
            return i;
        }

        int stride = (width * bpp + 31) / 32 * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int fileRow = topDown ? y : height - 1 - y;
            int o = fileRow * stride;
            for (int x = 0; x < width; x++)
            {
                var p = pixel(x, y);
                switch (bpp)
                {
                    case 32: pixels[o + x * 4] = p.B; pixels[o + x * 4 + 1] = p.G; pixels[o + x * 4 + 2] = p.R; pixels[o + x * 4 + 3] = p.A; break;
                    case 24: pixels[o + x * 3] = p.B; pixels[o + x * 3 + 1] = p.G; pixels[o + x * 3 + 2] = p.R; break;
                    case 16:
                        ushort v = (ushort)((p.R >> 3) << 11 | (p.G >> 2) << 5 | p.B >> 3);
                        pixels[o + x * 2] = (byte)v; pixels[o + x * 2 + 1] = (byte)(v >> 8);
                        break;
                    default:
                        int bit = x * bpp;
                        pixels[o + (bit >> 3)] |= (byte)(Index(p) << (8 - bpp - (bit & 7)));
                        break;
                }
            }
        }

        int dibSize = v5 ? 124 : 40;
        bool masks16 = bpp == 16;
        int maskBytes = masks16 ? 12 : 0;
        int paletteBytes = bpp <= 8 ? palette.Count * 4 : 0;
        int offset = 14 + dibSize + maskBytes + paletteBytes;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("BM"u8); w.Write(offset + pixels.Length); w.Write(0); w.Write(offset);
        w.Write(dibSize); w.Write(width); w.Write(topDown ? -height : height); w.Write((short)1); w.Write((short)bpp);
        w.Write(masks16 || v5 ? 3 : 0); w.Write(pixels.Length); w.Write(2835); w.Write(2835);
        w.Write(bpp <= 8 ? palette.Count : 0); w.Write(0);
        if (v5)
        {
            w.Write(0x00FF0000); w.Write(0x0000FF00); w.Write(0x000000FF); w.Write(unchecked((int)0xFF000000));
            w.Write(0x73524742); // 'sRGB'
            w.Write(new byte[124 - 60]);
        }
        if (masks16) { w.Write(0xF800); w.Write(0x07E0); w.Write(0x001F); }
        foreach (var (r, g, b) in palette) { w.Write(b); w.Write(g); w.Write(r); w.Write((byte)0); }
        w.Write(pixels);
        System.IO.File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>The colour of one pixel, as whole numbers.</summary>
    public static int[] PixelAt(string path, int x, int y)
    {
        using var img = Image.NewFromFile(path);
        return img.Getpoint(x, y).Select(v => (int)Math.Round(v)).ToArray();
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path)));

    /// <summary>Largest per-pixel difference between two images of the same size (0 = identical).</summary>
    public static double MaxDifference(string a, string b)
    {
        using var ia = Image.NewFromFile(a);
        using var ib = Image.NewFromFile(b);
        using var d = (ia - ib).Abs();
        return d.Max();
    }

    public static (int Width, int Height, int Bands, string Loader, int Orientation) Read(string path)
    {
        using var img = Image.NewFromFile(path);
        int orientation = img.Contains("orientation") ? (int)img.Get("orientation") : 1;
        return (img.Width, img.Height, img.Bands, (string)img.Get("vips-loader"), orientation);
    }
}
