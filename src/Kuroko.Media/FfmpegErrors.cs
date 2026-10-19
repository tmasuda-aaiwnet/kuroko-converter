namespace Kuroko.Media.Ffmpeg;

// FFmpeg の戻り値（AVERROR）のうち、結合層が判定に使うもの。
//
// **errno 由来の値は OS で違う。** AVERROR(e) は -errno で、EAGAIN は Windows/Linux/Android が 11、
// macOS/iOS/tvOS（BSD 系）が 35。以前は -11 を決め打ちしていたため、macOS では「出力待ち（正常）」が
// 致命エラー扱いになり、同じ呼び出しで復号済みのフレームまで捨てて**映像が 1 枚も出なかった**
//（macOS 側で実際に踏んだ：復号=300枚・出力=0、HResult=0xFFFFFFDD=-35）。
//
// AVERROR_EOF は MKTAG('E','O','F',' ') の符号反転＝タグ値なので OS 非依存。
public static class FfmpegErrors
{
    public static readonly int Eagain =
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()
        || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsFreeBSD()
            ? -35
            : -11;

    public const int Eof = -541478725;   // AVERROR_EOF = -MKTAG('E','O','F',' ')
}
