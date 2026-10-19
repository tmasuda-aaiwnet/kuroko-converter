using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Kuroko.Media.Dvd;

// DVD ISO の情報レポート（タイトル/メニューごとの総容量・コーデック・ストリーム別バイト数）を作る。
// 総容量はセル占有セクタ×2048、ストリーム別バイト数は VOB を実走査して PES ペイロードを集計する。
// 映像コーデックはストリームから実検出（H.264 / MPEG-2）、音声コーデックは IFO 宣言（AC-3/DTS/LPCM/MPEG）。
// メニューは「同一セル集合の PGC」をまとめて1件にし、共有セルの重複計上を避ける。
internal static class DvdIsoAnalyzer
{
    // 1タイトル/1メニュー分のストリーム別集計。
    private sealed class StreamBytes
    {
        public long Video;
        public long Subpicture;
        public long NavPadding;
        public long Other;
        public readonly Dictionary<int, long> Audio = new(); // サブストリームID(0x80.. / 0xC0..) → バイト数
        public bool MpegSeqFound;   // 00 00 01 B3（MPEG シーケンスヘッダ）
        public bool H264SpsFound;   // 00 00 01 [NAL type7]（H.264 SPS）
        public bool VideoResolved;  // コーデック判定済み（以降の走査検出は省略）

        public void Merge(StreamBytes o)
        {
            Video += o.Video; Subpicture += o.Subpicture; NavPadding += o.NavPadding; Other += o.Other;
            MpegSeqFound |= o.MpegSeqFound; H264SpsFound |= o.H264SpsFound; VideoResolved |= o.VideoResolved;
            foreach (var kv in o.Audio) Audio[kv.Key] = Audio.GetValueOrDefault(kv.Key) + kv.Value;
        }
    }

    public static string Analyze(string isoPath, IProgress<(double frac, string status)>? progress, CancellationToken ct)
    {
        using var disc = DvdDisc.Open(isoPath);
        var (titles, menus, titleErrors) = ScanAll(disc, progress, ct);

        // 出力：ヘッダ → サマリー → タイトル一覧 → メニュー一覧。
        var sb = new StringBuilder();
        sb.AppendLine($"ISO: {isoPath}");
        try { sb.AppendLine($"ファイルサイズ: {new FileInfo(isoPath).Length:N0} bytes"); } catch { }
        sb.AppendLine();

        long titleVideo = titles.Sum(t => t.Scan.Video);
        long titleAudio = titles.Sum(t => t.Scan.Audio.Values.Sum());
        long titleSubp = titles.Sum(t => t.Scan.Subpicture);
        long menuVideo = menus.Sum(m => m.Scan.Video);
        long menuAudio = menus.Sum(m => m.Scan.Audio.Values.Sum());
        long menuSubp = menus.Sum(m => m.Scan.Subpicture);
        int titleSubStreams = titles.Sum(t => t.Tp.SubpictureStreams.Count); // 論理字幕ストリーム総数
        sb.AppendLine("=== サマリー ===");
        sb.AppendLine($"  タイトル 映像合計: {titleVideo,18:N0} bytes");
        sb.AppendLine($"  タイトル 音声合計: {titleAudio,18:N0} bytes");
        sb.AppendLine($"  タイトル 字幕合計: {titleSubp,18:N0} bytes  (論理字幕 {titleSubStreams} 本)");
        sb.AppendLine($"  メニュー 映像合計: {menuVideo,18:N0} bytes");
        sb.AppendLine($"  メニュー 音声合計: {menuAudio,18:N0} bytes");
        sb.AppendLine($"  メニュー 字幕合計: {menuSubp,18:N0} bytes");
        sb.AppendLine();

        sb.AppendLine("=== タイトル一覧 ===");
        foreach (var err in titleErrors) { sb.AppendLine(err); sb.AppendLine(); }
        foreach (var t in titles) FormatEntry(sb, t.Head, t.Tp, t.Scan);

        sb.AppendLine();
        sb.AppendLine("=== メニュー一覧 ===");
        if (menus.Count == 0) sb.AppendLine("（セルを持つメニューはありません）");
        foreach (var m in menus) FormatEntry(sb, m.Head, m.Tp, m.Scan);

        progress?.Report((1.0, "完了"));
        return sb.ToString();
    }

