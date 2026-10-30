using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Scanning;

/// <summary>An image to add to the list, and where it sits inside a dropped folder ("" if at the top or dropped directly).</summary>
/// <param name="FoundByContent">
/// Set when the extension isn't one Paradis Resize lists but the file's first bytes show a supported image
/// (a JPEG named ".tiff"): what it really is.
/// </param>
public sealed record InputFile(string Path, string RelativeFolder, SourceKind? FoundByContent = null);

/// <summary>A file that was dropped or found but won't be added, and why.</summary>
public sealed record IgnoredFile(string Path, string Extension, string Reason);

/// <summary>
/// Turns dropped or chosen files and folders into a list of image files.
/// <para>
/// When scanning subfolders it never goes into a "Resized" or "Optimized" folder (the folders this app
/// creates), into the chosen custom output folder, or into hidden/system folders and links, so running
/// Paradis Resize twice on the same folder can't pick up its own copies and make copies of copies.
/// </para>
/// <para>
/// Files are picked up by extension. A file with any other extension gets one small look at its first
/// bytes (<see cref="ImageProbe.SniffLength"/>): if they clearly show a PNG, JPEG, WebP, BMP or AVIF, it is
/// added and processed as what it really is. GIF contents are never added this way, so files really named
/// ".gif" stay out of the list (Paradis Resize would lose their animation). Files stored only online
/// (OneDrive "files on demand") aren't opened, since that would download them.
/// Every file that isn't added is recorded in <see cref="Ignored"/> with the reason.
/// </para>
/// </summary>
public sealed class InputScanner
{
    private readonly bool _includeSubfolders;
    private readonly string? _customOutputFolder;
    private readonly HashSet<string> _seen;

    /// <summary>Files that were dropped or found but aren't added, with the reason for each.</summary>
    public List<IgnoredFile> Ignored { get; } = [];

    /// <summary>How many files were ignored (<see cref="Ignored"/>).</summary>
    public int SkippedNotImages => Ignored.Count;

    /// <summary>Leftover ".partial" files from an interrupted earlier run. Not counted as ignored images.</summary>
    public List<string> SkippedTemporaryFiles { get; } = [];

    /// <summary>Files added because their contents are a supported image although their extension isn't.</summary>
    public List<InputFile> FoundByContent { get; } = [];

    /// <summary>Folders not scanned because they are this app's own output folders.</summary>
    public List<string> SkippedOutputFolders { get; } = [];

    /// <param name="alreadyListed">Paths already in the list; they are not added twice.</param>
    public InputScanner(bool includeSubfolders, string? customOutputFolder, IEnumerable<string>? alreadyListed = null)
    {
        _includeSubfolders = includeSubfolders;
        _customOutputFolder = string.IsNullOrWhiteSpace(customOutputFolder) ? null : Normalize(customOutputFolder);
        _seen = new HashSet<string>(alreadyListed?.Select(Normalize) ?? [], PathComparer);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IEnumerable<InputFile> Expand(IEnumerable<string> paths, CancellationToken ct = default)
    {
        foreach (string raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            string path;
            try { path = Normalize(raw); }
            catch (Exception) { continue; }

            if (Directory.Exists(path))
            {
                foreach (var f in ScanFolder(path, path, ct)) yield return f;
            }
            else if (File.Exists(path))
            {
                if (Classify(path, "") is InputFile f && _seen.Add(path)) yield return f;
            }
        }
    }

    private IEnumerable<InputFile> ScanFolder(string root, string folder, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        string[] files;
        try { files = Directory.GetFiles(folder, "*", options); }
        catch (Exception ex)
        {
            CoreLog.Warn($"Could not list {folder}: {ex.GetType().Name}: {ex.Message}");
            yield break;
        }
        Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);

        string relative = Path.GetRelativePath(root, folder);
        if (relative == ".") relative = "";
        foreach (string f in files)
        {
            ct.ThrowIfCancellationRequested();
            string full = Normalize(f);
            if (Classify(full, relative) is InputFile input && _seen.Add(full)) yield return input;
        }

        if (!_includeSubfolders) yield break;

        string[] subfolders;
        try { subfolders = Directory.GetDirectories(folder, "*", options); }
        catch (Exception ex)
        {
            CoreLog.Warn($"Could not list subfolders of {folder}: {ex.GetType().Name}: {ex.Message}");
            yield break;
        }
        Array.Sort(subfolders, StringComparer.CurrentCultureIgnoreCase);

        foreach (string sub in subfolders)
        {
            ct.ThrowIfCancellationRequested();
            if (ShouldSkipFolder(sub)) continue;
            foreach (var f in ScanFolder(root, sub, ct)) yield return f;
        }
    }

