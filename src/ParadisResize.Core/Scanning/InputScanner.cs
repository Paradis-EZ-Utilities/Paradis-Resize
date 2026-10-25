using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Scanning;

/// <summary>An image to add to the list, and where it sits inside a dropped folder ("" if at the top or dropped directly).</summary>
public sealed record InputFile(string Path, string RelativeFolder);

/// <summary>
/// Turns dropped or chosen files and folders into a list of image files.
/// <para>
/// When scanning subfolders it never goes into a "Resized" or "Optimized" folder (the folders this app
/// creates), into the chosen custom output folder, or into hidden/system folders and links, so running
/// Paradis Resize twice on the same folder can't pick up its own copies and make copies of copies.
/// </para>
/// </summary>
public sealed class InputScanner
{
    private readonly bool _includeSubfolders;
    private readonly string? _customOutputFolder;
    private readonly HashSet<string> _seen;

    /// <summary>Files that were dropped or found but aren't PNG/JPEG/WebP by extension.</summary>
    public int SkippedNotImages { get; private set; }

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
                if (IsCandidate(path))
                {
                    if (_seen.Add(path)) yield return new InputFile(path, "");
                }
                else
                {
                    SkippedNotImages++;
                }
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
            if (!IsCandidate(f))
            {
                if (!f.EndsWith(FileNaming.TemporarySuffix, StringComparison.OrdinalIgnoreCase)) SkippedNotImages++;
                continue;
            }
            string full = Normalize(f);
            if (_seen.Add(full)) yield return new InputFile(full, relative);
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

    private static bool IsCandidate(string path) => SupportedFormats.HasSupportedExtension(path);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
