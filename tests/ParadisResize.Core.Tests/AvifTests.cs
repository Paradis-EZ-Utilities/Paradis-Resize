using NetVips;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Scanning;

namespace ParadisResize.Core.Tests;

/// <summary>AVIF input, read and written by the bundled libvips (libheif with aom). AVIF stays AVIF unless another format is chosen.</summary>
public class AvifTests
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

    private static string SaveAvif(Image img, string path, int q = 70)
    {
        img.Heifsave(path, q: q, compression: Enums.ForeignHeifCompression.Av1, effort: 2);
        return path;
    }

    [Fact]
    public void Probe_RecognisesAvif()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(160, 120)) SaveAvif(img, t.File("Photo.avif"));

        var info = ImageProbe.Read(t.File("Photo.avif"));

        Assert.Equal(SourceKind.Avif, info.Source);
        Assert.Equal(ImageFormat.Avif, info.Format);
        Assert.Equal("AVIF", info.SourceFormatName);
        Assert.Equal((160, 120), (info.Width, info.Height));
        Assert.False(info.HasAlphaChannel);
    }

    [Fact]
    public void Resize_KeepsAvif_AndLeavesOriginalAlone()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(320, 240)) SaveAvif(img, t.File("Photo.avif"));
        string before = Fixtures.Hash(t.File("Photo.avif"));

        var r = Run(Resize50, t.File("Photo.avif"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done", r.Status);
        Assert.Equal(Path.Combine(t.Path, "Resized", "Photo.avif"), r.OutputPath);
        var o = Fixtures.Read(r.OutputPath!);
        Assert.StartsWith("heif", o.Loader);
        Assert.Equal((160, 120), (o.Width, o.Height));
        Assert.Equal(before, Fixtures.Hash(t.File("Photo.avif")));
    }

    [Fact]
    public void Resize_PreservesTransparency()
    {
        using var t = new TempDir();
        using (var rgb = Fixtures.Graphic(200, 120))
        using (var rgba = Fixtures.WithAlpha(rgb, transparentCorner: true))
            SaveAvif(rgba, t.File("Logo.avif"), q: 90);

        Assert.True(ImageProbe.Read(t.File("Logo.avif")).HasAlphaChannel);
        var r = Run(Resize50, t.File("Logo.avif"));

        using var o = Image.NewFromFile(r.OutputPath!);
        Assert.Equal(4, o.Bands);
        Assert.True(o[3].Getpoint(2, 2)[0] < 10);
        Assert.True(o[3].Getpoint(80, 50)[0] > 245);
    }

    [Fact]
    public void Optimize_AlreadyCompactAvif_IsCopiedUnchanged()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200)) SaveAvif(img, t.File("Small.avif"), q: 35);

        var r = Run(Optimize, t.File("Small.avif"));

        Assert.Equal(ItemOutcome.CopiedUnchanged, r.Outcome);
        Assert.Equal("Already efficient", r.Status);
        Assert.Equal(Fixtures.Hash(t.File("Small.avif")), Fixtures.Hash(r.OutputPath!));
        Assert.EndsWith("Small.avif", r.OutputPath);
    }

    [Fact]
    public void ManualFormats_AreRespected()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(200, 100)) SaveAvif(img, t.File("Photo.avif"));

        var jpeg = Run(Resize50 with { OutputFormat = OutputFormatChoice.Jpeg }, t.File("Photo.avif"));
        var png = Run(Resize50 with { OutputFormat = OutputFormatChoice.Png }, t.File("Photo.avif"));

        Assert.Equal("Done (now JPEG)", jpeg.Status);
        Assert.StartsWith("jpeg", Fixtures.Read(jpeg.OutputPath!).Loader);
        Assert.Equal("Done (now PNG)", png.Status);
        Assert.StartsWith("png", Fixtures.Read(png.OutputPath!).Loader);
    }

    [Fact]
    public void HeicNamedAvif_IsNotSupported()
    {
        using var t = new TempDir();
        byte[] heic = new byte[64];
        new byte[] { 0, 0, 0, 0x18 }.CopyTo(heic, 0);
        "ftypheic"u8.CopyTo(heic.AsSpan(4));
        "mif1heic"u8.CopyTo(heic.AsSpan(16));
        File.WriteAllBytes(t.File("Phone.avif"), heic);

        var r = Run(Resize50, t.File("Phone.avif"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Equal("Not a supported image", r.Status);
    }

    [Fact]
    public void TruncatedAvif_FailsGracefully()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200)) SaveAvif(img, t.File("full.avif"));
        byte[] bytes = File.ReadAllBytes(t.File("full.avif"));
        File.WriteAllBytes(t.File("Cut.avif"), bytes[..(bytes.Length / 2)]);

        var r = Run(Resize50, t.File("Cut.avif"));

        Assert.Equal(ItemOutcome.Failed, r.Outcome);
        Assert.Contains(r.Status, new[] { "Damaged or incomplete", "Couldn't read image" });
        Assert.DoesNotContain(t.AllFiles(), f => f.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public void AvifFiles_ArePickedUpByFolderScan()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(20, 20)) SaveAvif(img, t.File("a.avif"));
        var scanner = new InputScanner(includeSubfolders: false, customOutputFolder: null);
        Assert.Equal(["a.avif"], scanner.Expand([t.Path]).Select(f => Path.GetFileName(f.Path)));
    }
}
