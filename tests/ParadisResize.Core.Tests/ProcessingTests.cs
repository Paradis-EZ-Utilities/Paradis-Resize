using NetVips;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;

namespace ParadisResize.Core.Tests;

public class ProcessingTests
{
    private static ItemResult Run(JobSettings settings, string path, string relative = "", CancellationToken ct = default)
    {
        VipsRuntime.Initialize();
        var processor = new ImageProcessor(settings, new OutputPlanner(settings.Mode, settings.CustomOutputFolder));
        return processor.Process(path, relative, null, ct);
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

    // ---------------- Safety ----------------

    [Fact]
    public void Resize_WritesIntoResizedFolder_AndNeverTouchesTheOriginal()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(800, 600)) Fixtures.SavePngUncompressed(img, t.File("Photo.png"));
        string before = Fixtures.Hash(t.File("Photo.png"));
        var mtime = File.GetLastWriteTimeUtc(t.File("Photo.png"));

        var r = Run(Resize50, t.File("Photo.png"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal(Path.Combine(t.Path, "Resized", "Photo.png"), r.OutputPath);
        Assert.Equal((400, 300), (r.OutputWidth, r.OutputHeight));
        var o = Fixtures.Read(r.OutputPath!);
        Assert.Equal((400, 300), (o.Width, o.Height));
        Assert.StartsWith("png", o.Loader);
        Assert.Equal(before, Fixtures.Hash(t.File("Photo.png")));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(t.File("Photo.png")));
        AssertNoPartials(t);
    }

