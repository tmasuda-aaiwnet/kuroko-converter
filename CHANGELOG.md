# 変更履歴 / Changelog

## v0.1.2

- 放送の TS の変換で、録画の終わりに次の番組の音声（場内音声など）がわずかに入っていると、その音声を 1 本の音声として
  扱おうとして変換が失敗していた（「Stream map '' matches no streams」）。形式（サンプルレート）が分からない音声は使わない
  ようにした（デュアルモノは、音声のデータそのものから見つけたときは使う）。使わなかった音声は変換のログに出す。
  / Converting a broadcast TS failed ("Stream map '' matches no streams") when the end of the recording contained a few frames
  of the next programme's extra audio (such as a stadium-sound track), because it was treated as an audio track. Audio whose
  format (sample rate) cannot be determined is no longer used (dual-mono audio found in the audio data itself is still used).
  Audio that is not used is noted in the conversion log.
- 放送の TS の変換で、番組の途中から始まる 2 本目の音声の形式を、変換と同じ範囲まで読んで調べるようにした。
  / For a broadcast TS, the format of a second audio track that starts partway into the recording is now checked over the
  same range that the conversion reads.
- Kuroko.Media（再生器と共有するライブラリ）と Kuroko SDK：krk の読み出し（シーク）の修正。変換器の動作には関係しない。
  / Kuroko.Media (the library shared with the player) and the Kuroko SDK: fixes to reading (seeking in) krk files. This does
  not affect the converter.

## v0.1.1

- DVD の 3:2（ソフトテレシネ）の中で、印だけインターレースになっている絵をインターレースの区間に入れないようにした（前後 10 枚で判定）。
  3:2 の区間の始まりなどで、実際にはインターレースでない数枚の絵が、印だけを見てインターレースとして扱われ、
  余計なデインターレースがかかっていた。
  / Frames inside DVD 3:2 pulldown (soft telecine) that are only flagged as interlaced are no longer put into the
  interlaced ranges (judged from the 10 frames before and after). A few frames, such as those where a 3:2 section
  begins, were treated as interlaced from their flags alone although they were not actually interlaced, and were
  deinterlaced needlessly.
- Kuroko.Media（再生器と共有するライブラリ）：krk の mkv を、ffmpeg の libavformat ではなく Kuroko SDK の `MatroskaReader` で
  読むようにした。変換器の動作には関係しない。
  / Kuroko.Media (the library shared with the player): Kuroko's mkv files are now read with the Kuroko SDK's
  `MatroskaReader` instead of ffmpeg's libavformat. This does not affect the converter.
- 書き込み中のファイルも開けるようにした（録画中の TS など）。
  / Files that are still being written (such as a TS being recorded) can now be opened.
- Kuroko SDK を v0.1.1 へ。/ Kuroko SDK updated to v0.1.1.

## v0.1.0 — プレビュー / Preview

最初の公開。/ First public release.

- DVD-Video の ISO → `dvd-video-vm`（メニュー・VM の命令・アングル・音声・SPU 字幕・章）。
  / DVD-Video ISO → `dvd-video-vm` (menus, VM commands, angles, audio, SPU subtitles, chapters).
- Blu-ray の ISO（BDMV）→ `none`（プレイリスト・アングル・音声・PGS 字幕・章）。
  / Blu-ray ISO (BDMV) → `none` (playlists, angles, audio, PGS subtitles, chapters).
- 放送の MPEG-2 TS → `none`（番組・音声・ARIB 字幕・番組情報）。
  / Broadcast MPEG-2 TS → `none` (programs, audio, ARIB captions, program information).
- 映像は AV1（既定）・H.265・H.264、音声は Opus。インターレース・ソフトテレシネを元の表示のまま格納。
  / Video as AV1 (default), H.265 or H.264, audio as Opus; interlaced video and soft telecine stored as originally displayed.
- 暗号化された入力（CSS・AACS・放送のスクランブル）を見分けて断る。
  / Encrypted input (CSS, AACS, broadcast scrambling) is detected and rejected.
