namespace Kuroko.Media.Dvd;

// クリップ（m2ts）に**実在する**エレメンタリストリームを PMT から読む。
//
// なぜ STN_table では駄目か：STN_table は PlayItem（＝プレイリスト）に属する「選べるストリームの一覧」で、
// **同じクリップでも参照するプレイリストが違えば内容が変わる**。また STN_table はアングル間で共有される
// （角度が変えるのはクリップ実体だけ）ので、そのクリップに本当にそのPIDがあるかは別問題。
// Kuroko の seg.json は「メディア属性の権威」なので、ここは物理の実体＝PMT を根拠にする。
internal static class BdClipStreams
{
    private const int Unit = 192;

    public sealed record Es(int Pid, int StreamType);

    // 先頭から scanBytes だけ走査して PMT を読む（PSI は先頭に必ず出る）。見つからなければ空。
    public static List<Es> Probe(Stream m2ts, int scanBytes = 4 << 20)
    {
        var result = new List<Es>();
        try
        {
            int want = (int)Math.Min(scanBytes, m2ts.Length);
            var buf = new byte[want];
            m2ts.Position = 0;
            int got = 0;
            while (got < buf.Length)
            {
                int n = m2ts.Read(buf, got, buf.Length - got);
                if (n <= 0) break;
                got += n;
            }

            var pat = ReadSection(buf, got, 0, 0x00);
            if (pat is null || pat.Length < 12) return result;
            int pmtPid = -1;
            for (int q = 8; q + 4 <= pat.Length - 4; q += 4)
            {
                if (((pat[q] << 8) | pat[q + 1]) == 0) continue;        // NIT
                pmtPid = ((pat[q + 2] & 0x1F) << 8) | pat[q + 3];
                break;
            }
            if (pmtPid < 0) return result;

            var pmt = ReadSection(buf, got, pmtPid, 0x02);
            if (pmt is null || pmt.Length < 16) return result;
            int infoLen = ((pmt[10] & 0x0F) << 8) | pmt[11];
            int end = pmt.Length - 4;                                   // CRC32 を除く
            for (int q = 12 + infoLen; q + 5 <= end;)
            {
                int type = pmt[q];
                int epid = ((pmt[q + 1] & 0x1F) << 8) | pmt[q + 2];
                int esLen = ((pmt[q + 3] & 0x0F) << 8) | pmt[q + 4];
                result.Add(new Es(epid, type));
                q += 5 + esLen;
            }
        }
        catch { }
        return result;
    }

    // 指定 PID・table_id のセクションを 1 つ取り出す。**セクションは複数の TS パケットに跨る**
    // （BD の PMT は音声・字幕が多く 188 バイトにまず収まらない）ので、payload_unit_start_indicator を
    // 起点に後続パケットのペイロードを連結して組み立てる。
    private static byte[]? ReadSection(byte[] buf, int got, int pid, int tableId)
    {
        var sec = new List<byte>(1024);
        int need = -1;
        for (int o = 0; o + Unit <= got; o += Unit)
        {
            int ts = o + 4;
            if (buf[ts] != 0x47) continue;
            if ((((buf[ts + 1] & 0x1F) << 8) | buf[ts + 2]) != pid) continue;
            int afc = (buf[ts + 3] >> 4) & 0x03;
            if (afc is 0 or 2) continue;
            int p = ts + (afc == 3 ? 5 + buf[ts + 4] : 4);
            int lim = o + Unit;
            if (p >= lim) continue;

            if ((buf[ts + 1] & 0x40) != 0)                              // ペイロード先頭
            {
                p += 1 + buf[p];                                        // pointer_field
                if (p >= lim || buf[p] != tableId) { sec.Clear(); need = -1; continue; }
                sec.Clear();
                need = -1;
            }
            else if (sec.Count == 0) continue;                          // 途中から拾わない

            for (int i = p; i < lim; i++) sec.Add(buf[i]);
            if (need < 0 && sec.Count >= 3) need = 3 + (((sec[1] & 0x0F) << 8) | sec[2]);
            if (need > 0 && sec.Count >= need) return sec.GetRange(0, need).ToArray();
        }
        return null;
    }

    // Blu-ray の stream_type から大分類を返す（診断・seg.json の組み立て用）。
    public static bool IsVideo(int t) => t is 0x02 or 0x1B or 0x24 or 0xEA;
    public static bool IsAudio(int t) => t is 0x03 or 0x04 or 0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xA1 or 0xA2;
    public static bool IsPg(int t) => t is 0x90 or 0x91 or 0x92;
}
