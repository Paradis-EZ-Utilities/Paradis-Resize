#!/usr/bin/env bash
# Assembles the Windows release from the published app:
#
#   publish/win-x64/ParadisResize.exe, libvips-42.dll   dotnet publish src/ParadisResize -p:PublishProfile=win-x64-self-contained
#
# Output in out/:
#   ParadisResize-<ver>-win-x64.zip     the app (unzip anywhere, run ParadisResize.exe)
#   ParadisResize-<ver>-source.zip      the app's own source (git HEAD)
#   SHA256SUMS.txt
#
# Usage: packaging/package-windows.sh   (on Linux/WSL with zip; licence texts come from Ubuntu's common-licenses)
set -euo pipefail
cd "$(dirname "$0")/.."
# The version comes from Directory.Build.props (the only place it is defined), evaluated by MSBuild.
VER=$(dotnet msbuild src/ParadisResize -getProperty:Version)
[[ $VER =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]] || { echo "unexpected version '$VER'" >&2; exit 1; }
NAME=ParadisResize-$VER-win-x64
OUT=out
STAGE=$OUT/$NAME
NUGET=${NUGET_PACKAGES:-$HOME/.nuget/packages}
RT=$(ls -d "$NUGET"/microsoft.netcore.app.runtime.win-x64/10.* | sort -V | tail -1)
WD=$(ls -d "$NUGET"/microsoft.windowsdesktop.app.runtime.win-x64/10.* | sort -V | tail -1)
VIPS=$(ls -d "$NUGET"/netvips.native.win-x64/* | sort -V | tail -1)

test -f publish/win-x64/ParadisResize.exe || { echo "publish the app first" >&2; exit 1; }
test -f publish/win-x64/libvips-42.dll || { echo "publish/win-x64/libvips-42.dll is missing; publish again" >&2; exit 1; }
# The published exe must be a build of this version: its Product version resource is exactly $VER.
python3 -c 'import sys; d=open(sys.argv[1],"rb").read(); sys.exit(("ProductVersion\0".encode("utf-16-le") not in d) or ((sys.argv[2]+"\0").encode("utf-16-le") not in d))' \
  publish/win-x64/ParadisResize.exe "$VER" \
  || { echo "publish/win-x64/ParadisResize.exe is not a $VER build; publish again" >&2; exit 1; }
# The app manifest carries its own copy of the version (Windows reads it as VERSION.0).
grep -q "assemblyIdentity version=\"${VER%%-*}.0\"" src/ParadisResize/app.manifest \
  || { echo "src/ParadisResize/app.manifest assemblyIdentity version is not ${VER%%-*}.0" >&2; exit 1; }
# The DLL must be the one from the NuGet package, unmodified.
cmp -s publish/win-x64/libvips-42.dll "$VIPS/runtimes/win-x64/native/libvips-42.dll" \
  || { echo "publish/win-x64/libvips-42.dll differs from the NuGet package's" >&2; exit 1; }

rm -rf "$STAGE" && mkdir -p "$STAGE/licenses"
cp publish/win-x64/ParadisResize.exe publish/win-x64/libvips-42.dll "$STAGE/"

L=$STAGE/licenses
cp LICENSE "$L/ParadisResize-LICENSE.txt"
cp THIRD_PARTY_NOTICES.md "$L/THIRD_PARTY_NOTICES.md"
cp "$VIPS/THIRD-PARTY-NOTICES.md" "$L/libvips-THIRD-PARTY-NOTICES.md"
cp "$VIPS/versions.json" "$L/libvips-versions.json"
cp /usr/share/common-licenses/LGPL-2.1 "$L/LGPL-2.1.txt"
cp /usr/share/common-licenses/LGPL-3 "$L/LGPL-3.txt"
cp /usr/share/common-licenses/GPL-3 "$L/GPL-3.txt"
cp packaging/licenses/NetVips-LICENSE.txt "$L/"
cp "$RT/LICENSE.TXT" "$L/dotnet-LICENSE.txt"
cp "$RT/THIRD-PARTY-NOTICES.TXT" "$L/dotnet-THIRD-PARTY-NOTICES.txt"
cp "$WD/LICENSE" "$L/dotnet-windowsdesktop-LICENSE.txt"
sed "s/@VERSION@/$VER/g" packaging/README.txt > "$STAGE/README.txt"
# Windows users open these in Notepad.
for f in "$STAGE/README.txt" "$L"/*.txt "$L"/*.md "$L"/*.json; do sed -i 's/\r\?$/\r/' "$f"; done

rm -f "$OUT/$NAME.zip" "$OUT/ParadisResize-$VER-source.zip"
(cd "$OUT" && zip -qrX9 "$NAME.zip" "$NAME")
git archive --format=zip --prefix="ParadisResize-$VER-source/" -o "$OUT/ParadisResize-$VER-source.zip" HEAD
(cd "$OUT" && sha256sum ./*.zip | sed 's: \./: :' > SHA256SUMS.txt && cat SHA256SUMS.txt)
ls -l "$OUT"
