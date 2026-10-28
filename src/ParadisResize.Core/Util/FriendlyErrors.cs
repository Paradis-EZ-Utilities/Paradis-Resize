using NetVips;

namespace ParadisResize.Core.Util;

public enum FailureKind
{
    Unsupported,
    Damaged,
    InputMissing,
    InputLocked,
    OutOfMemory,
    TooLargeForFormat,
    HasTransparency,
    DestinationNotWritable,
    DiskFull,
    OutputIsInput,
    Failed,
}

/// <summary>A failure explained in one plain sentence, with the technical detail kept for the log.</summary>
public sealed class ImageJobException : Exception
{
    public FailureKind Kind { get; }

    /// <summary>The short text shown in the list's Status column, e.g. "Not enough memory".</summary>
    public string Title { get; }

    /// <summary>Technical detail (libvips' error text, exception text). Goes to the log, not the user.</summary>
    public string? Detail { get; }

    public ImageJobException(FailureKind kind, string title, string message, string? detail = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
    }
}

/// <summary>Maps libvips' error text and .NET IO exceptions to something a person can act on.</summary>
public static class FriendlyErrors
{
    /// <summary>Whether the failure happened while opening/reading the original or while saving the copy.</summary>
    public enum Stage { Reading, Saving }

    public static ImageJobException Classify(Exception ex, Stage stage)
    {
        if (ex is ImageJobException ije) return ije;
        string t = ex is VipsException ? ex.Message : ex.ToString();
        bool Has(string s) => t.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (ex is OutOfMemoryException or InsufficientMemoryException
            || Has("out of memory") || Has("unable to allocate") || Has("memory area too large")
            || Has("not enough memory") || Has("cannot allocate"))
            return OutOfMemory(t, ex);

        if (Has("No space left on device") || Has("not enough space on the disk") || Has("disk full")
            || ex is IOException && (ex.HResult & 0xFFFF) is 0x27 or 0x70)
            return new ImageJobException(FailureKind.DiskFull, "Not enough disk space",
                "The drive ran out of space while saving. Free up some space or choose a folder on another drive.", t, ex);

        if (ex is FileNotFoundException || stage == Stage.Reading && (Has("No such file") || Has("does not exist") || Has("cannot find the file")))
            return new ImageJobException(FailureKind.InputMissing, "Image not found",
                "The image was moved, renamed or deleted after it was added to the list.", t, ex);

        if (stage == Stage.Reading && (ex is UnauthorizedAccessException || Has("Permission denied") || Has("Access is denied")
            || Has("being used by another process") || Has("sharing violation")))
            return new ImageJobException(FailureKind.InputLocked, "Couldn't open the image",
                "Windows didn't allow the image to be opened. It may be open in another program, or you may not have permission to read it.", t, ex);

        if (stage == Stage.Saving && (ex is UnauthorizedAccessException || Has("Permission denied") || Has("Access is denied")
            || Has("Read-only file system")))
            return new ImageJobException(FailureKind.DestinationNotWritable, "Can't save in that folder",
                "Windows didn't allow the new image to be saved there. Choose another output folder, such as Pictures or Documents.", t, ex);

        if (ex is DirectoryNotFoundException || stage == Stage.Saving && Has("No such file or directory"))
            return new ImageJobException(FailureKind.DestinationNotWritable, "Output folder not available",
                "The output folder was removed or its drive was disconnected. Choose a folder again.", t, ex);

        // Only files that start like a PNG, JPEG or WebP reach libvips, so if it then can't tell what the
        // file is, the contents are damaged rather than an unsupported type.
        if (Has("is not a known file format") || Has("not a known file format") || Has("unsupported image format")
            || Has("source not a known format"))
            return Unreadable(t);

        // Decoder complaints: truncated downloads, broken checksums, invalid data.
        if (Has("premature end") || Has("truncated") || Has("crc") || Has("corrupt") || Has("invalid")
            || Has("read error") || Has("not a PNG") || Has("not a JPEG") || Has("bad ") || Has("unexpected end")
            || Has("decode") || Has("libspng") || Has("pngload") || Has("jpegload") || Has("webpload") || Has("VipsJpeg")
            || Has("gifload") || Has("nsgif")
            || Has("out of order read"))
            return Damaged(t);

        if (ex is IOException)
            return new ImageJobException(FailureKind.DestinationNotWritable, "Couldn't save the file",
                "The new image couldn't be saved. If a file with that name is open in another program, close it and try again.", t, ex);

        return new ImageJobException(FailureKind.Failed, "Something went wrong",
            "This image couldn't be processed. The log has the technical details.", t, ex);
    }

    public static ImageJobException OutOfMemory(string? detail = null, Exception? inner = null) =>
        new(FailureKind.OutOfMemory, "Not enough memory",
            "This image is too large for the memory available right now. Close other programs and try again, or resize it to a smaller size.",
            detail, inner);

    public static ImageJobException Unsupported(string? detail = null) =>
        new(FailureKind.Unsupported, "Not a supported image",
            "This is a type of image Paradis Resize doesn't work with. Only PNG, JPEG and WebP images are supported.", detail);

    public static ImageJobException Damaged(string? detail = null) =>
        new(FailureKind.Damaged, "Damaged or incomplete",
            "This image appears to be damaged or incomplete (for example, a download that didn't finish).", detail);

    /// <summary>The name says PNG/JPEG/WebP but the contents aren't recognisable as any image.</summary>
    public static ImageJobException Unreadable(string? detail = null) =>
        new(FailureKind.Damaged, "Couldn't read image",
            "This file couldn't be read as an image. It may be damaged or incomplete, or it may not really be an image.", detail);

    public static ImageJobException Animated() =>
        new(FailureKind.Unsupported, "Animated image",
            "Animated images aren't supported. Only still images can be resized or optimized.");

    public static ImageJobException TooLargeForWebP(int width, int height) =>
        new(FailureKind.TooLargeForFormat, "Too large for WebP",
            $"WebP images can be at most 16,383 pixels wide and tall; this one would be {width:N0} × {height:N0}. Choose PNG or JPEG, or resize it smaller.");

    public static ImageJobException TransparencyNotAllowed() =>
        new(FailureKind.HasTransparency, "Has transparency",
            "This image has transparent areas, and JPEG can't store transparency. It was skipped so the transparency isn't lost. Choose PNG or WebP for this image.");
}
