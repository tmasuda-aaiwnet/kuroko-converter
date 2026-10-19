using System.Text;

namespace Kuroko.Media.Dvd;

// CLIPINF/<clipId>.clpi の ProgramInfo（クリップに実在するストリームと、その符号化属性）。
//
// なぜ PMT ではなくこちらを使うか：PMT は PID と stream_type しか持たないが、ProgramInfo は
// **映像形式（1080i/1080p 等）・フレームレート・アスペクト比・音声のチャンネル構成・言語**まで
// 宣言している。BD の映像ケイデンスはここで確定するので、変換時にソースをもう一度走査して
// repeat_pict を数える必要が無くなる（1080p23.976 は素の 24p、1080i29.97 は真のインターレース）。
internal sealed record BdClipStream(int Pid, int CodingType, string? Language)
{
    // 映像のみ（非映像は 0）。
    public int VideoFormat { get; init; }        // 1=480i 2=576i 3=480p 4=1080i 5=720p 6=1080p 7=576p
    public int FrameRateCode { get; init; }      // 1=23.976 2=24 3=25 4=29.97 6=50 7=59.94
    public int AspectCode { get; init; }         // 2=4:3 3=16:9
    // 音声のみ（非音声は 0）。
    public int AudioFormatCode { get; init; }    // 1=mono 3=stereo 6=multi(最大8ch) 12=stereo+multi
    public int SampleRateCode { get; init; }     // 1=48k 4=96k 5=192k 12=48/192k 14=48/96k

    public bool IsVideo => CodingType is 0x02 or 0x1B or 0x24 or 0xEA;
    public bool IsAudio => CodingType is 0x03 or 0x04 or 0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xA1 or 0xA2;
    public bool IsPg => CodingType is 0x90 or 0x91;
    public bool IsIg => CodingType == 0x92;

    // インターレース宣言（1080i/480i/576i）。
    public bool Interlaced => VideoFormat is 1 or 2 or 4;

    // "num/den"（Matroska/ffmpeg へ渡す形）。不明は 0 判定できるよう null。
    public string? FrameRate => FrameRateCode switch
    {
        1 => "24000/1001", 2 => "24/1", 3 => "25/1", 4 => "30000/1001", 6 => "50/1", 7 => "60000/1001", _ => null,
    };

    public string Aspect => AspectCode == 2 ? "4:3" : "16:9";

    public (int W, int H) Size => VideoFormat switch
    {
        1 => (720, 480), 2 => (720, 576), 3 => (720, 480), 7 => (720, 576),
        5 => (1280, 720), 4 or 6 => (1920, 1080), _ => (0, 0),
    };

    // 宣言上のチャンネル数（実体は復号しないと確定しないので目安）。
    public int Channels => AudioFormatCode switch { 1 => 1, 3 => 2, 6 => 6, 12 => 6, _ => 2 };
}

internal static class BdmvClipProgram
{
    // クリップの ProgramInfo を読む。読めなければ空（呼び出し側は PMT へ落ちる）。
    public static List<BdClipStream> Read(UdfReader udf, string clipId)
    {
        var result = new List<BdClipStream>();
        try
        {
            var ent = udf.FindEntry($"BDMV/CLIPINF/{clipId}.clpi");
            if (ent is null) return result;
            var b = udf.ReadAllBytes(ent);
            if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "HDMV") return result;

            int po = (int)BdmvPlaylist.Be32(b, 12);            // ProgramInfo 開始（SequenceInfo は 8）
            if (po <= 0 || po + 6 > b.Length) return result;
            // length(32) reserved(8) number_of_program_sequences(8)
            int nseq = b[po + 5];
            int o = po + 6;
            for (int s = 0; s < nseq && o + 8 <= b.Length; s++)
            {
                // SPN_program_sequence(32) program_map_PID(16) number_of_streams_in_ps(8) reserved(8)
                int nst = b[o + 6];
                o += 8;
                for (int i = 0; i < nst && o + 3 <= b.Length; i++)
                {
                    int pid = BdmvPlaylist.Be16(b, o);
                    int len = b[o + 2];                        // StreamCodingInfo length（自身を含まない）
                    int p = o + 3;
                    o = p + len;
                    if (p >= b.Length || len < 1) continue;

                    int type = b[p];
                    var es = new BdClipStream(pid, type, null);
                    if (es.IsVideo && p + 3 <= b.Length)
                        es = es with
                        {
                            VideoFormat = (b[p + 1] >> 4) & 0x0F,
                            FrameRateCode = b[p + 1] & 0x0F,
                            AspectCode = (b[p + 2] >> 4) & 0x0F,
                        };
                    else if (es.IsAudio && p + 5 <= b.Length)
                        es = es with
                        {
                            AudioFormatCode = (b[p + 1] >> 4) & 0x0F,
                            SampleRateCode = b[p + 1] & 0x0F,
                            Language = Lang(b, p + 2),
                        };
                    else if ((es.IsPg || es.IsIg) && p + 4 <= b.Length)
                        es = es with { Language = Lang(b, p + 1) };
                    result.Add(es);
                }
                // 先頭シーケンスだけ見る（BD のクリップは通常 1 本。以降は同じ構成）。
                break;
            }
        }
        catch { result.Clear(); }
        return result;
    }

    private static string? Lang(byte[] b, int o)
    {
        if (o + 3 > b.Length) return null;
        var s = Encoding.ASCII.GetString(b, o, 3).Trim('\0', ' ');
        return s.Length == 3 ? s.ToLowerInvariant() : null;
    }
}
