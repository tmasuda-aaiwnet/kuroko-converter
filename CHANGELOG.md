# 変更履歴 / Changelog

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
