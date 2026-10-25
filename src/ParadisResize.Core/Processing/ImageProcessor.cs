using System.Diagnostics;
using NetVips;
using ParadisResize.Core.Imaging;
using ParadisResize.Core.Planning;
using ParadisResize.Core.Util;

namespace ParadisResize.Core.Processing;

/// <summary>
/// Processes one image into a new file. The original is only ever opened for reading.
/// <para>
/// Every copy is written under a temporary ".partial" name in the output folder, checked (it opens,
/// and has the expected size and format), and only then renamed to its final name, which is chosen so
/// it never replaces an existing file. A failure or a cancel deletes the temporary file, so the output
/// folder only ever holds finished images.
/// </para>
/// <para>
/// Memory: images are decoded by libvips in strips as the encoder asks for them ("sequential" access),
/// so even a 300 MB, 12000 × 10000 PNG is processed with a few hundred MB rather than the several GB
/// a full decode would need. The one exception is a photo whose EXIF orientation says it is stored on
/// its side or upside down: turning it upright needs the whole (possibly shrunk) picture in memory.
/// </para>
/// </summary>
public sealed class ImageProcessor
{
    private enum Encoding { PngLossless, Jpeg, WebPLossy, WebPLossless }

    private sealed record Candidate(string Path, long Size, Encoding Encoding);

    private readonly JobSettings _settings;
    private readonly OutputPlanner _planner;

    public ImageProcessor(JobSettings settings, OutputPlanner planner)
    {
        _settings = settings;
        _planner = planner;
    }

