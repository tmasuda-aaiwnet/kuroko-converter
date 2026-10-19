namespace Kuroko.Media.Dvd;

// 「ISO/krk情報（一覧）」用の1ファイル分サマリー。DvdIsoAnalyzer.AnalyzeSummary（.iso）と
// KurokoInfoAnalyzer.AnalyzeSummary（.krk）が共通で返す。サイズはバイト。
internal sealed record MediaInfoSummary(
    string Format,      // "DVD-Video" / "拡張DVD-Video" / "krk"
    string VideoCodec,  // 最初のタイトルの映像形式（例: MPEG-2 / H.264 / H.265）
    string AudioCodec,  // 最初のタイトルの先頭音声形式（例: AC-3 / DTS / Opus）
    long FileSize,
    int TitleCount,
    long TitleVideo,
    long TitleAudio,
    long TitleSub,
    int MenuCount,
    long MenuVideo,
    long MenuAudio,
    long MenuSub);
