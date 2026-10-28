using NetVips;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Scanning;

namespace ParadisResize.Core.Tests;

/// <summary>
/// Files that are really GIFs but are named .jpg/.png/.webp: the first frame is recovered as a still image,
/// animation is never kept, and the original is never touched.
/// </summary>
public class GifRecoveryTests
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

    private static readonly double[] Red = [200, 30, 30], Green = [30, 200, 30], Blue = [30, 30, 200];

    private static void AssertNoPartials(TempDir t) =>
        Assert.DoesNotContain(t.AllFiles(), f => f.EndsWith(".partial", StringComparison.Ordinal));

    [Fact]
    public void Probe_RecognisesGifByContent_AndCountsFrames()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedGif(t.File("Funny.jpg"), 120, 80, Red, Green, Blue);

        VipsRuntime.Initialize();
        var info = ImageProbe.Read(t.File("Funny.jpg"));

        Assert.True(info.IsGif);
        Assert.Equal(3, info.FrameCount);
        Assert.Equal((120, 80), (info.Width, info.Height)); // one frame, not the whole strip
        Assert.Equal("GIF", info.SourceFormatName);
    }

    [Fact]
    public void AnimatedGifNamedJpg_Resize_SavesFirstFrameOnly_AsPng_AndLeavesOriginalAlone()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedGif(t.File("Funny.jpg"), 120, 80, Red, Green, Blue);
        string before = Fixtures.Hash(t.File("Funny.jpg"));
        var mtime = File.GetLastWriteTimeUtc(t.File("Funny.jpg"));

        var r = Run(Resize50, t.File("Funny.jpg"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (GIF → PNG)", r.Status);
        Assert.Contains("animation was not kept", r.Message);
        Assert.Equal(Path.Combine(t.Path, "Resized", "Funny.png"), r.OutputPath);
        var o = Fixtures.Read(r.OutputPath!);
        Assert.StartsWith("png", o.Loader);
        Assert.Equal((60, 40), (o.Width, o.Height));
        int[] px = Fixtures.PixelAt(r.OutputPath!, 30, 20);
        Assert.Equal([200, 30, 30], px[..3]); // the first frame (red), not a later one
        Assert.Equal(before, Fixtures.Hash(t.File("Funny.jpg")));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(t.File("Funny.jpg")));
        Assert.Single(t.FilesIn("Resized"));
        AssertNoPartials(t);
    }

    [Fact]
    public void GifAtSameSize_IsStillRecovered_NotCopiedAsAGif()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedGif(t.File("Small.png"), 60, 40, Red, Green);
        var settings = new JobSettings { Mode = JobMode.Resize, Resize = new ResizeSettings { Width = 400 } };

        var r = Run(settings, t.File("Small.png"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (GIF → PNG)", r.Status);
        Assert.StartsWith("png", Fixtures.Read(r.OutputPath!).Loader);
        Assert.Equal((60, 40), (r.OutputWidth, r.OutputHeight)); // not enlarged
    }

    [Fact]
    public void GifResize_RespectsChosenFormat()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedGif(t.File("Clip.webp"), 120, 80, Blue, Red);

        var jpeg = Run(Resize50 with { OutputFormat = OutputFormatChoice.Jpeg }, t.File("Clip.webp"));
        var webp = Run(Resize50 with { OutputFormat = OutputFormatChoice.WebP }, t.File("Clip.webp"));

        Assert.Equal("Done (GIF → JPEG)", jpeg.Status);
        Assert.StartsWith("jpeg", Fixtures.Read(jpeg.OutputPath!).Loader);
        Assert.EndsWith(".jpg", jpeg.OutputPath);
        Assert.Equal("Done (GIF → WebP)", webp.Status);
        Assert.StartsWith("webp", Fixtures.Read(webp.OutputPath!).Loader);
        Assert.EndsWith(".webp", webp.OutputPath);
    }

    [Fact]
    public void Optimize_GifIsNeverCopiedUnchanged_EvenWhenPngIsNotSmaller()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedGif(t.File("Tiny.jpg"), 16, 16, Red);

        var r = Run(Optimize, t.File("Tiny.jpg"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (GIF → PNG)", r.Status);
        Assert.StartsWith("png", Fixtures.Read(r.OutputPath!).Loader);
        Assert.DoesNotContain("animation", r.Message); // a single-frame GIF had no animation to lose
    }

    [Fact]
    public void Optimize_TransparentGif_KeepsTransparencyInPng_EvenWithConversionAllowed()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Photo(200, 150))
        using (var rgba = Fixtures.WithAlpha(rgb, transparentCorner: true))
            rgba.Gifsave(t.File("Sticker.jpg"));

        var r = Run(OptimizeConvert, t.File("Sticker.jpg"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (GIF → PNG)", r.Status);
        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(4, o.Bands);
        Assert.Equal(0, o[3].Min());
        Assert.Equal(255, o[3].Max());
    }

    [Fact]
    public void TransparentGif_ManualJpeg_IsSkipped_NotFlattened()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Graphic(120, 80))
        using (var rgba = Fixtures.WithAlpha(rgb, transparentCorner: true))
            rgba.Gifsave(t.File("Logo.png"));

        var r = Run(Optimize with { OutputFormat = OutputFormatChoice.Jpeg }, t.File("Logo.png"));

        Assert.Equal(ItemOutcome.Skipped, r.Outcome);
        Assert.Equal("Has transparency", r.Status);
        Assert.Empty(t.FilesIn("Optimized"));
    }

    [Fact]
    public void Optimize_OpaqueGif_ManualJpeg_IsConverted()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200)) img.Gifsave(t.File("Holiday.jpg"));

        var r = Run(Optimize with { OutputFormat = OutputFormatChoice.Jpeg }, t.File("Holiday.jpg"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (GIF → JPEG)", r.Status);
        Assert.Equal(Path.Combine(t.Path, "Optimized", "Holiday.jpg"), r.OutputPath);
        Assert.StartsWith("jpeg", Fixtures.Read(r.OutputPath!).Loader);
    }

    [Fact]
    public void Optimize_GifToWebP_IsLossless_AndExact()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Graphic(160, 100)) img.Gifsave(t.File("Chart.png"));

        var r = Run(Optimize with { OutputFormat = OutputFormatChoice.WebP }, t.File("Chart.png"));

        Assert.Equal("Done (GIF → WebP)", r.Status);
        using var gif = Image.NewFromFile(t.File("Chart.png"));
        using var webp = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(3, webp.Bands); // libvips reads GIFs with an alpha channel; a fully opaque one is dropped
        using var diff = (gif.ExtractBand(0, n: 3) - webp).Abs();
        Assert.Equal(0, diff.Max());
    }

    [Fact]
    public void DamagedGif_FailsClearly_AndLeavesNothingBehind()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200)) img.Gifsave(t.File("full.gif"));
        byte[] bytes = File.ReadAllBytes(t.File("full.gif"));
        File.WriteAllBytes(t.File("Broken.jpg"), bytes[..(bytes.Length / 3)]);

        var r = Run(Resize50, t.File("Broken.jpg"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Contains(r.Status, new[] { "Damaged or incomplete", "Couldn't read image" });
        AssertNoPartials(t);
        Assert.Empty(t.FilesIn("Resized"));
    }

    [Fact]
    public void Batch_CountsRecoveredGifs_AndLeavesThemOutOfTheSpaceSaved()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(400, 300)) Fixtures.SavePngUncompressed(img, t.File("Photo.png"));
        Fixtures.SaveAnimatedGif(t.File("Funny.jpg"), 120, 80, Red, Green, Blue);
        File.WriteAllText(t.File("broken.jpg"), "nope");

        var items = new[] { "Photo.png", "Funny.jpg", "broken.jpg" }.Select(p => new BatchItem(t.File(p), "")).ToList();
        var s = new BatchRunner(Resize50).Run(items, null, CancellationToken.None);

        Assert.Equal(2, s.Done);
        Assert.Equal(1, s.Failed);
        Assert.Equal(1, s.RecoveredFromGif);
        Assert.Equal(new FileInfo(t.File("Photo.png")).Length, s.OriginalBytes);
        Assert.Equal(new FileInfo(t.File("Resized/Photo.png")).Length, s.NewBytes);
    }

    [Fact]
    public void RealGifFiles_AreStillNotPickedUp()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedGif(t.File("Real.gif"), 20, 20, Red, Green);
        Fixtures.SaveAnimatedGif(t.File("Misnamed.jpg"), 20, 20, Red, Green);

        var scanner = new InputScanner(includeSubfolders: false, customOutputFolder: null);
        var found = scanner.Expand([t.Path]).Select(f => Path.GetFileName(f.Path)).ToList();

        Assert.Equal(["Misnamed.jpg"], found);
        Assert.Equal(1, scanner.SkippedNotImages);
    }
}
