using ParadisResize.Core.Planning;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Tests;

public class PlanningTests
{
    private static SizePlanner.Plan Plan(int w, int h, ResizeSettings s) => SizePlanner.For(w, h, s);

    [Fact]
    public void WidthOnly_KeepsProportions()
    {
        var p = Plan(12000, 10000, new ResizeSettings { Width = 4096 });
        Assert.Equal(4096, p.Width);
        Assert.Equal(3413, p.Height);
    }

    [Fact]
    public void HeightOnly_KeepsProportions()
    {
        var p = Plan(12000, 10000, new ResizeSettings { Height = 1000 });
        Assert.Equal((1200, 1000), (p.Width, p.Height));
    }

    [Fact]
    public void BothWithProportions_FitsInsideTheBox()
    {
        var landscape = Plan(4000, 3000, new ResizeSettings { Width = 1920, Height = 1080 });
        Assert.Equal((1440, 1080), (landscape.Width, landscape.Height));
        var portrait = Plan(3000, 4000, new ResizeSettings { Width = 1920, Height = 1080 });
        Assert.Equal((810, 1080), (portrait.Width, portrait.Height));
    }

    [Fact]
    public void DontEnlarge_LeavesSmallImagesAlone()
    {
        var p = Plan(800, 600, new ResizeSettings { Width = 4096 });
        Assert.Equal((800, 600), (p.Width, p.Height));
        var pct = Plan(800, 600, new ResizeSettings { By = ResizeBy.Percentage, Percent = 200 });
        Assert.Equal((800, 600), (pct.Width, pct.Height));
    }

    [Fact]
    public void Enlarge_WhenAllowed()
    {
        var p = Plan(800, 600, new ResizeSettings { Width = 1600, DontEnlarge = false });
        Assert.Equal((1600, 1200), (p.Width, p.Height));
    }

    [Fact]
    public void Percentage()
    {
        var p = Plan(12000, 10000, new ResizeSettings { By = ResizeBy.Percentage, Percent = 50 });
        Assert.Equal((6000, 5000), (p.Width, p.Height));
        var tiny = Plan(3, 3, new ResizeSettings { By = ResizeBy.Percentage, Percent = 1 });
        Assert.Equal((1, 1), (tiny.Width, tiny.Height));
    }

    [Fact]
    public void ProportionsOff_SetsSidesIndependently()
    {
        var both = Plan(4000, 3000, new ResizeSettings { Width = 1000, Height = 1000, KeepProportions = false });
        Assert.Equal((1000, 1000), (both.Width, both.Height));
        var one = Plan(4000, 3000, new ResizeSettings { Width = 1000, KeepProportions = false });
        Assert.Equal((1000, 3000), (one.Width, one.Height));
        var clamped = Plan(800, 600, new ResizeSettings { Width = 1000, Height = 300, KeepProportions = false });
        Assert.Equal((800, 300), (clamped.Width, clamped.Height));
    }

    [Fact]
    public void Validation()
    {
        Assert.NotNull(new ResizeSettings().Validate());
        Assert.Null(new ResizeSettings { Width = 10 }.Validate());
        Assert.NotNull(new ResizeSettings { Width = 0 }.Validate());
        Assert.NotNull(new ResizeSettings { Width = 70000 }.Validate());
        Assert.Null(new ResizeSettings { By = ResizeBy.Percentage, Percent = 25 }.Validate());
        Assert.NotNull(new ResizeSettings { By = ResizeBy.Percentage, Percent = 0 }.Validate());
    }

    [Fact]
    public void UniqueNames_CountFromOne()
    {
        var taken = new HashSet<string> { Path.Combine("x", "Image.png"), Path.Combine("x", "Image (1).png") };
        Assert.Equal(Path.Combine("x", "Image (2).png"), FileNaming.MakeUnique(Path.Combine("x", "Image.png"), taken.Contains));
        Assert.Equal(Path.Combine("x", "Other.png"), FileNaming.MakeUnique(Path.Combine("x", "Other.png"), taken.Contains));
    }

    [Fact]
    public void OutputPlanner_DefaultFolderIsNextToEachOriginal()
    {
        var resize = new OutputPlanner(JobMode.Resize, null);
        string src = Path.Combine(Path.GetTempPath(), "Pictures", "Huge.png");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Pictures", "Resized"), resize.FolderFor(src, "ignored"));
        var optimize = new OutputPlanner(JobMode.Optimize, null);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "Pictures", "Optimized"), optimize.FolderFor(src));
    }

    [Fact]
    public void OutputPlanner_CustomFolderKeepsSubfolders_AndReservesNames()
    {
        string custom = Path.Combine(Path.GetTempPath(), "Out-" + Guid.NewGuid().ToString("N")[..6]);
        var planner = new OutputPlanner(JobMode.Resize, custom);
        Assert.Equal(custom, planner.FolderFor("/a/b.png", ""));
        Assert.Equal(Path.Combine(custom, "Trip", "Day 1"), planner.FolderFor("/a/b.png", Path.Combine("Trip", "Day 1")));
        string first = planner.Reserve(custom, "Image", ".png");
        string second = planner.Reserve(custom, "Image", ".png");
        Assert.Equal(Path.Combine(custom, "Image.png"), first);
        Assert.Equal(Path.Combine(custom, "Image (1).png"), second);
    }

    [Fact]
    public void PercentSaved()
    {
        Assert.Equal(75, Units.PercentSaved(8_400, 2_100));
        Assert.Equal(-50, Units.PercentSaved(100, 150));
        Assert.Equal(99, Units.PercentSaved(1_000_000, 1));
    }
}
