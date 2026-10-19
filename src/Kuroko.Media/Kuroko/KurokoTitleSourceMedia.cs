using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kuroko;

namespace Kuroko.Media.Krk;

// krk の映像を開く処理（再生器側）。形式の読み取りは Kuroko SDK の KurokoTitleSource が受け持ち、ffmpeg を使う
// デマルチプレクサ（KurokoMkvDemuxer・KurokoPlaylistDemuxer）を作る部分だけを、形式の SDK の外（このライブラリ）に置いている。
internal static class KurokoTitleSourceMedia
{
    // タイトルのメディアを開いてデマルチプレクサを返す（呼び出し側が Dispose する）。
    //  ・v2（title.json に segments あり）：セグメント連結の KurokoPlaylistDemuxer（継ぎ目なし）。
    //  ・v1（segments 無し）：単一 av.mkv の KurokoMkvDemuxer。
    public static IKurokoDemuxer OpenTitleMedia(this KurokoTitleSource src, int titleId, string? ffmpegDir = null, int angle = 1)
    {
        if (!KurokoMkvDemuxer.TryLoad(ffmpegDir, out var err))
            throw new InvalidOperationException("ffmpeg(avformat) のロードに失敗しました: " + err);

        var title = src.ReadTitle(titleId, angle);
        if (title.Segments is { Count: > 0 })
        {
            var segs = new List<KurokoPlaylistDemuxer.SegSpec>(title.Segments.Count);
            foreach (var sref in title.Segments)
            {
                var seg = src.ReadSegment(sref.Id);
                var trackMap = BuildTrackMap(title, seg);
                long segOff = (long)System.Math.Round(KurokoTime.Parse(sref.StartTime).TotalMilliseconds);
                // メディア解像度パーツ（§8）があれば、各パーツを連続する SegSpec として展開する（連結 demux が
                // そのまま扱う＝境界で解像度が変わっても in-band SPS で1デコーダが再初期化）。無ければ単一 av.mkv。
                if (seg.Parts is { Count: > 0 })
                {
                    double partOff = 0;
                    for (int pi = 0; pi < seg.Parts.Count; pi++)
                    {
                        var part = seg.Parts[pi];
                        string partPath = $"{KurokoLayout.SegmentDir(sref.Id)}/{part.File}";
                        if (src.Container.FindEntry(partPath) is not KrkEntry avPart)
                            throw new FileNotFoundException($"{partPath} が見つかりません。");
                        // パーツ毎に映像トラック数（dual=2/film=1）が変わるので trackMap もパーツ毎に作る（§8.1）。
                        segs.Add(new KurokoPlaylistDemuxer.SegSpec
                        {
                            Id = $"{sref.Id}#{pi}",
                            OffsetMs = segOff + (long)System.Math.Round(partOff),
                            AvEntry = avPart,
                            TrackMap = BuildTrackMap(title, seg, part.FieldLayout),
                            IsStill = part.Still,
                            IsDual = string.Equals(part.FieldLayout, "dual", StringComparison.OrdinalIgnoreCase),
                        });
                        partOff += part.DurationMs;
                    }
                    continue;
                }
                if (src.Container.FindEntry(KurokoLayout.SegmentAvMkv(sref.Id)) is not KrkEntry avSeg)
                    throw new FileNotFoundException($"SEGMENTS/{sref.Id}/av.mkv が見つかりません。");
                segs.Add(new KurokoPlaylistDemuxer.SegSpec
                {
                    Id = sref.Id,
                    OffsetMs = segOff,
                    AvEntry = avSeg,
                    TrackMap = trackMap,
                });
            }
            return new KurokoPlaylistDemuxer(src.Container, ffmpegDir, segs);
        }

        // v1：単一 av.mkv。
        if (src.Container.FindEntry(KurokoLayout.TitleAvMkv(titleId)) is not KrkEntry av)
            throw new FileNotFoundException($"VIDEO/T{titleId:00}/av.mkv が見つかりません。");
        var stream = src.Container.OpenRead(av);
        try { return new KurokoMkvDemuxer(stream, leaveOpen: false); }
        catch { stream.Dispose(); throw; }
    }

