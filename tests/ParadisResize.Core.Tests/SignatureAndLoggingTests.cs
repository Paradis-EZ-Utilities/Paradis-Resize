using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Scanning;

namespace ParadisResize.Core.Tests;

/// <summary>
/// v1.0.1: files with an unlisted extension but supported image contents are found by a small header check;
/// every ignored file is logged by name with the reason; GIF files stay out of folder scans.
/// </summary>
public class SignatureAndLoggingTests
{
    private static readonly double[] Red = [200, 30, 30], Green = [30, 200, 30];

    private static ItemResult Run(JobSettings settings, string path)
    {
        VipsRuntime.Initialize();
        var processor = new ImageProcessor(settings, new OutputPlanner(settings.Mode, settings.CustomOutputFolder));
        return processor.Process(path, "", null, CancellationToken.None);
    }

    /// <summary>A folder as found in testing: misnamed JPEGs, a real GIF, an app database, an empty file, and so on.</summary>
    private static TempDir MixedFolder()
    {
        var t = new TempDir();
        using (var img = Fixtures.Photo(120, 90))
        {
            Fixtures.SaveJpeg(img, t.File("Holiday.tiff"), 90);
            img.Pngsave(t.File("Chart.dat"));
            img.Webpsave(t.File("Sticker.bin"));
            img.Jpegsave(t.File("Normal.jpg"));
        }
        Fixtures.SaveBmp(t.File("Old.dib"), 8, 8, 24, (x, y) => (1, 2, 3, 255));
        Fixtures.SaveAnimatedGif(t.File("Dance.gif"), 20, 20, Red, Green);
        Fixtures.SaveAnimatedGif(t.File("Clip.tiff"), 20, 20, Red, Green);
        File.WriteAllBytes(t.File("default.realm"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllText(t.File("notes.txt"), "shopping list");
        File.WriteAllBytes(t.File("blank.wdp"), []);
        using (var img = Fixtures.Photo(20, 20)) img.Tiffsave(t.File("Scan.tif"));
        File.WriteAllBytes(t.File("half.png.1234abcd-0.png.partial"), [1, 2, 3]);
        return t;
    }

    [Fact]
    public void Scan_AddsSupportedContentsWithUnlistedExtensions()
    {
        using var t = MixedFolder();
        var s = new InputScanner(includeSubfolders: false, customOutputFolder: null);

        var files = s.Expand([t.Path]).ToList();

        Assert.Equal(["Chart.dat", "Holiday.tiff", "Normal.jpg", "Old.dib", "Sticker.bin"], files.Select(f => Path.GetFileName(f.Path)));
        Assert.Null(files.Single(f => f.Path.EndsWith("Normal.jpg")).FoundByContent);
        Assert.Equal(SourceKind.Jpeg, files.Single(f => f.Path.EndsWith("Holiday.tiff")).FoundByContent);
        Assert.Equal(SourceKind.Png, files.Single(f => f.Path.EndsWith("Chart.dat")).FoundByContent);
        Assert.Equal(SourceKind.WebP, files.Single(f => f.Path.EndsWith("Sticker.bin")).FoundByContent);
        Assert.Equal(SourceKind.Bmp, files.Single(f => f.Path.EndsWith("Old.dib")).FoundByContent);
        Assert.Equal(4, s.FoundByContent.Count);
    }

    [Fact]
    public void Scan_RecordsEveryIgnoredFile_WithItsReason()
    {
        using var t = MixedFolder();
        var s = new InputScanner(includeSubfolders: false, customOutputFolder: null);
        _ = s.Expand([t.Path]).ToList();

        var ignored = s.Ignored.ToDictionary(f => Path.GetFileName(f.Path));
        Assert.Equal(["Clip.tiff", "Dance.gif", "Scan.tif", "blank.wdp", "default.realm", "notes.txt"], ignored.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(6, s.SkippedNotImages); // the aggregate count is kept
        Assert.Contains("GIF files aren't processed", ignored["Dance.gif"].Reason);
        Assert.Contains("contains a GIF", ignored["Clip.tiff"].Reason);
        Assert.Contains("TIFF", ignored["Scan.tif"].Reason);
        Assert.Contains("empty", ignored["blank.wdp"].Reason);
        Assert.Equal("not a supported image type", ignored["default.realm"].Reason);
        Assert.Equal(".realm", ignored["default.realm"].Extension);
        Assert.Equal(Path.Combine(t.Path, "default.realm"), ignored["default.realm"].Path);
    }

    [Fact]
    public void Log_ListsEachIgnoredFile_ByFullPath_ExtensionAndReason()
    {
        using var t = MixedFolder();
        var s = new InputScanner(includeSubfolders: false, customOutputFolder: null);
        _ = s.Expand([t.Path]).ToList();
        var lines = new List<string>();

        s.LogDetails(lines.Add);

        string Line(string name) => Assert.Single(lines, l => l.Contains(Path.Combine(t.Path, name)));
        Assert.Contains("Ignored:", Line("default.realm"));
        Assert.Contains("(extension .realm)", Line("default.realm"));
        Assert.Contains("not a supported image type", Line("default.realm"));
        Assert.Contains("(extension .gif): GIF files aren't processed", Line("Dance.gif"));
        Assert.Contains("(extension .txt)", Line("notes.txt"));
        Assert.Contains("(extension .wdp): empty file", Line("blank.wdp"));
        Assert.Contains("Added by content", Line("Holiday.tiff"));
        Assert.Contains("really a JPEG image", Line("Holiday.tiff"));
        Assert.Contains("does not match", Line("Holiday.tiff"));
        Assert.Contains("unfinished temporary file", Line("half.png.1234abcd-0.png.partial"));
        Assert.Equal(6 + 4 + 1, lines.Count);
    }

    [Fact]
    public void DroppedFile_WithUnlistedExtension_IsCheckedToo()
    {
        using var t = MixedFolder();
        var s = new InputScanner(false, null);
        var files = s.Expand([t.File("Holiday.tiff"), t.File("notes.txt"), t.File("Dance.gif")]).ToList();
        Assert.Equal(["Holiday.tiff"], files.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(2, s.SkippedNotImages);
    }

    [Fact]
    public void JpegNamedTiff_IsProcessedAsJpeg_CopyGetsJpgExtension_OriginalUntouched()
    {
        using var t = MixedFolder();
        string before = Fixtures.Hash(t.File("Holiday.tiff"));

        var r = Run(new JobSettings { Mode = JobMode.Resize, Resize = new ResizeSettings { By = ResizeBy.Percentage, Percent = 50 } },
            t.File("Holiday.tiff"));

        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal("Done", r.Status);
        Assert.Equal(Path.Combine(t.Path, "Resized", "Holiday.jpg"), r.OutputPath);
        Assert.StartsWith("jpeg", Fixtures.Read(r.OutputPath!).Loader);
        Assert.Contains("named .tiff but is really a JPEG", r.Message);
        Assert.Equal(before, Fixtures.Hash(t.File("Holiday.tiff")));
        Assert.True(File.Exists(t.File("Holiday.tiff")));
    }

    [Fact]
    public void JpegNamedTiff_CopiedUnchanged_GetsJpgExtension()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(200, 150)) Fixtures.SaveJpeg(img, t.File("Small.tiff"), 60);

        var r = Run(new JobSettings { Mode = JobMode.Optimize }, t.File("Small.tiff"));

        Assert.Equal(ItemOutcome.CopiedUnchanged, r.Outcome);
        Assert.Equal(Path.Combine(t.Path, "Optimized", "Small.jpg"), r.OutputPath);
        Assert.Equal(Fixtures.Hash(t.File("Small.tiff")), Fixtures.Hash(r.OutputPath!));
        Assert.Contains("named .tiff", r.Message);
    }

    [Fact]
    public void Batch_WithDamagedAndBlankFiles_CarriesOn()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(200, 150))
        {
            img.Jpegsave(t.File("Good.jpg"));
            Fixtures.SaveJpeg(img, t.File("Misnamed.tiff"), 90);
        }
        File.WriteAllBytes(t.File("Zero.jpg"), []);
        File.WriteAllBytes(t.File("Zeros.jpg"), new byte[5000]);
        File.WriteAllText(t.File("Fake.bmp"), "BM nonsense");
        File.WriteAllText(t.File("Fake.avif"), "not an avif at all");
        Fixtures.SaveAnimatedWebP(t.File("Clip.webp"), 40, 30, transparentCorner: false, lossless: true, Red, Green);

        var scanner = new InputScanner(false, null);
        var items = scanner.Expand([t.Path]).Select(f => new BatchItem(f.Path, f.RelativeFolder)).ToList();
        var s = new BatchRunner(new JobSettings { Mode = JobMode.Resize, Resize = new ResizeSettings { By = ResizeBy.Percentage, Percent = 50 } })
            .Run(items, null, CancellationToken.None);

        Assert.Equal(7, s.Total);
        Assert.Equal(3, s.Done);  // Good.jpg, Misnamed.tiff, Clip.webp
        Assert.Equal(4, s.Failed);
        Assert.Equal(1, s.RecoveredFromAnimatedWebP);
        Assert.False(s.Cancelled);
    }
}
