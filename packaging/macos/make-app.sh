#!/bin/bash
# Kuroko Converter の macOS 版（Apple Silicon・.NET 同梱）を .app にまとめ、配布用の zip を作る。
# Builds the macOS (Apple Silicon, self-contained) Kuroko Converter.app and packs it into a zip for release.
#
#   使い方 / usage:  packaging/macos/make-app.sh [-v 版] [-o 出力先]
#     -v  版（既定：src/Kuroko.Converter/Kuroko.Converter.csproj の <Version>）
#     -o  出力先のフォルダ（既定：今いるフォルダ）
#   出力 / output:   <出力先>/KurokoConverter-<版>-osx-arm64.zip
#                    （中身：KurokoConverter-<版>-osx-arm64/ に Kuroko Converter.app と LICENSE・THIRD-PARTY-NOTICES.md・
#                    README.md・CHANGELOG.md）
#   必要なもの / requires: macOS・.NET 10 SDK（dotnet）・iconutil・codesign・ditto（iconutil 以下は macOS に入っている）
#
# 署名はアドホック（codesign -s -）だけ。Apple Silicon は無署名だと起動しないため付けている。Developer ID の署名と
# 公証はしていないので、ダウンロードした .app を初めて開くと Gatekeeper の警告が出る（右クリック→「開く」）。
# Ad-hoc signed only (required to launch on Apple Silicon). Not signed with a Developer ID and not notarized.
set -euo pipefail

APP_NAME="Kuroko Converter"
BUNDLE_ID="net.aaiw.kuroko.converter"
EXECUTABLE="Kuroko.Converter"
PROJECT="src/Kuroko.Converter/Kuroko.Converter.csproj"
ICON_DIR="external/kuroko-authoring/art/icons/png"   # converter_<大きさ>.png（サブモジュールの絵）
ICON_KEY="converter"
ZIP_BASE="KurokoConverter"
RID="osx-arm64"

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION=""
OUT_DIR="$PWD"
while getopts "v:o:" opt; do
    case "$opt" in
        v) VERSION="$OPTARG" ;;
        o) OUT_DIR="$OPTARG" ;;
        *) echo "usage: $0 [-v version] [-o out-dir]" >&2; exit 2 ;;
    esac
done
if [ -z "$VERSION" ]; then
    VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/$PROJECT" | head -1)"
fi
[ -n "$VERSION" ] || { echo "error: 版が分からない（-v で指定する）" >&2; exit 1; }
command -v dotnet >/dev/null || { echo "error: dotnet が見つからない（.NET 10 SDK を入れる）" >&2; exit 1; }
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"

NAME="$ZIP_BASE-$VERSION-$RID"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PUB="$WORK/publish"
STAGE="$WORK/$NAME"
APP="$STAGE/$APP_NAME.app"

# 1. .NET 同梱で発行する
dotnet publish "$ROOT/$PROJECT" -c Release -r "$RID" --self-contained true -p:Version="$VERSION" -o "$PUB"

# 2. .app の形にする（デバッグ用の *.pdb は配布物に入れない）
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
rsync -a --exclude '*.pdb' "$PUB/" "$APP/Contents/MacOS/"

# 3. アイコン：16・32・128・256・512 と各 @2x（32〜1024）の PNG から icns を作る
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
    cp "$ROOT/$ICON_DIR/${ICON_KEY}_$s.png" "$ICONSET/icon_${s}x${s}.png"
    cp "$ROOT/$ICON_DIR/${ICON_KEY}_$((s * 2)).png" "$ICONSET/icon_${s}x${s}@2x.png"
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# 4. Info.plist
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>$EXECUTABLE</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSArchitecturePriority</key><array><string>arm64</string></array>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>© 2026 Takao Masuda</string>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist" >/dev/null

# 5. アドホック署名（Developer ID の署名・公証はしない）
codesign --force --deep -s - "$APP"
codesign -v --deep "$APP"

# 6. 文書を添えて zip にする（署名の一部が拡張属性にあるので残す。--sequesterRsrc で __MACOSX/ にまとめる）
for f in LICENSE THIRD-PARTY-NOTICES.md README.md CHANGELOG.md; do cp "$ROOT/$f" "$STAGE/"; done
rm -f "$OUT_DIR/$NAME.zip"
(cd "$WORK" && ditto -c -k --sequesterRsrc --keepParent "$NAME" "$OUT_DIR/$NAME.zip")
echo "done: $OUT_DIR/$NAME.zip"
