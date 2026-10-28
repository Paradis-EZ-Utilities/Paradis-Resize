namespace ParadisResize.Core.Processing;

/// <summary>
/// The encoder settings Paradis Resize chooses so the user never has to. All in one place, with the
/// reasons; DECISIONS.md explains them in more detail.
/// </summary>
public static class EncoderChoices
{
    /// <summary>
    /// JPEG quality 88 (the usual 1–100 libjpeg scale). Visually indistinguishable from the original for photos at
    /// normal viewing, while still far smaller than a PNG of the same photo. Below 90, libvips uses 4:2:0
    /// colour subsampling, which is what cameras and phones do too.
    /// </summary>
    public const int JpegQuality = 88;

    /// <summary>
    /// Up to this many pixels (about 6000 × 4000), JPEGs get optimized Huffman tables: a few percent smaller
    /// for free. Above it they don't, because libjpeg then buffers the whole image (see ImageProcessor).
    /// </summary>
    public const long JpegOptimizeCodingMaxPixels = 25_000_000;

    /// <summary>
    /// Lossy WebP quality 85 with "smart" colour subsampling: roughly the look of JPEG 90 at a smaller size.
    /// Transparent areas are stored losslessly (alpha quality 100).
    /// </summary>
    public const int WebPQuality = 85;

    /// <summary>Effort 4 of 0–6 for WebP: the libwebp default balance; 6 is much slower for a few percent.</summary>
    public const int WebPEffort = 4;

    /// <summary>
    /// zlib level for PNG (0–9). PNG is always lossless, so this only trades time for size.
    /// See <see cref="PngFilterAdaptive"/>.
    /// </summary>
    public const int PngCompression = 9;

    /// <summary>Let the encoder choose the best PNG row filter per row ("adaptive"), which usually saves the most.</summary>
    public const bool PngFilterAdaptive = true;

    /// <summary>
    /// Optimize, keeping the format: a lossless re-save (PNG, lossless WebP) must save at least 2% to be kept;
    /// below that the original is copied unchanged instead.
    /// </summary>
    public const double MinSavingLossless = 0.02;

    /// <summary>
    /// Optimize, keeping the format: a lossy re-save (JPEG, lossy WebP) must save at least 10%. Every lossy
    /// re-save costs a little quality, so a small saving isn't worth it; the original is copied unchanged instead.
    /// </summary>
    public const double MinSavingLossy = 0.10;

    /// <summary>
    /// Optimize with "Allow format conversion": a PNG becomes a JPEG only if the JPEG is at most half the size
    /// of the optimized PNG. Photos easily pass this (JPEG is often 5–10× smaller); screenshots, diagrams and
    /// text, where JPEG would blur edges and PNG compresses well anyway, don't.
    /// </summary>
    public const double ConversionMaxRatio = 0.5;
}
