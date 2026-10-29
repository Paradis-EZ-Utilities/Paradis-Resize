using NetVips;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Scanning;

namespace ParadisResize.Core.Tests;

/// <summary>
/// BMP input: read by Paradis Resize's own decoder, processed like a PNG, and never written as BMP. The
/// original is never touched.
/// </summary>
public class BmpTests
{
    private static ItemResult Run(JobSettings settings, string path)
    {
        VipsRuntime.Initialize();
        var processor = new ImageProcessor(settings, new OutputPlanner(settings.Mode, settings.CustomOutputFolder));
        return processor.Process(path, "", null, CancellationToken.None);
    }

    private static readonly JobSettings Resize50 = new()
    {
        Mode = JobMode.Resize,
        Resize = new ResizeSettings { By = ResizeBy.Percentage, Percent = 50 },
    };

    private static readonly JobSettings Optimize = new() { Mode = JobMode.Optimize };
    private static readonly JobSettings OptimizeConvert = new() { Mode = JobMode.Optimize, AllowFormatConversion = true };

    private static void AssertNoPartials(TempDir t) =>
        Assert.DoesNotContain(t.AllFiles(), f => f.EndsWith(".partial", StringComparison.Ordinal));

    /// <summary>Four flat quadrants (red top-left, green top-right, blue bottom-left, white bottom-right): shows orientation.</summary>
    private static (byte, byte, byte, byte) Quadrants(int x, int y, int w, int h) => (x < w / 2, y < h / 2) switch
    {
        (true, true) => (220, 20, 20, 255),
        (false, true) => (20, 220, 20, 255),
        (true, false) => (20, 20, 220, 255),
        _ => (255, 255, 255, 255),
    };

    private static void AssertQuadrants(string path, int w, int h)
    {
        Assert.Equal([220, 20, 20], Fixtures.PixelAt(path, w / 4, h / 4)[..3]);
        Assert.Equal([20, 220, 20], Fixtures.PixelAt(path, w * 3 / 4, h / 4)[..3]);
        Assert.Equal([20, 20, 220], Fixtures.PixelAt(path, w / 4, h * 3 / 4)[..3]);
        Assert.Equal([255, 255, 255], Fixtures.PixelAt(path, w * 3 / 4, h * 3 / 4)[..3]);
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(24, true)]
    [InlineData(32, false)]
    [InlineData(8, false)]
    [InlineData(4, true)]
    public void Optimize_DecodesExactly_AndSavesPng(int bpp, bool topDown)
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Scan.bmp"), 81, 61, bpp, (x, y) => Quadrants(x, y, 81, 61), topDown);
        string before = Fixtures.Hash(t.File("Scan.bmp"));

        var info = ImageProbe.Read(t.File("Scan.bmp"));
        Assert.True(info.IsBmp);
        Assert.Equal("BMP", info.SourceFormatName);
        Assert.Equal(ImageFormat.Png, info.Format);
        Assert.Equal((81, 61), (info.Width, info.Height));

        var r = Run(Optimize, t.File("Scan.bmp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (BMP → PNG)", r.Status);
        Assert.Contains("Converted from BMP", r.Message);
        Assert.True(r.Converted);
        Assert.Equal(Path.Combine(t.Path, "Optimized", "Scan.png"), r.OutputPath);
        var o = Fixtures.Read(r.OutputPath!);
        Assert.StartsWith("png", o.Loader);
        Assert.Equal(3, o.Bands); // no transparency in the file, none in the copy
        AssertQuadrants(r.OutputPath!, 81, 61);
        Assert.Equal(before, Fixtures.Hash(t.File("Scan.bmp")));
        AssertNoPartials(t);
    }

    [Fact]
    public void OneBitAndSixteenBit_AreDecoded()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Mono.bmp"), 50, 30, 1, (x, y) => x < 25 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255));
        Fixtures.SaveBmp(t.File("High.bmp"), 50, 30, 16, (x, y) => ((byte)248, (byte)124, (byte)0, (byte)255));

        var mono = Run(Optimize, t.File("Mono.bmp"));
        var high = Run(Optimize, t.File("High.bmp"));