    /// <param name="progress">0–1 within this image, for big images that take a while.</param>
    public ItemResult Process(string sourcePath, string relativeFolder, IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ImageInfo? info = null;
        var temps = new List<string>();
        string? finalPath = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            info = ImageProbe.Read(sourcePath);
            CoreLog.Info($"  Image: {info.Describe()}");

            var result = ProcessCore(info, relativeFolder, temps, p => finalPath = p, progress, ct);
            return result with { Source = info, Elapsed = sw.Elapsed };
        }
        catch (OperationCanceledException)
        {
            CoreLog.Info($"  Cancelled while processing {sourcePath}");
            return new ItemResult { Outcome = ItemOutcome.Cancelled, Status = "Cancelled", Source = info, Elapsed = sw.Elapsed };
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is VipsException)
        {
            // libvips reports a cancelled save as an ordinary error ("killed").
            CoreLog.Info($"  Cancelled while processing {sourcePath}");
            return new ItemResult { Outcome = ItemOutcome.Cancelled, Status = "Cancelled", Source = info, Elapsed = sw.Elapsed };
        }
        catch (Exception ex)
        {
            var friendly = FriendlyErrors.Classify(ex, finalPath == null ? FriendlyErrors.Stage.Reading : FriendlyErrors.Stage.Saving);
            bool skipped = friendly.Kind == FailureKind.HasTransparency;
            if (skipped) CoreLog.Warn($"  Skipped: {friendly.Title}. {friendly.Message}");
            else CoreLog.Error($"  Failed: {friendly.Title}. {friendly.Message}" +
                               (friendly.Detail != null ? $"{Environment.NewLine}  Detail: {friendly.Detail}" : ""),
                               friendly.InnerException ?? (ex is ImageJobException ? null : ex));
            return new ItemResult
            {
                Outcome = skipped ? ItemOutcome.Skipped : ItemOutcome.Failed,
                Status = friendly.Title,
                Message = friendly.Message,
                Source = info,
                Elapsed = sw.Elapsed,
            };
        }
        finally
        {
            foreach (string t in temps) TryDelete(t);
            if (finalPath != null && !File.Exists(finalPath)) _planner.Release(finalPath);
        }
    }

    private ItemResult ProcessCore(ImageInfo info, string relativeFolder, List<string> temps, Action<string> setFinal,
                                   IProgress<double>? progress, CancellationToken ct)
    {
        bool optimize = _settings.Mode == JobMode.Optimize;

        // ---- New size ----
        var size = optimize
            ? new SizePlanner.Plan(info.Width, info.Height)
            : SizePlanner.For(info.Width, info.Height, _settings.Resize);

        // ---- Output format ----
        ImageFormat? manual = _settings.OutputFormat switch
        {
            OutputFormatChoice.Png => ImageFormat.Png,
            OutputFormatChoice.Jpeg => ImageFormat.Jpeg,
            OutputFormatChoice.WebP => ImageFormat.WebP,
            _ => null,
        };
        if (manual == info.Format) manual = null; // "PNG" chosen for a PNG is the same as Keep Original
        ImageFormat target = manual ?? info.Format;
        bool tryJpeg = optimize && manual == null && _settings.AllowFormatConversion && info.Format == ImageFormat.Png;

        // ---- Transparency ----
        // An alpha channel that is 100% opaque everywhere (common in PNGs from some programs) holds no
        // information, so it can be dropped: a smaller PNG, and a PNG that may become a JPEG.
        bool needAlphaCheck = info.HasAlphaChannel && (target == ImageFormat.Jpeg || tryJpeg || optimize && target == ImageFormat.Png);
        bool opaque = !info.HasAlphaChannel || needAlphaCheck && AlphaIsOpaque(info.Path, ct);
        if (info.HasAlphaChannel && needAlphaCheck)
            CoreLog.Info(opaque ? "  Alpha channel is fully opaque: it will be dropped (no visible change)"
                                : "  Has transparent areas: transparency will be kept");
        if (target == ImageFormat.Jpeg && !opaque) throw FriendlyErrors.TransparencyNotAllowed();
        if (tryJpeg && !opaque)
        {
            tryJpeg = false;
            CoreLog.Info("  Not converting to JPEG: the image has transparency");
        }

        // ---- Format limits ----
        if (target == ImageFormat.WebP && (size.Width > SupportedFormats.WebPMaxDimension || size.Height > SupportedFormats.WebPMaxDimension))
            throw FriendlyErrors.TooLargeForWebP(size.Width, size.Height);
        if (target == ImageFormat.Jpeg && (size.Width > SupportedFormats.JpegMaxDimension || size.Height > SupportedFormats.JpegMaxDimension))
            throw new ImageJobException(FailureKind.TooLargeForFormat, "Too large for JPEG",
                $"JPEG images can be at most {SupportedFormats.JpegMaxDimension:N0} pixels wide and tall. Choose PNG, or resize it smaller.");
        if (tryJpeg && (size.Width > SupportedFormats.JpegMaxDimension || size.Height > SupportedFormats.JpegMaxDimension)) tryJpeg = false;

        // ---- Where ----
        string folder = _planner.FolderFor(info.Path, relativeFolder);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Saving);
        }
        string stem = Path.GetFileNameWithoutExtension(info.Path);
        string jobId = Guid.NewGuid().ToString("N")[..8];

        Encoding targetEncoding = EncodingFor(target, info);
        bool dropAlpha = info.HasAlphaChannel && opaque;
        bool sameSize = size.IsSameAs(info.Width, info.Height);

        // ---- Resize ----
        if (!optimize)
        {
            CoreLog.Info($"  Plan: {info.Width}x{info.Height} -> {size.Width}x{size.Height} as {Describe(targetEncoding)}");
            if (sameSize && manual == null && info.Orientation == 1)
            {
                string why = _settings.Resize.DontEnlarge ? "Already this size or smaller" : "Already this size";
                return CopyOriginal(info, folder, stem, jobId, temps, setFinal, why,
                    $"{why}, so the original was copied unchanged (not enlarged or re-saved).");
            }
            var c = Encode(info, size, targetEncoding, dropAlpha, folder, stem, jobId, temps, progress, 0, 1, ct);
            return Finish(info, c, size, folder, stem, temps, setFinal, converted: manual != null,
                note: c.Size > info.FileSize ? "The new file is larger than the original." : null);
        }

        // ---- Optimize ----
        CoreLog.Info($"  Plan: optimize as {Describe(targetEncoding)}{(tryJpeg ? ", and try JPEG" : "")}");
        double share = tryJpeg ? 0.6 : 1.0;
        var primary = Encode(info, size, targetEncoding, dropAlpha, folder, stem, jobId, temps, progress, 0, share, ct);
        CoreLog.Info($"  {Describe(primary.Encoding)}: {primary.Size:N0} bytes ({Units.PercentSaved(info.FileSize, primary.Size)}% smaller than the original)");
        Candidate chosen = primary;

        if (tryJpeg)
        {
            var jpeg = Encode(info, size, Encoding.Jpeg, dropAlpha: true, folder, stem, jobId, temps, progress, share, 1 - share, ct);
            double ratio = jpeg.Size / (double)Math.Max(1, primary.Size);
            CoreLog.Info($"  JPEG: {jpeg.Size:N0} bytes ({ratio:P0} of the PNG)");
            if (ratio <= EncoderChoices.ConversionMaxRatio)
            {
                chosen = jpeg;
                CoreLog.Info("  Converting to JPEG: it is much smaller and the image has no transparency");
            }
            else
            {
                CoreLog.Info("  Keeping PNG: JPEG wouldn't be much smaller (likely a screenshot, drawing or text)");
            }
        }

        bool converted = manual != null || chosen.Encoding == Encoding.Jpeg && info.Format != ImageFormat.Jpeg;
        if (manual == null)
        {
            // Keep only a worthwhile saving; otherwise the original is the better file.
            double minSaving = chosen.Encoding is Encoding.PngLossless or Encoding.WebPLossless && !converted
                ? EncoderChoices.MinSavingLossless
                : EncoderChoices.MinSavingLossy;
            if (chosen.Size > info.FileSize * (1 - minSaving))
            {
                CoreLog.Info($"  Saving under {minSaving:P0}: copying the original unchanged instead");
                foreach (string t in temps) TryDelete(t);
                temps.Clear();
                return CopyOriginal(info, folder, stem, jobId, temps, setFinal, "Already efficient",
                    "This image is already stored efficiently. Re-saving it wouldn't make it noticeably smaller, so the original was copied unchanged.");
            }
        }

        return Finish(info, chosen, size, folder, stem, temps, setFinal, converted,
            note: chosen.Size > info.FileSize ? "The new file is larger than the original." : null);
    }

    private static Encoding EncodingFor(ImageFormat format, ImageInfo info) => format switch
    {
        ImageFormat.Png => Encoding.PngLossless,
        ImageFormat.Jpeg => Encoding.Jpeg,
        // A lossless WebP (usually a graphic) stays lossless; anything else saved as WebP is a high-quality lossy WebP.
        _ => info.Format == ImageFormat.WebP && info.IsLosslessWebP ? Encoding.WebPLossless : Encoding.WebPLossy,
    };

    private static ImageFormat FormatOf(Encoding e) => e switch
    {
        Encoding.PngLossless => ImageFormat.Png,
        Encoding.Jpeg => ImageFormat.Jpeg,
        _ => ImageFormat.WebP,
    };

    private static string Describe(Encoding e) => e switch
    {
        Encoding.PngLossless => $"PNG (lossless, zlib {EncoderChoices.PngCompression}, adaptive filter)",
        Encoding.Jpeg => $"JPEG (quality {EncoderChoices.JpegQuality})",
        Encoding.WebPLossy => $"WebP (quality {EncoderChoices.WebPQuality})",
        _ => "WebP (lossless)",
    };

    /// <summary>
    /// Decodes the image once, in strips, and checks whether its alpha channel is fully opaque.
    /// Costs one pass over the file but almost no memory.
    /// </summary>
    private static bool AlphaIsOpaque(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var img = Image.NewFromFile(path, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error);
            using var alpha = img.ExtractBand(img.Bands - 1);
            double max = img.Format == Enums.BandFormat.Ushort ? 65535 : 255;
            alpha.SetProgress(new Progress<int>(_ => { }), ct);
            return alpha.Min() >= max;
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Cancelled", ex, ct);
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Reading);
        }
    }

    /// <summary>Resizes (if needed), turns upright, and saves to a temporary file in the output folder.</summary>
    private static Candidate Encode(ImageInfo info, SizePlanner.Plan size, Encoding encoding, bool dropAlpha,
                                    string folder, string stem, string jobId, List<string> temps,
                                    IProgress<double>? progress, double progressStart, double progressShare, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string ext = SupportedFormats.ExtensionFor(info.Path, FormatOf(encoding));
        string temp = Path.Combine(folder, $"{TruncateStem(stem)}.{jobId}-{temps.Count}{ext}{FileNaming.TemporarySuffix}");
        temps.Add(temp);

        if (FreeSpace(folder) is long free && free < 16 * Units.BytesPerMB)
            throw FriendlyErrors.Classify(new IOException("not enough space on the disk"), FriendlyErrors.Stage.Saving);

        var sw = Stopwatch.StartNew();
        Image pipeline;
        try
        {
            // thumbnail is libvips' route for large images: it shrinks while decoding where the format allows
            // (JPEG, WebP), streams PNG in strips, applies EXIF orientation (and resets it to 1), converts CMYK
            // to sRGB, and handles transparency correctly while resampling. With the original size it doesn't
            // resample at all. failOn: Error makes a damaged file fail instead of producing a grey-filled copy.
            pipeline = Image.Thumbnail(info.Path, size.Width, height: size.Height, size: Enums.Size.Force,
                failOn: Enums.FailOn.Error);
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Reading);
        }

        var disposables = new List<Image> { pipeline };
        try
        {
            Image img = pipeline;
            if (dropAlpha && img.HasAlpha())
            {
                img = img.ExtractBand(0, n: img.Bands - 1);
                disposables.Add(img);
            }
            if (encoding == Encoding.Jpeg && img.HasAlpha())
                throw FriendlyErrors.TransparencyNotAllowed(); // guarded earlier; never flatten silently

            if (progress != null || ct.CanBeCanceled)
            {
                var p = new Progress<int>(percent => progress?.Report(progressStart + progressShare * Math.Clamp(percent, 0, 100) / 100.0));
                img.SetProgress(p, ct);
            }

            switch (encoding)
            {
                case Encoding.PngLossless:
                    img.Pngsave(temp, compression: EncoderChoices.PngCompression,
                        filter: EncoderChoices.PngFilterAdaptive ? Enums.ForeignPngFilter.All : Enums.ForeignPngFilter.None,
                        keep: Enums.ForeignKeep.All);
                    break;
                case Encoding.Jpeg:
                    // Optimized Huffman tables save a few percent but make libjpeg hold the whole image's
                    // coefficients in memory (about 1 GB for 12000 × 10000), so huge images skip them.
                    img.Jpegsave(temp, q: EncoderChoices.JpegQuality,
                        optimizeCoding: (long)size.Width * size.Height <= EncoderChoices.JpegOptimizeCodingMaxPixels,
                        keep: Enums.ForeignKeep.All);
                    break;
                case Encoding.WebPLossy:
                    img.Webpsave(temp, q: EncoderChoices.WebPQuality, smartSubsample: true, alphaQ: 100,
                        effort: EncoderChoices.WebPEffort, keep: Enums.ForeignKeep.All);
                    break;
                case Encoding.WebPLossless:
                    img.Webpsave(temp, lossless: true, effort: EncoderChoices.WebPEffort, keep: Enums.ForeignKeep.All);
                    break;
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Cancelled", ex, ct);
        }
        catch (ImageJobException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A save pulls the pixels through, so a damaged original is often only discovered here.
            throw FriendlyErrors.Classify(ex, LooksLikeReadError(ex) ? FriendlyErrors.Stage.Reading : FriendlyErrors.Stage.Saving);
        }
        finally
        {
            for (int i = disposables.Count - 1; i >= 0; i--) disposables[i].Dispose();
        }

        Verify(temp, encoding, size);
        long bytes = new FileInfo(temp).Length;
        CoreLog.Info($"  Encoded {Describe(encoding)} {size.Width}x{size.Height} in {sw.Elapsed.TotalSeconds:0.0} s");
        return new Candidate(temp, bytes, encoding);
    }

    /// <summary>Opens the finished temporary file and checks it has the expected format and size.</summary>
    private static void Verify(string temp, Encoding encoding, SizePlanner.Plan size)
    {
        try
        {
            using var check = Image.NewFromFile(temp, access: Enums.Access.Sequential);
            string loader = check.Get("vips-loader") as string ?? "";
            string expected = encoding switch
            {
                Encoding.PngLossless => "png",
                Encoding.Jpeg => "jpeg",
                _ => "webp",
            };
            if (!loader.StartsWith(expected, StringComparison.Ordinal) || check.Width != size.Width || check.Height != size.Height)
                throw new InvalidDataException($"Saved file check failed: {loader} {check.Width}x{check.Height}, expected {expected} {size.Width}x{size.Height}");
        }
        catch (Exception ex) when (ex is not ImageJobException)
        {
            throw new ImageJobException(FailureKind.Failed, "Couldn't check the new file",
                "The new image was saved but couldn't be read back, so it was discarded. The original is untouched.", ex.ToString(), ex);
        }
    }

    private ItemResult Finish(ImageInfo info, Candidate chosen, SizePlanner.Plan size, string folder, string stem,
                              List<string> temps, Action<string> setFinal, bool converted, string? note)
    {
        var format = FormatOf(chosen.Encoding);
        string final = MoveIntoPlace(info, chosen.Path, folder, stem, SupportedFormats.ExtensionFor(info.Path, format), setFinal);
        temps.Remove(chosen.Path);
        CoreLog.Info($"  Saved {final} ({chosen.Size:N0} bytes, {Units.PercentSaved(info.FileSize, chosen.Size)}% smaller" +
                     $"{(converted ? $", converted {SupportedFormats.DisplayName(info.Format)} -> {SupportedFormats.DisplayName(format)}" : "")})");
        return new ItemResult
        {
            Outcome = ItemOutcome.Done,
            Status = converted ? $"Done (now {SupportedFormats.DisplayName(format)})" : "Done",
            Message = note,
            OutputPath = final,
            OutputSize = chosen.Size,
            OutputWidth = size.Width,
            OutputHeight = size.Height,
            OutputFormat = format,
            Converted = converted,
        };
    }

    private ItemResult CopyOriginal(ImageInfo info, string folder, string stem, string jobId, List<string> temps,
                                    Action<string> setFinal, string status, string message)
    {
        string ext = Path.GetExtension(info.Path);
        string temp = Path.Combine(folder, $"{TruncateStem(stem)}.{jobId}-copy{ext}{FileNaming.TemporarySuffix}");
        temps.Add(temp);
        try
        {
            File.Copy(info.Path, temp, overwrite: false);
        }
        catch (Exception ex)
        {
            throw FriendlyErrors.Classify(ex, ex is FileNotFoundException ? FriendlyErrors.Stage.Reading : FriendlyErrors.Stage.Saving);
        }
        string final = MoveIntoPlace(info, temp, folder, stem, ext, setFinal);
        temps.Remove(temp);
        CoreLog.Info($"  {status}: copied unchanged to {final}");
        return new ItemResult
        {
            Outcome = ItemOutcome.CopiedUnchanged,
            Status = status,
            Message = message,
            OutputPath = final,
            OutputSize = info.FileSize,
            OutputWidth = info.Width,
            OutputHeight = info.Height,
            OutputFormat = info.Format,
        };
    }

    /// <summary>
    /// Gives the temporary file its final name. The rename itself refuses to replace an existing file,
    /// so even a file that appeared since the name was chosen is never overwritten: a new name is picked.
    /// </summary>
    private string MoveIntoPlace(ImageInfo info, string temp, string folder, string stem, string ext, Action<string> setFinal)
    {
        for (int attempt = 0; ; attempt++)
        {
            string final = _planner.Reserve(folder, stem, ext);
            setFinal(final);
            PathSafety.EnsureNotInput(info.Path, final);
            try
            {
                File.Move(temp, final, overwrite: false);
                return final;
            }
            catch (IOException) when (File.Exists(final) && attempt < 5)
            {
                // Someone else created this name in the meantime; keep it reserved and try the next one.
            }
            catch (Exception ex)
            {
                _planner.Release(final);
                throw FriendlyErrors.Classify(ex, FriendlyErrors.Stage.Saving);
            }
        }
    }

    private static bool LooksLikeReadError(Exception ex)
    {
        string m = ex.Message;
        return m.Contains("load", StringComparison.OrdinalIgnoreCase) || m.Contains("read", StringComparison.OrdinalIgnoreCase)
            || m.Contains("png", StringComparison.OrdinalIgnoreCase) && !m.Contains("pngsave", StringComparison.OrdinalIgnoreCase)
            || m.Contains("jpeg", StringComparison.OrdinalIgnoreCase) && !m.Contains("jpegsave", StringComparison.OrdinalIgnoreCase)
            || m.Contains("truncated", StringComparison.OrdinalIgnoreCase) || m.Contains("premature", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateStem(string stem) => stem.Length > 100 ? stem[..100] : stem;

    private static long? FreeSpace(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null; // network shares and the like: let the save itself report a full disk
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CoreLog.Warn($"Could not delete temporary file {path}: {ex.Message}");
        }
    }
}
