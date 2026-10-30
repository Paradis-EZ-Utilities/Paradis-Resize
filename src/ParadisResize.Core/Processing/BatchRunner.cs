using System.Diagnostics;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Processing;

/// <summary>One image waiting in the batch.</summary>
public sealed record BatchItem(string Path, string RelativeFolder);

/// <summary>Sent when an image starts (<see cref="Result"/> null), as it progresses, and when it finishes.</summary>
public sealed record BatchProgress(int Index, int Total, string Path, double ItemFraction, ItemResult? Result);

/// <summary>The totals for the completion summary.</summary>
public sealed record BatchSummary
{
    public int Total { get; init; }
    public int Done { get; init; }
    public int CopiedUnchanged { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }

    /// <summary>Images never started because the batch was cancelled, plus the one interrupted.</summary>
    public int NotProcessed { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>Still images recovered from GIFs with a PNG/JPEG/WebP name (included in <see cref="Done"/>).</summary>
    public int RecoveredFromGif { get; init; }

    /// <summary>Still images recovered from the first frame of animated WebPs (included in <see cref="Done"/>).</summary>
    public int RecoveredFromAnimatedWebP { get; init; }

    /// <summary>
    /// Sizes of the images that were saved (processed or copied), before and after. Images recovered from
    /// GIFs or animated WebPs aren't counted: dropping an animation's other frames isn't a saving to report.
    /// </summary>
    public long OriginalBytes { get; init; }
    public long NewBytes { get; init; }

    /// <summary>Every folder something was saved in.</summary>
    public IReadOnlyList<string> OutputFolders { get; init; } = [];

    public TimeSpan Elapsed { get; init; }

    public int Saved => Done + CopiedUnchanged;
    public long BytesSaved => OriginalBytes - NewBytes;
}

/// <summary>
/// Runs a batch one image at a time on a background thread. One at a time is deliberate: a single huge
/// image can need hundreds of MB, and libvips already uses every CPU core within each image, so running
/// images side by side would add memory risk for little speed. A failed image never stops the batch.
/// </summary>
public sealed class BatchRunner
{
    private readonly JobSettings _settings;

    public BatchRunner(JobSettings settings) => _settings = settings;

    public Task<BatchSummary> RunAsync(IReadOnlyList<BatchItem> items, IProgress<BatchProgress>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(items, progress, ct), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public BatchSummary Run(IReadOnlyList<BatchItem> items, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        VipsRuntime.Initialize();
        var sw = Stopwatch.StartNew();
        var planner = new OutputPlanner(_settings.Mode, _settings.CustomOutputFolder);
        var processor = new ImageProcessor(_settings, planner);
        var folders = new List<string>();
        int done = 0, copied = 0, skipped = 0, failed = 0, notProcessed = 0, fromGif = 0, fromWebP = 0;
        long originalBytes = 0, newBytes = 0;
        bool cancelled = false;

        CoreLog.Info($"Batch started: {items.Count} image(s). {_settings.Describe()}");
        for (int i = 0; i < items.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                notProcessed += items.Count - i;
                break;
            }

            var item = items[i];
            int index = i;
            CoreLog.Info($"[{i + 1}/{items.Count}] {item.Path}");
            progress?.Report(new BatchProgress(index, items.Count, item.Path, 0, null));
            var itemProgress = progress == null ? null
                : new ThrottledProgress(f => progress.Report(new BatchProgress(index, items.Count, item.Path, f, null)));

            ItemResult result;
            try
            {
                result = processor.Process(item.Path, item.RelativeFolder, itemProgress, ct);
            }
            catch (Exception ex)
            {
                // Process() already turns every failure into a result; this is the last line of defence.
                CoreLog.Error($"Unexpected failure processing {item.Path}", ex);
                result = new ItemResult { Outcome = ItemOutcome.Failed, Status = "Something went wrong", Message = "The log has the technical details." };
            }

            switch (result.Outcome)
            {
                case ItemOutcome.Done: done++; break;
                case ItemOutcome.CopiedUnchanged: copied++; break;
                case ItemOutcome.Skipped: skipped++; break;
                case ItemOutcome.Failed: failed++; break;
                case ItemOutcome.Cancelled: cancelled = true; notProcessed++; break;
            }
            if (result.Saved && result.Source is { IsGif: true }) fromGif++;
            if (result.Saved && result.Source is { IsAnimatedWebP: true }) fromWebP++;
            if (result.Saved && result.Source != null)
            {
                if (!result.Source.IsFrameRecovery)
                {
                    originalBytes += result.Source.FileSize;
                    newBytes += result.OutputSize;
                }
                string? folder = Path.GetDirectoryName(result.OutputPath);
                if (folder != null && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
            }
            if (result.Saved)
                CoreLog.Info($"  Result: {result.Status} in {result.Elapsed.TotalSeconds:0.0} s, " +
                             $"{result.Source?.FileSize ?? 0:N0} -> {result.OutputSize:N0} bytes");
            progress?.Report(new BatchProgress(index, items.Count, item.Path, 1, result));
            if (result.Outcome == ItemOutcome.Cancelled)
            {
                notProcessed += items.Count - i - 1;
                break;
            }
        }

        var summary = new BatchSummary
        {
            Total = items.Count,
            Done = done,
            CopiedUnchanged = copied,
            Skipped = skipped,
            Failed = failed,
            NotProcessed = notProcessed,
            RecoveredFromGif = fromGif,
            RecoveredFromAnimatedWebP = fromWebP,
            Cancelled = cancelled,
            OriginalBytes = originalBytes,
            NewBytes = newBytes,
            OutputFolders = folders,
            Elapsed = sw.Elapsed,
        };
        CoreLog.Info($"Batch {(cancelled ? "cancelled" : "finished")} in {Units.FormatDuration(summary.Elapsed)}: " +
                     $"{done} processed ({fromGif} recovered from GIF, {fromWebP} from animated WebP), {copied} copied unchanged, {skipped} skipped, {failed} failed, {notProcessed} not processed; " +
                     $"{originalBytes:N0} -> {newBytes:N0} bytes ({Units.PercentSaved(originalBytes, newBytes)}% smaller" +
                     $"{(fromGif + fromWebP > 0 ? "; recovered still images not included" : "")})");
        return summary;
    }

    /// <summary>Passes on at most ~10 updates a second, so a fast stream of libvips progress can't flood the UI.</summary>
    private sealed class ThrottledProgress(Action<double> report) : IProgress<double>
    {
        private long _last;

        public void Report(double value)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _last) < 100 && value < 1) return;
            Interlocked.Exchange(ref _last, now);
            report(value);
        }
    }
}
