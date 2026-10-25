using ParadisResize.Core.Util;

namespace ParadisResize.Core.Planning;

/// <summary>
/// Decides where each copy goes and what it is called, and never picks a name that is already taken:
/// not by a file on disk, and not by an earlier copy in the same batch.
/// <list type="bullet">
/// <item>Default: a "Resized" or "Optimized" folder inside each original's own folder, so images from
/// unrelated folders are never mixed together: <c>D:\Pictures\Huge.png</c> → <c>D:\Pictures\Resized\Huge.png</c>.</item>
/// <item>Custom folder: copies go there; images found inside a dropped folder's subfolders keep that
/// subfolder structure, so same-named images from different subfolders don't collide.</item>
/// </list>
/// </summary>
public sealed class OutputPlanner
{
    public const string ResizedFolderName = "Resized";
    public const string OptimizedFolderName = "Optimized";

    /// <summary>The folder names Paradis Resize creates. Folder scanning never descends into them.</summary>
    public static readonly string[] OwnFolderNames = [ResizedFolderName, OptimizedFolderName];

    private readonly JobMode _mode;
    private readonly string? _customFolder;
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    public OutputPlanner(JobMode mode, string? customFolder)
    {
        _mode = mode;
        _customFolder = string.IsNullOrWhiteSpace(customFolder) ? null : Path.GetFullPath(customFolder);
    }

    public string DefaultFolderName => _mode == JobMode.Resize ? ResizedFolderName : OptimizedFolderName;

    /// <param name="sourcePath">The original image.</param>
    /// <param name="relativeFolder">The image's subfolder inside a dropped folder ("" for top level or a dropped file).</param>
    public string FolderFor(string sourcePath, string relativeFolder = "")
    {
        if (_customFolder == null)
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, DefaultFolderName);
        return string.IsNullOrEmpty(relativeFolder) ? _customFolder : Path.Combine(_customFolder, relativeFolder);
    }

    /// <summary>
    /// A free path for the copy: <c>folder\stem.ext</c>, or <c>stem (1).ext</c>, <c>stem (2).ext</c>…
    /// The name is held for the rest of the batch.
    /// </summary>
    public string Reserve(string folder, string stem, string extension)
    {
        string path = FileNaming.MakeUnique(Path.Combine(folder, stem + extension),
            p => _reserved.Contains(p) || File.Exists(p) || Directory.Exists(p));
        _reserved.Add(path);
        return path;
    }

    /// <summary>Gives back a name that ended up unused (for example the image failed).</summary>
    public void Release(string path) => _reserved.Remove(path);
}
