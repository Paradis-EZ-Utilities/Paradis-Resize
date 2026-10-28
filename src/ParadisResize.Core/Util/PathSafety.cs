using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ParadisResize.Core.Util;

/// <summary>
/// Guards the one rule that matters most: the original is never written to. Two paths are treated as
/// the same file if they normalise to the same full path, or — when both exist — if the file system
/// says they are the same file. On Windows that catches different letter case, <c>.\</c> and <c>..\</c>
/// segments, forward slashes, 8.3 short names, hard links, junctions, <c>subst</c> drives and
/// <c>\\?\</c> prefixes; on Linux/macOS (used only for testing) symlinks are resolved.
/// </summary>
public static class PathSafety
{
    public static bool IsSameFile(string a, string b)
    {
        string fa, fb;
        try
        {
            fa = Path.GetFullPath(a);
            fb = Path.GetFullPath(b);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(TrimSeparators(fa), TrimSeparators(fb), cmp)) return true;

        // If either doesn't exist, they can't currently be the same file.
        if (!File.Exists(fa) || !File.Exists(fb)) return false;

        if (OperatingSystem.IsWindows())
        {
            var ia = WindowsFileId(fa);
            var ib = WindowsFileId(fb);
            if (ia != null && ib != null) return ia.Value == ib.Value;
            return false;
        }

        string? ra = UnixRealPath(fa), rb = UnixRealPath(fb);
        return ra != null && rb != null && string.Equals(ra, rb, cmp);
    }

    /// <summary>Throws if writing <paramref name="output"/> could touch <paramref name="input"/>.</summary>
    public static void EnsureNotInput(string input, string output)
    {
        if (IsSameFile(input, output))
            throw new ImageJobException(FailureKind.OutputIsInput, "That would replace your original",
                "The new file would have replaced the original image. Paradis Resize never overwrites originals, so this image was skipped.");
    }

    private static string TrimSeparators(string p) =>
        p.Length > 3 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;

    // ---- Windows: volume serial number + file index identifies a file regardless of the path used ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    private static (uint vol, ulong index)? WindowsFileId(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(h, out var info)) return null;
            return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Unix (tests only) ----

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPathNative(byte[] path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr p);

    private static string? UnixRealPath(string path)
    {
        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(path + "\0");
            IntPtr r = RealPathNative(bytes, IntPtr.Zero);
            if (r == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(r); }
            finally { Free(r); }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
