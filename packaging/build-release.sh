#!/usr/bin/env bash
# Windows（x64）・Linux（x64）向けのビルド済みのもの（.NET 同梱）を作る。macOS は packaging/macos/ を使う。
# Builds the self-contained Windows (x64) and Linux (x64) release packages. For macOS, see packaging/macos/.
#
# 使い方 / Usage:  packaging/build-release.sh [出力フォルダ / output dir]   （既定 / default: ./dist）
# 必要なもの / Requires: .NET 10 SDK, python3
# 署名はしない / The packages are not code-signed.
set -euo pipefail

NAME=KurokoConverter
PROJECT=src/Kuroko.Converter/Kuroko.Converter.csproj
EXE=Kuroko.Converter          # Linux の実行ファイル / Linux executable

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(mkdir -p "${1:-$ROOT/dist}" && cd "${1:-$ROOT/dist}" && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/$PROJECT" | head -1)"
[ -n "$VERSION" ] || { echo "no <Version> in $PROJECT" >&2; exit 1; }
PY="$(command -v python3 || command -v python)"

for RID in win-x64 linux-x64; do
  PKG="$NAME-$VERSION-$RID"
  STAGE="$OUT/$PKG"
  if [ -e "$STAGE" ]; then echo "already exists: $STAGE" >&2; exit 1; fi
  dotnet publish "$ROOT/$PROJECT" -c Release -r "$RID" --self-contained true -p:DebugType=none -o "$STAGE" -nologo -v q
  find "$STAGE" -maxdepth 1 -name '*.pdb' -delete     # パッケージに入っている native の .pdb も除く
  cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$STAGE/"
  if [ "$RID" = linux-x64 ]; then
    "$PY" "$ROOT/packaging/pack.py" "$STAGE" "$OUT/$PKG.tar.gz" "$EXE" createdump   # 実行の権限を付ける
  else
    "$PY" "$ROOT/packaging/pack.py" "$STAGE" "$OUT/$PKG.zip"
  fi
done