    // 全タイトル・全メニュー（同一セル集合をまとめたグループ）を走査して集計する（Analyze/AnalyzeSummary 共用）。
    private static (List<(string Head, DvdTitlePlayback Tp, StreamBytes Scan)> Titles,
                    List<(string Head, DvdTitlePlayback Tp, StreamBytes Scan)> Menus,
                    List<string> TitleErrors)
        ScanAll(DvdDisc disc, IProgress<(double frac, string status)>? progress, CancellationToken ct)
    {
        // 進捗用に総セクタ数を見積もる（タイトル全セル＋ユニークなメニューセル）。
        long totalSectors = EstimateTotalSectors(disc);
        long scanned = 0;

        var titles = new List<(string Head, DvdTitlePlayback Tp, StreamBytes Scan)>();
        var titleErrors = new List<string>();
        foreach (var te in disc.Titles)
        {
            ct.ThrowIfCancellationRequested();
            DvdTitlePlayback tp;
            try { tp = disc.OpenTitle(te.TitleNumber); }
            catch (Exception ex) { titleErrors.Add($"タイトル{te.TitleNumber}: 解析エラー {ex.Message}"); continue; }
            var scan = ScanPlayback(tp, $"タイトル{te.TitleNumber}", progress, ref scanned, totalSectors, ct);
            string head = $"タイトル{te.TitleNumber}  (VTS{te.VtsNumber}/T{te.VtsTitleNumber})  尺={tp.Duration:hh\\:mm\\:ss}";
            titles.Add((head, tp, scan));
        }

        // ドメイン横断でメニューPGCを集め、同一セル集合のものをまとめる（重複計上回避）。
        var menus = new List<(string Head, DvdTitlePlayback Tp, StreamBytes Scan)>();
        foreach (var g in CollectMenuGroups(disc))
        {
            ct.ThrowIfCancellationRequested();
            var tp = disc.OpenGroup(g);
            if (tp is null) continue;
            var scan = ScanPlayback(tp, g.Label, progress, ref scanned, totalSectors, ct);
            menus.Add((g.Label, tp, scan));
        }
        return (titles, menus, titleErrors);
    }

    // 一覧表示用のサマリーのみ（レポート文字列を作らない）。ISO は VOB 実走査のため重い（Analyze と同等）。
    public static MediaInfoSummary AnalyzeSummary(string isoPath, IProgress<(double frac, string status)>? progress, CancellationToken ct)
    {
        using var disc = DvdDisc.Open(isoPath);
        var (titles, menus, _) = ScanAll(disc, progress, ct);
        string vcodec = "-", acodec = "-";
        if (titles.Count > 0)
        {
            var (_, tp, scan) = titles[0];
            vcodec = VideoCodecName(tp, scan);
            int firstAudio = scan.Audio.Count > 0 ? scan.Audio.Keys.Min() : -1;
            acodec = firstAudio >= 0 ? AudioCodecName(firstAudio)
                : tp.AudioStreams.Count > 0 ? tp.AudioStreams[0].CodingMode switch
                {
                    DvdAudioCodingMode.Ac3 => "AC-3",
                    DvdAudioCodingMode.Dts => "DTS",
                    DvdAudioCodingMode.Lpcm => "LPCM",
                    DvdAudioCodingMode.Mpeg1 or DvdAudioCodingMode.Mpeg2Ext => "MPEG音声",
                    _ => "-",
                } : "-";
        }
        return new MediaInfoSummary(
            Format: disc.VideoIsH264 ? "拡張DVD-Video" : "DVD-Video",
            VideoCodec: vcodec,
            AudioCodec: acodec,
            FileSize: new FileInfo(isoPath).Length,
            TitleCount: titles.Count,
            TitleVideo: titles.Sum(t => t.Scan.Video),
            TitleAudio: titles.Sum(t => t.Scan.Audio.Values.Sum()),
            TitleSub: titles.Sum(t => t.Scan.Subpicture),
            MenuCount: menus.Count,
            MenuVideo: menus.Sum(m => m.Scan.Video),
            MenuAudio: menus.Sum(m => m.Scan.Audio.Values.Sum()),
            MenuSub: menus.Sum(m => m.Scan.Subpicture));
    }

