# Third-party software in Paradis Resize

Paradis Resize itself is MIT-licensed (see `LICENSE`). The Windows release bundles the following.
Full licence texts are in the `licenses` folder of the release.

## libvips (libvips-42.dll), LGPL version 2.1 or later

`libvips-42.dll` next to `ParadisResize.exe` is libvips 8.18.7, exactly as published in the
[`NetVips.Native.win-x64` 8.18.7](https://www.nuget.org/packages/NetVips.Native.win-x64/8.18.7)
NuGet package (built by the [libvips-packaging](https://github.com/kleisauke/libvips-packaging)
project from [sharp-libvips](https://github.com/lovell/sharp-libvips) build scripts). Paradis
Resize calls it as a separate dynamic library through NetVips and does not modify it. You may
replace it with your own build of libvips 8.18 (it must be named `libvips-42.dll`).

libvips is licensed under the **GNU Lesser General Public License, version 2.1 or later**. Source:
https://github.com/libvips/libvips (tag `v8.18.7`). The libraries statically linked into this DLL,
and the terms they are used under, as listed by the package:

| Library | Version | Licence |
|---|---|---|
| libvips | 8.18.7 | LGPL 2.1+ |
| glib | 2.90.0 | LGPL 2.1+ |
| libexif | 0.6.26 | LGPL 2.1+ |
| libheif | 1.23.5 | LGPL 3 (used via the "any later version" clause) |
| librsvg | 2.63.2 | LGPL 2.1+ |
| pango | 1.58.2 | LGPL 2.1+ |
| fribidi | 1.0.17 | LGPL 2.1+ |
| proxy-libintl | 0.5 | LGPL 2.1+ |
| aom | 3.15.1 | BSD 2-clause + AOM Patent License 1.0 |
| cairo | 1.18.6 | MPL 1.1 / LGPL 2.1 |
| cgif | 0.5.4 | MIT |
| expat | 2.8.5 | MIT |
| fontconfig | 2.18.3 | fontconfig licence (BSD-like) |
| freetype | 2.14.3 | FreeType licence (BSD-like) |
| harfbuzz | 14.5.0 | MIT |
| highway | 1.4.0 | BSD 3-clause |
| lcms | 2.19.1 | MIT |
| libarchive | 3.8.9 | BSD 2-clause |
| libffi | 3.8.0 | MIT |
| libimagequant | 2.4.1 | BSD 2-clause |
| libnsgif | — | MIT |
| libpng | 1.6.58 | libpng licence |
| libtiff | 4.7.2 | libtiff licence (BSD-like) |
| libultrahdr | 2.0.2 | MIT |
| libwebp | 1.6.0 | BSD 3-clause |
| libxml2 | 2.15.4 | MIT |
| mozjpeg | 0826579 | zlib, IJG and BSD 3-clause |
| pixman | 0.46.4 | MIT |
| zlib-ng | 2.3.3 | zlib licence |

Paradis Resize only uses libvips' PNG, JPEG and WebP support; the other format libraries are part
of the prebuilt DLL but are never handed a file (see DECISIONS.md, section 2).

## NetVips, MIT

The .NET binding for libvips, compiled into `ParadisResize.exe`.
https://github.com/kleisauke/net-vips — Copyright © 2018 Kleis Auke Wolthuizen. MIT licence.

## .NET runtime and WPF, MIT

`ParadisResize.exe` is a self-contained .NET 10 application and includes the .NET runtime and
Windows Desktop runtime (WPF). Copyright © .NET Foundation and Contributors. MIT licence; their
third-party notices are in `licenses/`.
