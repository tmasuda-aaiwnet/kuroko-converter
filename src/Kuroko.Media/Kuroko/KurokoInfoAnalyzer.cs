using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Kuroko.Media.Dvd; // KurokoContainerReader / KrkEntry

namespace Kuroko.Media.Krk;

// .krk（Kuroko）の情報レポートを作る（変換器の「ISO/krk情報」ボタン用）。manifest/disc/各title.json を読み、
// タイトル・映像・音声・字幕（アスペクト別変種つき）を一覧し、サマリーに音声/字幕の合計本数を出す。
// 映像/音声/字幕のバイトサイズは av.mkv（.krk 内）を ffprobe で見て求める。
//  ・第1段：mkvmerge が付けるトラック統計タグ NUMBER_OF_BYTES を codec_type 別に合算（全パケット走査せず高速）。
//  ・第2段：タグが無ければ**パケットのサイズを実際に合算**する。ffmpeg が直接書いた mkv
//    （BD 変換は mkvmerge を通さない）はタグを持たないため。遅い（実測 1.2GB で約10秒）が、
//    タグのある krk では走らないので普段の速さは変わらない。
internal static class KurokoInfoAnalyzer
{
    public static string Analyze(string krkPath, string? ffprobePath = null, CancellationToken ct = default)
    {
        using var iso = KurokoContainerReader.Open(krkPath);
        // タイトルの読み出しはリーダ（KurokoTitleSource）に委ねる。clean（dvd-video-vm v>=3）/none/旧 vm を
        // 吸収して常に内部モデル KurokoTitle を返すので、形式変更に追従できる（title.json を直接デシリアライズしない）。
        using var src = SafeOpen(krkPath);
        var sb = new StringBuilder();
        sb.AppendLine($"krk: {krkPath}");
        try { sb.AppendLine($"ファイルサイズ: {new FileInfo(krkPath).Length:N0} bytes"); } catch { }

        var manifest = src?.Manifest ?? TryJson<KurokoManifest>(iso, KurokoLayout.ManifestFile);
        var disc = src?.Disc ?? TryJson<KurokoDisc>(iso, KurokoLayout.DiscJson);
        sb.AppendLine($"format={manifest?.Format} version={manifest?.Version} controller={manifest?.Controller} createdBy={manifest?.CreatedBy}");
        sb.AppendLine();

        int totalAudio = 0, totalSub = 0, totalSubVariants = 0;
        long sumAv = 0, sumVideo = 0, sumAudio = 0, sumSub = 0, sumSubStored = 0, sumSubFiles = 0;
        bool anySize = false;
        var body = new StringBuilder();
        var titles = disc?.Titles ?? new System.Collections.Generic.List<KurokoTitleEntry>();

        // v2（セグメント/共有）：全 title.json を先読みし、セグメントの共有状況（何タイトルが参照するか）を集める。
        // タイトル行の容量は参照セグメントの合算（共有分も各タイトルに表示）、サマリーの合計は実容量（1回のみ計上）。
        var titleJsons = new System.Collections.Generic.Dictionary<int, KurokoTitle?>();
        // アングル（§10.1）を持つタイトルは、**アングルごとにプレイリストが違う**。既定の ReadTitle は
        // アングル1しか返さないので、それだけを見ると他アングルのセグメントが丸ごと集計から漏れる
        // （実測：ある krk で S0005〜S0007 が一覧にも合計にも出ていなかった）。全アングルを読む。
        var titleAngles = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(int Angle, KurokoTitle T)>>();
        var segUse = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var te in titles)
        {
            titleJsons[te.Id] = ReadTitle(src, iso, te.Id);
            var variants = new System.Collections.Generic.List<(int Angle, KurokoTitle T)>();
            if (titleJsons[te.Id] is { } t0) variants.Add((1, t0));
            int angleCount = 1;
            try { angleCount = src?.ReadTitleAngles(te.Id).Count ?? 1; } catch { }
            for (int a = 2; a <= angleCount; a++)
                try { if (src?.ReadTitle(te.Id, a) is { } ta) variants.Add((a, ta)); } catch { }
            titleAngles[te.Id] = variants;
            foreach (var (_, tv) in variants)
                if (tv.Segments is { } srs)
                    foreach (var sr in srs)
                        segUse[sr.Id] = segUse.TryGetValue(sr.Id, out var n) ? n + 1 : 1;
        }
        // セグメントの probe 結果キャッシュ（共有セグメントを何度も ffprobe しない）
        var segCache = new System.Collections.Generic.Dictionary<string,
            (long bytes, long off, (long video, long audio, long sub, bool ok) sz,
             System.Collections.Generic.List<(int idx, string codec, int ch, string? lang, long bytes)>? tracks,
             long subsBytes, int subsN, long subsStored)>();
        var segCounted = new System.Collections.Generic.HashSet<string>();