    private bool ShouldSkipFolder(string folder)
    {
        string name = Path.GetFileName(folder);
        if (OutputPlanner.OwnFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            SkippedOutputFolders.Add(folder);
            return true;
        }
        string full = Normalize(folder);
        if (_customOutputFolder != null && PathComparer.Equals(full, _customOutputFolder))
        {
            SkippedOutputFolders.Add(folder);
            return true;
        }
        try
        {
            // Junctions and symbolic links could lead back up the tree, or somewhere unrelated.
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        catch (Exception)
        {
            return true;
        }
        return false;
    }

    /// <summary>The file to add, or null after recording why it's ignored.</summary>
    private InputFile? Classify(string path, string relative)
    {
        if (SupportedFormats.HasSupportedExtension(path)) return new InputFile(path, relative);
        if (path.EndsWith(FileNaming.TemporarySuffix, StringComparison.OrdinalIgnoreCase))
        {
            SkippedTemporaryFiles.Add(path);
            return null;
        }

        string ext = Path.GetExtension(path);
        string? reason = null;
        SourceKind? kind = null;
        string? other = null;
        try
        {
            var fi = new FileInfo(path);
            // Offline, RecallOnOpen (0x40000), RecallOnDataAccess (0x400000): reading would download it.
            if ((fi.Attributes & (FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
                reason = "not a supported image extension; contents not checked because the file is stored online only";
            else if (fi.Length == 0)
                reason = "empty file (0 bytes)";
            else
                kind = ImageProbe.Sniff(path, out other);
        }
        catch (Exception ex)
        {
            reason = $"not a supported image extension, and the contents couldn't be checked ({ex.GetType().Name}: {ex.Message})";
        }

        if (reason == null)
        {
            switch (kind)
            {
                case SourceKind.Png or SourceKind.Jpeg or SourceKind.WebP or SourceKind.Bmp or SourceKind.Avif:
                    var found = new InputFile(path, relative, kind);
                    FoundByContent.Add(found);
                    return found;
                case SourceKind.Gif:
                    reason = ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                        ? "GIF files aren't processed (their animation would be lost)"
                        : "contains a GIF; GIFs are only recovered when the file has a PNG, JPEG, WebP, BMP or AVIF name";
                    break;
                default:
                    reason = other != null
                        ? $"{other} image, which isn't a supported format"
                        : "not a supported image type";
                    break;
            }
        }
        Ignored.Add(new IgnoredFile(path, ext, reason));
        return null;
    }

    /// <summary>
    /// Writes one line per ignored file, per file added by its contents and per leftover temporary file, so a
    /// big folder's log says exactly what was left out and why.
    /// </summary>
    public void LogDetails(Action<string> write)
    {
        foreach (var f in FoundByContent)
        {
            string name = SupportedFormats.DisplayName(f.FoundByContent!.Value);
            write($"  Added by content: {f.Path} (extension {ExtensionText(Path.GetExtension(f.Path))}) is really a {name} image; " +
                  $"it will be processed as {name}. The extension does not match the contents; the original is not renamed.");
        }
        foreach (var f in Ignored)
            write($"  Ignored: {f.Path} (extension {ExtensionText(f.Extension)}): {f.Reason}");
        foreach (string f in SkippedTemporaryFiles)
            write($"  Ignored: {f}: unfinished temporary file left by an interrupted earlier run");
    }

    private static string ExtensionText(string ext) => ext.Length == 0 ? "none" : ext;

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