    // メニューPGCのグループ（同一セル集合＝1件）。
    private sealed record MenuGroup(string Label, bool Vmgm, int Vts, int FirstPgc);

    private static List<MenuGroup> CollectMenuGroups(DvdDisc disc)
    {
        var result = new List<MenuGroup>();

        void AddDomain(string domain, bool vmgm, int vts, IEnumerable<DvdMenuPgc> menus)
        {
            // セル集合シグネチャ → そのグループの PGC 番号一覧。
            var bySig = new Dictionary<string, List<int>>();
            var order = new List<string>();
            foreach (var m in menus)
            {
                if (m.Pgc.Cells.Count == 0) continue; // 映像なし（コマンドのみ）は対象外
                string sig = string.Join(",", m.Pgc.Cells.Select(c => c.FirstSector).OrderBy(s => s));
                if (!bySig.TryGetValue(sig, out var list)) { list = new List<int>(); bySig[sig] = list; order.Add(sig); }
                list.Add(m.Pgc.Number);
            }
            foreach (var sig in order)
            {
                var pgcs = bySig[sig];
                string label = $"{domain} メニュー  PGC#{string.Join(",", pgcs)}";
                result.Add(new MenuGroup(label, vmgm, vts, pgcs[0]));
            }
        }

        try { AddDomain("VMGM", true, 0, disc.GetVmgmPgcs()); } catch { }
        foreach (var vts in disc.Titles.Select(t => t.VtsNumber).Distinct().OrderBy(v => v))
            try { AddDomain($"VTSM(VTS{vts})", false, vts, disc.GetMenuPgcs(vts)); } catch { }
        return result;
    }

    private static DvdTitlePlayback? OpenGroup(this DvdDisc disc, MenuGroup g)
        => g.Vmgm ? disc.OpenVmgmMenuPgc(g.FirstPgc) : disc.OpenMenuPgc(g.Vts, g.FirstPgc);

    private static long EstimateTotalSectors(DvdDisc disc)
    {
        long total = 0;
        foreach (var te in disc.Titles)
        {
            try { total += disc.OpenTitle(te.TitleNumber).Pgc.Cells.Sum(c => Math.Max(0, c.LastSector - c.FirstSector + 1)); }
            catch { }
        }
        foreach (var g in CollectMenuGroups(disc))
        {
            try { var tp = disc.OpenGroup(g); if (tp is not null) total += tp.Pgc.Cells.Sum(c => Math.Max(0, c.LastSector - c.FirstSector + 1)); }
            catch { }
        }
        return Math.Max(1, total);
    }

