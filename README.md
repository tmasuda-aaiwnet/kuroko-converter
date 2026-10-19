# Kuroko Converter

[English](#english)

手持ちの **DVD-Video・Blu-ray の ISO** と **放送の MPEG-2 TS** を、Kuroko 形式（`.krk`）へ変換するツールです
（Windows・macOS・Linux。画面は Avalonia）。
メニュー・複数のタイトル・音声・字幕・章をそのまま運び、映像は H.264・H.265・AV1 で小さく格納します。
インターレースやソフトテレシネ（3:2 プルダウン）は、元の表示のケイデンスを保ったまま格納します。

- 状態：**開発中**
- ライセンス：[GPL-3.0-or-later](LICENSE)（第三者のソフトウェアは [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)）
- 形式の仕様：[kuroko-spec](https://github.com/tmasuda-aaiwnet/kuroko-spec)
- © 2026 Takao Masuda（増田システムデザイニング）

## 暗号化されたものは変換できません

この変換器は、暗号やスクランブルの解除をしません。**暗号化されたままの入力は、変換の前に見分けて断ります**
（DVD の CSS・Blu-ray の AACS・放送のスクランブル）。自分で作ったディスクや、暗号化されていない録画など、
変換してよいものだけを入れてください。

## 変換できるもの

| 入力 | 出力（コントローラ） | 内容 |
|---|---|---|
| DVD-Video の ISO | `dvd-video-vm` | メニュー（ボタン・ハイライト・モーションメニュー）、DVD の仮想マシンの命令、複数のタイトル・アングル、音声（AC-3・DTS・LPCM → Opus など）、字幕（SPU のまま）、章 |
| Blu-ray の ISO（BDMV） | `none` | タイトル（プレイリスト）、アングル、音声、字幕（PGS のまま）、章 |
| 放送の MPEG-2 TS | `none` | 番組、音声（デュアルモノを含む）、ARIB 字幕、番組情報 |

## ダウンロード

ビルド済みのもの（.NET を同梱）は [Releases](https://github.com/tmasuda-aaiwnet/kuroko-converter/releases) にあります。
展開して `Kuroko.Converter`（Windows は `Kuroko.Converter.exe`）を起動します。

- 署名はしていません。Windows のスマートアプリコントロールが有効だと起動できず、SmartScreen の警告が出ることがあります。
  macOS では、初回は Finder で右クリック→「開く」で起動してください。
- ffmpeg・mkvmerge は同梱していません（下の「必要なもの」）。

## 必要なもの

- Windows 10 以降・macOS・Linux（ソースからビルドするときは [.NET 10 SDK](https://dotnet.microsoft.com/)）
- **ffmpeg と ffprobe**（同梱しない）：libx264・libx265・libsvtav1・libopus と matroska を含むビルド。
  - Windows：例えば https://github.com/BtbN/FFmpeg-Builds/releases の `ffmpeg-master-latest-win64-gpl-shared`
  - macOS：`brew install ffmpeg`
  - Linux：配布元の ffmpeg が libsvtav1 を含まない場合は、含むビルドを入れる
- **mkvmerge**（MKVToolNix。同梱しない）：https://mkvtoolnix.download/（macOS は `brew install mkvtoolnix` でもよい）

ffmpeg・mkvmerge の場所は画面で指定します（一度使った場所を覚えます。PATH・Homebrew の場所・MKVToolNix の
標準のインストール先も探します）。

## ビルドと起動

```sh
git clone --recurse-submodules https://github.com/tmasuda-aaiwnet/kuroko-converter.git
cd kuroko-converter
dotnet build KurokoConverter.slnx
dotnet run --project src/Kuroko.Converter
```

引数なしで起動すると画面が開きます。引数を付けると CLI として動きます（例：
`--dvd-convert-kuroko <in.iso> <out.krk> <report.txt> vcodec=av1`、
`--check-encryption <in.iso|in.ts> <out.txt>`）。

## 構成

| ディレクトリ | 中身 |
|---|---|
| `src/Kuroko.Converter` | 変換器の画面（Avalonia。Windows・macOS・Linux）。引数を付けて起動すると CLI として動く |
| `src/Kuroko.Converter.Core` | 変換器の中身（DVD／BD／TS から krk への変換・暗号化の確認・外部ツールの場所・CLI）。画面を持たない |
| `src/Kuroko.Media` | DVD-Video（IFO・NAV・SPU・UDF／ISO 9660）・Blu-ray（MPLS・CLPI）の解析と、krk の映像の読み出し |
| `external/kuroko-authoring` | Kuroko SDK（形式の読み書き・DVD 互換の仮想マシン。MIT。サブモジュール） |

## 変更の受け入れについて

このリポジトリのコードは、著作権者が別の条件（再生器 Vuecove）でも使っています。そのため、**今のところ外部からの
変更（プルリクエスト）は受け付けていません**。不具合の報告や提案は Issue で歓迎します（[CONTRIBUTING.md](CONTRIBUTING.md)）。

---

## English

A tool that converts your own **DVD-Video and Blu-ray ISO images** and **broadcast MPEG-2 TS recordings** to the Kuroko
format (`.krk`) (Windows, macOS and Linux; the user interface is built with Avalonia and is currently in Japanese only).
Menus, multiple titles, audio, subtitles and chapters are carried over, and video is stored compactly as H.264, H.265
or AV1. Interlaced video and soft telecine (3:2 pulldown) are stored preserving the original display cadence.

- Status: **in development**
- License: [GPL-3.0-or-later](LICENSE) (third-party software: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md))
- Format specification: [kuroko-spec](https://github.com/tmasuda-aaiwnet/kuroko-spec)

### Encrypted input is not supported

This converter does not decrypt or descramble anything. **Encrypted input is detected and rejected before conversion**
(CSS on DVD, AACS on Blu-ray, broadcast scrambling). Please convert only material you are entitled to convert, such as
discs you made yourself or unencrypted recordings.

### What it converts

| Input | Output (controller) | Contents |
|---|---|---|
| DVD-Video ISO | `dvd-video-vm` | menus (buttons, highlights, motion menus), DVD virtual machine commands, multiple titles and angles, audio (AC-3, DTS, LPCM → Opus etc.), subtitles (kept as SPU), chapters |
| Blu-ray ISO (BDMV) | `none` | titles (playlists), angles, audio, subtitles (kept as PGS), chapters |
| Broadcast MPEG-2 TS | `none` | programs, audio (including dual mono), ARIB captions, program information |

### Download

Pre-built packages (self-contained, including .NET) are available on
[Releases](https://github.com/tmasuda-aaiwnet/kuroko-converter/releases). They are not code-signed: with Windows Smart
App Control enabled they cannot be started, and SmartScreen may warn; on macOS, open the app the first time with
right-click → Open.

### Requirements

- **ffmpeg and ffprobe** (not included) with libx264, libx265, libsvtav1, libopus and the matroska muxer — for example
  `ffmpeg-master-latest-win64-gpl-shared` from https://github.com/BtbN/FFmpeg-Builds/releases on Windows, or
  `brew install ffmpeg` on macOS.
- **mkvmerge** from MKVToolNix (not included): https://mkvtoolnix.download/ (or `brew install mkvtoolnix` on macOS).
- To build from source, the [.NET 10 SDK](https://dotnet.microsoft.com/):

```sh
git clone --recurse-submodules https://github.com/tmasuda-aaiwnet/kuroko-converter.git
cd kuroko-converter
dotnet build KurokoConverter.slnx
dotnet run --project src/Kuroko.Converter
```

Started without arguments it opens the window; with arguments it runs as a command-line tool (for example
`--dvd-convert-kuroko <in.iso> <out.krk> <report.txt> vcodec=av1`, `--check-encryption <in.iso|in.ts> <out.txt>`).

### Contributing

The code in this repository is also used by the copyright holder under different terms (the Vuecove player), so
**pull requests are not accepted at this time**. Bug reports and suggestions are welcome as Issues
(see [CONTRIBUTING.md](CONTRIBUTING.md)).

© 2026 Takao Masuda (Masuda System Designing)
