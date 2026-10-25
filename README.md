# Paradis Resize

**Make Big Images Smaller**, part of **Paradis EZ Utilities**.

*Small. Simple. Free. EZ.*

A small Windows app for the moment you think: "This image is stupidly huge. Just make it smaller
without ruining it." Drop in images or whole folders, choose **Resize** or **Optimize**, press the
big button. Everything happens on your computer, and your original images are never changed:
every result is a new copy.

- Drag and drop images, several images, or folders, or use **Add Files** / **Add Folder**.
  **Include subfolders** is off by default.
- PNG, JPEG and WebP. Built for very large files: a 350 MB, 12000 × 10000 PNG is resized in about
  10 seconds using under 300 MB of memory.
- **Resize images:** a width, a height, or both (in pixels, an empty box means Auto), or a
  percentage. **Keep proportions** and **Don't enlarge smaller images** are on by default.
- **Optimize images:** same width and height, smaller file, same look. PNGs are re-compressed
  without changing a single pixel; JPEG and WebP are re-saved at high quality only when that saves
  a worthwhile amount. Optional **Allow format conversion for smaller files** lets a PNG photo with
  no transparency become a much smaller JPEG.
- **Save as:** same format as the original (default), PNG, JPEG or WebP.
- Copies go into a **Resized** or **Optimized** folder next to each original, or a folder you choose.
  Existing files are never overwritten: a clash becomes `Image (1).png`, `Image (2).png`, ….
- A list with each image's format, dimensions, size and status; afterwards the new dimensions, new
  size and how much was saved. Progress, a safe **Cancel**, and a summary at the end.
- No account, no subscription, no ads, no telemetry, no watermark, no internet connection, no file
  count or size limits.

## Running it

1. Unzip `ParadisResize-1.0.0-win-x64.zip` anywhere and keep `libvips-42.dll` next to
   `ParadisResize.exe`.
2. Double-click `ParadisResize.exe`. Nothing is installed; there is no separate .NET to install.
3. The build is not code-signed, so Windows SmartScreen may say "Windows protected your PC".
   Click **More info**, then **Run anyway**.

Requires 64-bit Windows 10 or 11.

## How it decides

| You choose | What happens |
|---|---|
| **Resize**, width 4096, height Auto | Each image becomes 4096 pixels wide; its height follows its own shape. Images already narrower are copied unchanged (with **Don't enlarge** on). |
| **Resize**, width *and* height, Keep proportions on | Each image is made to fit inside that box, keeping its shape. |
| **Resize**, Keep proportions off | Each side is set separately (pictures can be stretched); an Auto side stays as it is. |
| **Resize**, percentage | Width and height are multiplied by that percentage. |
| **Optimize**, PNG | Re-compressed losslessly (every pixel identical). An alpha channel that is fully opaque is dropped. Kept only if at least 2% smaller; otherwise the original is copied unchanged and marked *Already efficient*. |
| **Optimize**, JPEG | Re-saved at quality 88. Kept only if at least 10% smaller, so an already-compressed JPEG isn't degraded for nothing. |
| **Optimize**, WebP | Lossless WebP stays lossless; lossy WebP is re-saved at quality 85. Transparency is kept. Same 2% / 10% rules. |
| **Optimize** + **Allow format conversion** | A PNG with no transparency becomes a JPEG only if the JPEG is at most half the size of the optimized PNG. Photos qualify; screenshots, drawings and text stay PNG. |
| **Save as JPEG** on an image with transparency | Skipped with a clear reason, never silently flattened onto a background. |

Pictures are always turned upright according to their EXIF orientation, and the copy is marked
upright so viewers don't turn it again. Colour profiles, EXIF and XMP metadata are kept where the
format allows. Copies are 8 bits per channel (16-bit PNGs become 8-bit); see
[DECISIONS.md](DECISIONS.md) for why, and for every other choice that affects quality, memory,
safety or compatibility.

## If something goes wrong

A damaged, locked, unsupported or too-large image is marked **Failed** in the list with a short
reason (hover over it for more), and the batch carries on with the next image. Click **Log folder**
at the bottom of the window for the details. Logs are in `%LocalAppData%\ParadisResize\Logs`,
named `ParadisResize-YYYYMMDD.log` (then `-1`, `-2`, … if a day's log reaches 5 MB; the 10 newest
are kept). They list settings, file paths, image sizes, timings and error messages, never any
picture data.

## Building

See [BUILD.md](BUILD.md). In short: .NET 10 SDK, `dotnet build`, `dotnet test`,
`dotnet publish src/ParadisResize -p:PublishProfile=win-x64-self-contained`.

## Project layout

```
src/ParadisResize.Core/    the engine: no WPF, tested on any OS against the real libvips
  Imaging/                 header reading (format from content, not extension), WebP lossless detection
  Planning/                settings, new-size maths, output folders and never-overwrite naming
  Scanning/                files/folders → image list, skipping the app's own output folders
  Processing/              the resize/optimize pipeline, encoder choices, the batch runner
  Util/                    friendly errors, path safety, units
src/ParadisResize/         the WPF app: window, list, summary, About, settings, log
tests/ParadisResize.Core.Tests/
packaging/                 builds the release ZIP
```

## Licence

MIT (see [LICENSE](LICENSE)). The release bundles libvips under the LGPL; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
