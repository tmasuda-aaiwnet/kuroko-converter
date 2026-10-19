using System.IO;

namespace Kuroko.Media.Dvd;

// Blu-ray の PGS 字幕を **復号せずに生のまま** 取り出す（Kuroko 仕様 §11 の `pgs`）。
//
// 方針：PGS は元から RLE 圧縮されたビットマップで、**パレット更新の Display Set がフェード・色替えを担う**。
// PNG へ展開すると枚数が増えるうえ再現度が落ちるので、表示 1 回ぶんのバイト列をそのまま保存し、
// 再生時に同梱 ffmpeg の PGS 復号器へ渡す（内蔵TSエンジンが ISO 再生で使っているのと同じ経路）。
//
// 解釈するのは「セグメント境界」と「表示の開始/終了」だけ：
//   PGS セグメント … type(1) + length(2) + payload
//     PCS(0x16) 表示構成。payload[10]＝composition object 数。**0 なら消去**
//     WDS(0x17) / PDS(0x14) / ODS(0x15) / END(0x80)
internal static class BdPgsExtractor
{
    private const int Unit = 192;          // BDAV：TP_extra_header(4)＋TSパケット(188)
    private const byte SegPcs = 0x16, SegEnd = 0x80;

    public sealed record Cue(double StartMs, double EndMs, byte[] Data);

    // 1 字幕 PID ぶんの組み立て途中状態。
    private sealed class PidState
    {
        public readonly MemoryStream Pes = new();    // 組み立て中の PES ペイロード
        public long PesPts = -1;
        public readonly MemoryStream Ds = new();     // 組み立て中の Display Set
        public long DsPts = -1;
        public readonly MemoryStream Open = new();   // 表示中 cue のバイト列
        public long OpenPts = -1;
    }

    // clipStart90k＝このクリップの STC 開始（clpi の presentation_start）。cue の時刻はここを 0 とした ms。
    // maxBytes>0 なら先頭からその量だけ走査する（診断用）。
    public static List<Cue> Extract(Stream m2ts, int pid, long clipStart90k, long maxBytes = 0, CancellationToken ct = default)
    {
        var cues = new List<Cue>();
        ExtractMany(m2ts, new[] { pid }, clipStart90k, (_, c) => cues.Add(c), maxBytes, ct);
        return cues;
    }

