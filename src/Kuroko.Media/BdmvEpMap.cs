using System.Text;

namespace Kuroko.Media.Dvd;

// clpi の **EP マップ（CPI）** ＝「映像の I ピクチャの時刻 → ソースパケット番号」の索引。
// 公開仕様（Blu-ray Disc Read-Only Format part 3 / CPI）に基づく自作クリーンルーム実装。
//
// 何に使うか：
//  ・シーク … 時刻からバイト位置を直接引く（PCR 二分探索と GOP 後方走査が要らなくなり、
//              しかも **I フレームの先頭パケット**に着地する）。
//  ・IN/OUT トリム … PlayItem の IN/OUT 時刻をバイト範囲へ直す。
//
// clpi の構造（先頭）：
//   "HDMV" + version(4) + SequenceInfo開始(4) + ProgramInfo開始(4) + CPI開始(4) + ClipMark開始(4) + ...
// CPI()：length(4) ＋ reserved(12bit)+CPI_type(4bit) ＝ 2 バイト ＋ EP_map()
internal sealed class BdEpMap
{
    public BdEpMap(int pid, long[] pts90k, long[] spn)
    {
        Pid = pid; Pts90k = pts90k; Spn = spn;
    }

    public int Pid { get; }
    public long[] Pts90k { get; }   // 昇順（クリップ自身の STC。clpi の presentation_start と同じ軸）
    public long[] Spn { get; }      // 対応するソースパケット番号（×192 がバイト位置）
    public int Count => Pts90k.Length;

    public const int SourcePacketBytes = 192;   // BDAV：TP_extra_header(4)＋TSパケット(188)

    public long ByteAt(int index) => Spn[index] * (long)SourcePacketBytes;

    // 指定時刻**以下**で最大の EP の添字。手前に無ければ 0（先頭）。
    public int IndexAtOrBefore(long pts90k)
    {
        int lo = 0, hi = Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Pts90k[mid] <= pts90k) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    // 指定時刻**以上**で最小の EP の添字。無ければ -1。
    public int IndexAtOrAfter(long pts90k)
    {
        int lo = 0, hi = Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Pts90k[mid] >= pts90k) { found = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return found;
    }
}

internal static class BdmvEpMap
{
    // CLIPINF/<clipId>.clpi の EP マップを読む。読めなければ null（呼び出し側は従来動作へ落ちる）。
    public static BdEpMap? Read(UdfReader udf, string clipId)
    {
        try
        {
            var ent = udf.FindEntry($"BDMV/CLIPINF/{clipId}.clpi");
            if (ent is null) return null;
            var b = udf.ReadAllBytes(ent);
            if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "HDMV") return null;

            int cpi = (int)BdmvPlaylist.Be32(b, 16);
            if (cpi <= 0 || cpi + 6 > b.Length) return null;
            if (BdmvPlaylist.Be32(b, cpi) == 0) return null;      // CPI 長 0 ＝索引なし

            int epMap = cpi + 4 + 2;                              // length(4) ＋ reserved+CPI_type(2)
            if (epMap + 2 > b.Length) return null;
            int entries = b[epMap + 1];                           // reserved(8) ＋ number_of_stream_PID_entries(8)
            int hdr = epMap + 2;

            for (int k = 0; k < entries; k++)
            {
                int o = hdr + k * 12;
                if (o + 12 > b.Length) return null;
                // stream_PID(16) / reserved(10) EP_stream_type(4) number_of_EP_coarse_entries(16)
                // number_of_EP_fine_entries(18) / EP_map_for_one_stream_PID_start_address(32)
                int pid = BdmvPlaylist.Be16(b, o);
                uint a = BdmvPlaylist.Be32(b, o + 2);
                ushort m = BdmvPlaylist.Be16(b, o + 6);
                uint addr = BdmvPlaylist.Be32(b, o + 8);
                int coarse = (int)((a >> 2) & 0xFFFF);
                int fine = (int)(((a & 0x03) << 16) | m);
                var map = ParseOne(b, epMap + (int)addr, coarse, fine, pid);
                if (map is { Count: > 0 }) return map;            // 通常は主映像の1本だけ
            }
            return null;
        }
        catch { return null; }
    }

    // EP_map_for_one_stream_PID()：
    //   EP_fine_table_start_address(32・この構造の先頭からの相対)
    //   粗テーブル … ref_to_EP_fine_id(18) + PTS_EP_coarse(14) + SPN_EP_coarse(32) ＝ 8 バイト
    //   細テーブル … is_angle_change_point(1) + I_end_position_offset(3) + PTS_EP_fine(11) + SPN_EP_fine(17) ＝ 4 バイト
    //
    // 復元：粗の最下位ビットは細の最上位ビットと重なるので落としてから足す。
    //   PTS = ((PTS_EP_coarse & ~1) << 19) + (PTS_EP_fine << 9)
    //   SPN =  (SPN_EP_coarse & ~0x1FFFF)  +  SPN_EP_fine
    private static BdEpMap? ParseOne(byte[] b, int start, int coarseCount, int fineCount, int pid)
    {
        if (start <= 0 || start + 4 > b.Length || fineCount <= 0 || coarseCount <= 0) return null;
        int fineTable = start + (int)BdmvPlaylist.Be32(b, start);
        if (start + 4 + coarseCount * 8 > b.Length) return null;
        if (fineTable + fineCount * 4 > b.Length) return null;

        var pts = new long[fineCount];
        var spn = new long[fineCount];
        for (int i = 0; i < coarseCount; i++)
        {
            int co = start + 4 + i * 8;
            uint w = BdmvPlaylist.Be32(b, co);
            int first = (int)(w >> 14);
            long ptsCoarse = w & 0x3FFF;
            long spnCoarse = BdmvPlaylist.Be32(b, co + 4);
            int last = i + 1 < coarseCount ? (int)(BdmvPlaylist.Be32(b, co + 8) >> 14) : fineCount;
            if (last > fineCount) last = fineCount;
            for (int j = first; j < last; j++)
            {
                if (j < 0) break;
                uint f = BdmvPlaylist.Be32(b, fineTable + j * 4);
                pts[j] = ((ptsCoarse & ~1L) << 19) + (long)((f >> 17) & 0x7FF) * 512;
                spn[j] = (spnCoarse & ~0x1FFFFL) + (f & 0x1FFFF);
            }
        }
        // 昇順であることを確認（壊れていたら使わない：誤ったシークより従来動作のほうが安全）。
        for (int i = 1; i < fineCount; i++)
            if (pts[i] < pts[i - 1] || spn[i] < spn[i - 1]) return null;
        return new BdEpMap(pid, pts, spn);
    }
}
