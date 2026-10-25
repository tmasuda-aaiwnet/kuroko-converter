# 変更履歴 / Changelog

## v0.1.1

- DVD の 3:2（ソフトテレシネ）の中で、印だけインターレースになっている絵をインターレースの区間に入れないようにした（前後 10 枚で判定）。
  映画の始まりの数枚などが、不要にインターレースとして扱われていた。
  / Frames inside DVD 3:2 pulldown (soft telecine) that are only flagged as interlaced are no longer put into the
  interlaced ranges (judged from the 10 frames before and after). A few frames, such as those at the start of a film,
  were needlessly treated as interlaced.
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
