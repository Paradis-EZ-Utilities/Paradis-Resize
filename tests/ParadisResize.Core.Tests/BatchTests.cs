using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;

namespace ParadisResize.Core.Tests;

public class BatchTests
{
    [Fact]
    public void BadImage_DoesNotStopTheBatch_AndSummaryAddsUp()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(600, 400)) Fixtures.SavePngUncompressed(img, t.File("one/a.png"));
        File.WriteAllText(t.File("one/broken.png"), "nope");
        using (var img = Fixtures.Photo(300, 200)) Fixtures.SaveJpeg(img, t.File("two/b.jpg"), 95);
        using (var img = Fixtures.Photo(100, 100)) Fixtures.SaveJpeg(img, t.File("two/small.jpg"), 95);

        var settings = new JobSettings { Resize = new ResizeSettings { Width = 200 } };
        var items = new[] { "one/a.png", "one/broken.png", "two/b.jpg", "two/small.jpg" }
            .Select(p => new BatchItem(t.File(p), "")).ToList();
        var reports = new List<BatchProgress>();

        var s = new BatchRunner(settings).Run(items, new SyncProgress<BatchProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(4, s.Total);
        Assert.Equal(2, s.Done);
        Assert.Equal(1, s.CopiedUnchanged); // small.jpg is already narrower than 200
        Assert.Equal(1, s.Failed);
        Assert.False(s.Cancelled);
        Assert.Equal(2, s.OutputFolders.Count); // one\Resized and two\Resized: unrelated folders aren't mixed
        Assert.True(File.Exists(t.File("one/Resized/a.png")));
        Assert.True(File.Exists(t.File("two/Resized/b.jpg")));
        Assert.Equal(4, reports.Count(r => r.Result != null));
        long originals = new[] { "one/a.png", "two/b.jpg", "two/small.jpg" }.Sum(p => new FileInfo(t.File(p)).Length);
        Assert.Equal(originals, s.OriginalBytes);
    }

    [Fact]
    public void SameNamesFromDifferentFolders_IntoOneCustomFolder_GetNumbered()
    {
        using var t = new TempDir();
        using (var img = Fixtures.Photo(300, 200)) { img.Pngsave(t.File("x/Image.png")); img.Pngsave(t.File("y/Image.png")); }
        var settings = new JobSettings { Resize = new ResizeSettings { Width = 100 }, CustomOutputFolder = t.File("out") };
        var s = new BatchRunner(settings).Run([new(t.File("x/Image.png"), ""), new(t.File("y/Image.png"), "")], null, CancellationToken.None);
        Assert.Equal(2, s.Done);
        Assert.True(File.Exists(t.File("out/Image.png")));
        Assert.True(File.Exists(t.File("out/Image (1).png")));
    }

    [Fact]
    public void Cancel_KeepsFinishedFiles_AndCountsTheRest()
    {
        using var t = new TempDir();
        var items = new List<BatchItem>();
        for (int i = 0; i < 6; i++)
        {
            using var img = Fixtures.Photo(400, 300, seed: i);
            img.Pngsave(t.File($"p{i}.png"));
            items.Add(new BatchItem(t.File($"p{i}.png"), ""));
        }
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(p => { if (p.Result != null && p.Index == 1) cts.Cancel(); });

        var s = new BatchRunner(new JobSettings { Resize = new ResizeSettings { Width = 100 } }).Run(items, progress, cts.Token);

        Assert.True(s.Cancelled);
        Assert.Equal(2, s.Done);
        Assert.Equal(4, s.NotProcessed);
        Assert.Equal(2, t.FilesIn("Resized").Length);
        Assert.Equal(6, t.FilesIn("").Length); // originals all still there
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T>
    {
        public void Report(T value) => a(value);
    }
}
