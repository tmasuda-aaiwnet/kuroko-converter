using System.Collections.Generic;
using Kuroko.Media.Dvd;

namespace Kuroko.Media.Krk;

// v2（docs §13.6）：タイトルのセグメント・プレイリストを「1本の連続ストリーム」として供給する demux。
// セグメント av.mkv を順に開き、各パケットの pts/dts を「そのセグメントのタイトル開始時刻」で再基準化し、
// セグメント毎に異なる mkv トラック番号を title.json の**正準（canonical）トラック番号**へ写像して返す。
// これにより上位（KurokoPlaybackEngine）は v1 と同じ I/F・同じ正準トラックで、継ぎ目なくパケットを読める。
//  ・映像：各セグメント先頭がクリーンIDR（SPS/PPS在留）なので、単一デコーダへ連続供給しても境界で再初期化される。
//  ・時刻：seg pts + segOffset。segOffset は title.json.segments[].startTime（＝先行セグメント尺の累積）＝連続。
//  ・音声のギャップレス化（Opus pre-skip 等）は上位側で詰める（本 demux は連続パケットとして供給）。
internal sealed class KurokoPlaylistDemuxer : IKurokoDemuxer
{
    // 1セグメントの再生仕様。TrackMap＝そのセグメントの mkv トラック番号 → 正準トラック番号の**リスト**。
    // 1つの物理トラックが複数の正準トラックに対応し得る（AST_CTL 非恒等で複数論理音声が同一物理サブストリームを
    // 指すディスク＝ある市販ディスク タイトル2 等。論理0/1 とも 0x80 を指す）。この場合そのパケットを各正準トラックとして
    // 供給し、どの論理音声を選んでも同じ物理音声が鳴るようにする（従来は Dictionary で衝突→片方が無音だった）。
    public sealed class SegSpec
    {
        public string Id = "";
        public long OffsetMs;                       // タイトル通し時刻でのこのセグメント開始
        public KrkEntry AvEntry = default!;     // /SEGMENTS/<id>/av.mkv
        public Dictionary<int, List<int>> TrackMap = new();
        public bool IsStill;                        // 疎セル（静止画）パーツ＝投入直後に即 Drain（§8.2）
        public bool IsDual;                         // fields dual（top/bottom の2映像トラック）パーツか（§8.1）
    }

    private readonly KurokoContainerReader _iso;
    private readonly string? _ffmpegDir;
    private readonly List<SegSpec> _segs;
    private int _cur = -1;
    private KurokoMkvDemuxer? _demux;
    private bool _disposed;
    private readonly Queue<KurokoMkvDemuxer.MkvPacket> _pending = new(); // 1物理→複数正準の余剰パケット

    // 診断の出力先（DvdCore はログを持たないので上位が設定する。既定は出さない）。
    public static System.Action<string>? Diag { get; set; }

    // パーツ（セグメント）の範囲から大きく外れた時刻のパケットは捨てる。この余裕を超えたものだけ。
    // 変換不良の krk への保険：ある DVD 由来の krk の S0016/av.p00.mkv（1 フレームの weave パーツ）は映像が
    // 0 枚で、音声の時刻が 0 起点へ直っておらず 1835 秒から始まっていた。そのまま連結オフセットを足すと
    // タイトル尺を超えた未来の時刻の音声が入り、以後の映像・音声が出なくなった（シークバーだけ進む）。
    // 正常なパーツの外れは Opus の pre-skip（-6.5ms）と音声末尾の余白（数十 ms）程度なので、1 秒あれば十分。
    private const double OutOfRangeMarginMs = 1000;
    private int _outOfRangeDropped;   // 現セグメントで捨てた数（ログは最初の 1 回だけ）

    public KurokoPlaylistDemuxer(KurokoContainerReader iso, string? ffmpegDir, List<SegSpec> segs)
    {
        _iso = iso;
        _ffmpegDir = ffmpegDir;
        _segs = segs;
    }

    private bool OpenSeg(int i)
    {
        DisposeCur();
        if (i < 0 || i >= _segs.Count) return false;
        var stream = _iso.OpenRead(_segs[i].AvEntry);
        try { _demux = new KurokoMkvDemuxer(stream, leaveOpen: false); }
        catch { stream.Dispose(); return false; }
        _cur = i;
        _outOfRangeDropped = 0;
        return true;
    }

    private void DisposeCur()
    {
        try { _demux?.Dispose(); } catch { }
        _demux = null;
    }

    // 断検出は現セグメントの内部 demux へ委譲。
    public bool LastReadThrew => _demux?.LastReadThrew ?? false;
    public bool LastReadUnexpectedEof => _demux?.LastReadUnexpectedEof ?? false;