        Assert.Equal([0, 0, 0], Fixtures.PixelAt(mono.OutputPath!, 5, 5)[..3]);
        Assert.Equal([255, 255, 255], Fixtures.PixelAt(mono.OutputPath!, 45, 5)[..3]);
        // 5-6-5: 248 → 31/31 → 255, 124 → 31/63 → 125.
        Assert.Equal([255, 125, 0], Fixtures.PixelAt(high.OutputPath!, 10, 10)[..3]);
    }

    [Fact]
    public void Rle8_IsDecoded()
    {
        using var t = new TempDir();
        // 6 × 3, bottom-up, palette: 0 black, 1 red, 2 green. Rows (top to bottom): RRRGGG / GGGRRR / skipped (colour 0).
        byte[] rle =
        [
            0, 2, 0, 1,             // delta: skip the bottom row (move 0 right, 1 up)
            3, 2, 3, 1, 0, 0,       // middle row: GGG RRR, end of line
            0, 3, 1, 1, 1, 0,       // top row: a literal run of 3 reds (padded to a word)...
            3, 2,                   // ...then 3 greens
            0, 1,                   // end of bitmap
        ];
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            int offset = 14 + 40 + 3 * 4;
            w.Write("BM"u8); w.Write(offset + rle.Length); w.Write(0); w.Write(offset);
            w.Write(40); w.Write(6); w.Write(3); w.Write((short)1); w.Write((short)8); w.Write(1); w.Write(rle.Length);
            w.Write(0); w.Write(0); w.Write(3); w.Write(0);
            w.Write(new byte[] { 0, 0, 0, 0, 0, 0, 255, 0, 0, 255, 0, 0 });
            w.Write(rle);
            File.WriteAllBytes(t.File("Rle.bmp"), ms.ToArray());
        }

        var r = Run(Optimize, t.File("Rle.bmp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal([255, 0, 0], Fixtures.PixelAt(r.OutputPath!, 0, 0)[..3]);
        Assert.Equal([0, 255, 0], Fixtures.PixelAt(r.OutputPath!, 5, 0)[..3]);
        Assert.Equal([0, 255, 0], Fixtures.PixelAt(r.OutputPath!, 0, 1)[..3]);
        Assert.Equal([255, 0, 0], Fixtures.PixelAt(r.OutputPath!, 5, 1)[..3]);
        Assert.Equal([0, 0, 0], Fixtures.PixelAt(r.OutputPath!, 3, 2)[..3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThirtyTwoBitWithAlpha_KeepsTransparency(bool v5)
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Icon.bmp"), 60, 40, 32, (x, y) => x < 10 && y < 10 ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)200, (byte)100, (byte)50, (byte)255), v5: v5);

        Assert.True(ImageProbe.Read(t.File("Icon.bmp")).HasAlphaChannel);
        var r = Run(OptimizeConvert, t.File("Icon.bmp"));

        Assert.Equal("Done (BMP → PNG)", r.Status); // transparency rules out JPEG
        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(4, o.Bands);
        Assert.Equal(0, o[3].Getpoint(2, 2)[0]);
        Assert.Equal(255, o[3].Getpoint(30, 30)[0]);
        Assert.Equal([200, 100, 50], Fixtures.PixelAt(r.OutputPath!, 30, 30)[..3]);
    }

    [Fact]
    public void ThirtyTwoBitWithUnusedAlphaBytes_IsOpaque()
    {
        using var t = new TempDir();
        // Paint and most programs write 0 in the 4th byte: that's padding, not "fully transparent".
        Fixtures.SaveBmp(t.File("Paint.bmp"), 60, 40, 32, (x, y) => ((byte)10, (byte)120, (byte)240, (byte)0));

        var r = Run(Optimize, t.File("Paint.bmp"));

        var o = Fixtures.Read(r.OutputPath!);
        Assert.Equal(3, o.Bands);
        Assert.Equal([10, 120, 240], Fixtures.PixelAt(r.OutputPath!, 5, 5));
    }

    [Fact]
    public void Resize_SavesPng_AtTheNewSize()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Scan.bmp"), 200, 100, 24, (x, y) => Quadrants(x, y, 200, 100));

        var r = Run(Resize50, t.File("Scan.bmp"));

        Assert.Equal("Done (BMP → PNG)", r.Status);
        Assert.Equal(Path.Combine(t.Path, "Resized", "Scan.png"), r.OutputPath);
        Assert.Equal((100, 50), (r.OutputWidth, r.OutputHeight));
        AssertQuadrants(r.OutputPath!, 100, 50);
    }

    [Fact]
    public void Resize_AlreadySmallEnough_IsStillConverted_NotCopiedAsBmp()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Small.bmp"), 40, 30, 24, (x, y) => Quadrants(x, y, 40, 30));
        var settings = new JobSettings { Mode = JobMode.Resize, Resize = new ResizeSettings { Width = 400 } };

        var r = Run(settings, t.File("Small.bmp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.EndsWith("Small.png", r.OutputPath);
        Assert.Equal((40, 30), (r.OutputWidth, r.OutputHeight)); // not enlarged
    }

    [Fact]
    public void Optimize_PhotoWithConversion_BecomesJpeg()
    {
        using var t = new TempDir();
        using (var photo = Fixtures.Photo(400, 300))
        {
            byte[] px = photo.WriteToMemory();
            Fixtures.SaveBmp(t.File("Photo.bmp"), 400, 300, 24, (x, y) =>
            {
                int o = (y * 400 + x) * 3;
                return (px[o], px[o + 1], px[o + 2], 255);
            });
        }

        var r = Run(OptimizeConvert, t.File("Photo.bmp"));

        Assert.Equal("Done (BMP → JPEG)", r.Status);
        Assert.Equal(Path.Combine(t.Path, "Optimized", "Photo.jpg"), r.OutputPath);
        Assert.StartsWith("jpeg", Fixtures.Read(r.OutputPath!).Loader);
    }

    [Fact]
    public void ManualWebP_IsRespected()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Scan.bmp"), 80, 60, 24, (x, y) => Quadrants(x, y, 80, 60));
        var r = Run(Optimize with { OutputFormat = OutputFormatChoice.WebP }, t.File("Scan.bmp"));
        Assert.Equal("Done (BMP → WebP)", r.Status);
        Assert.EndsWith("Scan.webp", r.OutputPath);
    }

    [Fact]
    public void TruncatedBmp_FailsAsDamaged()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("full.bmp"), 200, 100, 24, (x, y) => Quadrants(x, y, 200, 100));
        byte[] bytes = File.ReadAllBytes(t.File("full.bmp"));
        File.WriteAllBytes(t.File("Cut.bmp"), bytes[..(bytes.Length / 2)]);

        var r = Run(Resize50, t.File("Cut.bmp"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Damaged or incomplete", r.Status);
        AssertNoPartials(t);
    }

    [Fact]
    public void HugeClaimedSize_InATinyFile_FailsFast_WithoutAllocating()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Liar.bmp"), 4, 4, 24, (x, y) => (1, 2, 3, 255));
        byte[] bytes = File.ReadAllBytes(t.File("Liar.bmp"));
        BitConverter.GetBytes(60000).CopyTo(bytes, 18);
        BitConverter.GetBytes(60000).CopyTo(bytes, 22);
        File.WriteAllBytes(t.File("Liar.bmp"), bytes);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = Run(Resize50, t.File("Liar.bmp"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Damaged or incomplete", r.Status);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 50 * 1024 * 1024);
    }

    [Fact]
    public void BmpWithEmbeddedJpeg_IsNotSupported()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("Odd.bmp"), 4, 4, 24, (x, y) => (1, 2, 3, 255));
        byte[] bytes = File.ReadAllBytes(t.File("Odd.bmp"));
        BitConverter.GetBytes(4).CopyTo(bytes, 30); // BI_JPEG
        File.WriteAllBytes(t.File("Odd.bmp"), bytes);

        var r = Run(Resize50, t.File("Odd.bmp"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Not a supported image", r.Status);
    }

    [Fact]
    public void GarbageNamedBmp_IsReportedAsUnreadable()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("notes.bmp"), "BM but not really a bitmap");
        var r = Run(Resize50, t.File("notes.bmp"));
        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Couldn't read image", r.Status);
    }

    [Fact]
    public void BmpFiles_ArePickedUpByFolderScan()
    {
        using var t = new TempDir();
        Fixtures.SaveBmp(t.File("a.bmp"), 4, 4, 24, (x, y) => (1, 2, 3, 255));
        Fixtures.SaveBmp(t.File("b.BMP"), 4, 4, 24, (x, y) => (1, 2, 3, 255));
        var scanner = new InputScanner(includeSubfolders: false, customOutputFolder: null);
        Assert.Equal(["a.bmp", "b.BMP"], scanner.Expand([t.Path]).Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(0, scanner.SkippedNotImages);
    }
}
