namespace ParadisResize.Core.Imaging;

/// <summary>
/// Reads just enough of a WebP file's RIFF structure to tell lossless (VP8L) from lossy (VP8) storage
/// and to spot animation. libvips decodes both kinds the same way and doesn't report which one it was,
/// but the choice of how to save the copy depends on it: a lossless original stays lossless.
/// </summary>
public static class WebPInspector
{
    public readonly record struct Result(bool IsWebP, bool IsLossless, bool IsAnimated);

    public static Result Inspect(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            Span<byte> header = stackalloc byte[12];
            if (fs.Read(header) < 12) return default;
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WEBP"u8)) return default;

            bool lossless = false, animated = false, sawImage = false;
            Span<byte> chunk = stackalloc byte[8];
            // Walk the chunks until the first image chunk. Bounded so a damaged file can't loop for long.
            for (int i = 0; i < 64 && fs.Read(chunk) == 8; i++)
            {
                var fourCc = chunk[..4];
                uint size = BitConverter.ToUInt32(chunk[4..8]);
                if (fourCc.SequenceEqual("VP8L"u8)) { lossless = true; sawImage = true; break; }
                if (fourCc.SequenceEqual("VP8 "u8)) { sawImage = true; break; }
                if (fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8)) { animated = true; break; }
                long next = fs.Position + size + (size & 1);
                if (next > fs.Length) break;
                fs.Position = next;
            }
            return new Result(true, sawImage && lossless, animated);
        }
        catch (Exception)
        {
            return default;
        }
    }
}
