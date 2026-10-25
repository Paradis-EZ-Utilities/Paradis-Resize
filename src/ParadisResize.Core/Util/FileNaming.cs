namespace ParadisResize.Core.Util;

public static class FileNaming
{
    /// <summary>
    /// "…\Image.png" → "…\Image (1).png" if taken, then "(2)" and so on. Existing files are never
    /// replaced: <paramref name="exists"/> decides what counts as taken (by default, anything on disk).
    /// </summary>
    public static string MakeUnique(string path, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; i < 100_000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>
    /// The name the encoder writes to before the result is checked and given its real name. Ends in
    /// ".partial" so no image viewer or scanner mistakes a half-written file for an image.
    /// </summary>
    public static string TemporaryPath(string finalPath, string jobId)
    {
        string dir = Path.GetDirectoryName(finalPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(finalPath);
        if (stem.Length > 120) stem = stem[..120];
        return Path.Combine(dir, $"{stem}.{jobId}{Path.GetExtension(finalPath)}.partial");
    }

    public const string TemporarySuffix = ".partial";
}
