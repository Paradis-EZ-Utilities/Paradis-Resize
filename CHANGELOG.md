# Changelog

## 1.0.0 (unreleased), first prototype

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
