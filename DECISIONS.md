# Architecture decisions

Why Paradis Resize is built the way it is, and what the alternatives would have cost. The spec
asked for any choice that materially affects image quality, memory, safety, portability or output
compatibility to be explained; these are those choices.

---

## 1. libvips (through NetVips) for all image work

**Decision.** Every decode, resize and encode goes through [libvips](https://www.libvips.org)
8.18, called from C# with [NetVips](https://github.com/kleisauke/net-vips). The Windows build
ships libvips as one self-contained DLL (`libvips-42.dll`, 19 MB) from the official
`NetVips.Native.win-x64` package. No image encoder or decoder is written here.

**Why.** The defining requirement is 100–300+ MB PNGs with huge pixel dimensions. A 12000 × 10000
RGB image is 360 MB once decoded (480 MB with alpha, double that at 16 bits), and most libraries
decode the whole thing before doing anything, then allocate a second full-size buffer for the
result. libvips is built for exactly this: it is demand-driven and streams the image through the
pipeline in small strips, so decoding, shrinking and re-encoding happen together and only a few
strips are ever in memory. Measured on the test machine (Linux, 4 cores) with a 348 MB,
12000 × 10000 PNG:

| Job | Time | Peak memory of the whole process |
|---|---|---|
| Read header (for the list) | 1 ms | — |
| Resize to 4096 wide, save PNG | 10 s | 275 MB |
| Optimize (lossless PNG re-save) | 32 s | 135 MB |
| Optimize + conversion (PNG and JPEG candidates, JPEG kept) | 35 s | 175–304 MB (varies between runs) |
| Batch: two of these plus a truncated copy, at 25% | 12 s | 336 MB |

It also does everything else the spec asks for: EXIF auto-rotation, ICC/EXIF/XMP preservation,
correct resampling of images with transparency, CMYK JPEGs, and shrink-on-load for JPEG and WebP
(a huge JPEG is decoded at a fraction of its size when it's being made much smaller).

**Alternatives considered.**

- *WPF / Windows Imaging Component* (built in, no extra DLL). The managed WPF imaging API decodes
  whole frames into memory, has no WebP encoder, and WebP decoding depends on an optional Store
  codec being installed. Memory for the 348 MB PNG would be several hundred MB to over 1 GB per
  image, with no way to stream.
- *ImageSharp* (pure .NET, no native code). Excellent API and metadata support, but it decodes the
  full image into memory (480 MB+ for the test image, then more for the resize), is several times
  slower than libvips on huge images, and its Six Labors Split Licence adds terms for some
  commercial users.
- *SkiaSharp.* Fast, but full decodes only, and it drops EXIF/XMP on encode.
- *Magick.NET.* Very capable, but its pixel cache is full-size (it spills to disk instead of
  streaming), and it is much larger.

**Cost.**

- About 19 MB more in the download: the release ZIP is 64 MB, of which libvips is about a third (the self-contained .NET runtime and WPF are most of the rest).
- libvips is LGPL-2.1-or-later. That is compatible with an MIT app as long as libvips stays a
  separately replaceable library and its licence and notices are shipped. For that reason
  `libvips-42.dll` sits next to `ParadisResize.exe` instead of being packed into it (which would
  also mean extracting it to `%TEMP%` on every start). See THIRD_PARTY_NOTICES.md.
- A native dependency means a native crash is possible in principle. libvips is mature and is
  used at very large scale (it is the engine behind `sharp` in Node.js), and every image is
  processed inside a `try` so libvips *errors* fail one image, not the batch.

## 2. Header first, format from content

**Decision.** Files are recognised by their first bytes (PNG, JPEG, WebP, AVIF, BMP signatures),
not their extension. Only PNG, JPEG, WebP and AVIF are ever passed to libvips, plus GIFs that carry
a supported image name (see 12); BMPs are read by Paradis Resize's own decoder (see 14). Headers
are read for the list without decoding pixels.

**Why.** A JPEG renamed `.png` is common and must be treated as a JPEG. The bundled libvips can
also open TIFF, HEIC, SVG, PDF and more; refusing everything else before libvips sees it keeps the
attack surface and the behaviour exactly as specified (no scope creep).
Files actually named `.gif` are still not picked up (v1.0.1 keeps this on purpose: see 16).
Animated WebPs give a still image of their first frame since v1.0.1 (see 13).

## 3. Originals are only ever read; copies appear only when finished

**Decision.**

1. Originals are opened read-only, by libvips or by `File.Copy`. Nothing in the code writes, moves,
   renames or deletes anything outside the output folder.
2. Each copy is written to `<name>.<id>-<n>.<ext>.partial` in the output folder, then opened again
   and checked (right format, right width and height), and only then renamed to its final name.
3. The final name is chosen so it never exists: not on disk, not reserved by an earlier image in
   the same batch. The rename itself is the no-overwrite kind (`File.Move(..., overwrite: false)`),
   so even a file that appears in between is never replaced; a new name is picked.
4. A failure or a cancel deletes the `.partial` file. The output folder only ever contains
   finished images.
5. `PathSafety.EnsureNotInput` compares the final path with the original by volume serial and file
   index on Windows (catching case differences, 8.3 names, `subst` drives, junctions and hard
   links), as in Paradis Compress.

**Why.** Priority 1 of the spec. The checks are layered so that no single bug can overwrite an
original or leave a half-written file that looks like a finished image.

## 4. Output folders and never scanning them

**Decision.** By default each copy goes into `Resized` or `Optimized` inside its original's own
folder, so images from unrelated folders are never mixed. With a custom folder, images found in a
dropped folder's subfolders keep that subfolder structure under it, and same-named images from
different places get `(1)`, `(2)` suffixes.

When scanning subfolders, the scanner never enters a folder named `Resized` or `Optimized` (either
mode's, since a resize after an optimize must not pick up the optimized copies), the chosen custom
folder, hidden or system folders, or junctions/symbolic links (which could loop). Partial files are
ignored. A folder the user drops *directly* is always scanned, even if it is called `Resized`: that
was a deliberate choice by the user.

**Cost.** A user's own folder that happens to be called "Resized" is skipped during a recursive
scan of its parent. The log names every folder skipped this way.

## 5. One image at a time

**Decision.** The batch runs on one background thread, one image at a time. libvips uses all CPU
cores *within* each image.

**Why.** Running several huge images side by side multiplies peak memory for little gain, since
each one already uses every core. One at a time keeps memory predictable (the spec puts stability
above throughput) and makes progress and cancel simple to reason about.

## 6. Optimize: worthwhile savings only

**Decision.**

| Original | Re-saved as | Kept only if at least |
|---|---|---|
| PNG | PNG, zlib level 9, adaptive row filters, lossless | 2% smaller |
| JPEG | JPEG quality 88 | 10% smaller |
| Lossy WebP | WebP quality 85, smart subsampling, lossless alpha | 10% smaller |
| Lossless WebP | Lossless WebP | 2% smaller |

Otherwise the original is **copied byte for byte** into the output folder and marked
*Already efficient*.

**Why.** Lossless re-saves cost nothing in quality, so any real saving is worth keeping. Lossy
re-saves lose a little quality every time; a JPEG that's already compressed would get slightly
worse for a few percent, which is exactly what "avoid repeatedly destroying already efficiently
compressed images" rules out. Copying instead of skipping means the Optimized folder is a complete
set that can be compared with (and later used in place of) the original folder.

Quality 88 for JPEG and 85 for WebP are visually transparent for photos at normal viewing while
still giving large savings over camera JPEGs (which are typically quality 92–98). Below quality 90,
libvips uses 4:2:0 chroma subsampling, as cameras and phones do.

PNG level 9 with adaptive filtering gave 1.5–7% smaller files than level 6 in tests, at about three
times the encode time (3.6 s vs 1.1 s for a 9-megapixel photo-like PNG). For a utility whose
point is saving space, the smaller file wins.

**Opaque alpha.** Many PNGs have an alpha channel that is 255 everywhere. Optimize checks this
(one extra streaming pass over the file) and drops the channel, which typically saves another
quarter, with no visible change. The same check lets such a PNG become a JPEG.

**Optimized Huffman tables for JPEG** save a few percent, but make the JPEG encoder hold the whole
image's coefficients in memory: the 12000 × 10000 conversion peaked at 993 MB with them and 175 MB
without. They are used up to 25 megapixels and skipped above.

## 7. Format conversion is conservative

**Decision.** With "Allow format conversion" on, a PNG with no transparency (or only fully opaque
alpha) is also saved as a JPEG candidate. The JPEG is used only if it is at most **half** the size
of the optimized PNG. Transparency is never dropped: a PNG with any transparent pixel stays PNG.
Conversion never goes to WebP automatically (see 9).

**Why.** Photos saved as PNG shrink 5–10× as JPEG with no visible change; that's the big win the
spec describes (the test 348 MB PNG became 18 MB). Screenshots, diagrams and text look worse as
JPEG (ringing around edges) and compress well as PNG anyway, so JPEG rarely beats half the PNG
size for them; the 50% rule tells the two apart without guessing from content.

**Cost.** Two encodes per PNG when conversion is on (about twice the time for that image).

## 8. Copies are 8 bits per channel

**Decision.** 16-bit PNGs become 8-bit copies (libvips' thumbnail pipeline does this).

**Why.** Every screen shows 8 bits per channel, so the difference is invisible, and it halves the
size of the pixel data. 16-bit masters are for editing, which is outside Paradis Resize's job.

**Cost.** Someone who keeps 16-bit PNGs for further editing would lose that headroom in the copy
(the original is untouched, as always). If that turns out to matter, a future version could keep
16-bit for PNG → PNG.

## 9. Manual "Save as"

- **JPEG** on an image with transparency: skipped with the reason, never flattened onto an invented
  background colour.
- **WebP** is high-quality lossy (quality 85) except when the original is a lossless WebP. WebP
  can't exceed 16,383 pixels per side, so larger images fail with that reason instead of a cryptic
  error. Some older programs can't open WebP, which is why it's never chosen automatically.
- **PNG** chosen for a PNG means the same as "Same format as original".

## 10. Damaged images fail instead of turning grey

**Decision.** Decoding uses libvips' `fail-on=error`.

**Why.** By default libvips tolerates truncated files and fills the missing part with grey, which
would produce a "successful" copy with half the picture missing. With `fail-on=error` the image
fails with "Damaged or incomplete" and nothing is saved.

"Not a supported image" is kept for files that are recognisably another kind of image (BMP,
TIFF, HEIC/AVIF, JPEG XL, ICO, PSD). A file named like a PNG, JPEG or WebP whose contents can't be
recognised at all is far more likely to be damaged than to be the wrong type, so it is shown as
"Couldn't read image".

## 11. What isn't in v1, on purpose

No crop, filters, metadata editing, watermarks, upscaling, cloud, accounts or settings panel. No
quality slider: the encoder choices above are made for the user. No "replace original" option.
The only remembered settings are the visible ones (mode, sizes, checkboxes, Save as); a custom
output folder is not remembered, so every session starts with the predictable default.

## 12. GIFs with the wrong name: a still image is recovered

**Decision.** A file named `.jpg`, `.png` or `.webp` (and, since v1.0.1, `.bmp` or `.avif`) that is really a GIF (common with downloaded
pictures) is not refused. Its first frame is decoded, as libvips does by default, and treated as a
PNG from then on: the same resize, the same transparency checks and the same conversion rules.

- Same format as original saves a **PNG**: a GIF frame has at most 256 colours, so PNG keeps it
  exactly, transparency included. With "Allow format conversion" an opaque frame becomes a JPEG under
  the usual rule (JPEG at most half the PNG's size). Save as JPEG on a frame with transparency is
  skipped as for any PNG. Save as WebP is lossless, so the frame is kept exactly.
- The copy is always made, never "copied unchanged": copying would just give another GIF, possibly
  still animated, with the wrong name.
- Status is "Done (GIF → PNG)", "Done (GIF → JPEG)" or "Done (GIF → WebP)", the list's format
  column says GIF, the tooltip says how many frames there were and that the animation was not kept,
  and the summary counts these files separately.
- They aren't counted in the space saved, and their row shows no "% saved": leaving out an
  animation's other frames isn't a saving.
- Damaged GIFs fail with "Damaged or incomplete" or "Couldn't read image" like any other image.

**Why.** These files are intact pictures with the wrong extension, so refusing them isn't helpful.
Recovering a still keeps Paradis Resize a still-image tool: there is no animation support of any
kind, and files named `.gif` are still left alone.

## 13. Animated WebP: a still image is recovered from the first frame (v1.0.1)

**Decision.** An animated WebP is no longer refused with "Animated image". Its first frame is decoded
(libvips' default, page 0, composited onto the full canvas) and processed as a still image. This
follows the GIF recovery in 12, with one difference: the copy stays a **WebP** by default.

- Same format as original saves a still WebP: lossless if the first frame is stored losslessly
  (VP8L), otherwise high-quality lossy (quality 85), exactly like a still WebP. Transparency is kept.
- Optimize with "Allow format conversion": an opaque frame becomes a JPEG under the usual rule (JPEG
  at most half the size of the WebP). With transparency it stays WebP.
- Save as PNG/JPEG/WebP is respected; Save as JPEG on a frame with transparency is skipped.
- Never "copied unchanged" or "Already efficient": that would copy the animation. Every copy is
  checked to be a single still image before it is kept.
- Status "Done (animated → still WebP)" (or JPEG/PNG), list format "WebP (animated)", a tooltip and
  log line saying how many frames there were and that the animation was not kept, and its own line
  in the summary.
- Left out of the space saved, with no "% saved" on the row, like recovered GIFs: dropping 49 of 50
  frames isn't compression.

**Why WebP and not PNG (as for GIFs).** WebP is one of the formats Paradis Resize writes, so keeping
it is the least surprising "same format", it keeps transparency, and the frame keeps its own
storage (a lossy photo frame as PNG could be several times larger than the whole animation). GIF
isn't a format Paradis Resize writes, so recovered GIFs need another home; PNG is exact for them.

## 14. BMP input, PNG output (v1.0.1)

**Decision.** `.bmp` files are added and processed. BMP is read, never written: "Same format as
original" saves a **PNG** (lossless, so nothing is lost), or a JPEG under the usual conversion rule
when "Allow format conversion" is on and the image is an opaque photo. Status "Done (BMP → PNG)".
A BMP is never "copied unchanged", since that would just give another BMP.

The bundled libvips has no BMP reader (its BMP support goes through ImageMagick, which isn't in the
NetVips build), so `BmpDecoder` reads BMPs itself: 1, 4 and 8-bit palettes (plain or RLE), 16-bit
(5-5-5 or bit masks), 24-bit and 32-bit, bottom-up or top-down, with the v3, v4 and v5 headers that
Windows, Paint, Photoshop, GIMP and scanners write. Checked against ImageMagick's and Pillow's BMPs.
- Transparency: 32-bit BMPs with an alpha mask, or whose 4th byte isn't zero everywhere, keep their
  transparency. Plain 32-bit BMPs whose 4th byte is all zero are opaque (it's padding, as Windows and
  browsers treat it).
- Safety: every size in the header is checked against the real file size before any memory is set
  aside, so a damaged header fails at once with "Damaged or incomplete" instead of trying to allocate
  gigabytes. BMPs that embed a JPEG or PNG, and OS/2-only variants, are "Not a supported image".
- Memory: a BMP is uncompressed, so it is decoded whole (width × height × 3 or 4 bytes; 360 MB for a
  12000 × 10000 BMP, which is itself a 360 MB file). Large PNGs and JPEGs still stream.

## 15. AVIF input, kept as AVIF (v1.0.1)

**Decision.** `.avif` files are added and processed. The bundled `libvips-42.dll` already contains
libheif 1.23.5 with the aom 3.15.1 AV1 codec, which both decodes and encodes AVIF, so this needs no
new dependency, no new file in the ZIP and no packaging change. AVIF stays AVIF for "Same format as
original" (quality 60, effort 4, 8-bit; about 5 s for a 12-megapixel photo on 4 cores), with the
usual lossy rule in Optimize (kept only if at least 10% smaller, otherwise copied unchanged). Save as
PNG, JPEG or WebP works as for any image; AVIF is not added to the Save as menu, and nothing else is
ever converted to AVIF. Transparency is kept.

- HEIC files share AVIF's container but need a codec that isn't bundled; they are recognised by
  their brand and stay "Not a supported image". An AVIF holding several images is refused as
  "Animated image".
- AVIF can be lossless, but a lossless AVIF can't be told apart cheaply; in Optimize it would be
  re-saved lossy if that saved 10%. Lossless AVIFs are rare (they are usually larger than PNG).

## 16. Files whose extension doesn't match their contents (v1.0.1)

**Decision.** A file whose extension isn't one Paradis Resize lists gets one small look at its first
64 bytes. If they clearly show a PNG, JPEG, WebP, BMP or AVIF, the file is added and processed as
what it really is (eight JPEGs named `.tiff` in testing). The original keeps its name; the copy gets
the extension that matches its contents (`Holiday.tiff` → `Resized\Holiday.jpg`, also when copied
unchanged). The log and the row's tooltip say that the extension didn't match.

- Never added this way: GIF contents (so `.gif` files keep their animation and stay out of scans;
  GIFs are only recovered when their name says PNG/JPEG/WebP/BMP/AVIF, see 12), TIFF, HEIC, JPEG XR
  (`.wdp`), and anything unrecognised, such as an app database (`.realm`) or an empty file.
- Cheap and safe: one 64-byte read, only for files with other extensions. Files that are stored only
  online (OneDrive "files on demand") aren't opened at all, as reading them would download them.

## 17. Every ignored file is logged (v1.0.1)

**Decision.** Adding files still logs "Added N image(s); M other file(s) ignored", followed by one
line per ignored file with its full path, extension and reason ("not a supported image type",
"GIF files aren't processed", "TIFF image, which isn't a supported format", "empty file (0 bytes)"),
one line per file added by its contents, and one per leftover `.partial` file. The window itself
doesn't list them, to keep it uncluttered; the "No images found" message points to the log.
