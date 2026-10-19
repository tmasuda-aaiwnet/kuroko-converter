# 第三者のソフトウェア

## このリポジトリに含むもの（移植したもの）

| もの | ライセンス | 使っている所 |
|---|---|---|
| libaribcaption（https://github.com/xqq/libaribcaption） | MIT（Copyright (c) 2022 magicxqq） | ARIB 字幕の文字・色・外字の表（Kuroko SDK の `src/Kuroko.Sdk/Arib/AribCaptionTables.cs`）と、字幕データの解釈（`AribCaptionDecoder.cs`） |
| ariblib（https://github.com/youzaka/ariblib） | MIT（Copyright (c) 2013 youzaka） | ARIB の 8 単位符号の文字列の解釈（`AribB24Decoder.cs`）と、追加記号の対応表（`AribGaiji.cs`） |
| epgdump_py（Yasumasa Murakami） | 自由な使用・改変・再配布を認める許諾（Copyright (C) 2011 Yasumasa Murakami） | ariblib を通じて、追加記号の対応表と文字列の解釈の考え方（`AribGaiji.cs`・`AribB24Decoder.cs`）。元の配布先は現在公開されていない |
| Kuroko SDK（https://github.com/tmasuda-aaiwnet/kuroko-authoring） | MIT（同じ著作権者） | krk の形式の読み書き・DVD 互換の仮想マシン・ARIB 字幕の解析（サブモジュール。上の 3 つは SDK に入っている） |

追加記号の対応表は、ARIB STD-B24 の表（追加記号集合の符号値と Unicode の対応）と照合し、足りない分を補っています。

## 画面が使うもの（NuGet パッケージ）

画面（`src/Kuroko.Converter`）は Avalonia を使います。Avalonia（MIT）・Avalonia.Fonts.Inter（MIT。中の Inter フォントは
SIL Open Font License 1.1）・SkiaSharp（MIT。中の Skia は BSD 3 条項）・HarfBuzzSharp（MIT。中の HarfBuzz は Old MIT）・
ANGLE（Windows。BSD 3 条項）・MicroCom.Runtime（MIT）・Tmds.DBus.Protocol（Linux。MIT）。各ライセンスの全文と
入手元は、オーサリングツールの [THIRD-PARTY-NOTICES.md](https://github.com/tmasuda-aaiwnet/kuroko-authoring/blob/main/THIRD-PARTY-NOTICES.md)
と同じです（同じ版の Avalonia を使う）。配布用のパッケージを作るときは、各ライセンスの全文を一緒に入れること。

## このリポジトリに含まないもの（使う人が入れる）

| もの | ライセンス | 使い方 |
|---|---|---|
| ffmpeg・ffprobe | 配布元のビルドによる（libx264・libx265 を含むビルドは GPL） | 別の実行ファイルとして呼ぶ（映像・音声の符号化・解析） |
| mkvmerge（MKVToolNix） | GPL-2.0 | 別の実行ファイルとして呼ぶ（時刻の焼き込み・結合） |

## MIT License（libaribcaption）

```
MIT License

Copyright (c) 2022 magicxqq

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## The MIT License (MIT)（ariblib）

```
The MIT License (MIT)

Copyright (c) 2013 youzaka

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
