using System.IO;
using System.Text;
using Kuroko.Media.Dvd;

namespace Kuroko.Media.Krk;

// 診断：ISO を UDF として読み、ファイル階層を出す（`--udf-list`）。
// 純 UDF の Blu-ray ISO を [UdfReader](UdfReader.cs) が正しく辿れるかの確認に使う。
internal static class UdfListReport
{
    public static string Run(string isoPath, int maxEntries = 400)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"udf-list: {isoPath}");
        try { sb.AppendLine($"ファイルサイズ: {new FileInfo(isoPath).Length:N0} bytes"); } catch { }

        using (var probe = File.OpenRead(isoPath))
            sb.AppendLine($"UDF 認識: {(UdfReader.LooksLikeUdf(probe) ? "あり" : "無し")}");

        try
        {
            using var udf = new UdfReader(isoPath);
            sb.AppendLine(udf.Describe());
            int count = 0;
            Walk(udf, udf.Root, "", sb, ref count, maxEntries);
            sb.AppendLine($"（表示 {count} 件{(count >= maxEntries ? "・上限で打ち切り" : "")}）");

            // BD かどうかの判定材料。
            sb.AppendLine();
            foreach (var p in new[] { "BDMV/index.bdmv", "BDMV/MovieObject.bdmv", "VIDEO_TS/VIDEO_TS.IFO" })
                sb.AppendLine($"  {p,-28} {(udf.FindEntry(p) is { } e ? $"あり ({e.Length:N0} bytes)" : "無し")}");

            // 実体を読めるかの確認（先頭16バイト）。
            if (udf.FindEntry("BDMV/index.bdmv") is { } idx)
            {
                var head = udf.ReadAllBytes(idx);
                sb.AppendLine($"  index.bdmv 識別子: '{Encoding.ASCII.GetString(head, 0, Math.Min(8, head.Length))}'");

                // プレイリスト＝タイトル。尺の長い順に出す（本編の見当をつける）。
                var pls = BdmvPlaylist.ReadAll(udf);
                sb.AppendLine();
                sb.AppendLine($"プレイリスト: {pls.Count} 本");
                foreach (var pl in pls.OrderByDescending(x => x.DurationTicks))
                {
                    // クリップ構成は**出現順**で全部出す（言語別に先頭クリップだけ違う版があるため、
                    // 先頭を省くと同じタイトルに見えてしまう）。角度は展開前なので既定角度で並べ、
                    // 連続する同じクリップは回数でまとめる（451 回繰り返す盤があるため）。
                    var clips = new BdTitle(0, 0, 1, pl).ClipsDisplay;
                    sb.AppendLine($"  {pl.FileName}  {FmtT(pl.Duration)}  items={pl.Items.Count} chapters={pl.Chapters.Count}  clips={clips}");
                }
                // タイトル一覧（集約＋角度展開）。実際に再生器へ出すのはこれ。
                var titles = BdmvTitleSet.Build(udf);
                sb.AppendLine();
                sb.AppendLine($"タイトル一覧（集約後・{BdmvTitleSet.DefaultMinDuration.TotalMinutes:0}分未満は非表示）: {titles.Count} 本");
                foreach (var t in titles)
                    sb.AppendLine($"  {t.DisplayName,-22} {FmtT(t.Duration)}  {t.Playlist.FileName}  ch={t.Chapters.Count}  clips={t.ClipsDisplay}");

                sb.AppendLine($"  自動再生の既定（再生器）: {titles.FirstOrDefault()}");
                sb.AppendLine($"  本編推定（診断用・反復プレイリストを除外した最長）: {BdmvTitleSet.PickMain(titles)}");

                // STC の繋がりを実測（境界前後の PCR が単調に繋がっているか）。
                if (BdmvTitleSet.PickMain(titles) is { } mainTitle)
                {
                    sb.AppendLine();
                    sb.AppendLine($"STC 検査: {mainTitle.DisplayName}");

                    // STN_table（論理番号↔PID）。Kuroko 変換の source 決定に使う。
                    for (int i = 0; i < mainTitle.Playlist.Items.Count && i < 4; i++)
                    {
                        var it = mainTitle.Playlist.Items[i];
                        sb.AppendLine($"  STN #{i} {it.ClipId}: 音声 {it.AudioStreams.Count} / 字幕 {it.PgStreams.Count}");
                        foreach (var a in it.AudioStreams)
                            sb.AppendLine($"    音声{a.Number} PID=0x{a.Pid:X4} coding=0x{a.CodingType:X2} lang={a.Language ?? "-"}");
                        foreach (var s in it.PgStreams)
                            sb.AppendLine($"    字幕{s.Number} PID=0x{s.Pid:X4} coding=0x{s.CodingType:X2} lang={s.Language ?? "-"}");
                    }

                    // EP マップ（clpi の CPI）と、そこから作るシーク索引。
                    var layout = BdTitleLayout.Build(udf, mainTitle);
                    foreach (var cid in mainTitle.ClipIds.Distinct())
                    {
                        var ep = BdmvEpMap.Read(udf, cid);
                        sb.AppendLine(ep is null
                            ? $"  EPマップ {cid}: 読めず"
                            : $"  EPマップ {cid}: PID=0x{ep.Pid:X4} {ep.Count} 点  "
                              + $"{FmtP(ep.Pts90k[0])} 〜 {FmtP(ep.Pts90k[ep.Count - 1])}  "
                              + $"平均間隔={(ep.Count > 1 ? (ep.Pts90k[ep.Count - 1] - ep.Pts90k[0]) / 90000.0 / (ep.Count - 1) : 0):F2}s");
                    }
                    if (layout is not null)
                    {
                        sb.AppendLine($"  構成: {layout.Parts.Count} 区間  合計={layout.TotalBytes:N0} bytes  origin={FmtP(layout.Origin90k)}");
                        for (int i = 0; i < layout.Parts.Count && i < 4; i++)
                        {
                            var p = layout.Parts[i];
                            var it = mainTitle.Playlist.Items[i];
                            sb.AppendLine($"    [{i}] {p.ClipId} clip[{p.ClipByteFrom:N0}..{p.ClipByteFrom + p.Bytes:N0}) "
                                + $"stream@{p.StreamByteFrom:N0}  in={FmtP(p.In90k)} off={p.Offset90k}  "
                                + $"IN/OUT={FmtP((long)it.InTime * 2)}〜{FmtP((long)it.OutTime * 2)}"
                                + (p.ClipByteFrom == 0 && i < layout.Parts.Count ? "  （トリム無し）" : "  （トリム）"));
                        }
                        // 索引引きの妥当性：数点で「引いたバイト位置の PCR」が要求時刻に近いかを見る。
                        using var st = BdTitleStream.CreateFactory(isoPath, mainTitle)();
                        foreach (var sec in new[] { 0, 60, 600, 3600, 7200 })
                        {
                            var t = TimeSpan.FromSeconds(sec);
                            long? b = layout.ByteForContentTime(t);
                            if (b is not long bv) { sb.AppendLine($"    索引 {t:hh\\:mm\\:ss} → 引けず"); continue; }
                            long pcr = ScanPcr(st, bv);
                            var got = pcr < 0 ? TimeSpan.MinValue : TimeSpan.FromSeconds(pcr / 90000.0) - TimeSpan.FromSeconds(layout.Origin90k / 90000.0);
                            sb.AppendLine($"    索引 {t:hh\\:mm\\:ss} → byte={bv,14:N0}  その位置のPCR={(pcr < 0 ? "-" : got.ToString(@"hh\:mm\:ss\.fff"))}"
                                + (pcr < 0 ? "" : $"  差={(got - t).TotalMilliseconds:F0}ms"));
                        }
                    }

                    // チャプター：理論値（PlayItem の尺の累積）と、連結後の再生タイムライン上の位置。
                    // 境界ごとの隙間ぶん後者が後ろへずれる。再生器が使うのは後者。
                    var chTheory = mainTitle.Chapters;
                    var chReal = BdTitleStream.ComputeChapterTimes(isoPath, mainTitle);
                    sb.AppendLine($"  チャプター {chReal.Count} 個（理論値 {chTheory.Count} 個）");
                    for (int i = 0; i < chReal.Count && i < 8; i++)
                    {
                        double diff = i < chTheory.Count ? (chReal[i] - chTheory[i]).TotalMilliseconds : double.NaN;
                        sb.AppendLine($"    #{i + 1,-3} 再生={chReal[i]:hh\\:mm\\:ss\\.fff}"
                            + (i < chTheory.Count ? $"  理論={chTheory[i]:hh\\:mm\\:ss\\.fff}  差={diff:F0}ms" : ""));
                    }
                    if (chReal.Count > 8)
                    {
                        int last = chReal.Count - 1;
                        double d2 = last < chTheory.Count ? (chReal[last] - chTheory[last]).TotalMilliseconds : double.NaN;
                        sb.AppendLine($"    #{last + 1,-3} 再生={chReal[last]:hh\\:mm\\:ss\\.fff}"
                            + (last < chTheory.Count ? $"  理論={chTheory[last]:hh\\:mm\\:ss\\.fff}  差={d2:F0}ms" : "") + "（末尾）");
                    }

                    // clpi が申告する STC 範囲（連結オフセットの基準）。実測 PCR と突き合わせて検算する。
                    foreach (var cid in mainTitle.ClipIds.Distinct())
                    {
                        var (s45, e45) = BdmvPlaylist.ReadClipStcRange(udf, cid);
                        sb.AppendLine($"  clpi {cid}: start={s45,12} end={e45,12}"
                            + (s45 >= 0
                                ? $"  = {FmtP(s45 * 2)} 〜 {FmtP(e45 * 2)}  尺={(e45 - s45) / 45000.0:F3}s"
                                : "  読めず"));
                    }
                    // 実際に各クリップ単体の先頭/末尾で PCR を測る（clpi の申告が正しいかの答え合わせ）。
                    foreach (var cid in mainTitle.ClipIds.Distinct())
                    {
                        if (udf.FindEntry($"BDMV/STREAM/{cid}.m2ts") is not { } me) continue;
                        using var ms = udf.OpenRead(me);
                        long p0 = ScanPcr(ms, 0);
                        long p1 = ScanPcr(ms, Math.Max(0, ms.Length - (2 << 20)));
                        var r0 = BdTitleStream.ScanPtsRange(ms, 0, 8 << 20);
                        var r1 = BdTitleStream.ScanPtsRange(ms, Math.Max(0, ms.Length - (8 << 20)), 8 << 20);
                        sb.AppendLine($"  実測 {cid}: PCR {FmtP(p0)} 〜 {FmtP(p1)}   PTS 先頭窓 min={FmtP(r0.Min)} / 末尾窓 max={FmtP(r1.Max)}");
                    }

                    foreach (var cid in mainTitle.ClipIds.Distinct())
                        if (udf.FindEntry($"BDMV/STREAM/{cid}.m2ts") is { } ce) sb.Append(udf.DescribeExtents(ce));
                    try
                    {
                        using var st = BdTitleStream.CreateFactory(isoPath, mainTitle)();
                        long clip0 = 0;
                        using (var u2 = new UdfReader(isoPath))
                            if (u2.FindEntry($"BDMV/STREAM/{mainTitle.ClipIds[0]}.m2ts") is { } e0) clip0 = e0.Length;
                        foreach (var (label, pos) in new (string, long)[]
                        {
                            ("先頭", 0), ("境界-4MB", Math.Max(0, clip0 - (4 << 20))), ("境界直前", Math.Max(0, clip0 - (1 << 20))),
                            ("境界直後", clip0), ("境界+4MB", clip0 + (4 << 20)), ("末尾-8MB", Math.Max(0, st.Length - (8 << 20))),
                        })
                        {
                            var pcr = ScanPcr(st, pos);
                            sb.AppendLine($"  {label,-10} pos={pos,14:N0}  PCR={(pcr < 0 ? "見つからず" : TimeSpan.FromSeconds(pcr / 90000.0).ToString(@"hh\:mm\:ss\.fff"))}");
                        }
                        // 境界前後の PTS が重なっていないか（重なると提示が詰まる）。
                        var bTail = BdTitleStream.ScanPtsRange(st, Math.Max(0, clip0 - (8 << 20)), 8 << 20);
                        var bHead = BdTitleStream.ScanPtsRange(st, clip0, 8 << 20);
                        sb.AppendLine($"  境界前PTS最大={FmtP(bTail.Max)}  境界後PTS最小={FmtP(bHead.Min)}  重なり={(bTail.Max >= 0 && bHead.Min >= 0 ? ((bTail.Max - bHead.Min) / 90000.0).ToString("F3") + "s" : "-")}");

                        // 連続読みの実効速度（再生に足りるか）。境界の少し先から 200MB 読む。
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        st.Position = Math.Min(st.Length - 1, clip0 + (64L << 20));
                        var rb = new byte[1 << 20];
                        long read = 0;
                        while (read < (200L << 20))
                        {
                            int n = st.Read(rb, 0, rb.Length);
                            if (n <= 0) break;
                            read += n;
                        }
                        sw.Stop();
                        double mbps = read / 1048576.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        sb.AppendLine($"  連続読み: {read / 1048576.0:F0}MB を {sw.Elapsed.TotalSeconds:F2}s → {mbps:F0} MB/s");
                    }
                    catch (Exception ex) { sb.AppendLine($"  ERROR: {ex.Message}"); }
                }

                var main = pls.OrderByDescending(x => x.DurationTicks).FirstOrDefault();
                if (main is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"最長プレイリスト {main.FileName} の内訳:");
                    for (int i = 0; i < main.Items.Count && i < 12; i++)
                    {
                        var it = main.Items[i];
                        var m2ts = udf.FindEntry($"BDMV/STREAM/{it.ClipId}.m2ts");
                        sb.AppendLine($"  #{i} clip={it.ClipId} {FmtT(it.Duration)} cc={it.ConnectionCondition}"
                            + $" m2ts={(m2ts is null ? "無し" : $"{m2ts.Length:N0} bytes")}");
                    }
                    sb.AppendLine("  チャプター: " + string.Join(", ", main.Chapters.Take(12).Select(FmtT)));
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        return sb.ToString();
    }

    private static string FmtT(TimeSpan t) => t.ToString(@"hh\:mm\:ss");

    private static string FmtP(long pts90k)
        => pts90k < 0 ? "-" : TimeSpan.FromSeconds(pts90k / 90000.0).ToString(@"hh\:mm\:ss\.fff");

    // 指定位置から 192B 単位で走査し、最初に見つかった PCR（90kHz）を返す。
    private static long ScanPcr(Stream s, long pos)
    {
        const int Unit = 192;
        long start = pos / Unit * Unit;
        if (start >= s.Length) return -1;
        var buf = new byte[Math.Min(2 << 20, (int)Math.Min(s.Length - start, int.MaxValue))];
        s.Position = start;
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) break;
            got += n;
        }
        for (int o = 0; o + Unit <= got; o += Unit)
        {
            int ts = o + 4;
            if (buf[ts] != 0x47) continue;
            int afc = (buf[ts + 3] >> 4) & 0x03;
            if (afc != 2 && afc != 3) continue;
            int afLen = buf[ts + 4];
            if (afLen <= 0 || (buf[ts + 5] & 0x10) == 0) continue;
            return ((long)buf[ts + 6] << 25) | ((long)buf[ts + 7] << 17) | ((long)buf[ts + 8] << 9)
                 | ((long)buf[ts + 9] << 1) | ((long)(buf[ts + 10] >> 7) & 0x01);
        }
        return -1;
    }

    private static void Walk(UdfReader udf, UdfEntry dir, string prefix, StringBuilder sb, ref int count, int max)
    {
        if (count >= max) return;
        foreach (var e in udf.ListChildren(dir))
        {
            if (count >= max) return;
            count++;
            sb.AppendLine(e.IsDirectory ? $"  {prefix}{e.Name}/" : $"  {prefix}{e.Name}  {e.Length:N0} bytes");
            if (e.IsDirectory) Walk(udf, e, prefix + e.Name + "/", sb, ref count, max);
        }
    }
}
