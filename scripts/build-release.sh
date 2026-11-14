#!/usr/bin/env bash
#
# Build the release artifacts for iSCSI-Util.
#
# Usage:
#   scripts/build-release.sh [version]
#
# The version defaults to the <Version> value in ISCSI-Util/ISCSI-Util.csproj.
# Artifacts are written to dist/<version>/:
#   - ISCSI-Util              self-contained single-file executable
#   - ISCSI-Util.pdb          portable debug symbols
#   - ISCSI-Util.tar.gz       folder-based self-contained build + desktop files
#   - iSCSI_Util-x86_64.AppImage   (only when APPIMAGETOOL is set)
#   - SHA256SUMS
#
# To build the AppImage:
#   APPIMAGETOOL=/path/to/appimagetool-x86_64.AppImage \
#   APPIMAGETOOL_FLAGS=--appimage-extract-and-run \
#   scripts/build-release.sh
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/ISCSI-Util/ISCSI-Util.csproj"
RID="${RID:-linux-x64}"

VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)}"
if [[ -z "$VERSION" ]]; then
    echo "error: no version found; pass one as an argument" >&2
    exit 1
fi

DIST="$ROOT/dist/$VERSION"
SINGLE="$DIST/.single"
FOLDER="$DIST/.folder"

echo "==> Building iSCSI-Util $VERSION ($RID)"
rm -rf "$DIST"
mkdir -p "$SINGLE" "$FOLDER"

# Restore once with the target RID so both publishes share the same assets
# file. A plain build in between can otherwise overwrite it without the RID.
dotnet restore "$PROJECT" -r "$RID" --nologo

echo "==> Single-file publish"
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    --no-restore \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=false \
    -o "$SINGLE" \
    --nologo

cp "$SINGLE/ISCSI-Util" "$DIST/ISCSI-Util"
if [[ -f "$SINGLE/ISCSI-Util.pdb" ]]; then
    cp "$SINGLE/ISCSI-Util.pdb" "$DIST/ISCSI-Util.pdb"
fi

echo "==> Folder publish"
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    --no-restore \
    -p:PublishSingleFile=false \
    -o "$FOLDER" \
    --nologo

cp "$ROOT/packaging/iscsi-util.desktop" "$ROOT/packaging/iscsi-util.png" "$FOLDER/"

echo "==> Creating ISCSI-Util.tar.gz"
tar -czf "$DIST/ISCSI-Util.tar.gz" -C "$FOLDER" .

if [[ -n "${APPIMAGETOOL:-}" ]]; then
    echo "==> Creating AppImage"
    APPDIR="$DIST/.AppDir"
    mkdir -p \
        "$APPDIR/usr/bin" \
        "$APPDIR/usr/share/applications" \
        "$APPDIR/usr/share/icons/hicolor/1024x1024/apps"

    cp "$DIST/ISCSI-Util" "$APPDIR/usr/bin/ISCSI-Util"
    cp "$ROOT/packaging/AppRun" "$APPDIR/AppRun"
    cp "$ROOT/packaging/iscsi-util.desktop" "$APPDIR/iscsi-util.desktop"
    cp "$ROOT/packaging/iscsi-util.desktop" "$APPDIR/usr/share/applications/iscsi-util.desktop"
    cp "$ROOT/packaging/iscsi-util.png" "$APPDIR/iscsi-util.png"
    cp "$ROOT/packaging/iscsi-util.png" "$APPDIR/usr/share/icons/hicolor/1024x1024/apps/iscsi-util.png"
    ln -sf iscsi-util.png "$APPDIR/.DirIcon"
    chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/ISCSI-Util"

    read -r -a APPIMAGETOOL_FLAGS <<< "${APPIMAGETOOL_FLAGS:-}"
    ARCH=x86_64 "$APPIMAGETOOL" "${APPIMAGETOOL_FLAGS[@]}" \
        "$APPDIR" "$DIST/iSCSI_Util-x86_64.AppImage"
    rm -rf "$APPDIR"
fi

rm -rf "$SINGLE" "$FOLDER"

echo "==> Checksums"
(
    cd "$DIST"
    shopt -s nullglob
    sha256sum ISCSI-Util ISCSI-Util.pdb ISCSI-Util.tar.gz ./*.AppImage > SHA256SUMS
)

echo "==> Done: $DIST"
ls -lh "$DIST"