    public bool ReadPacket(out KurokoMkvDemuxer.MkvPacket packet)
    {
        packet = default;
        if (_disposed) return false;
        if (_pending.Count > 0) { packet = _pending.Dequeue(); return true; } // 1物理→複数正準の残りを先に出す
        while (true)
        {
            if (_cur < 0 && !OpenSeg(0)) return false;
            if (_demux!.ReadPacket(out var p))
            {
                var seg = _segs[_cur];
                if (!seg.TrackMap.TryGetValue(p.StreamIndex, out var unis) || unis.Count == 0) continue; // 選択外/不要トラックは捨てる
                long off = seg.OffsetMs;
                // パーツの範囲（0〜宣言尺）から大きく外れたパケットは捨てる（上の OutOfRangeMarginMs 参照）。
                double localMs = p.HasPts ? p.PtsMs : p.HasDts ? p.DtsMs : 0;
                double spanMs = SegmentEndMs(_cur) - off;
                if (localMs < -OutOfRangeMarginMs || localMs > spanMs + OutOfRangeMarginMs)
                {
                    if (_outOfRangeDropped++ == 0)
                        Diag?.Invoke($"Kuroko demux: セグメント {seg.Id}（#{_cur}）にパーツの範囲外の時刻のパケットがある → 捨てる"
                            + $"（パーツ内 {localMs:F0}ms・宣言尺 {(spanMs < 1e12 ? spanMs.ToString("F0") : "∞")}ms。krk の変換不良）");
                    continue;
                }
                // 連結オフセット（seg.json 由来・ms 整数）を足す。PtsMs は ms の**小数あり**なので double のまま扱う
                // （1μs スケールの krk でサブms を落とさない）。
                // 判定は **HasPts/HasDts**（＝AV_NOPTS_VALUE だったか）で行う。`< 0` で見てはいけない：
                // Opus の pre-skip（312サンプル）で各セグメント先頭の音声パケットは **-6.5ms** という実時刻を持ち、
                // これを時刻無しとして落とすとそのパケットだけオフセットを失う。上位（KurokoPlaybackEngine）は
                // 時刻不明の音声を 0 付近とみなすため、**セグメントの先頭で音声時刻が 0 へ落ち**、0.69.1 の
                // 無音補填がその落差＝セグメント開始時刻ぶんを埋めてしまう（境界ごとに数秒の無音が入り、
                // 溜まった無音のぶん以降の音声が遅れて途切れる。ある krk のタイトル1で 19.4 秒ぶん発生した）。
                double pts = p.HasPts ? p.PtsMs + off : -1;
                double dts = p.HasDts ? p.DtsMs + off : -1;
                // 先頭正準トラックを返し、残りは pending へ（複数論理が同一物理を指す場合＝同じデータを各正準trackで供給）。
                for (int i = 0; i < unis.Count; i++)
                {
                    var mp = new KurokoMkvDemuxer.MkvPacket(unis[i], pts, dts, p.KeyFrame, p.Data, _cur,
                        HasPts: p.HasPts, HasDts: p.HasDts);
                    if (i == 0) packet = mp; else _pending.Enqueue(mp);
                }
                return true;
            }
            // 現セグメント EOF → 次セグメントへ（最後なら本当の EOF）。
            if (_cur + 1 >= _segs.Count || !OpenSeg(_cur + 1)) return false;
        }
    }

    // このセグメント（SegmentIndex）が疎セル＝静止画パーツか（§8.2）。
    public bool IsStillSegment(int segmentIndex)
        => segmentIndex >= 0 && segmentIndex < _segs.Count && _segs[segmentIndex].IsStill;

    // このセグメント（SegmentIndex）が fields dual パーツか（§8.1）。
    // 上位は single↔dual の切り替わりで前段（デコーダ保持分・マージ段）を排出する必要がある。
    public bool IsDualSegment(int segmentIndex)
        => segmentIndex >= 0 && segmentIndex < _segs.Count && _segs[segmentIndex].IsDual;

    // このセグメントの終端＝次セグメントの開始オフセット。最後のセグメントは無限大（打ち切らない）。
    public double SegmentEndMs(int segmentIndex)
        => segmentIndex >= 0 && segmentIndex + 1 < _segs.Count ? _segs[segmentIndex + 1].OffsetMs : double.MaxValue;

    // タイトル通し時刻 ms へシーク。含むセグメントを開き、その内部（ms - offset）へシークする。
    public bool SeekMs(int streamIndex, long ms)
    {
        if (_disposed || _segs.Count == 0) return false;
        _pending.Clear(); // シーク前の残余パケットを破棄
        int target = 0;
        for (int i = 0; i < _segs.Count; i++)
            if (ms >= _segs[i].OffsetMs) target = i; else break;
        if (!OpenSeg(target)) return false;
        long local = System.Math.Max(0, ms - _segs[target].OffsetMs);
        // 正準トラック→セグメント内トラックの逆写像は不要：映像(=正準0)はセグメントでも 0。映像基準でシークする。
        _demux!.SeekMs(0, local);
        return true;
    }

    // 正準トラック番号 → 現セグメントの実トラックへ逆写像して AVCodecParameters を返す。
    // セグメントを跨ぐと実体（av.mkv）が変わるので、**現在開いているセグメントのもの**を返す。
    public IntPtr TryGetCodecPar(int streamIndex)
    {
        if (_disposed || _demux is null || _cur < 0) return IntPtr.Zero;
        foreach (var kv in _segs[_cur].TrackMap)
            if (kv.Value.Contains(streamIndex)) return _demux.TryGetCodecPar(kv.Key);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCur();
    }
}
