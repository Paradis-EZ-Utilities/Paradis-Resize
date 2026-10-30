using System.ComponentModel;
using System.Runtime.CompilerServices;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Processing;
using ParadisResize.Core.Util;

namespace ParadisResize.UI;

public enum QueueState
{
    Reading,
    Ready,
    Unreadable,
    Waiting,
    Working,
    Done,
    Copied,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>One row of the image list.</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    public QueueItem(string path, string relativeFolder)
    {
        Path = path;
        RelativeFolder = relativeFolder;
        FileName = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string RelativeFolder { get; }
    public string FileName { get; }

    public ImageInfo? Info { get; private set; }
    public ItemResult? Result { get; private set; }

    /// <summary>Why the header couldn't be read, when <see cref="State"/> is Unreadable.</summary>
    public string? ReadError { get; private set; }

    public QueueState State { get; private set; } = QueueState.Reading;

    private double _progress;

    /// <summary>0–1 while this image is being processed.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.01) return;
            _progress = value;
            OnChanged(nameof(StatusText));
        }
    }

    public string FormatText => Info != null ? (Info.IsAnimatedWebP ? "WebP (animated)" : Info.SourceFormatName)
        : (SupportedFormats.KindFromExtension(Path) is SourceKind k ? SupportedFormats.DisplayName(k) : "");

    public string DimensionsText
    {
        get
        {
            if (Info == null) return "";
            string original = Units.FormatDimensions(Info.Width, Info.Height);
            if (Result is { Saved: true } r && (r.OutputWidth != Info.Width || r.OutputHeight != Info.Height))
                return $"{original} → {Units.FormatDimensions(r.OutputWidth, r.OutputHeight)}";
            return original;
        }
    }

    public string SizeText
    {
        get
        {
            if (Info == null) return "";
            if (Result is { Saved: true } r)
            {
                string arrow = r.Converted && r.OutputFormat is ImageFormat f ? $" {SupportedFormats.DisplayName(f)}" : "";
                return $"{Units.FormatSize(Info.FileSize)} → {Units.FormatSize(r.OutputSize)}{arrow}";
            }
            return Units.FormatSize(Info.FileSize);
        }
    }

    public string SavedText
    {
        get
        {
            if (Info == null || Result is not { Saved: true } r) return "";
            // A still recovered from a GIF or an animated WebP isn't a saving: an animation's other frames were simply left out.
            if (r.Outcome == ItemOutcome.CopiedUnchanged || Info.IsFrameRecovery) return "–";
            int pct = Units.PercentSaved(Info.FileSize, r.OutputSize);
            return pct >= 0 ? $"{pct}%" : $"+{-pct}% larger";
        }
    }

    public string StatusText => State switch
    {
        QueueState.Reading => "Reading…",
        QueueState.Ready => Info switch
        {
            { IsGif: true } => "Ready (GIF, still image only)",
            { IsAnimatedWebP: true } => "Ready (animated, first frame only)",
            { IsBmp: true } => "Ready (BMP, saved as PNG)",
            _ => "Ready",
        },
        QueueState.Unreadable => ReadError ?? "Can't read",
        QueueState.Waiting => "Waiting",
        QueueState.Working => Progress > 0.01 ? $"Working… {Progress:P0}" : "Working…",
        _ => Result?.Status ?? State.ToString(),
    };

    /// <summary>Used by the list to colour the status: ok, muted, warn, error, busy.</summary>
    public string StatusKind => State switch
    {
        QueueState.Done => "ok",
        QueueState.Copied => "muted",
        QueueState.Skipped => "warn",
        QueueState.Failed or QueueState.Unreadable => "error",
        QueueState.Working => "busy",
        _ => "muted",
    };

    public string ToolTipText
    {
        get
        {
            var lines = new List<string> { Path };
            if (Info != null) lines.Add($"{Info.SourceFormatName}, {Units.FormatDimensions(Info.Width, Info.Height)}, {Units.FormatBytes(Info.FileSize)}");
            if (Info is { IsGif: true } && Result == null)
                lines.Add(Info.FrameCount > 1
                    ? $"This file is really an animated GIF ({Info.FrameCount:N0} frames) with the wrong name. Only its first frame will be saved, as a still image; the animation won't be kept."
                    : "This file is really a GIF with the wrong name. It will be saved as a still image in a supported format.");
            if (Info is { IsAnimatedWebP: true } && Result == null)
                lines.Add($"This is an animated WebP ({Info.FrameCount:N0} frames). Only its first frame will be saved, as a still image; the animation won't be kept. The original is untouched.");
            if (Info is { IsBmp: true } && Result == null)
                lines.Add("Paradis Resize doesn't save BMP files, so the copy will be a PNG (or a JPEG, if conversion is allowed and it's a photo).");
            if (Info is { HasForeignExtension: true } && Result == null)
                lines.Add($"Named {System.IO.Path.GetExtension(Path)}, but the file is really a {Info.SourceFormatName} image. It will be processed as {Info.SourceFormatName}; the original keeps its name.");
            if (State == QueueState.Unreadable && ReadError != null) lines.Add(ReadErrorMessage ?? ReadError);
            if (Result?.Message != null) lines.Add(Result.Message);
            if (Result?.OutputPath != null) lines.Add($"Saved as: {Result.OutputPath}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    private string? ReadErrorMessage { get; set; }

    public bool CanProcess => State is not (QueueState.Reading or QueueState.Unreadable);

    public void SetInfo(ImageInfo info)
    {
        Info = info;
        State = QueueState.Ready;
        RaiseAll();
    }

    public void SetUnreadable(ImageJobException ex)
    {
        ReadError = ex.Title;
        ReadErrorMessage = ex.Message;
        State = QueueState.Unreadable;
        RaiseAll();
    }

    public void SetWaiting()
    {
        Result = null;
        _progress = 0;
        State = QueueState.Waiting;
        RaiseAll();
    }

    public void SetWorking()
    {
        _progress = 0;
        State = QueueState.Working;
        RaiseAll();
    }

    public void SetResult(ItemResult result)
    {
        Result = result;
        if (result.Source != null) Info = result.Source;
        State = result.Outcome switch
        {
            ItemOutcome.Done => QueueState.Done,
            ItemOutcome.CopiedUnchanged => QueueState.Copied,
            ItemOutcome.Skipped => QueueState.Skipped,
            ItemOutcome.Cancelled => QueueState.Cancelled,
            _ => QueueState.Failed,
        };
        RaiseAll();
    }

    /// <summary>After a cancelled batch, images that never started go back to Ready.</summary>
    public void ResetIfWaiting()
    {
        if (State != QueueState.Waiting) return;
        State = Info != null ? QueueState.Ready : QueueState.Reading;
        RaiseAll();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