    // 1再生モデル（タイトル/メニュー）の全セルを走査してストリーム別バイト数を集計。
    private static StreamBytes ScanPlayback(DvdTitlePlayback tp, string status,
        IProgress<(double, string)>? progress, ref long scanned, long totalSectors, CancellationToken ct)
    {
        var acc = new StreamBytes();
        var cells = tp.Pgc.Cells;
        var buf = new byte[256 * 2048]; // 512KB ずつ読む
        for (int ci = 0; ci < cells.Count; ci++)
        {
            ct.ThrowIfCancellationRequested();
            long cellSectors = Math.Max(0, cells[ci].LastSector - cells[ci].FirstSector + 1);
            if (cellSectors == 0) continue;
            Stream s;
            try { s = tp.OpenStream(ci); } catch { continue; }
            using (s)
            {
                long remaining = cellSectors * 2048;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buf.Length, remaining);
                    int got = ReadFull(s, buf, want);
                    if (got <= 0) break;
                    for (int o = 0; o + 2048 <= got; o += 2048)
                        AccumPack(buf, o, 2048, acc);
                    remaining -= got;
                    scanned += got / 2048;
                    progress?.Report((Math.Clamp((double)scanned / totalSectors, 0, 1), status));
                }
            }
        }
        return acc;
    }

    private static int ReadFull(Stream s, byte[] buf, int want)
    {
        int got = 0;
        while (got < want)
        {
            int n = s.Read(buf, got, want - got);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }

    // 1 DVD パック（2048B、先頭=00 00 01 BA）を解析し、PES ペイロードを種別カウンタへ加算する。
    private static void AccumPack(byte[] b, int baseOff, int len, StreamBytes acc)
    {
        if (len < 14 || b[baseOff] != 0 || b[baseOff + 1] != 0 || b[baseOff + 2] != 1 || b[baseOff + 3] != 0xBA) return;
        int off = baseOff + 14 + (b[baseOff + 13] & 0x07); // pack_header(14) + stuffing
        int end = baseOff + len;
        // 任意の system_header をスキップ
        if (off + 6 <= end && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1 && b[off + 3] == 0xBB)
            off += 6 + ((b[off + 4] << 8) | b[off + 5]);

        while (off + 6 <= end && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1)
        {
            int sid = b[off + 3];
            int pesLen = (b[off + 4] << 8) | b[off + 5];
            int payloadHdr = off + 6;
            int pesEnd = pesLen == 0 ? end : Math.Min(end, payloadHdr + pesLen); // 映像は length=0（パック末尾まで）あり

            if (sid == 0xBF) { acc.NavPadding += Math.Max(0, pesEnd - payloadHdr); off = pesEnd; continue; } // NAV(private_2)
            if (sid == 0xBE) { acc.NavPadding += Math.Max(0, pesEnd - payloadHdr); off = pesEnd; continue; } // padding

            // 標準PESヘッダ：flags(2) + PES_header_data_length(1) + 拡張
            if (payloadHdr + 3 > pesEnd) break;
            int hdrLen = b[payloadHdr + 2];
            int payload = payloadHdr + 3 + hdrLen;
            if (payload > pesEnd) { off = pesEnd; continue; }
            int plen = pesEnd - payload;

            if (sid >= 0xE0 && sid <= 0xEF)
            {
                acc.Video += plen;
                if (!acc.VideoResolved) DetectVideoCodec(b, payload, pesEnd, acc);
            }
            else if (sid >= 0xC0 && sid <= 0xDF)
            {
                acc.Audio[sid] = acc.Audio.GetValueOrDefault(sid) + plen; // MPEG 音声（streamid 自体がID）
            }
            else if (sid == 0xBD && payload < pesEnd)
            {
                int sub = b[payload]; // private_stream_1 のサブストリームID（先頭1バイト）
                if (sub >= 0x20 && sub <= 0x3F) acc.Subpicture += plen;
                else acc.Audio[sub] = acc.Audio.GetValueOrDefault(sub) + plen; // AC-3/DTS/LPCM
            }
            else acc.Other += plen;

            off = pesEnd;
        }
    }

    // 映像PESペイロードから 00 00 01 B3(MPEG seq) / 00 00 01 [NAL type7](H.264 SPS) を探して種別フラグを立てる。
    private static void DetectVideoCodec(byte[] b, int start, int end, StreamBytes acc)
    {
        for (int i = start; i + 3 < end; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0 || b[i + 2] != 1) continue;
            int code = b[i + 3];
            if (code == 0xB3) { acc.MpegSeqFound = true; acc.VideoResolved = true; return; }      // MPEG シーケンスヘッダ
            if ((code & 0x80) == 0 && (code & 0x1F) == 7) { acc.H264SpsFound = true; acc.VideoResolved = true; return; } // H.264 SPS
        }
    }

    private static string VideoCodecName(DvdTitlePlayback tp, StreamBytes s)
    {
        if (s.H264SpsFound && !s.MpegSeqFound) return "H.264";
        if (s.MpegSeqFound) return tp.VideoAttr.IsMpeg2 ? "MPEG-2" : "MPEG-1";
        if (s.Video == 0) return "（映像なし）";
        return tp.VideoAttr.IsMpeg2 ? "MPEG-2" : "MPEG-1"; // 検出できなければ IFO 宣言
    }

    private static string AudioCodecName(int id) =>
        id >= 0x80 && id <= 0x87 ? "AC-3" :
        id >= 0x88 && id <= 0x8F ? "DTS" :
        id >= 0xA0 && id <= 0xA7 ? "LPCM" :
        id >= 0xC0 && id <= 0xDF ? "MPEG音声" : "不明";

    private static void FormatEntry(StringBuilder sb, string head, DvdTitlePlayback tp, StreamBytes s)
    {
        long cellSectors = tp.Pgc.Cells.Sum(c => Math.Max(0, c.LastSector - c.FirstSector + 1));
        long totalBytes = cellSectors * 2048;
        sb.AppendLine($"{head}");
        sb.AppendLine($"  総容量: {totalBytes:N0} bytes ({cellSectors:N0} sectors)");
        sb.AppendLine($"  映像: {VideoCodecName(tp, s),-7} {s.Video,18:N0} bytes");

        // IFO 宣言の音声（チャンネル数/言語の注記用）を実サブストリームID へマッピング。
        var ifoById = MapIfoAudio(tp);
        double durSec = tp.Duration.TotalSeconds;
        foreach (var kv in s.Audio.OrderBy(k => k.Key))
        {
            string codec = AudioCodecName(kv.Key);
            string extra = ifoById.TryGetValue(kv.Key, out var a)
                ? $" {a.Channels}ch{(a.HasLanguage ? "/" + a.LanguageCode : "")}" : "";
            // ビットレートは PESペイロード総量÷再生時間（CBRのAC-3/DTS等はほぼ実ビットレート）。
            string br = durSec > 0.5 ? $" {(long)Math.Round(kv.Value * 8.0 / durSec / 1000)}kbps" : "";
            sb.AppendLine($"  音声[0x{kv.Key:X2} {codec}{extra}{br}]:".PadRight(40) + $"{kv.Value,18:N0} bytes");
        }
        if (s.Subpicture > 0) sb.AppendLine($"  字幕:".PadRight(40) + $"{s.Subpicture,18:N0} bytes");
        if (s.NavPadding > 0) sb.AppendLine($"  NAV/パディング:".PadRight(40) + $"{s.NavPadding,18:N0} bytes");
        if (s.Other > 0) sb.AppendLine($"  その他:".PadRight(40) + $"{s.Other,18:N0} bytes");
        sb.AppendLine();
    }

    // IFO 音声ストリーム → 実サブストリームID へマッピング（注記用）。
    // 実IDは「コーデック基底(AC3=0x80/DTS=0x88/LPCM=0xA0/MPEG=0xC0) + 実ストリーム番号」。
    // 実ストリーム番号は PGC 音声制御表(AudioControl) を優先（非連番ディスク対応）。無ければ論理index で代替
    //（サブストリーム番号＝音声ストリーム番号が規格の既定。コーデック別の連番ではない。内蔵エンジンと同じ）。
    private static Dictionary<int, DvdAudioAttr> MapIfoAudio(DvdTitlePlayback tp)
    {
        var streams = tp.AudioStreams;
        var ctl = tp.Pgc.AudioControl;
        var map = new Dictionary<int, DvdAudioAttr>();
        for (int i = 0; i < streams.Count; i++)
        {
            var a = streams[i];
            int baseId = a.CodingMode switch
            {
                DvdAudioCodingMode.Ac3 => 0x80,
                DvdAudioCodingMode.Dts => 0x88,
                DvdAudioCodingMode.Lpcm => 0xA0,
                DvdAudioCodingMode.Mpeg1 or DvdAudioCodingMode.Mpeg2Ext => 0xC0,
                _ => -1,
            };
            int number = (i < ctl.Count && ctl[i].Available) ? ctl[i].Number : i;
            int id = baseId < 0 ? -1 : baseId + number;
            if (id >= 0) map[id] = a;
        }
        return map;
    }
}
