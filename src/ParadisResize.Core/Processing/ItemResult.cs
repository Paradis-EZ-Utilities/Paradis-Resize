using ParadisResize.Core.Imaging;

namespace ParadisResize.Core.Processing;

public enum ItemOutcome
{
    /// <summary>A new, processed copy was saved.</summary>
    Done,

    /// <summary>Processing wouldn't help (already small enough or already efficient), so the original was copied byte for byte.</summary>
    CopiedUnchanged,

    /// <summary>Deliberately not processed (for example JPEG was chosen for an image with transparency). Nothing saved.</summary>
    Skipped,

    /// <summary>Something went wrong. Nothing saved.</summary>
    Failed,

    /// <summary>The batch was cancelled while this image was being processed. Nothing saved.</summary>
    Cancelled,
}

/// <summary>What happened to one image.</summary>
public sealed record ItemResult
{
    public required ItemOutcome Outcome { get; init; }

    /// <summary>Short text for the list's Status column: "Done", "Already efficient", "Not enough memory".</summary>
    public required string Status { get; init; }

    /// <summary>A sentence or two for the tooltip and failure details. Null when the status says it all.</summary>
    public string? Message { get; init; }

    public ImageInfo? Source { get; init; }

    public string? OutputPath { get; init; }
    public long OutputSize { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }
    public ImageFormat? OutputFormat { get; init; }

    /// <summary>The copy is in a different format from the original (by choice or by automatic conversion).</summary>
    public bool Converted { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool Saved => Outcome is ItemOutcome.Done or ItemOutcome.CopiedUnchanged;
}
