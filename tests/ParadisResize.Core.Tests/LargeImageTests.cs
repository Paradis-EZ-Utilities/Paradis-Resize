using System.Diagnostics;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Processing;
using Xunit.Abstractions;

namespace ParadisResize.Core.Tests;

/// <summary>Skips a test unless PARADIS_LARGE_TESTS=1, because these write and read several 300+ MB files.</summary>
public sealed class LargeFactAttribute : FactAttribute
{
    public LargeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PARADIS_LARGE_TESTS") != "1")
            Skip = "Large-image test: set PARADIS_LARGE_TESTS=1 to run";
    }
}

/// <summary>
/// The core promise: a 12000 × 10000 PNG of hundreds of MB is processed without loading it whole.
/// A full decode of this image is 12000 × 10000 × 3 bytes = 360 MB before any working copies; the
/// tests require the whole process to stay well under that.
/// </summary>
[Collection("Large")]
public class LargeImageTests(ITestOutputHelper output)
{
    private const int W = 12000, H = 10000;
    private static readonly Lazy<string> HugePng = new(MakeHugePng);

    private static string MakeHugePng()
    {
        string dir = Path.Combine(Path.GetTempPath(), "paradis-resize-large");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "HugeImage.png");
        if (!File.Exists(path))
        {
            using var img = Fixtures.Photo(W, H);
            img.Pngsave(path, compression: 1, filter: NetVips.Enums.ForeignPngFilter.None);
        }
        return path;
    }

    /// <summary>Peak resident memory of this process (Linux), reset before each measured step.</summary>
    private static void ResetPeak()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists("/proc/self/clear_refs")) File.WriteAllText("/proc/self/clear_refs", "5");
    }

    private static long PeakMB()
    {
        if (!File.Exists("/proc/self/status")) return Process.GetCurrentProcess().PeakWorkingSet64 / 1_000_000;
        string line = File.ReadLines("/proc/self/status").First(l => l.StartsWith("VmHWM:"));
        return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024 / 1_000_000;
    }

    private ItemResult RunMeasured(JobSettings settings, string path, string label, out long peakMB)
    {
        var processor = new ImageProcessor(settings, new OutputPlanner(settings.Mode, settings.CustomOutputFolder));
        ResetPeak();
        var sw = Stopwatch.StartNew();
        var r = processor.Process(path, "", null, CancellationToken.None);
        peakMB = PeakMB();
        output.WriteLine($"{label}: {r.Status}, {new FileInfo(path).Length / 1_000_000} MB -> {r.OutputSize / 1_000_000.0:0.0} MB, " +
                         $"{r.OutputWidth}x{r.OutputHeight}, {sw.Elapsed.TotalSeconds:0.0} s, peak memory {peakMB} MB");
        return r;
    }

    [LargeFact]
    public void Probe_IsInstant()
    {
        string png = HugePng.Value;
        var sw = Stopwatch.StartNew();
        var info = ImageProbe.Read(png);
        output.WriteLine($"Probe: {info.Describe()} in {sw.ElapsedMilliseconds} ms");
        Assert.Equal((W, H), (info.Width, info.Height));
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }

    [LargeFact]
    public void Resize_HugePng_To4096Wide()
    {
        string png = HugePng.Value;
        using var t = new TempDir();
        var r = RunMeasured(new JobSettings { Resize = new ResizeSettings { Width = 4096 }, CustomOutputFolder = t.Path }, png, "Resize to 4096", out long peak);
        Assert.Equal(ItemOutcome.Done, r.Outcome);
        Assert.Equal((4096, 3413), (r.OutputWidth, r.OutputHeight));
        Assert.True(peak < 600, $"peak {peak} MB");
    }

    [LargeFact]
    public void Optimize_HugePng_Lossless()
    {
        string png = HugePng.Value;
        using var t = new TempDir();
        var r = RunMeasured(new JobSettings { Mode = JobMode.Optimize, CustomOutputFolder = t.Path }, png, "Optimize (PNG)", out long peak);
        Assert.True(r.Saved);
        Assert.True(peak < 600, $"peak {peak} MB");
    }

    [LargeFact]
    public void Optimize_HugePng_WithConversion_BecomesJpeg()
    {
        string png = HugePng.Value;
        using var t = new TempDir();
        var r = RunMeasured(new JobSettings { Mode = JobMode.Optimize, AllowFormatConversion = true, CustomOutputFolder = t.Path },
            png, "Optimize + conversion", out long peak);
        Assert.Equal(ImageFormat.Jpeg, r.OutputFormat);
        Assert.True(r.OutputSize < new FileInfo(png).Length / 4);
        Assert.True(peak < 600, $"peak {peak} MB");
    }

    [LargeFact]
    public void Batch_OfHugeImages_WithABrokenOne_Continues()
    {
        string png = HugePng.Value;
        using var t = new TempDir();
        File.Copy(png, t.File("a/Huge1.png"));
        using (var src = File.OpenRead(png))
        using (var dst = File.Create(t.File("a/Truncated.png")))
        {
            var buf = new byte[1 << 20];
            for (long copied = 0; copied < new FileInfo(png).Length / 3;)
            {
                int n = src.Read(buf);
                dst.Write(buf, 0, n);
                copied += n;
            }
        }
        File.Copy(png, t.File("b/Huge2.png"));
        var items = new[] { "a/Huge1.png", "a/Truncated.png", "b/Huge2.png" }.Select(p => new BatchItem(t.File(p), "")).ToList();

        ResetPeak();
        var s = new BatchRunner(new JobSettings { Resize = new ResizeSettings { By = ResizeBy.Percentage, Percent = 25 } })
            .Run(items, null, CancellationToken.None);
        long peak = PeakMB();
        output.WriteLine($"Batch: {s.Done} done, {s.Failed} failed in {s.Elapsed.TotalSeconds:0.0} s, peak {peak} MB");

        Assert.Equal(2, s.Done);
        Assert.Equal(1, s.Failed);
        Assert.Equal(2, s.OutputFolders.Count);
        Assert.True(peak < 600, $"peak {peak} MB");
        Assert.DoesNotContain(t.AllFiles(), f => f.EndsWith(".partial"));
    }
}
