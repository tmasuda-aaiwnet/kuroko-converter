using System;
using System.Runtime.InteropServices;
using Kuroko.Matroska;

namespace Kuroko.Media.Krk;

// 自前の demux（MatroskaReader）が返すトラックの情報を、ffmpeg（n8）の AVCodecParameters と同じ形の入れ物にする。
// 再生器はデコーダを作るときにこの入れ物から extradata・標本化周波数・チャンネル数・大きさなどを位置で読む
//（libavformat の codecpar を渡していた所へ、そのまま渡せるようにするため）。中身は自前で確保・解放する。
internal static unsafe class FfCodecPar
{
    private const int Size = 512;
    private const int OffCodecType = 0, OffCodecId = 4, OffCodecTag = 8, OffExtradata = 16, OffExtradataSize = 24, OffFormat = 44,
        OffBitRate = 48, OffBitsPerCoded = 56, OffWidth = 72, OffHeight = 76, OffSarNum = 80, OffSarDen = 84, OffChOrder = 128,
        OffChNb = 132, OffSampleRate = 152, OffBlockAlign = 156;

    public static IntPtr Create(MatroskaTrack t)
    {
        var p = (byte*)NativeMemory.AllocZeroed(Size);
        *(int*)(p + OffCodecType) = t.Type switch { 1 => 0, 2 => 1, 17 => 3, _ => -1 };
        *(int*)(p + OffCodecId) = CodecId(t);
        *(int*)(p + OffFormat) = -1;
        if (t.CodecPrivate is { Length: > 0 } cp)
        {
            var ex = (byte*)NativeMemory.AllocZeroed((nuint)cp.Length + 64);
            Marshal.Copy(cp, 0, (IntPtr)ex, cp.Length);
            *(IntPtr*)(p + OffExtradata) = (IntPtr)ex;
            *(int*)(p + OffExtradataSize) = cp.Length;
        }
        if (t.Type == 1)
        {
            *(int*)(p + OffWidth) = t.PixelWidth;
            *(int*)(p + OffHeight) = t.PixelHeight;
            // 表示の大きさ（DisplayUnit＝0 は画素）から SAR を出す（ffmpeg と同じ）
            if (t.DisplayUnit == 0 && t.DisplayWidth > 0 && t.DisplayHeight > 0 && t.PixelWidth > 0 && t.PixelHeight > 0)
            {
                long num = (long)t.DisplayWidth * t.PixelHeight, den = (long)t.DisplayHeight * t.PixelWidth;
                long g = Gcd(num, den);
                *(int*)(p + OffSarNum) = (int)(num / g);
                *(int*)(p + OffSarDen) = (int)(den / g);
            }
            else *(int*)(p + OffSarDen) = 1;
        }
        else if (t.Type == 2)
        {
            *(int*)(p + OffChOrder) = 0;
            *(int*)(p + OffChNb) = t.Channels;
            // ffmpeg の matroskadec と同じく OutputSamplingFrequency（無ければ SamplingFrequency）
            *(int*)(p + OffSampleRate) = (int)Math.Round(t.OutputSamplingFrequency > 0 ? t.OutputSamplingFrequency : t.SamplingFrequency);
            *(int*)(p + OffBitsPerCoded) = t.BitDepth;
        }
        _ = OffCodecTag; _ = OffBitRate; _ = OffBlockAlign;
        return (IntPtr)p;
    }

    public static void Free(IntPtr par)
    {
        if (par == IntPtr.Zero) return;
        var ex = *(IntPtr*)((byte*)par + OffExtradata);
        if (ex != IntPtr.Zero) NativeMemory.Free((void*)ex);
        NativeMemory.Free((void*)par);
    }

    private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Max(1, a); }

    // Matroska の CodecID → AVCodecID（n8）。分からなければ 0（AV_CODEC_ID_NONE）
    public static int CodecId(MatroskaTrack t) => t.CodecId switch
    {
        "V_MPEG4/ISO/AVC" => 27,
        "V_MPEGH/ISO/HEVC" => 173,
        "V_AV1" => 225,
        "V_VP8" => 139,
        "V_VP9" => 167,
        "V_MPEG2" => 2,
        "V_MPEG1" => 1,
        "V_MPEG4/ISO/ASP" or "V_MPEG4/ISO/SP" or "V_MPEG4/ISO/AP" => 12,
        "V_THEORA" => 30,
        "V_MJPEG" => 7,
        "A_AAC" or "A_AAC/MPEG2/LC" or "A_AAC/MPEG4/LC" or "A_AAC/MPEG2/LC/SBR" or "A_AAC/MPEG4/LC/SBR" or "A_AAC/MPEG4/MAIN" => 86018,
        "A_AC3" => 86019,
        "A_EAC3" => 86056,
        "A_DTS" => 86020,
        "A_TRUEHD" => 86060,
        "A_MLP" => 0x1501D,
        "A_OPUS" => 86076,
        "A_VORBIS" => 86021,
        "A_FLAC" => 86028,
        "A_MPEG/L3" => 0x15001,
        "A_MPEG/L2" or "A_MPEG/L1" => 0x15000,
        "S_HDMV/PGS" => 94214,
        _ => 0,
    };
}
