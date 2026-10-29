# Changelog

## 1.0.1 (unreleased)

A compatibility and reliability update from testing on about 54,000 real images. Everything else
works as in 1.0.0.

- **Animated WebP:** no longer refused. A still image is recovered from the first frame (kept as WebP,
  lossless if the frame was; transparency kept; conversion to JPEG under the usual rule). Animation is
  not kept, the original is untouched, and these files are counted separately in the summary and
  left out of the space saved, like recovered GIFs.
- **BMP input:** `.bmp` files are added and processed (1/4/8-bit, RLE, 16/24/32-bit, transparency).
  Copies are PNG ("Done (BMP → PNG)"), or JPEG under the conversion rule. BMP is never written.
- **AVIF input:** `.avif` files are added and processed with the libvips already bundled (no new
  files). AVIF stays AVIF unless another format is chosen; transparency is kept. HEIC is still not
  supported.
- **Wrong extensions:** a file with an unlisted extension whose first bytes show a PNG, JPEG, WebP,
  BMP or AVIF (for example a JPEG named `.tiff`) is added and processed as what it is; the copy gets
  the right extension and the log says the extension didn't match. GIF contents are never added this
  way, so `.gif` files stay out of scans and keep their animation.
- **Log:** every ignored file is now listed with its full path, extension and reason, as well as the
  total.
- A damaged animated WebP or AVIF is reported as "Damaged or incomplete" rather than "Something went
  wrong".

## 1.0.0 (2026-10-28)

First release.

- Drag and drop images, several images or folders; Add Files and Add Folder; optional subfolders
  (off by default) that never descend into the app's own Resized/Optimized folders.
- PNG, JPEG and WebP, recognised by content rather than extension.
- **Resize** by width/height (Auto for either) or percentage, with Keep proportions and Don't
  enlarge smaller images (both on by default).
- **Optimize**: lossless PNG re-compression, high-quality JPEG/WebP re-saves kept only when worthwhile,
  opaque alpha channels dropped; optional conservative PNG → JPEG conversion.
- Save as: same format, PNG, JPEG or WebP; transparency is never silently flattened.
- Copies in a Resized/Optimized folder next to each original or in a chosen folder; never overwrites,
  numbered `(1)`, `(2)` names; originals only ever read.
- Built for 100–300+ MB images: streaming libvips pipeline, one image at a time.
- Progress, safe Cancel, per-image status, completion summary with space saved.
- Daily log `ParadisResize-YYYYMMDD.log` in `%LocalAppData%\ParadisResize\Logs`.
- Damaged files are told apart from unsupported ones: a corrupted PNG/JPEG/WebP shows "Couldn't read
  image" or "Damaged or incomplete"; "Not a supported image" is only for other image types such as
  BMP or TIFF.
- A GIF with a `.jpg`, `.png` or `.webp` name is no longer refused: a still image is recovered from its
  first frame ("Done (GIF → PNG)" and so on). Animation is never kept, and `.gif` files are still not
  picked up.