        foreach (var te in titles)
        {
            ct.ThrowIfCancellationRequested();
            var tj = titleJsons.GetValueOrDefault(te.Id);
            var dur = tj?.Timing is { TotalMs: > 0 } t ? System.TimeSpan.FromMilliseconds(t.TotalMs) : te.DurationValue;

            var angleList = titleAngles.GetValueOrDefault(te.Id)
                ?? new System.Collections.Generic.List<(int Angle, KurokoTitle T)>();
            if (tj?.Segments is { Count: > 0 })
            {
                // ── v2：タイトルの実体は /SEGMENTS/<id>/av.mkv（プレイリスト）。参照分を合算して表示する ──
                // アングルを持つタイトルは**アングルごとにプレイリストが違う**ので、全アングルを回る。
                // krk は全アングルを格納しているので、タイトルの容量も全アングルの合算が実態に合う。
                long tBytes = 0, tv = 0, ta = 0, ts = 0; bool tOk = false;
                int segRefCount = 0, sharedN = 0;
                var agg = new System.Collections.Generic.Dictionary<(int idx, string codec, int ch, string? lang), long>();
                var segLines = new System.Collections.Generic.List<string>();
                foreach (var (angleNo, tAng) in angleList)
                {
                var segRefs = tAng.Segments ?? new System.Collections.Generic.List<KurokoSegmentRef>();
                if (angleList.Count > 1) segLines.Add($"    ── アングル{angleNo}（セグメント{segRefs.Count}個）");
                segRefCount += segRefs.Count;
                sharedN += segRefs.Count(x => segUse.TryGetValue(x.Id, out var u2) && u2 > 1);
                foreach (var sr in segRefs)
                {
                    if (!segCache.TryGetValue(sr.Id, out var e))
                    {
                        e = (0, 0, default, null, 0, 0, 0);
                        try
                        {
                            // §11：字幕は av.mkv でなく subs/（PNG）にある。その容量を字幕合計へ加える。
                            long subsBytes = iso.SumBytesUnder(KurokoLayout.SubsDir(sr.Id), out int subsN, out long subsStored);
                            var segJson = TryJson<KurokoNoneSegment>(iso, KurokoLayout.SegmentJson(sr.Id));
                            if (segJson?.Parts is { Count: > 0 } parts)
                            {
                                // 解像度パーツ（§8.1・film×interlace 混在）：av.mkv でなく av.pNN.mkv を順に合算する。
                                long pLen = 0, pOff = 0; (long video, long audio, long sub, bool ok) pSz = default;
                                System.Collections.Generic.List<(int idx, string codec, int ch, string? lang, long bytes)>? pTr = null;
                                for (int pi = 0; pi < parts.Count; pi++)
                                    if (iso.FindEntry($"{KurokoLayout.SegmentDir(sr.Id)}/{parts[pi].File}") is KrkEntry pav)
                                    {
                                        if (pi == 0) pOff = pav.DataOffset;
                                        var (psz, ptr) = ProbeStreamBytes(krkPath, pav.DataOffset, pav.Length, ffprobePath, ct);
                                        pLen += pav.Length;
                                        if (psz.ok) { pSz.ok = true; pSz.video += psz.video; pSz.audio += psz.audio; pSz.sub += psz.sub; }
                                        pTr ??= ptr;
                                    }
                                pSz.sub += subsBytes; if (subsBytes > 0) pSz.ok = true;
                                e = (pLen, pOff, pSz, pTr, subsBytes, subsN, subsStored);
                            }
                            else if (iso.FindEntry(KurokoLayout.SegmentAvMkv(sr.Id)) is KrkEntry av)
                            {
                                var (sz2, tr2) = ProbeStreamBytes(krkPath, av.DataOffset, av.Length, ffprobePath, ct);
                                sz2.sub += subsBytes;
                                if (subsBytes > 0) sz2.ok = true;
                                e = (av.Length, av.DataOffset, sz2, tr2, subsBytes, subsN, subsStored);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                        segCache[sr.Id] = e;
                    }
                    tBytes += e.bytes;
                    if (e.sz.ok) { tOk = true; tv += e.sz.video; ta += e.sz.audio; ts += e.sz.sub; }
                    if (e.tracks is not null)
                        foreach (var tk in e.tracks)
                        {
                            var key = (tk.idx, tk.codec, tk.ch, tk.lang);
                            agg[key] = agg.TryGetValue(key, out var b) ? b + tk.bytes : tk.bytes;
                        }
                    int use = segUse.TryGetValue(sr.Id, out var u) ? u : 1;
                    string subsStr = e.subsN > 0
                        ? $"  字幕subs={e.subsBytes:N0} bytes({e.subsN}枚"
                          + (e.subsStored > 0 && e.subsStored < e.subsBytes ? $"、格納 {e.subsStored:N0} bytes" : "") + ")"
                        : "";
                    segLines.Add($"    {sr.Id}: av.mkv={e.bytes:N0} bytes @off={e.off}{subsStr}{(use > 1 ? $"  （{use}タイトルで共有）" : "")}");
                    if (segCounted.Add(sr.Id)) // サマリー実容量は共有セグメントを1回だけ計上
                    {
                        sumAv += e.bytes;
                        sumSubStored += e.subsStored; sumSubFiles += e.subsBytes;
                        if (e.sz.ok) { anySize = true; sumVideo += e.sz.video; sumAudio += e.sz.audio; sumSub += e.sz.sub; }
                    }
                }
                }
                body.AppendLine($"タイトル{te.Id:00}  尺={dur:hh\\:mm\\:ss}  av.mkv={tBytes:N0} bytes  (セグメント{segRefCount}個{(sharedN > 0 ? $"、うち共有{sharedN}個" : "")}{(angleList.Count > 1 ? $"、アングル{angleList.Count}本" : "")})");
                foreach (var l in segLines) body.AppendLine(l);
                if (tOk)
                {
                    body.AppendLine($"  サイズ: 映像 {tv:N0} / 音声 {ta:N0} / 字幕 {ts:N0} bytes");
                    foreach (var kv in agg.OrderBy(x => x.Key.idx))
                        body.AppendLine($"    track{kv.Key.idx} {kv.Key.codec}{ChSuffix(kv.Key.ch)}{(string.IsNullOrEmpty(kv.Key.lang) ? "" : " " + kv.Key.lang)}: {kv.Value:N0} bytes");
                }
                else
                {
                    body.AppendLine("  サイズ: (NUMBER_OF_BYTES タグなし／ffprobe 不可)");
                }
            }
            else
            {
                // ── v1：タイトル直下の av.mkv（従来） ──
                long avBytes = 0;
                (long video, long audio, long sub, bool ok) sz = default;
                System.Collections.Generic.List<(int idx, string codec, int ch, string? lang, long bytes)>? tracks = null;
                long avOff = 0;
                try
                {
                    if (iso.FindEntry(KurokoLayout.TitleAvMkv(te.Id)) is KrkEntry av)
                    {
                        avBytes = av.Length;
                        avOff = av.DataOffset;
                        (sz, tracks) = ProbeStreamBytes(krkPath, avOff, av.Length, ffprobePath, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }

                sumAv += avBytes;
                body.AppendLine($"タイトル{te.Id:00}  尺={dur:hh\\:mm\\:ss}  av.mkv={avBytes:N0} bytes  @off={avOff} len={avBytes}");
                if (sz.ok)
                {
                    anySize = true;
                    sumVideo += sz.video; sumAudio += sz.audio; sumSub += sz.sub;
                    body.AppendLine($"  サイズ: 映像 {sz.video:N0} / 音声 {sz.audio:N0} / 字幕 {sz.sub:N0} bytes");
                    if (tracks is not null)
                        foreach (var tk in tracks)
                            body.AppendLine($"    track{tk.idx} {tk.codec}{ChSuffix(tk.ch)}{(string.IsNullOrEmpty(tk.lang) ? "" : " " + tk.lang)}: {tk.bytes:N0} bytes");
                }
                else
                {
                    body.AppendLine("  サイズ: (NUMBER_OF_BYTES タグなし／ffprobe 不可)");
                }
            }
            if (tj is not null)
            {
                // fields（半高格納）：Height はフィールド高。表示は全高（×2）。格納方式(interlaceMode/fieldLayout)も併記。
                bool fields = string.Equals(tj.Video.InterlaceMode, "fields", System.StringComparison.OrdinalIgnoreCase);
                string modeStr = fields
                    ? $"  格納={tj.Video.InterlaceMode}/{tj.Video.FieldLayout}(表示{tj.Video.Width}x{tj.Video.Height * 2})"
                    : (string.Equals(tj.Video.InterlaceMode, "weave", System.StringComparison.OrdinalIgnoreCase) ? "" : $"  格納={tj.Video.InterlaceMode}");
                body.AppendLine($"  映像: {tj.Video.Width}x{tj.Video.Height}  {tj.Video.Aspect}  {tj.Video.Fps}  interlaced={tj.Video.Interlaced}  tff={tj.Video.Tff}  bob区間={tj.Video.InterlacedRanges?.Count ?? 0}{modeStr}");
                // 解像度パーツ（§8.1・film×interlace 混在）：セグメントに parts があれば列挙する。
                if (tj.Segments is { Count: > 0 } partSegRefs)
                    foreach (var sr in partSegRefs)
                    {
                        var sj = TryJson<KurokoNoneSegment>(iso, KurokoLayout.SegmentJson(sr.Id));
                        if (sj?.Parts is { Count: > 0 } prts)
                            body.AppendLine($"    パーツ({sr.Id} {prts.Count}個): " + string.Join(", ",
                                prts.Select(p => $"{p.File} {tj.Video.Width}x{p.PixelHeight} {p.InterlaceMode}/{p.FieldLayout} {p.DurationMs / 1000.0:F1}s")));
                    }
                // controller="none"：seg.json の音声スパンから「論理音声(source) → 実トラックの時系列」を集める。
                // **1つの論理音声が時間で複数の実トラックに分かれる**ことがある（チャンネル数が途中で変わる素材は
                // Matroska/Opus の制約で区間ごとに別トラックへ格納し、スパンで指し分ける）。title.json は論理しか
                // 持たず、KurokoTitle.Audio は先頭スパン由来なので、これを出さないと後半の 5.1ch が見えない。
                var srcRuns = new System.Collections.Generic.Dictionary<int,
                    System.Collections.Generic.List<(int Track, string Codec, int Ch, double StartMs, double EndMs)>>();
                if (tj.Segments is { Count: > 0 } audSegRefs)
                    foreach (var sr in audSegRefs)
                    {
                        var sj = TryJson<KurokoNoneSegment>(iso, KurokoLayout.SegmentJson(sr.Id));
                        if (sj?.Audio is not { Count: > 0 } spans) continue;
                        double segOff = KurokoTime.Parse(sr.StartTime).TotalMilliseconds;
                        foreach (var sp in spans)
                            foreach (var tr in sp.Tracks)
                            {
                                if (!srcRuns.TryGetValue(tr.Source, out var l)) srcRuns[tr.Source] = l = new();
                                // 同じトラックが続くスパン（番組境界などトラックが変わらない切れ目）は1つにまとめる。
                                if (l.Count > 0 && l[^1].Track == tr.Track) { var last = l[^1]; last.EndMs = segOff + sp.EndMs; l[^1] = last; }
                                else l.Add((tr.Track, tr.Codec, Math.Max(1, tr.Channels), segOff + sp.StartMs, segOff + sp.EndMs));
                            }
                    }
                body.AppendLine($"  音声({tj.Audio.Count}): " + string.Join(", ", tj.Audio.Select(a =>
                {
                    string head = $"#{a.DvdStream}{(a.Substream >= 0 ? $"@0x{a.Substream:X2}" : "")}";
                    string lang = string.IsNullOrEmpty(a.Language) ? "" : " " + a.Language;
                    // 複数トラックに分かれている論理音声だけ、区間つきで並べる（単一なら従来どおりの表記）。
                    if (srcRuns.TryGetValue(a.Substream, out var runs) && runs.Count > 1)
                        return head + lang + " [" + string.Join(" → ", runs.Select(r =>
                            $"{r.StartMs / 1000.0:F1}s mkv{r.Track} {r.Codec} {r.Ch}ch")) + "]";
                    return $"{head} mkv{a.MkvTrack} {a.Codec} {a.Channels}ch{lang}";
                })));
                // 表示モード変種：clean は物理サブID（Phys**＝subs/img/<物理>/）、旧krkは mkvトラック（Track**）。
                static (string label, int[] v) SubVariants(KurokoSubtitle s)
                {
                    var phys = new[] { s.Phys43, s.PhysWide, s.PhysLetterbox, s.PhysPanScan };
                    return phys.Any(x => x >= 0)
                        ? ("phys", phys)
                        : ("mkv", new[] { s.Track43, s.TrackWide, s.TrackLetterbox, s.TrackPanScan });
                }
                body.AppendLine($"  字幕({tj.Subtitle.Count}): " + string.Join(", ",
                    tj.Subtitle.Select(s => { var (lbl, v) = SubVariants(s);
                        return $"#{s.DvdStream}{(string.IsNullOrEmpty(s.Language) ? "" : " " + s.Language)} [{lbl} 4:3={v[0]} wide={v[1]} LB={v[2]} P&S={v[3]}]"; })));
                totalAudio += tj.Audio.Count;
                totalSub += tj.Subtitle.Count;
                totalSubVariants += tj.Subtitle.Sum(s => SubVariants(s).v.Count(x => x >= 0));
            }
            else
            {
                body.AppendLine("  (title.json を読めません)");
            }
            body.AppendLine();
        }

        // ── メニュー（disc.json の menuDomains → 各PGCの buttons.json / video.mkv） ──────────────
        int menuDomainCount = 0, menuPgcTotal = 0, menuMediaPgc = 0, totalMenuButtons = 0;
        long sumMenuAv = 0, sumMenuVideo = 0, sumMenuAudio = 0, sumMenuSub = 0;
        bool anyMenuSize = false;
        var menuBody = new StringBuilder();
        var domains = disc?.MenuDomains ?? new System.Collections.Generic.List<KurokoMenuDomain>();
        foreach (var dom in domains)
        {
            menuDomainCount++;
            menuBody.AppendLine($"[{dom.Id}]  PGC数={dom.Pgcs.Count}");
            foreach (var mp in dom.Pgcs)
            {
                ct.ThrowIfCancellationRequested();
                menuPgcTotal++;
                int pre = mp.Commands?.Pre.Count ?? 0, post = mp.Commands?.Post.Count ?? 0, cellc = mp.Commands?.Cell.Count ?? 0;
                string typeStr = string.IsNullOrEmpty(mp.Type) ? "" : $" type={mp.Type}";
                menuBody.AppendLine($"  PGC{mp.Pgc:00}{typeStr}  PRE={pre} POST={post} CELL命令={cellc}  UOP=0x{mp.UopControl:X8}");

                // v3（セル単位ファイル：menu.json ＋ C<nn>.mkv/.spu）を優先。無ければ v2（buttons.json ＋ video.mkv）へ。
                var mv3 = TryJson<KurokoMenuV3>(iso, KurokoLayout.MenuJson(dom.Id, mp.Pgc));
                if (mv3 is not null && mv3.Cells.Count > 0)
                {
                    menuMediaPgc++;
                    int btnSum = mv3.Cells.Sum(c => c.Pci.Count == 0 ? 0 : c.Pci.Max(p => p.Buttons.Count));
                    totalMenuButtons += btnSum;
                    string stills = string.Join(",", mv3.Cells.Select(c => c.Still == -1 ? "inf" : c.Still.ToString()));
                    int spuCells = mv3.Cells.Count(c => !string.IsNullOrEmpty(c.Spu));
                    string aud = mv3.Audio.FirstOrDefault(a => a.Available)?.Codec ?? "なし";
                    menuBody.AppendLine($"    セル数={mv3.Cells.Count} ボタン計={btnSum} still=[{stills}] SPUセル={spuCells} 音声={aud} {mv3.Video.Codec} {mv3.Video.Aspect} interlaced={mv3.Video.Interlaced} tff={mv3.Video.Tff}");
                    // 各セルの C<nn>.mkv（映像+音声）を ffprobe で合算、C<nn>.spu（字幕図柄）は生バイトを字幕として計上。
                    long cellAv = 0, spuBytes = 0, cv = 0, ca = 0, cs = 0; bool cok = false;
                    foreach (var c in mv3.Cells)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (iso.FindEntry(KurokoLayout.MenuCellAv(dom.Id, mp.Pgc, c.Index)) is KrkEntry cav)
                        {
                            cellAv += cav.Length;
                            var (csz, _) = ProbeStreamBytes(krkPath, cav.DataOffset, cav.Length, ffprobePath, ct);
                            if (csz.ok) { cok = true; cv += csz.video; ca += csz.audio; cs += csz.sub; }
                        }
                        if (iso.FindEntry(KurokoLayout.MenuCellSpu(dom.Id, mp.Pgc, c.Index)) is KrkEntry cspu)
                            spuBytes += cspu.Length;
                    }
                    if (cellAv > 0 || spuBytes > 0)
                    {
                        sumMenuAv += cellAv + spuBytes;
                        menuBody.AppendLine($"    C**.mkv={cellAv:N0} ＋ C**.spu={spuBytes:N0} bytes");
                        if (cok || spuBytes > 0)
                        {
                            anyMenuSize = true; sumMenuVideo += cv; sumMenuAudio += ca; sumMenuSub += cs + spuBytes;
                            menuBody.AppendLine($"      サイズ: 映像 {cv:N0} / 音声 {ca:N0} / 字幕(SPU) {(cs + spuBytes):N0} bytes");
                        }
                    }
                }
                else
                {
                    // v2（旧 krk）：buttons.json ＋ video.mkv
                    var btns = TryJson<KurokoButtons>(iso, KurokoLayout.MenuButtonsJson(dom.Id, mp.Pgc));
                    long vmkv = 0, vOff = 0;
                    (long video, long audio, long sub, bool ok) msz = default;
                    try
                    {
                        if (iso.FindEntry(KurokoLayout.MenuVideoMkv(dom.Id, mp.Pgc)) is KrkEntry mv)
                        {
                            vmkv = mv.Length; vOff = mv.DataOffset;
                            (msz, _) = ProbeStreamBytes(krkPath, vOff, mv.Length, ffprobePath, ct);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    if (btns is not null && btns.Cells.Count > 0)
                    {
                        menuMediaPgc++;
                        int btnSum = btns.Cells.Sum(c => c.Pci?.Buttons.Count ?? 0);
                        totalMenuButtons += btnSum;
                        string stills = string.Join(",", btns.Cells.Select(c => c.Still == -1 ? "inf" : c.Still.ToString()));
                        menuBody.AppendLine($"    セル数={btns.Cells.Count} ボタン計={btnSum} still=[{stills}] 音声track={btns.AudioTrack} interlaced={btns.Interlaced} tff={btns.Tff}");
                        if (btns.HighlightSpu is { } hl)
                            menuBody.AppendLine($"    ハイライトSPU: #{hl.DvdStream} mkv{hl.MkvTrack} [4:3={hl.Track43} wide={hl.TrackWide} LB={hl.TrackLetterbox} P&S={hl.TrackPanScan}]");
                        if (vmkv > 0)
                        {
                            sumMenuAv += vmkv;
                            menuBody.AppendLine($"    video.mkv={vmkv:N0} bytes  @off={vOff}");
                            if (msz.ok)
                            {
                                anyMenuSize = true; sumMenuVideo += msz.video; sumMenuAudio += msz.audio; sumMenuSub += msz.sub;
                                menuBody.AppendLine($"      サイズ: 映像 {msz.video:N0} / 音声 {msz.audio:N0} / 字幕(SPU) {msz.sub:N0} bytes");
                            }
                        }
                    }
                    else
                    {
                        menuBody.AppendLine("    （命令のみPGC／背景なし）");
                    }
                }
            }
            menuBody.AppendLine();
        }

        sb.AppendLine("=== サマリー ===");
        // 先頭に主要容量を桁揃えで出す（ISO情報のサマリーと同じ体裁）。サイズ不明（ffprobe 不可等）は 0 表示。
        sb.AppendLine($"  タイトル 映像合計: {sumVideo,18:N0} bytes");
        sb.AppendLine($"  タイトル 音声合計: {sumAudio,18:N0} bytes");
        // 字幕の実体（subs/ の .pgs/.spu/.png）は ZIP で圧縮されるので、**実データ量と
        // 実際に .krk が占める量が大きく違う**（実測：ある krk の .pgs は 466MB → 205MB）。
        // ここは先頭の「ファイルサイズ」と突き合わせて見る場所なので、**格納量を主**にする
        // （以前は実データ量を出していたため、合計がファイルサイズを超えて見えていた）。
        long subOnDisk = sumSub - sumSubFiles + sumSubStored;
        sb.AppendLine($"  タイトル 字幕合計: {subOnDisk,18:N0} bytes  (論理字幕 {totalSub} 本"
            + (sumSubFiles > sumSubStored ? $"、subs/ は実データ {sumSubFiles:N0} bytes を圧縮して格納" : "") + ")");
        sb.AppendLine($"  メニュー 映像合計: {sumMenuVideo,18:N0} bytes");
        sb.AppendLine($"  メニュー 音声合計: {sumMenuAudio,18:N0} bytes");
        sb.AppendLine($"  メニュー 字幕合計: {sumMenuSub,18:N0} bytes");
        sb.AppendLine($"  上記の合計       : {subOnDisk + sumVideo + sumAudio + sumMenuVideo + sumMenuAudio + sumMenuSub,18:N0} bytes"
            + "  （残りは mkv/ZIP の構造情報と JSON）");
        // 注意書きは**実在する部分**についてだけ出す。メニューを持たない krk（controller none 等）で
        // anyMenuSize が立たないのは当たり前なので、それを理由に警告しない。
        if (!anySize || (menuPgcTotal > 0 && !anyMenuSize))
            sb.AppendLine("  ※ 一部サイズを取得できていません（NUMBER_OF_BYTES タグなし／ffprobe 不可）");
        sb.AppendLine();
        sb.AppendLine($"  タイトル数: {titles.Count}");
        sb.AppendLine($"  音声合計: {totalAudio} 本");
        sb.AppendLine($"  字幕合計: {totalSub} 本（アスペクト別の物理変種 {totalSubVariants} 本）");
        bool anyShared = segUse.Values.Any(v => v > 1);
        sb.AppendLine($"  av.mkv 合計: {sumAv:N0} bytes{(anyShared ? "（共有セグメントは1回のみ計上）" : "")}");
        if (segUse.Count > 0)
            sb.AppendLine($"  セグメント数: {segUse.Count}（うち共有 {segUse.Values.Count(v => v > 1)}）");
        sb.AppendLine($"  メニュードメイン数: {menuDomainCount}");
        sb.AppendLine($"  メニューPGC数: {menuPgcTotal}（うち背景実体あり {menuMediaPgc}）");
        sb.AppendLine($"  メニューボタン合計: {totalMenuButtons} 個");
        sb.AppendLine($"  メニュー video.mkv 合計: {sumMenuAv:N0} bytes");
        sb.AppendLine();
        sb.AppendLine("=== タイトル一覧 ===");
        sb.Append(body);
        sb.AppendLine();
        sb.AppendLine("=== メニュー一覧 ===");
        sb.Append(menuBody);
        return sb.ToString();
    }

    // 一覧表示用のサマリーのみ（レポート文字列・トラック明細を作らない軽量版）。サイズは Analyze と同じ導出
    //（av.mkv の NUMBER_OF_BYTES タグを ffprobe で読む。v2 は共有セグメントを1回のみ計上）。
    public static MediaInfoSummary AnalyzeSummary(
        string krkPath, string? ffprobePath, CancellationToken ct)
    {
        using var iso = KurokoContainerReader.Open(krkPath);
        using var src = SafeOpen(krkPath);
        var disc = src?.Disc ?? TryJson<KurokoDisc>(iso, KurokoLayout.DiscJson);
        var titles = disc?.Titles ?? new System.Collections.Generic.List<KurokoTitleEntry>();

        long tV = 0, tA = 0, tS = 0;
        string vcodec = "-", acodec = "-";
        bool codecTaken = false;
        var counted = new System.Collections.Generic.HashSet<string>(); // 共有セグメントは1回のみ計上
        void AddEntry(KrkEntry av)
        {
            var (sz, _) = ProbeStreamBytes(krkPath, av.DataOffset, av.Length, ffprobePath, ct);
            if (sz.ok) { tV += sz.video; tA += sz.audio; tS += sz.sub; }
        }
        foreach (var te in titles)
        {
            ct.ThrowIfCancellationRequested();
            var tj = ReadTitle(src, iso, te.Id);
            if (!codecTaken && tj is not null)
            {
                codecTaken = true;
                vcodec = tj.Video.Codec?.Trim().ToLowerInvariant() switch
                {
                    "h265" => "H.265",
                    "av1" => "AV1",
                    null or "" or "h264" => "H.264",
                    var other => other,
                };
                acodec = tj.Audio.Count > 0 ? KrkAudioName(tj.Audio[0].Codec) : "-";
            }
            if (tj?.Segments is { Count: > 0 } srs)
            {
                foreach (var sr in srs)
                    if (counted.Add(sr.Id))
                    {
                        // 解像度パーツ（§8.1）があれば av.pNN.mkv を合算、無ければ av.mkv。
                        var sj = TryJson<KurokoNoneSegment>(iso, KurokoLayout.SegmentJson(sr.Id));
                        if (sj?.Parts is { Count: > 0 } prts)
                        {
                            foreach (var p in prts)
                                if (iso.FindEntry($"{KurokoLayout.SegmentDir(sr.Id)}/{p.File}") is KrkEntry pav) AddEntry(pav);
                        }
                        else if (iso.FindEntry(KurokoLayout.SegmentAvMkv(sr.Id)) is KrkEntry avs) AddEntry(avs);
                        tS += iso.SumBytesUnder(KurokoLayout.SubsDir(sr.Id), out _); // §11：字幕は subs/（PNG）
                    }
            }
            else if (iso.FindEntry(KurokoLayout.TitleAvMkv(te.Id)) is KrkEntry av) // v1
            {
                AddEntry(av);
            }
        }

        int menuMedia = 0;
        long mV = 0, mA = 0, mS = 0;
        foreach (var dom in disc?.MenuDomains ?? new System.Collections.Generic.List<KurokoMenuDomain>())
            foreach (var mp in dom.Pgcs)
            {
                ct.ThrowIfCancellationRequested();
                // v3（menu.json ＋ C<nn>.mkv/.spu）を優先。無ければ v2（video.mkv）。
                var mv3 = TryJson<KurokoMenuV3>(iso, KurokoLayout.MenuJson(dom.Id, mp.Pgc));
                if (mv3 is not null && mv3.Cells.Count > 0)
                {
                    menuMedia++;
                    foreach (var c in mv3.Cells)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (iso.FindEntry(KurokoLayout.MenuCellAv(dom.Id, mp.Pgc, c.Index)) is KrkEntry cav)
                        {
                            var (sz, _) = ProbeStreamBytes(krkPath, cav.DataOffset, cav.Length, ffprobePath, ct);
                            if (sz.ok) { mV += sz.video; mA += sz.audio; mS += sz.sub; }
                        }
                        if (iso.FindEntry(KurokoLayout.MenuCellSpu(dom.Id, mp.Pgc, c.Index)) is KrkEntry cspu)
                            mS += cspu.Length;
                    }
                }
                else if (iso.FindEntry(KurokoLayout.MenuVideoMkv(dom.Id, mp.Pgc)) is KrkEntry mv) // v2
                {
                    menuMedia++;
                    var (sz, _) = ProbeStreamBytes(krkPath, mv.DataOffset, mv.Length, ffprobePath, ct);
                    if (sz.ok) { mV += sz.video; mA += sz.audio; mS += sz.sub; }
                }
            }

        return new MediaInfoSummary(
            Format: "krk", VideoCodec: vcodec, AudioCodec: acodec,
            FileSize: new FileInfo(krkPath).Length, TitleCount: titles.Count,
            TitleVideo: tV, TitleAudio: tA, TitleSub: tS,
            MenuCount: menuMedia, MenuVideo: mV, MenuAudio: mA, MenuSub: mS);
    }

    private static string KrkAudioName(string codec) => codec.ToLowerInvariant() switch
    {
        "opus" => "Opus",
        "ac3" => "AC-3",
        "dts" => "DTS",
        "pcm" => "LPCM",
        "mp2" => "MPEG音声",
        _ => codec,
    };

    // av.mkv の .krk 内バイト範囲を subfile プロトコルで ffprobe に渡し、各ストリームの
    // codec_type と NUMBER_OF_BYTES タグを読んで映像/音声/字幕別に合算する。
    // 音声のチャンネル数の表記（" 2ch"）。映像/字幕（ch=0）は空。
    private static string ChSuffix(int ch) => ch > 0 ? $" {ch}ch" : "";

    private static ((long video, long audio, long sub, bool ok), System.Collections.Generic.List<(int idx, string codec, int ch, string? lang, long bytes)>?) ProbeStreamBytes(
        string krkPath, long start, long length, string? ffprobePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffprobePath)) return (default, null);
        string url = $"subfile,,start,{start},end,{start + length},,:{krkPath}";
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=index,codec_type,codec_name,channels:stream_tags=NUMBER_OF_BYTES,language");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);

        Process? p;
        try { p = Process.Start(psi); }
        catch { return (default, null); }
        if (p is null) return (default, null);
        using (p)
        {
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } return (default, null); }
            ct.ThrowIfCancellationRequested();

            var list = new System.Collections.Generic.List<(int idx, string codec, int ch, string? lang, long bytes)>();
            // タグが無くても後段（パケット合算）で使えるよう、ストリームの素性は常に控える。
            var streams = new System.Collections.Generic.List<(int idx, string type, string name, int ch, string? lang)>();
            long video = 0, audio = 0, sub = 0; bool ok = false;
            int idx = -1; string type = "", name = ""; string? lang = null; long bytes = -1; int ch = 0;
            void Flush()
            {
                if (idx >= 0) streams.Add((idx, type, name, ch, lang));
                if (idx >= 0 && bytes >= 0)
                {
                    if (type == "video") video += bytes;
                    else if (type == "audio") audio += bytes;
                    else if (type == "subtitle") sub += bytes;
                    list.Add((idx, string.IsNullOrEmpty(name) ? type : name, type == "audio" ? ch : 0, lang, bytes));
                    ok = true;
                }
                idx = -1; type = ""; name = ""; lang = null; bytes = -1; ch = 0;
            }
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("index=")) { Flush(); int.TryParse(line.Substring("index=".Length), out idx); }
                else if (line.StartsWith("codec_type=")) type = line.Substring("codec_type=".Length);
                else if (line.StartsWith("codec_name=")) name = line.Substring("codec_name=".Length);
                else if (line.StartsWith("channels=")) int.TryParse(line.Substring("channels=".Length), out ch);
                else if (line.StartsWith("TAG:language=")) lang = line.Substring("TAG:language=".Length);
                else if (line.StartsWith("TAG:NUMBER_OF_BYTES="))
                    long.TryParse(line.Substring("TAG:NUMBER_OF_BYTES=".Length), out bytes);
            }
            Flush();
            // mkvmerge の統計タグが無い mkv（**ffmpeg が直接書いたもの**。BD 変換は mkvmerge を
            // 通さないのでこちら）は、ここまででサイズが 1 つも取れない。その場合だけ第2段として
            // **パケットのサイズを実際に合算**する。全パケットを読むので遅い（実測 1.2GB で約10秒）が、
            // タグがある krk では走らないので普段の速さは変わらない。
            if (!ok && list.Count == 0 && streams.Count > 0)
            {
                var byIdx = SumPacketBytes(url, ffprobePath, ct);
                if (byIdx is not null)
                    foreach (var st in streams)
                    {
                        if (!byIdx.TryGetValue(st.idx, out long b)) continue;
                        if (st.type == "video") video += b;
                        else if (st.type == "audio") audio += b;
                        else if (st.type == "subtitle") sub += b;
                        list.Add((st.idx, string.IsNullOrEmpty(st.name) ? st.type : st.name,
                                  st.type == "audio" ? st.ch : 0, st.lang, b));
                        ok = true;
                    }
            }
            return ((video, audio, sub, ok), list);
        }
    }

    // ストリーム別のパケットサイズ合計。ffprobe に全パケットを列挙させて足し合わせる。
    // 失敗（起動不可・打ち切り）は null。
    private static System.Collections.Generic.Dictionary<int, long>? SumPacketBytes(
        string url, string ffprobePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("packet=stream_index,size");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("csv=p=0");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);

        Process? p;
        try { p = Process.Start(psi); }
        catch { return null; }
        if (p is null) return null;
        using (p)
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
        {
            // 出力が数百万行になるので溜め込まず1行ずつ足す。stderr は別スレッドで捨てて詰まりを防ぐ。
            var errTask = p.StandardError.ReadToEndAsync();
            var agg = new System.Collections.Generic.Dictionary<int, long>();
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (!int.TryParse(line.AsSpan(0, comma), out int idx)) continue;
                var rest = line.AsSpan(comma + 1);
                int end = rest.IndexOf(',');
                if (end >= 0) rest = rest[..end];
                if (!long.TryParse(rest, out long size) || size < 0) continue;
                agg[idx] = agg.TryGetValue(idx, out var acc) ? acc + size : size;
            }
            p.WaitForExit();
            try { errTask.GetAwaiter().GetResult(); } catch { }
            ct.ThrowIfCancellationRequested();
            return agg.Count > 0 ? agg : null;
        }
    }

    // .krk を KurokoTitleSource で開く。Kuroko でない/controller 未対応等で開けなければ null（旧来の直接読みへ）。
    private static KurokoTitleSource? SafeOpen(string krkPath)
    {
        try { return KurokoTitleSource.Open(krkPath); } catch { return null; }
    }

    // 形式（clean=dvd-video-vm v>=3／none／旧 vm・v1）を吸収してタイトルを内部モデル KurokoTitle で読む。
    // src（KurokoTitleSource）が使えればそれに委ね、駄目なら旧来どおり title.json を直接デシリアライズする。
    private static KurokoTitle? ReadTitle(KurokoTitleSource? src, KurokoContainerReader iso, int titleId)
    {
        if (src is not null)
            try { return src.ReadTitle(titleId); } catch { }
        try { return KurokoJson.Deserialize<KurokoTitle>(ReadFile(iso, KurokoLayout.TitleJson(titleId))); }
        catch { return null; }
    }

    private static T? TryJson<T>(KurokoContainerReader iso, string path) where T : class
    {
        try { return KurokoJson.Deserialize<T>(ReadFile(iso, path)); }
        catch { return null; }
    }

    private static byte[] ReadFile(KurokoContainerReader iso, string path)
        => iso.FindEntry(path) is KrkEntry f ? iso.ReadAllBytes(f)
            : throw new FileNotFoundException($"{path} が .krk 内に見つかりません。");
}
