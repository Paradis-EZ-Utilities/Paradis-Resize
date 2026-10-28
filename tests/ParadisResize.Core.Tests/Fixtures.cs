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