    // **複数の字幕 PID を 1 パスで**取り出す。cue は出来た順に onCue(pid, cue) へ渡す。
    //
    // なぜ一括か：クリップは本編で 40GB に達し、字幕は 1 クリップに 14 本入ることがある。PID ごとに
    // 呼び直すと同じ 40GB を 14 回読むことになる。cue を貯め込むとメモリも保たない（1 本 30KB×数千×14）ので、
    // 呼び出し側がその場でファイルへ書けるようコールバックで返す。
    //
    // videoPid >= 0 なら、**同じ走査ついでに映像 PES の PTS も拾って** onVideoPts へ渡す。クリップは本編で
    // 40GB に達するので、時刻のためだけにもう一度読むのは避ける。PES ヘッダの解釈は字幕と同じなので、
    // PUSI のパケットで PTS を読むだけで済む（PES は復号順なので、表示順にするのは呼び出し側で並べ替える）。
    public static void ExtractMany(Stream m2ts, IReadOnlyCollection<int> pids, long clipStart90k,
        Action<int, Cue> onCue, long maxBytes = 0, CancellationToken ct = default,
        int videoPid = -1, Action<long>? onVideoPts = null)
    {
        if (pids.Count == 0 && videoPid < 0) return;
        var states = new Dictionary<int, PidState>(pids.Count);
        foreach (var p in pids) states[p] = new PidState();

        var buf = new byte[Unit * 512];
        m2ts.Position = 0;
        long scanned = 0;
        int carry = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int want = buf.Length - carry;
            if (maxBytes > 0 && scanned + want > maxBytes) want = (int)Math.Max(0, maxBytes - scanned);
            if (want <= 0) break;
            int got = 0;
            while (got < want)
            {
                int n = m2ts.Read(buf, carry + got, want - got);
                if (n <= 0) break;
                got += n;
            }
            if (got <= 0) break;
            scanned += got;
            int avail = carry + got;

            int o = 0;
            for (; o + Unit <= avail; o += Unit)
            {
                int ts = o + 4;
                if (buf[ts] != 0x47) continue;
                int p = ((buf[ts + 1] & 0x1F) << 8) | buf[ts + 2];
                bool isVideo = videoPid >= 0 && p == videoPid;
                if (!isVideo && !states.ContainsKey(p)) continue;
                bool start = (buf[ts + 1] & 0x40) != 0;
                int afc = (buf[ts + 3] >> 4) & 0x03;
                if (afc is 0 or 2) continue;
                int payload = ts + (afc == 3 ? 5 + buf[ts + 4] : 4);
                int payLen = o + Unit - payload;
                if (payLen <= 0) continue;

                if (isVideo)
                {
                    // 映像は PES 先頭の PTS だけ拾う（本体は読まない）。
                    if (start && payLen >= 14 && buf[payload] == 0 && buf[payload + 1] == 0 && buf[payload + 2] == 1
                        && ((buf[payload + 7] >> 6) & 0x02) != 0)
                    {
                        int q = payload + 9;
                        long v = ((long)(buf[q] & 0x0E) << 29) | ((long)buf[q + 1] << 22)
                               | ((long)(buf[q + 2] & 0xFE) << 14) | ((long)buf[q + 3] << 7)
                               | ((long)(buf[q + 4] & 0xFE) >> 1);
                        onVideoPts?.Invoke(v);
                    }
                    continue;
                }
                var st = states[p];

                if (start)
                {
                    FlushPes(st, p, onCue, clipStart90k);
                    // PES ヘッダ：00 00 01 sid + len(2) + flags(2) + hdrLen(1) [+ PTS(5)]
                    if (payLen < 9 || buf[payload] != 0 || buf[payload + 1] != 0 || buf[payload + 2] != 1) continue;
                    int hdrLen = buf[payload + 8];
                    int q = payload + 9;
                    st.PesPts = ((buf[payload + 7] >> 6) & 0x02) != 0 && payLen >= 14
                        ? ((long)(buf[q] & 0x0E) << 29) | ((long)buf[q + 1] << 22)
                          | ((long)(buf[q + 2] & 0xFE) << 14) | ((long)buf[q + 3] << 7)
                          | ((long)(buf[q + 4] & 0xFE) >> 1)
                        : -1;
                    int body = payload + 9 + hdrLen;
                    if (body < o + Unit) st.Pes.Write(buf, body, o + Unit - body);
                }
                else if (st.Pes.Length > 0 || st.PesPts >= 0)
                {
                    st.Pes.Write(buf, payload, payLen);
                }
            }
            carry = avail - o;
            if (carry > 0) Buffer.BlockCopy(buf, o, buf, 0, carry);
        }
        foreach (var (pid, st) in states)
        {
            FlushPes(st, pid, onCue, clipStart90k);
            // 閉じられなかった表示（末尾で消去が来ない）は、その時点で打ち切る。
            if (st.Open.Length > 0 && st.OpenPts >= 0)
                onCue(pid, new Cue(Ms(st.OpenPts, clipStart90k), Ms(st.OpenPts, clipStart90k), st.Open.ToArray()));
        }
    }

    private static double Ms(long pts90k, long origin90k) => Math.Max(0, pts90k - origin90k) / 90.0;

    // 組み立て終わった PES ペイロードを Display Set 単位に切り、表示の開始/終了を判定して cue を作る。
    private static void FlushPes(PidState st, int pid, Action<int, Cue> onCue, long origin90k)
    {
        if (st.Pes.Length == 0) { st.PesPts = -1; return; }
        var data = st.Pes.GetBuffer();
        int len = (int)st.Pes.Length;
        int i = 0;
        while (i + 3 <= len)
        {
            byte type = data[i];
            int segLen = (data[i + 1] << 8) | data[i + 2];
            if (i + 3 + segLen > len) break;
            if (st.DsPts < 0) st.DsPts = st.PesPts;
            st.Ds.Write(data, i, 3 + segLen);

            if (type == SegEnd)
            {
                // Display Set が1つ完成。PCS の composition object 数で「表示 / 消去」を判定する。
                var ds = st.Ds.ToArray();
                st.Ds.SetLength(0);
                int objects = CompositionObjects(ds);
                if (objects > 0)
                {
                    if (st.Open.Length == 0) st.OpenPts = st.DsPts;  // 表示開始
                    st.Open.Write(ds, 0, ds.Length);                 // パレット更新等はこの cue に含める
                }
                else if (st.Open.Length > 0)
                {
                    onCue(pid, new Cue(Ms(st.OpenPts, origin90k), Ms(st.DsPts, origin90k), st.Open.ToArray()));
                    st.Open.SetLength(0);
                    st.OpenPts = -1;
                }
                st.DsPts = -1;
            }
            i += 3 + segLen;
        }
        st.Pes.SetLength(0);
        st.PesPts = -1;
    }

    // Display Set の先頭 PCS から composition object 数を読む。PCS が無ければ 0（＝消去扱い）。
    private static int CompositionObjects(byte[] ds)
    {
        int i = 0;
        while (i + 3 <= ds.Length)
        {
            byte type = ds[i];
            int segLen = (ds[i + 1] << 8) | ds[i + 2];
            if (i + 3 + segLen > ds.Length) break;
            if (type == SegPcs)
                return segLen >= 11 ? ds[i + 3 + 10] : 0;
            i += 3 + segLen;
        }
        return 0;
    }
}
