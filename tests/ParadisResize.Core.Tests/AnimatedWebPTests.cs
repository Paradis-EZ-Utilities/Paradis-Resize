using NetVips;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Scanning;

namespace ParadisResize.Core.Tests;

/// <summary>
/// Animated WebPs: a still image is recovered from the first frame, animation is never kept, the original is
/// never touched, and the shrink from dropping frames isn't reported as a saving (as for GIFs).
/// </summary>
public class AnimatedWebPTests
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

    private static int Frames(string path)
    {
        using var img = Image.NewFromFile(path);
        return img.Contains("n-pages") ? (int)img.Get("n-pages") : 1;
    }

    /// <summary>An animated WebP of photo-like frames (frame 1 = seed 1, ...).</summary>
    private static string SavePhotoAnimation(string path, int width, int height, int frames, bool lossless)
    {
        var images = Enumerable.Range(1, frames).Select(i => Fixtures.Photo(width, height, seed: i * 10)).ToArray();
        try
        {
            using var strip = Image.Arrayjoin(images, across: 1);
            using var anim = strip.Mutate(m =>
            {
                m.Set(GValue.GIntType, "page-height", height);
                m.Set(GValue.ArrayIntType, "delay", Enumerable.Repeat(80, frames).ToArray());
            });
            if (lossless) anim.Webpsave(path, lossless: true, effort: 0);
            else anim.Webpsave(path, q: 90);
        }
        finally
        {
            foreach (var i in images) i.Dispose();
        }
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Probe_RecognisesAnimatedWebP_CountsFrames_AndReadsFirstFrameStorage(bool lossless)
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedWebP(t.File("Clip.webp"), 120, 80, transparentCorner: false, lossless, Red, Green, Blue);

        var info = ImageProbe.Read(t.File("Clip.webp"));

        Assert.True(info.IsAnimatedWebP);
        Assert.True(info.IsFrameRecovery);
        Assert.False(info.IsGif);
        Assert.Equal(3, info.FrameCount);
        Assert.Equal((120, 80), (info.Width, info.Height)); // the canvas, not the whole strip
        Assert.Equal(ImageFormat.WebP, info.Format);
        Assert.Equal(lossless, info.IsLosslessWebP);
    }

    [Fact]
    public void StillWebP_IsNotTreatedAsAnimated()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(100, 80)) img.Webpsave(t.File("Still.webp"));
        var info = ImageProbe.Read(t.File("Still.webp"));
        Assert.False(info.IsAnimatedWebP);
        Assert.False(info.IsFrameRecovery);
        Assert.Equal(1, info.FrameCount);
    }

    [Fact]
    public void Resize_SavesFirstFrameOnly_AsStillWebP_AndLeavesOriginalAlone()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedWebP(t.File("Clip.webp"), 120, 80, transparentCorner: false, lossless: true, Red, Green, Blue);
        string before = Fixtures.Hash(t.File("Clip.webp"));
        var mtime = File.GetLastWriteTimeUtc(t.File("Clip.webp"));

        var r = Run(Resize50, t.File("Clip.webp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (animated → still WebP)", r.Status);
        Assert.Contains("first frame", r.Message);
        Assert.Contains("animation was not kept", r.Message);
        Assert.Contains("original is untouched", r.Message);
        Assert.Equal(Path.Combine(t.Path, "Resized", "Clip.webp"), r.OutputPath);
        var o = Fixtures.Read(r.OutputPath!);
        Assert.StartsWith("webp", o.Loader);
        Assert.Equal((60, 40), (o.Width, o.Height));
        Assert.Equal(1, Frames(r.OutputPath!)); // a still image, not a shorter animation
        Assert.True(ImageProbe.Read(r.OutputPath!).IsLosslessWebP); // a lossless animation's frame stays lossless
        Assert.Equal([200, 30, 30], Fixtures.PixelAt(r.OutputPath!, 30, 20)[..3]); // the first frame (red)
        Assert.Equal(before, Fixtures.Hash(t.File("Clip.webp")));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(t.File("Clip.webp")));
        Assert.Single(t.FilesIn("Resized"));
        AssertNoPartials(t);
    }

    [Fact]
    public void Resize_PreservesTransparency()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedWebP(t.File("Sticker.webp"), 120, 80, transparentCorner: true, lossless: false, Blue, Red);

        var r = Run(Resize50, t.File("Sticker.webp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(4, o.Bands);
        Assert.Equal(0, o[3].Getpoint(0, 0)[0]);   // the transparent corner
        Assert.Equal(255, o[3].Getpoint(50, 30)[0]);
    }

    [Fact]
    public void AtSameSize_IsStillRecovered_NotCopiedAsAnAnimation()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedWebP(t.File("Small.webp"), 60, 40, transparentCorner: false, lossless: true, Red, Green);
        var settings = new JobSettings { Mode = JobMode.Resize, Resize = new ResizeSettings { Width = 400 } };

        var r = Run(settings, t.File("Small.webp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal((60, 40), (r.OutputWidth, r.OutputHeight)); // not enlarged
        Assert.Equal(1, Frames(r.OutputPath!));
    }

    [Fact]
    public void Optimize_IsNeverCopiedUnchanged_AndKeepsTransparency_EvenWithConversionAllowed()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedWebP(t.File("Tiny.webp"), 16, 16, transparentCorner: true, lossless: true, Red, Green);

        var r = Run(OptimizeConvert, t.File("Tiny.webp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (animated → still WebP)", r.Status);
        Assert.Equal(1, Frames(r.OutputPath!));
        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(4, o.Bands);
        Assert.Equal(0, o[3].Min());
    }

    [Fact]
    public void Optimize_OpaquePhotoAnimation_WithConversion_BecomesJpeg()
    {
        using var t = new TempDir();
        SavePhotoAnimation(t.File("Holiday.webp"), 400, 300, frames: 2, lossless: true);

        var r = Run(OptimizeConvert, t.File("Holiday.webp"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done (animated → still JPEG)", r.Status);
        Assert.Equal(Path.Combine(t.Path, "Optimized", "Holiday.jpg"), r.OutputPath);
        Assert.StartsWith("jpeg", Fixtures.Read(r.OutputPath!).Loader);
        Assert.True(r.Converted);
    }

    [Fact]
    public void Optimize_WithoutConversion_StaysWebP()
    {
        using var t = new TempDir();
        SavePhotoAnimation(t.File("Holiday.webp"), 300, 200, frames: 3, lossless: false);

        var r = Run(Optimize, t.File("Holiday.webp"));

        Assert.Equal("Done (animated → still WebP)", r.Status);
        Assert.False(r.Converted);
        Assert.Equal(1, Frames(r.OutputPath!));
        Assert.False(ImageProbe.Read(r.OutputPath!).IsLosslessWebP); // a lossy animation's frame stays lossy
    }

    [Fact]
    public void ManualPng_IsRespected()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedWebP(t.File("Clip.webp"), 120, 80, transparentCorner: true, lossless: true, Green, Red);

        var r = Run(Resize50 with { OutputFormat = OutputFormatChoice.Png }, t.File("Clip.webp"));

        Assert.Equal("Done (animated → still PNG)", r.Status);
        Assert.EndsWith("Clip.png", r.OutputPath);
        var o = Fixtures.Read(r.OutputPath!);
        Assert.StartsWith("png", o.Loader);
        Assert.Equal(4, o.Bands);
    }

    [Fact]
    public void DamagedAnimatedWebP_FailsClearly_AndLeavesNothingBehind()
    {
        using var t = new TempDir();
        SavePhotoAnimation(t.File("full.webp"), 300, 200, frames: 3, lossless: false);
        byte[] bytes = File.ReadAllBytes(t.File("full.webp"));
        File.WriteAllBytes(t.File("Broken.webp"), bytes[..(bytes.Length / 4)]);

        var r = Run(Resize50, t.File("Broken.webp"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Contains(r.Status, new[] { "Damaged or incomplete", "Couldn't read image" });
        AssertNoPartials(t);
        Assert.Empty(t.FilesIn("Resized"));
    }

    [Fact]
    public void Batch_CountsRecoveredAnimations_AndLeavesThemOutOfTheSpaceSaved()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(400, 300)) Fixtures.SavePngUncompressed(img, t.File("Photo.png"));
        SavePhotoAnimation(t.File("Clip.webp"), 300, 200, frames: 4, lossless: true);
        Fixtures.SaveAnimatedGif(t.File("Funny.jpg"), 120, 80, Red, Green, Blue);
        File.WriteAllText(t.File("broken.webp"), "nope");

        var items = new[] { "Photo.png", "Clip.webp", "Funny.jpg", "broken.webp" }.Select(p => new BatchItem(t.File(p), "")).ToList();
        var s = new BatchRunner(Optimize).Run(items, null, CancellationToken.None);

        Assert.Equal(3, s.Done);
        Assert.Equal(1, s.Failed);
        Assert.Equal(1, s.RecoveredFromAnimatedWebP);
        Assert.Equal(1, s.RecoveredFromGif);
        // Only the PNG counts towards the space saved.
        Assert.Equal(new FileInfo(t.File("Photo.png")).Length, s.OriginalBytes);
        Assert.Equal(new FileInfo(t.File("Optimized/Photo.png")).Length, s.NewBytes);
    }

    [Fact]
    public void AnimatedWebP_IsPickedUpByFolderScan()
    {
        using var t = new TempDir();
        Fixtures.SaveAnimatedWebP(t.File("Clip.webp"), 20, 20, transparentCorner: false, lossless: true, Red, Green);
        var scanner = new InputScanner(includeSubfolders: false, customOutputFolder: null);
        Assert.Equal(["Clip.webp"], scanner.Expand([t.Path]).Select(f => Path.GetFileName(f.Path)));
    }
}