    // セグメントの mkv トラック番号 → title.json の正準トラック番号の**リスト**。映像は 0→[0]。
    // v2b（§13.3/13.4）：title=論理→物理（substream/phys**）、seg=物理→実トラック。物理IDで写像するので、
    // 共有セグメントをタイトルごとに異なる AST_CTL/SPST_CTL で参照しても正しく解決される。
    // 旧v2（substream 無し=-1）：従来どおり dvdStream（論理番号）一致で写像（後方互換）。
    // 1つのセグメントトラックが複数の正準トラックに対応し得る（AST_CTL 非恒等で複数論理音声が同一物理サブストリームを
    // 指す＝ある市販ディスク タイトル2：論理0/1 とも 0x80）。この場合リストへ両方積み、demux が各正準trackとして供給する。
    // partFieldLayout：解像度パーツ（§8.1）を開くときは、そのパーツの fieldLayout を渡す。dual パーツは映像2トラック
    //（track1=bottom）＋音声が track2+ へ1つずれるため、seg.audio.mkvTrack（film 基準=1+i）に +1 して写像する。
    // null＝パーツなし（単一 av.mkv）。この場合は seg.Video.FieldLayout で判定し、音声は seg.audio.mkvTrack をそのまま使う。
    private static Dictionary<int, List<int>> BuildTrackMap(KurokoTitle title, KurokoSegment seg, string? partFieldLayout = null)
    {
        bool dualVideo = partFieldLayout != null
            ? string.Equals(partFieldLayout, "dual", StringComparison.OrdinalIgnoreCase)
            : string.Equals(seg.Video.FieldLayout, "dual", StringComparison.OrdinalIgnoreCase);
        int audioShift = (partFieldLayout != null && dualVideo) ? 1 : 0; // dual パーツ：音声基準(1+i)→track2+
        var map = new Dictionary<int, List<int>>();
        void M(int segT, int canonT)
        {
            if (segT <= 0 || canonT <= 0) return;
            if (!map.TryGetValue(segT, out var l)) map[segT] = l = new List<int>();
            if (!l.Contains(canonT)) l.Add(canonT);
        }
        M2(map, 0, 0); // 映像（top。fields dual では top フィールドトラック）
        // fields dual：mkv track1 = bottom フィールド映像 → 専用正準へ。再生側が2つ目のデコーダへ振り分ける。
        if (dualVideo)
            M2(map, 1, KurokoLayout.BottomVideoCanonical);
        foreach (var ta in title.Audio)
        {
            // 1つの論理音声が**時間で複数の実トラックに分かれている**ことがある（controller="none"：
            // チャンネル数が途中で変わる素材は、Matroska/Opus の制約で 2ch 区間と 5.1ch 区間を別トラックに
            // 分けて格納し、seg.json のスパンで指し分ける）。FirstOrDefault だと先頭トラックしか写像されず、
            // **後半トラックのパケットが demux で捨てられて無音になる**ので、一致する全トラックを通す。
            var matches = (ta.Substream >= 0
                ? seg.Audio.Where(a => a.Substream == ta.Substream)
                : seg.Audio.Where(a => a.DvdStream == ta.DvdStream)).ToList();
            if (matches.Count == 0) continue;
            M(matches[0].MkvTrack + audioShift, ta.MkvTrack); // 代表＝論理の正準トラック
            // 残りは自分自身の番号を正準として通す（スパンの track がその番号を指している）。
            for (int k = 1; k < matches.Count; k++)
                M(matches[k].MkvTrack + audioShift, matches[k].MkvTrack);
        }
        foreach (var ts in title.Subtitle)
        {
            if (ts.Phys43 >= 0 || ts.PhysWide >= 0 || ts.PhysLetterbox >= 0 || ts.PhysPanScan >= 0)
            {
                int SegTrackOf(int physId) => physId >= 0
                    ? (seg.Subtitle.FirstOrDefault(s => s.Substream == physId)?.MkvTrack ?? -1) : -1;
                M(SegTrackOf(ts.Phys43), ts.Track43); M(SegTrackOf(ts.PhysWide), ts.TrackWide);
                M(SegTrackOf(ts.PhysLetterbox), ts.TrackLetterbox); M(SegTrackOf(ts.PhysPanScan), ts.TrackPanScan);
            }
            else
            {
                var ss = seg.Subtitle.FirstOrDefault(s => s.DvdStream == ts.DvdStream);
                if (ss is null) continue;
                M(ss.Track43, ts.Track43); M(ss.TrackWide, ts.TrackWide);
                M(ss.TrackLetterbox, ts.TrackLetterbox); M(ss.TrackPanScan, ts.TrackPanScan);
                M(ss.MkvTrack, ts.MkvTrack);
            }
        }
        return map;
    }

    // seg track → 正準track をリストへ追加（重複なし）。
    private static void M2(Dictionary<int, List<int>> map, int segT, int canonT)
    {
        if (segT < 0 || canonT < 0) return;
        if (!map.TryGetValue(segT, out var l)) map[segT] = l = new List<int>();
        if (!l.Contains(canonT)) l.Add(canonT);
    }

