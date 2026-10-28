using ParadisResize.Core.Scanning;

namespace ParadisResize.Core.Tests;

public class ScanningTests
{
    private static void Touch(string path) => File.WriteAllBytes(path, [1, 2, 3]);

    private static TempDir Tree()
    {
        var t = new TempDir();
        Touch(t.File("a.png"));
        Touch(t.File("b.JPG"));
        Touch(t.File("notes.txt"));
        Touch(t.File("sub/c.webp"));
        Touch(t.File("sub/deeper/d.jpeg"));
        Touch(t.File("Resized/a.png"));
        Touch(t.File("sub/Optimized/c.webp"));
        Touch(t.File("half.png.1234abcd-0.png.partial"));
        return t;
    }

    [Fact]
    public void TopLevelOnly_ByDefault()
    {
        using var t = Tree();
        var s = new InputScanner(includeSubfolders: false, customOutputFolder: null);
        var files = s.Expand([t.Path]).Select(f => Path.GetFileName(f.Path)).ToList();
        Assert.Equal(["a.png", "b.JPG"], files);
        Assert.Equal(1, s.SkippedNotImages); // notes.txt; the .partial file isn't counted
    }

    [Fact]
    public void Subfolders_SkipOwnOutputFolders()
    {
        using var t = Tree();
        var s = new InputScanner(includeSubfolders: true, customOutputFolder: null);
        var files = s.Expand([t.Path]).ToList();
        Assert.Equal(["a.png", "b.JPG", "c.webp", "d.jpeg"], files.Select(f => Path.GetFileName(f.Path)));
        Assert.DoesNotContain(files, f => f.Path.Contains("Resized") || f.Path.Contains("Optimized"));
        Assert.Equal(2, s.SkippedOutputFolders.Count);
        Assert.Equal(Path.Combine("sub", "deeper"), files.Single(f => f.Path.EndsWith("d.jpeg")).RelativeFolder);
    }

    [Fact]
    public void Subfolders_SkipCustomOutputFolder()
    {
        using var t = Tree();
        Touch(t.File("My Output/x.png"));
        var s = new InputScanner(includeSubfolders: true, customOutputFolder: Path.Combine(t.Path, "My Output"));
        Assert.DoesNotContain(s.Expand([t.Path]), f => f.Path.EndsWith("x.png"));
    }

    [Fact]
    public void DroppedFiles_AndDuplicates()
    {
        using var t = Tree();
        var s = new InputScanner(false, null, alreadyListed: [Path.Combine(t.Path, "b.JPG")]);
        var files = s.Expand([Path.Combine(t.Path, "a.png"), Path.Combine(t.Path, "a.png"), t.Path, Path.Combine(t.Path, "notes.txt")]).ToList();
        Assert.Single(files);
        Assert.Equal("", files[0].RelativeFolder);
        Assert.Equal(2, s.SkippedNotImages); // notes.txt dropped directly and found in the folder
    }

    [Fact]
    public void ExplicitlyDroppedOutputFolder_IsStillScanned()
    {
        using var t = Tree();
        var s = new InputScanner(true, null);
        Assert.Single(s.Expand([Path.Combine(t.Path, "Resized")]));
    }
}
