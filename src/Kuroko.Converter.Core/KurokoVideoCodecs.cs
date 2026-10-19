using System.Text;

namespace Kuroko.Converter;

// krk の映像コーデック（"h264"／"h265"／"av1"）の正規化と、AV1（SVT-AV1）の符号化指定。DVD・BD・TS の変換器で共通。
// AV1 の決めごと（docs/kuroko-format-spec.md・2026-10-06 の調査）：
//   ・インターレースは H.265 と同じく weave フレームをプログレッシブとして符号化する（AV1 にフィールド符号化の道具は無い）。
//   ・GOP は常にクローズド。SVT-AV1 のオープン GOP（前方キーフレーム）は容器上でキーフレームの印が付かず、シークできない。
//   ・プリセットは x264/x265 の名前を SVT-AV1 の段階（0〜13。小さいほど遅く高圧縮）へ読み替える。
//   ・CRF の目盛りは x265 と違う（0〜63）。測定では x265 より 8〜15 大きい値でほぼ同じ画質。既定 32。
internal static class KurokoVideoCodecs
{
    public const int DefaultAv1Crf = 32;

    public static string Normalize(string? videoCodec) => videoCodec?.Trim().ToLowerInvariant() switch
    {
        "h265" or "hevc" => "h265",
        "av1" => "av1",
        _ => "h264",
    };

    public static bool IsAv1(string? videoCodec) => Normalize(videoCodec) == "av1";

    // H.265・AV1 は weave をプログレッシブとして符号化する（H.264 だけがインターレース符号化を持つ）。
    public static bool EncodesWeaveAsProgressive(string? videoCodec) => Normalize(videoCodec) != "h264";

    // x264/x265 のプリセット名 → SVT-AV1 の段階。数値ならそのまま（0〜13）。
    public static int SvtPreset(string? preset) => preset?.Trim().ToLowerInvariant() switch
    {
        "ultrafast" => 12,
        "superfast" => 11,
        "veryfast" => 10,
        "faster" => 9,
        "fast" => 8,
        "medium" => 6,
        "slow" => 4,
        "slower" => 3,
        "veryslow" => 2,
        "placebo" => 1,
        var s when int.TryParse(s, out int n) && n is >= 0 and <= 13 => n,
        _ => 6,
    };

    // AV1 の符号化指定（-c:v 以降）。keyint>0 で間隔固定＋場面転換の自動キーフレームを止める（x26x の scenecut=0 相当）。
    // allIntra（状態別の静止メニュー）は全フレームをキーフレームにする（SVT-AV1 は -g 1 が効かないので毎フレーム強制）。
    // 呼び出し側は allIntra のとき別の -force_key_frames を付けないこと（後のものが勝つ）。
    public static void AppendAv1Args(StringBuilder a, int preset, int crf, string aspect, int keyint, bool allIntra)
    {
        a.Append($"-c:v libsvtav1 -preset {preset} -crf {crf} -pix_fmt yuv420p -aspect {aspect} ");
        if (allIntra) a.Append("-force_key_frames expr:1 ");
        else if (keyint > 0) a.Append($"-g {keyint} -svtav1-params scd=0 ");
    }
}
