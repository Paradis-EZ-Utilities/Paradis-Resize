Paradis Resize @VERSION@ - Make Big Images Smaller
Part of Paradis EZ Utilities

STARTING IT
  1. Unzip this whole folder anywhere (for example Documents or the Desktop).
     Keep libvips-42.dll next to ParadisResize.exe.
  2. Double-click ParadisResize.exe. Nothing is installed; delete the folder to remove it.
  3. The first time, Windows may show "Windows protected your PC" because this build is not
     code-signed. Click "More info", then "Run anyway".

USING IT
  1. Drop images or folders onto the window (or click Add Files / Add Folder).
     PNG, JPEG, WebP, BMP and AVIF. Tick "Include subfolders" to also add images inside
     sub-folders. BMP copies are saved as PNG (or JPEG). An animated WebP, or a GIF saved with
     an image name like .jpg, gets a still image of its first frame; animation is not kept.
     Files named .gif are left alone. A picture with the wrong extension (a JPEG named .tiff)
     is recognised by its contents. The log lists every file that was left out, and why.
  2. Choose "Resize images" or "Optimize images".
       Resize:   type a width or a height in pixels (leave the other on Auto), or a percentage.
       Optimize: same width and height, smaller file, same look. Optionally allow a PNG photo
                 without transparency to become a much smaller JPEG.
  3. Optional: "Save as" another format, or "Choose Folder..." for the copies.
  4. Click the big button.

  Your original images are never changed. New copies go into a "Resized" or "Optimized" folder
  next to each original (or the folder you chose). Existing files are never overwritten: a new
  name like "Image (1).png" is used instead. Compare the copies before deleting anything.

IF SOMETHING GOES WRONG
  An image that can't be processed is marked Failed in the list (hover over it to see why) and the
  rest carry on. Click "Log folder" at the bottom of the window for details. The log lists
  settings, file names, sizes and error messages. It never contains the pictures themselves.
  Logs: %LocalAppData%\ParadisResize\Logs

WHAT'S IN THIS FOLDER
  ParadisResize.exe   the app (includes the .NET runtime; no separate install needed)
  libvips-42.dll      libvips 8.18.7, which reads and writes the images (LGPL 2.1 or later)
  licenses\           licence texts for the app and everything bundled with it

No account, no internet connection, no ads, no tracking.

Requires 64-bit Windows 10 or 11.