    [Fact]
    public void ExistingOutput_IsNeverOverwritten()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(200, 100)) Fixtures.SavePngUncompressed(img, t.File("Image.png"));
        File.WriteAllText(t.File("Resized/Image.png"), "keep me");
        File.WriteAllText(t.File("Resized/Image (1).png"), "keep me too");

        var r = Run(Resize50, t.File("Image.png"));

        Assert.Equal(Path.Combine(t.Path, "Resized", "Image (2).png"), r.OutputPath);
        Assert.Equal("keep me", File.ReadAllText(t.File("Resized/Image.png")));
        Assert.Equal("keep me too", File.ReadAllText(t.File("Resized/Image (1).png")));
    }

    [Fact]
    public void CustomFolder_UsedAndCreated()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(200, 100)) Fixtures.SavePngUncompressed(img, t.File("in/Image.png"));
        var settings = Resize50 with { CustomOutputFolder = t.File("out/new folder") };
        var r = Run(settings, t.File("in/Image.png"), relative: "Trip");
        Assert.Equal(Path.Combine(t.Path, "out", "new folder", "Trip", "Image.png"), r.OutputPath);
        Assert.False(Directory.Exists(t.File("in/Resized")));
    }

    // ---------------- Resize ----------------

    [Fact]
    public void Resize_ByWidth_KeepsAspect_AndFormat()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(1200, 1000)) Fixtures.SaveJpeg(img, t.File("Photo.jpeg"), 95);
        var r = Run(new JobSettings { Resize = new ResizeSettings { Width = 600 } }, t.File("Photo.jpeg"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.EndsWith("Photo.jpeg", r.OutputPath); // original extension spelling kept
        var o = Fixtures.Read(r.OutputPath!);
        Assert.Equal((600, 500), (o.Width, o.Height));
        Assert.StartsWith("jpeg", o.Loader);
    }

    [Fact]
    public void Resize_SmallerImage_IsCopiedUnchanged_NotEnlarged()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200)) Fixtures.SaveJpeg(img, t.File("Small.jpg"), 90);
        var r = Run(new JobSettings { Resize = new ResizeSettings { Width = 4096 } }, t.File("Small.jpg"));
        Assert.Equal(ItemOutcome.CopiedUnchanged, r.Outcome);
        Assert.Equal(Fixtures.Hash(t.File("Small.jpg")), Fixtures.Hash(r.OutputPath!));
    }

    [Fact]
    public void Resize_RespectsExifOrientation()
    {
        using var t = new TempDir();
        // Stored 800 wide x 600 tall, tagged "rotate 90° clockwise to view": shown 600 x 800.
        using (var img = Fixtures.Photo(800, 600)) Fixtures.SaveJpeg(img, t.File("Phone.jpg"), 92, orientation: 6);
        var info = ImageProbe.Read(t.File("Phone.jpg"));
        Assert.Equal((600, 800), (info.Width, info.Height));

        var r = Run(Resize50, t.File("Phone.jpg"));

        var o = Fixtures.Read(r.OutputPath!);
        Assert.Equal((300, 400), (o.Width, o.Height)); // upright
        Assert.Equal(1, o.Orientation); // and no longer asks viewers to rotate it again
    }

    [Fact]
    public void Resize_KeepsTransparency()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Graphic(400, 300))
        using (var img = Fixtures.WithAlpha(rgb, transparentCorner: true))
            img.Pngsave(t.File("Logo.png"));
        var r = Run(Resize50, t.File("Logo.png"));
        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(4, o.Bands);
        Assert.Equal(0, o[3].Min());
    }

    [Fact]
    public void Resize_ToWebP_AndJpeg_ManualFormat()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(600, 400)) Fixtures.SavePngUncompressed(img, t.File("Photo.png"));
        var webp = Run(Resize50 with { OutputFormat = OutputFormatChoice.WebP }, t.File("Photo.png"));
        Assert.EndsWith("Photo.webp", webp.OutputPath);
        Assert.True(webp.Converted);
        Assert.StartsWith("webp", Fixtures.Read(webp.OutputPath!).Loader);
        var jpg = Run(Resize50 with { OutputFormat = OutputFormatChoice.Jpeg }, t.File("Photo.png"));
        Assert.EndsWith("Photo.jpg", jpg.OutputPath);
    }

    [Fact]
    public void ManualJpeg_OnTransparentImage_IsSkipped_NotFlattened()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Graphic(200, 200))
        using (var img = Fixtures.WithAlpha(rgb, transparentCorner: true))
            img.Pngsave(t.File("Logo.png"));
        var r = Run(Resize50 with { OutputFormat = OutputFormatChoice.Jpeg }, t.File("Logo.png"));
        Assert.Equal(ItemOutcome.Skipped, r.Outcome);
        Assert.Null(r.OutputPath);
        Assert.Empty(t.FilesIn("Resized"));
    }

    [Fact]
    public void ManualJpeg_OnOpaqueAlpha_IsFine()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Photo(200, 200))
        using (var img = Fixtures.WithAlpha(rgb, transparentCorner: false))
            img.Pngsave(t.File("Opaque.png"));
        var r = Run(Resize50 with { OutputFormat = OutputFormatChoice.Jpeg }, t.File("Opaque.png"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal(3, Fixtures.Read(r.OutputPath!).Bands);
    }

    [Fact]
    public void SixteenBitPng_BecomesEightBit()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200))
        using (var wide = (img.Cast(Enums.BandFormat.Ushort) * 257).Cast(Enums.BandFormat.Ushort).Copy(interpretation: Enums.Interpretation.Rgb16))
            wide.Pngsave(t.File("Deep.png"));
        Assert.True(ImageProbe.Read(t.File("Deep.png")).IsHighBitDepth);
        var r = Run(Resize50, t.File("Deep.png"));
        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(Enums.BandFormat.Uchar, o.Format);
    }

    [Fact]
    public void CmykJpeg_IsResizedToRgb()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200))
        using (var cmyk = img.IccTransform("cmyk", intent: Enums.Intent.Relative))
            cmyk.Jpegsave(t.File("Print.jpg"), q: 95);
        Assert.Equal(4, ImageProbe.Read(t.File("Print.jpg")).Bands);
        var r = Run(Resize50 with { OutputFormat = OutputFormatChoice.Png }, t.File("Print.jpg"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal(3, Fixtures.Read(r.OutputPath!).Bands);
    }

    // ---------------- Optimize ----------------

    [Fact]
    public void Optimize_WastefulPng_BecomesSmallerPng_PixelIdentical()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Graphic(800, 600)) Fixtures.SavePngUncompressed(img, t.File("Shot.png"));
        var r = Run(Optimize, t.File("Shot.png"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.EndsWith(Path.Combine("Optimized", "Shot.png"), r.OutputPath);
        Assert.True(r.OutputSize < new FileInfo(t.File("Shot.png")).Length / 10);
        Assert.Equal(0, Fixtures.MaxDifference(t.File("Shot.png"), r.OutputPath!));
    }

    [Fact]
    public void Optimize_OpaqueAlpha_IsDropped_WithoutVisibleChange()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Graphic(400, 300))
        using (var img = Fixtures.WithAlpha(rgb, transparentCorner: false))
            Fixtures.SavePngUncompressed(img, t.File("Opaque.png"));
        var r = Run(Optimize, t.File("Opaque.png"));
        var o = Fixtures.Read(r.OutputPath!);
        Assert.Equal(3, o.Bands);
        using var a = Image.NewFromFile(t.File("Opaque.png"));
        using var rgbOnly = a.ExtractBand(0, n: 3);
        using var b = Image.NewFromFile(r.OutputPath!);
        using var diff = (rgbOnly - b).Abs();
        Assert.Equal(0, diff.Max());
    }

    [Fact]
    public void Optimize_AlreadyCompactPng_IsCopiedUnchanged()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Graphic(400, 300))
            img.Pngsave(t.File("Tight.png"), compression: 9, filter: Enums.ForeignPngFilter.All);
        var r = Run(Optimize, t.File("Tight.png"));
        Assert.Equal(ItemOutcome.CopiedUnchanged, r.Outcome);
        Assert.Equal("Already efficient", r.Status);
        Assert.Equal(Fixtures.Hash(t.File("Tight.png")), Fixtures.Hash(r.OutputPath!));
        AssertNoPartials(t);
    }

    [Fact]
    public void Optimize_PhotoPng_WithConversion_BecomesJpeg()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(1200, 900)) img.Pngsave(t.File("Holiday.png"));
        var r = Run(OptimizeConvert, t.File("Holiday.png"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.True(r.Converted);
        Assert.Equal(ImageFormat.Jpeg, r.OutputFormat);
        Assert.EndsWith("Holiday.jpg", r.OutputPath);
        Assert.Equal("Done (now JPEG)", r.Status);
        Assert.True(r.OutputSize < new FileInfo(t.File("Holiday.png")).Length / 2);
    }

    [Fact]
    public void Optimize_PhotoPng_WithoutConversion_StaysPng()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(600, 400)) Fixtures.SavePngUncompressed(img, t.File("Holiday.png"));
        var r = Run(Optimize, t.File("Holiday.png"));
        Assert.Equal(ImageFormat.Png, r.OutputFormat);
        Assert.False(r.Converted);
    }

    [Fact]
    public void Optimize_GraphicPng_WithConversion_StaysPng()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Graphic(1200, 900)) Fixtures.SavePngUncompressed(img, t.File("Diagram.png"));
        var r = Run(OptimizeConvert, t.File("Diagram.png"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal(ImageFormat.Png, r.OutputFormat);
        Assert.False(r.Converted);
    }

    [Fact]
    public void Optimize_TransparentPhotoPng_WithConversion_StaysPngWithAlpha()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Photo(600, 400))
        using (var img = Fixtures.WithAlpha(rgb, transparentCorner: true))
            Fixtures.SavePngUncompressed(img, t.File("Cutout.png"));
        var r = Run(OptimizeConvert, t.File("Cutout.png"));
        Assert.Equal(ImageFormat.Png, r.OutputFormat);
        Assert.Equal(4, Fixtures.Read(r.OutputPath!).Bands);
    }

    [Fact]
    public void Optimize_HighQualityJpeg_GetsSmaller()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(1200, 900)) Fixtures.SaveJpeg(img, t.File("Camera.jpg"), 100);
        var r = Run(Optimize, t.File("Camera.jpg"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal(ImageFormat.Jpeg, r.OutputFormat);
        Assert.True(r.OutputSize < new FileInfo(t.File("Camera.jpg")).Length * 0.9);
    }

    [Fact]
    public void Optimize_AlreadyCompressedJpeg_IsCopiedUnchanged()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(800, 600)) Fixtures.SaveJpeg(img, t.File("Web.jpg"), 70);
        var r = Run(Optimize, t.File("Web.jpg"));
        Assert.Equal(ItemOutcome.CopiedUnchanged, r.Outcome);
        Assert.Equal(Fixtures.Hash(t.File("Web.jpg")), Fixtures.Hash(r.OutputPath!));
    }

    [Fact]
    public void Optimize_LosslessWebP_StaysLossless()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Graphic(600, 400)) img.Webpsave(t.File("Icon.webp"), lossless: true, effort: 0);
        Assert.True(ImageProbe.Read(t.File("Icon.webp")).IsLosslessWebP);
        var r = Run(Optimize, t.File("Icon.webp"));
        if (r.Outcome == ItemOutcome.Done)
        {
            Assert.True(ImageProbe.Read(r.OutputPath!).IsLosslessWebP);
            Assert.Equal(0, Fixtures.MaxDifference(t.File("Icon.webp"), r.OutputPath!));
        }
        else
        {
            Assert.Equal(ItemOutcome.CopiedUnchanged, r.Outcome);
        }
    }

    [Fact]
    public void Optimize_LossyWebPWithAlpha_KeepsAlpha()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Photo(600, 400))
        using (var img = Fixtures.WithAlpha(rgb, transparentCorner: true))
            img.Webpsave(t.File("Sticker.webp"), q: 100);
        Assert.False(ImageProbe.Read(t.File("Sticker.webp")).IsLosslessWebP);
        var r = Run(Optimize, t.File("Sticker.webp"));
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(4, o.Bands);
        Assert.Equal(0, o[3].Min());
    }

    // ---------------- Failures ----------------

    [Fact]
    public void TruncatedPng_Fails_Clearly_AndLeavesNothingBehind()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(600, 400)) Fixtures.SavePngUncompressed(img, t.File("full.png"));
        byte[] bytes = File.ReadAllBytes(t.File("full.png"));
        File.WriteAllBytes(t.File("Broken.png"), bytes[..(bytes.Length / 2)]);
        File.Delete(t.File("full.png"));

        var r = Run(Resize50, t.File("Broken.png"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Damaged or incomplete", r.Status);
        AssertNoPartials(t);
        Assert.Empty(t.FilesIn("Resized"));
    }

    [Fact]
    public void TextFileNamedPng_IsNotSupported()
    {
        using var t = new TempDir();
        File.WriteAllText(t.File("fake.png"), "this is not an image");
        var r = Run(Resize50, t.File("fake.png"));
        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Not a supported image", r.Status);
    }

    [Fact]
    public void GifRenamedToPng_IsNotSupported()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Graphic(50, 50)) img.Gifsave(t.File("anim.png"));
        var r = Run(Resize50, t.File("anim.png"));
        Assert.Equal("Not a supported image", r.Status);
    }

    [Fact]
    public void MissingFile_Fails()
    {
        using var t = new TempDir();
        var r = Run(Resize50, t.File("gone.png"));
        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Image not found", r.Status);
    }

    [Fact]
    public void WebPOutput_TooLarge_FailsWithReason()
    {
        using var t = new TempDir();
        using (var img = Image.Black(17000, 20).NewFromImage(120, 130, 140).Copy(interpretation: Enums.Interpretation.Srgb))
            img.Pngsave(t.File("Banner.png"));
        var r = Run(Optimize with { OutputFormat = OutputFormatChoice.WebP }, t.File("Banner.png"));
        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Too large for WebP", r.Status);
    }

    [Fact]
    public void Cancelled_BeforeStart_SavesNothing()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(200, 100)) img.Pngsave(t.File("a.png"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = Run(Resize50, t.File("a.png"), ct: cts.Token);
        Assert.Equal(ItemOutcome.Cancelled, r.Outcome);
        Assert.Empty(t.FilesIn("Resized"));
    }

    [Fact]
    public void Cancelled_WhileSaving_DeletesThePartialFile()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(4000, 3000)) Fixtures.SavePngUncompressed(img, t.File("Big.png"));
        string before = Fixtures.Hash(t.File("Big.png"));
        using var cts = new CancellationTokenSource();
        var processor = new ImageProcessor(Optimize, new OutputPlanner(JobMode.Optimize, null));
        var progress = new SyncProgress(f => { if (f > 0.05) cts.Cancel(); });

        var r = processor.Process(t.File("Big.png"), "", progress, cts.Token);

        Assert.Equal(ItemOutcome.Cancelled, r.Outcome);
        AssertNoPartials(t);
        Assert.Empty(t.FilesIn("Optimized"));
        Assert.Equal(before, Fixtures.Hash(t.File("Big.png")));
    }

    private sealed class SyncProgress(Action<double> a) : IProgress<double>
    {
        public void Report(double value) => a(value);
    }
}