    // メニューPGCの背景 video.mkv を開いてデマルチプレクサを返す（映像＋ボタン図柄サブピクチャ）。無ければ null。
    public static KurokoMkvDemuxer? OpenMenuMedia(this KurokoTitleSource src, string domain, int pgc, string? ffmpegDir = null)
    {
        if (src.Container.FindEntry(KurokoLayout.MenuVideoMkv(domain, pgc)) is not KrkEntry v) return null;
        if (!KurokoMkvDemuxer.TryLoad(ffmpegDir, out var err))
            throw new InvalidOperationException("ffmpeg(avformat) のロードに失敗しました: " + err);
        var stream = src.Container.OpenRead(v);
        try { return new KurokoMkvDemuxer(stream, leaveOpen: false); }
        catch { stream.Dispose(); throw; }
    }

    // v3 メニューの1セル C<nn>.mkv（映像+音声）を開く。静止セルは1フレーム、モーションは複数フレーム。無ければ null。
    public static KurokoMkvDemuxer? OpenMenuCellMedia(this KurokoTitleSource src, string domain, int pgc, int cell, string? ffmpegDir = null)
    {
        if (src.Container.FindEntry(KurokoLayout.MenuCellAv(domain, pgc, cell)) is not KrkEntry v) return null;
        if (!KurokoMkvDemuxer.TryLoad(ffmpegDir, out var err))
            throw new InvalidOperationException("ffmpeg(avformat) のロードに失敗しました: " + err);
        var stream = src.Container.OpenRead(v);
        try { return new KurokoMkvDemuxer(stream, leaveOpen: false); }
        catch { stream.Dispose(); throw; }
    }



    // v3 メニューの [startCell .. 最初の still≠0 セル（含む）] を gapless に連結した playlist を開く。
    // 各セル C<nn>.mkv はトラック 0=映像 / 1=音声（あれば）。OffsetMs はセル尺(DurationMs)の累積で連続時刻に。
    // セルの映像/SPU/PCI は menu.json（呼び出し側が保持）とこの Sequence の通し時刻で対応付ける。無ければ null。
    public static KurokoMenuV3Playback? OpenMenuMediaV3(this KurokoTitleSource src, KurokoMenuV3 menu, string domain, int pgc, int startCell, string? ffmpegDir = null)
    {
        if (menu.Cells.Count == 0) return null;
        startCell = System.Math.Clamp(startCell, 0, menu.Cells.Count - 1);
        if (!KurokoMkvDemuxer.TryLoad(ffmpegDir, out var err))
            throw new InvalidOperationException("ffmpeg(avformat) のロードに失敗しました: " + err);
        // 停止セル＝内蔵エンジン FirstStillStopCell と同条件：静止(Still≠0)だけでなく「セル終端命令(CellCommand≠0)を
        // 持つセル」でも止める。ループメニューは本体セル(Still=0)に CellCommand=LinkPGN/CN で自セルへ戻るループがあり、
        // これを見ず次の still まで再生すると本体ページが見えず・ループ命令も実行されない（ある市販ディスク VTS01/PGC2 cell1）。
        int stop = startCell;
        while (stop < menu.Cells.Count - 1 && menu.Cells[stop].Still == 0 && menu.Cells[stop].CellCommand == 0) stop++;

        var segs = new List<KurokoPlaylistDemuxer.SegSpec>();
        var seq = new List<(int, double, double)>();
        double off = 0;
        for (int c = startCell; c <= stop; c++)
        {
            if (src.Container.FindEntry(KurokoLayout.MenuCellAv(domain, pgc, c)) is not KrkEntry av) return null;
            segs.Add(new KurokoPlaylistDemuxer.SegSpec
            {
                Id = $"C{c:00}",
                OffsetMs = (long)System.Math.Round(off),
                AvEntry = av,
                TrackMap = new Dictionary<int, List<int>> { [0] = new() { 0 }, [1] = new() { 1 } }, // 映像0／音声1（メニューは単一背景音声）
            });
            seq.Add((c, off, menu.Cells[c].DurationMs));
            off += menu.Cells[c].DurationMs;
        }
        return new KurokoMenuV3Playback { Demux = new KurokoPlaylistDemuxer(src.Container, ffmpegDir, segs), Sequence = seq, StopCell = stop };
    }
}

// v3 メニューの再生プレイリスト。Demux＝[startCell..停止セル] を連結した gapless ストリーム。
// Sequence＝再生するセル列と、その通し時刻オフセット（playlist 時刻→セル＋セル内時刻の写像に使う）。
internal sealed class KurokoMenuV3Playback
{
    public IKurokoDemuxer Demux = default!;
    public List<(int Cell, double OffsetMs, double DurationMs)> Sequence = new();
    public int StopCell;                 // 列の末尾セル（この Still で保持/ループを判断）
}
