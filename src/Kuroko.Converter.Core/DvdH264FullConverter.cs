using System.Diagnostics;
using System.IO;
using System.Text;
using Kuroko.Media.Krk;

namespace Kuroko.Converter;

// DVD-Video（ISO）を Kuroko（.krk・controller="dvd-video-vm"）へ変換する（ConvertToKuroko）。
// タイトルは共有 run 単位のセグメントへ分けて H.264／H.265／AV1 で符号化し、ケイデンス（RFF・インターレース）を
// タイムスタンプとメタデータで保つ。メニューはセル単位のファイル（menu.json・C<nn>.mkv・C<nn>.spu）へ、
// VM の命令（First Play・PRE/POST/セル命令・ボタン命令）は JSON へそのまま運ぶ。字幕は SPU の生データで持つ。
internal static class DvdH264FullConverter
{
    private const int Sector = 2048;

    // ── Kuroko（.krk）出力（v2・docs §13）：タイトルを「セグメント（共有run）のプレイリスト」に統一する。
    //    共有run を dedup し、各セグメントを1回だけ av.mkv＋seg.json 化（/SEGMENTS）、title.json はプレイリスト＋
    //    セル(segment/segCell参照)。共有が無いタイトルは自動的に1セグメント（＝従来の単一 av.mkv 相当・継ぎ目なし）。
    public static string ConvertToKuroko(string srcIso, string outKrk, DvdH264Converter.Options opt,
        IProgress<DvdH264Converter.Progress>? progress = null, System.Threading.CancellationToken ct = default)
    {
        var log = new StringBuilder();
        // 暗号化（CSS）されたままの DVD は断る（解除はしない）。
        try { if (EncryptedInputCheck.CheckDvdIso(srcIso) is { } enc) return "ERROR: " + enc.Message("この変換器", "変換"); }
        catch (Exception ex) { log.AppendLine($"暗号化の確認に失敗（続ける）: {ex.Message}"); }
        string work = Path.Combine(Path.GetTempPath(), "vuecove-krk-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);
        try
        {
            using var disc = DvdDisc.Open(srcIso);
            bool interlaced = string.Equals(opt.Deinterlace, "none", StringComparison.OrdinalIgnoreCase);
            bool recompress = string.Equals(opt.AudioMode, "recompress", StringComparison.OrdinalIgnoreCase);

            var writer = new KurokoZipWriter();

            // 共有run へ分割（物理同一 dedup）。共有が無いタイトルは1セグメントになる。
            // アングル（§10.1 angleType:"dvd"）もここで決まる＝マニフェストの version より先に作る。
            var plan = KurokoSegmentPlanner.Plan(disc);
            bool hasAngles = plan.TitleAngleCount.Values.Any(n => n > 1);

            // clean 形式（dvd-video-vm）で出力する。レイアウト version 5（公開仕様 1.0：番号は 1 始まり・セルの位置は
            // 0 始まり・時刻は ms・パレットは 16 進の文字列・メニューの種類は名前・言語は 3 文字）。リーダは 3・4 も読む。
            var manifest = KurokoDiscBuilder.BuildManifest("Kuroko Converter (DVD)");
            manifest.Version = KurokoLayout.CleanLayoutVersion;
            manifest.Comment = OptionsComment(opt); // 符号化パラメータ（インターレース方式/CRF/キーフレーム間隔 等）
            writer.AddBytes(KurokoLayout.ManifestFile, KurokoJson.Serialize(manifest));
            // disc.json は KurokoDisc（＝KurokoVmDisc とキー一致）なので現行 BuildDisc の出力をそのまま使う。
            writer.AddBytes(KurokoLayout.DiscJson, KurokoJson.Serialize(KurokoDiscBuilder.BuildDisc(disc)));

            log.AppendLine($"krk src: titles={disc.Titles.Count} segments={plan.Segments.Count} angles={(hasAngles ? "あり" : "なし")} version={manifest.Version} crf={opt.Crf} preset={opt.Preset} deint={opt.Deinterlace} audio={opt.AudioMode}");

            // 各セグメントを1回だけ符号化 → /SEGMENTS/<id>/{av.mkv, seg.json}。
            var segById = new Dictionary<string, KurokoSegment>();
            int segTotal = Math.Max(1, plan.Segments.Count), segDone = 0;
            // 進捗の重み＝変換元バイト数（セクタ範囲から即算出・読み込み無し）。セグメント数の等割りだと
            // 長短が混ざるジョブで長いセグメント中に進捗が止まって見えるため、バイト比で進める。
            var segSrcBytes = new long[plan.Segments.Count];
            long totalSrcBytes = 0;
            for (int i = 0; i < plan.Segments.Count; i++)
            {
                try
                {
                    var s0 = plan.Segments[i];
                    using var st = disc.OpenTitle(s0.SourceTitle).OpenStream(s0.SourceCells[0], s0.SourceCells[^1]);
                    segSrcBytes[i] = Math.Max(1, st.Length);
                }
                catch { segSrcBytes[i] = 1; }
                totalSrcBytes += segSrcBytes[i];
            }
            long srcBytesDone = 0;
            // 診断：VUECOVE_KRK_ONLY_SEGMENTS=S0016,S0017 で指定セグメントだけ符号化する（不具合の切り分け用。
            // 出力 krk は不完全になる）。VUECOVE_KRK_KEEP_WORK=1 で作業フォルダを残す（パーツの中間 mkv を調べる）。
            var onlySegs = Environment.GetEnvironmentVariable("VUECOVE_KRK_ONLY_SEGMENTS")?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var s in plan.Segments)
            {
                ct.ThrowIfCancellationRequested();
                if (onlySegs is { Count: > 0 } && !onlySegs.Contains(s.Id))
                {
                    srcBytesDone += segSrcBytes[segDone]; segDone++;
                    continue;
                }
                int si = segDone;
                // セグメント内進捗（0..1＋フェーズ文言）→ 全体進捗（バイト重み・0..0.90）＋状況表示へ写像。
                double segBase = (double)srcBytesDone / totalSrcBytes;
                double segSpan = (double)segSrcBytes[si] / totalSrcBytes;
                void SegReport(double f, string phase) => progress?.Report(new DvdH264Converter.Progress(
                    Math.Clamp(0.90 * (segBase + segSpan * Math.Clamp(f, 0, 1)), 0, 0.9),
                    $"セグメント {si + 1}/{segTotal}（{s.Id}）: {phase}"));
                SegReport(0, "準備中…");
                var tp = disc.OpenTitle(s.SourceTitle);
                int a = s.SourceCells[0], b = s.SourceCells[^1];
                var res = EncodeSegment(tp, a, b, opt, interlaced, recompress, work, s.Id, log, ct, SegReport, s.SourceAngle);
                srcBytesDone += segSrcBytes[si];
                if (res is null) { log.AppendLine($"segment {s.Id}: 符号化失敗（スキップ）"); segDone++; continue; }
                // clean seg.json（KurokoNoneSegment）へ写して出力する（リーダの AdaptSegment が読む形）。
                writer.AddBytes(KurokoLayout.SegmentJson(s.Id), KurokoJson.Serialize(ToNoneSegment(res.Seg)));
                if (res.PartCleanMkvs.Count > 0)
                    for (int pi = 0; pi < res.PartCleanMkvs.Count; pi++)
                        writer.AddFile(KurokoLayout.SegmentPartMkv(s.Id, pi), res.PartCleanMkvs[pi]);
                else
                    writer.AddFile(KurokoLayout.SegmentAvMkv(s.Id), res.CleanMkv);
                foreach (var (p, d) in res.SubsFiles) writer.AddBytes(p, d); // 字幕：subs/img/<物理>/（§11）
                // ラウドネス・ゲインエンベロープ：loudness/<source>.json（§9.1）。点数が多くなり得るので seg.json とは別ファイル。
                foreach (var la in res.Seg.Audio)
                    if (la.LoudnessEnvelopes is { } env)
                        writer.AddBytes(KurokoLayout.SegmentLoudness(s.Id, la.Substream), KurokoJson.Serialize(env));
                segById[s.Id] = res.Seg;
                long segBytes = res.PartCleanMkvs.Count > 0
                    ? res.PartCleanMkvs.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0)
                    : (!string.IsNullOrEmpty(res.CleanMkv) && File.Exists(res.CleanMkv) ? new FileInfo(res.CleanMkv).Length : 0);
                log.AppendLine($"segment {s.Id}: cells={s.Keys.Count} src=T{s.SourceTitle}[{a}..{b}] dur={res.Seg.Timing.TotalMs / 1000.0:F1}s audio={res.Seg.Audio.Count} sub={res.Seg.Subtitle.Count} mkv={segBytes}B parts={res.PartCleanMkvs.Count} usedBy=[{string.Join(",", s.UsedByTitles.Select(x => "T" + x))}]");
                segDone++;
            }

            // タイトルごとに v2 title.json（プレイリスト＋セル参照＋タイトル通し時刻）。
            foreach (var title in disc.Titles)
            {
                ct.ThrowIfCancellationRequested();
                var tp = disc.OpenTitle(title.TitleNumber);
                if (tp.CellCount == 0) continue;
                // アングル（§10.1 angleType:"dvd"）ごとの参照列。1 本でも欠けていればこのタイトルは出さない
                //（半端なアングルを作るより、タイトルごと落として制限として記録するほうが扱いやすい）。
                int angleCount = plan.AngleCountOf(title.TitleNumber);
                var refsByAngle = new List<List<KurokoPlannedRef>>();
                for (int a = 1; a <= angleCount; a++)
                {
                    var ra = plan.PlaylistOf(title.TitleNumber, a);
                    if (ra is null || ra.Count == 0) { refsByAngle.Clear(); break; }
                    refsByAngle.Add(ra);
                }
                if (refsByAngle.Count == 0) continue;
                if (refsByAngle.Any(rr => rr.Any(r => !segById.ContainsKey(r.SegmentId))))
                { log.AppendLine($"title{title.TitleNumber}: 参照セグメント欠落のためスキップ"); continue; }
                // アングル1でタイトルの時間軸（セル開始時刻・章・総尺・bob区間）を作る。アングル2以降は
                // 「同じ時間軸へ別のセグメントを差し込む」だけ（ブロック内の各アングルの尺は規格上等しい）。
                var refs = refsByAngle[0];

                var kt = KurokoDiscBuilder.BuildTitleSkeleton(tp);
                kt.Video.Interlaced = interlaced;
                kt.Video.Codec = KurokoVideoCodec(opt);
                kt.Segments.Clear();
                double titleOffMs = 0;
                KurokoSegment? firstSeg = null; // Tff は先頭セグメントの実測に合わせる
                var titleRanges = new List<KurokoRange>(); // bob区間をタイトル通し時刻へ集約
                foreach (var r in refs)
                {
                    var seg = segById[r.SegmentId];
                    kt.Segments.Add(new KurokoSegmentRef
                    {
                        Id = r.SegmentId,
                        TitleCellStart = r.TitleCellStart,
                        CellCount = r.CellCount,
                        StartTime = KurokoTime.Format(TimeSpan.FromMilliseconds(titleOffMs)),
                    });
                    for (int k = 0; k < r.CellCount; k++)
                    {
                        int tc = r.TitleCellStart + k;
                        if (tc < 0 || tc >= kt.Cells.Count) continue;
                        kt.Cells[tc].Segment = r.SegmentId;
                        kt.Cells[tc].SegCell = k;
                        double segCellMs = k < seg.Cells.Count ? KurokoTime.Parse(seg.Cells[k].StartInSegment).TotalMilliseconds : 0;
                        kt.Cells[tc].StartTime = KurokoTime.Format(TimeSpan.FromMilliseconds(titleOffMs + segCellMs));
                    }
                    // セグメントの bob 区間（0基準）をタイトル通し時刻へずらして集約。末尾フレームが実インターレース
                    // だと EndMs≒totalMs+端数となり、次セグメント先頭フレームを呑み込んで「フィルム先頭1枚を誤 bob」する。
                    // 再生側 demux は各セグメント開始を整数msへ丸めるため、totalMs だけのクランプでは端数（例：92976 <
                    // 92976.2166）で先頭を拾ってしまう。丸めた境界の 0.5ms 手前まで区間終端をクランプして確実に除外する。
                    if (seg.Video.InterlacedRanges is { } rr)
                    {
                        double nextBoundary = Math.Round(titleOffMs + seg.Timing.TotalMs);
                        foreach (var rg in rr)
                            titleRanges.Add(new KurokoRange
                            {
                                StartMs = rg.StartMs + titleOffMs,
                                EndMs = Math.Min(rg.EndMs + titleOffMs, nextBoundary - 0.5),
                            });
                    }
                    firstSeg ??= seg;
                    titleOffMs += seg.Timing.TotalMs;
                }
                kt.Video.InterlacedRanges = titleRanges.Count > 0 ? titleRanges : null;
                if (firstSeg is not null) kt.Video.Tff = firstSeg.Video.Tff;

                // ── アングル（§10.1 angleType:"dvd"）────────────────────────────────────
                // cells[] は PGC セルと 1:1 のまま全アングルぶん載せる（LinkCN・章・プログラムが参照する
                // セル番号を壊さないため）。アングルごとに違うのは playlist だけ。
                if (angleCount > 1)
                {
                    var pgcA = tp.Pgc;
                    // アングルブロックの兄弟セルは「同じ区間の別アングル」なので、ブロック先頭（アングル1の
                    // セル）の開始時刻を共有する。あわせて各セルのアングル番号を記録する。
                    for (int c = 0; c < kt.Cells.Count; )
                    {
                        var (ordinal, blockSize) = pgcA.AngleOrdinalOfCell(c);
                        if (ordinal == 0 || blockSize <= 0) { c++; continue; }
                        int n = Math.Min(blockSize, kt.Cells.Count - c);
                        for (int j = 0; j < n; j++)
                        {
                            kt.Cells[c + j].Angle = j + 1;
                            if (j > 0) kt.Cells[c + j].StartTime = kt.Cells[c].StartTime;
                        }
                        c += n;
                    }
                    kt.AngleType = "dvd";
                    kt.Angles.Clear();
                    for (int a = 1; a <= angleCount; a++)
                    {
                        var ang = new KurokoTitleAngle { Number = a };
                        foreach (var r in refsByAngle[a - 1])
                        {
                            // プレイリストの開始時刻は**共通の時間軸**（アングル1で作ったセル開始時刻）から採る。
                            // アングルごとに実測尺を積み直すと、符号化の端数だけアングル間で時間軸がずれる。
                            string st = r.TitleCellStart >= 0 && r.TitleCellStart < kt.Cells.Count
                                ? kt.Cells[r.TitleCellStart].StartTime : "00:00:00";
                            ang.Segments.Add(new KurokoSegmentRef
                            {
                                Id = r.SegmentId,
                                TitleCellStart = r.TitleCellStart,
                                CellCount = r.CellCount,
                                StartTime = st,
                            });
                            // このアングルが再生するセルへ実体を割り当てる（共有セルは全アングルで同じ値になる）。
                            for (int k = 0; k < r.CellCount; k++)
                            {
                                int tc = r.TitleCellStart + k;
                                if (tc < 0 || tc >= kt.Cells.Count) continue;
                                kt.Cells[tc].Segment = r.SegmentId;
                                kt.Cells[tc].SegCell = k;
                            }
                        }
                        kt.Angles.Add(ang);
                    }
                    kt.Segments.Clear(); // Angles と Segments は排他（§10.1）
                }

                // title.json の audio/subtitle＝「このタイトルの論理→物理」（自PGCの AST_CTL/SPST_CTL 由来。§13.4）。
                // mkvTrack は正準トラック番号（映像0、音声1..nA を論理順、以降字幕を物理ID昇順）。セグメント実体
                //（物理→実トラック）へは再生側が substream/phys** で写像する。共有セグメントはタイトルごとに
                // AST_CTL/SPST_CTL が違っても正しく解決される（旧実装の「代表セグメントからコピー」は他タイトルの
                // 論理対応が混入し得たため廃止）。
                // **再符号化するときだけ** Opus。コピー指定なら元のコーデック/チャンネル数がそのまま残る。
                bool opus = recompress && string.Equals(opt.AudioCodec, "opus", StringComparison.OrdinalIgnoreCase);
                // 実体の有無は**全アングル**のセグメントで見る（アングル2にしか無い音声/字幕を落とさない）。
                var segsOfTitle = refsByAngle.SelectMany(rr => rr).Select(r => segById[r.SegmentId]).Distinct().ToList();
                var audioPhys = segsOfTitle.SelectMany(s => s.Audio.Select(a => a.Substream)).Where(x => x >= 0).ToHashSet();
                var subPhys = segsOfTitle.SelectMany(s => s.Subtitle.Select(x => x.Substream)).Where(x => x >= 0).ToHashSet();
                kt.Audio.Clear();
                for (int i = 0; i < tp.AudioStreams.Count; i++)
                {
                    int phys = AudioPhysId(tp, i, out int astNum);
                    if (phys < 0 || !audioPhys.Contains(phys)) continue; // セグメントに実体の無い宣言は載せない
                    var a = tp.AudioStreams[i];
                    kt.Audio.Add(new KurokoAudio
                    {
                        DvdStream = i,
                        Substream = phys,
                        AstCtl = astNum,
                        MkvTrack = 1 + kt.Audio.Count,
                        Codec = opus ? "opus" : recompress ? "ac3" : KurokoAudioCodec(a.CodingMode),
                        Channels = recompress ? OutAudioChannels(opt, opus, a.Channels) : a.Channels,
                        Language = a.HasLanguage ? a.LanguageCode : null,
                    });
                }
                kt.Subtitle.Clear();
                var subPlanT = BuildSubpicturePlan(tp);
                var usedSubPhys = subPlanT.SelectMany(s => new[] { s.Id43, s.IdWide, s.IdLb, s.IdPs })
                    .Where(id => id >= 0x20 && subPhys.Contains(id)).Distinct().OrderBy(x => x).ToList();
                int subBase = 1 + kt.Audio.Count;
                int CanonSub(int phys) => phys >= 0x20 && usedSubPhys.IndexOf(phys) is int k and >= 0 ? subBase + k : -1;
                static int FirstValid(params int[] xs) { foreach (var x in xs) if (x >= 0) return x; return -1; }
                bool wideT = tp.VideoAttr.AspectRatio == "16:9";
                foreach (var s in subPlanT)
                {
                    int p43 = subPhys.Contains(s.Id43) ? s.Id43 : -1, pW = subPhys.Contains(s.IdWide) ? s.IdWide : -1;
                    int pLb = subPhys.Contains(s.IdLb) ? s.IdLb : -1, pPs = subPhys.Contains(s.IdPs) ? s.IdPs : -1;
                    int t43 = CanonSub(p43), tW = CanonSub(pW), tLb = CanonSub(pLb), tPs = CanonSub(pPs);
                    int def = wideT ? FirstValid(tW, t43, tLb, tPs) : FirstValid(t43, tW, tLb, tPs);
                    if (def < 0) continue; // どの変種も実体なし
                    kt.Subtitle.Add(new KurokoSubtitle
                    {
                        DvdStream = s.Logical,
                        Language = s.Lang,
                        SpstCtl = p43 >= 0x20 ? p43 - 0x20 : -1,
                        Phys43 = p43, PhysWide = pW, PhysLetterbox = pLb, PhysPanScan = pPs,
                        Track43 = t43, TrackWide = tW, TrackLetterbox = tLb, TrackPanScan = tPs,
                        MkvTrack = def,
                    });
                }
                foreach (var ch in kt.Chapters)
                    if (ch.StartCell >= 0 && ch.StartCell < kt.Cells.Count) ch.StartTime = kt.Cells[ch.StartCell].StartTime;
                kt.Timing = new KurokoTiming { FrameRate = kt.Video.Fps, TotalMs = titleOffMs };

                // clean title.json（KurokoVmTitle・純論理）へ写して出力する（リーダの AdaptVmTitle が読む形）。
                writer.AddBytes(KurokoLayout.TitleJson(title.TitleNumber), KurokoJson.Serialize(ToVmTitle(kt)));
                log.AppendLine($"title{title.TitleNumber}: segments={refs.Count} cells={tp.CellCount} dur={titleOffMs / 1000.0:F1}s angles={angleCount} playlist=[{string.Join(" -> ", refs.Select(r => r.SegmentId))}]");
                for (int a = 2; a <= angleCount; a++)
                    log.AppendLine($"title{title.TitleNumber} angle{a}: playlist=[{string.Join(" -> ", refsByAngle[a - 1].Select(r => r.SegmentId))}]");
            }

            // メニュー実体（M1b-2）：各メニュードメインの各PGCの背景 video.mkv／buttons.json／palette.json。
            progress?.Report(new DvdH264Converter.Progress(0.90, "メニューを生成中…"));
            WriteKurokoMenus(disc, writer, opt, work, log, ct, progress);

            progress?.Report(new DvdH264Converter.Progress(1.0, ".krk を書き出し中…"));
            writer.Write(outKrk);
            log.AppendLine($"done: {outKrk} size={new FileInfo(outKrk).Length}");
            return log.ToString();
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("VUECOVE_KRK_KEEP_WORK") == "1") log.AppendLine($"作業フォルダを残した: {work}");
            else try { Directory.Delete(work, true); } catch { }
        }
    }

    // ── clean（dvd-video-vm・version>=3）への写像 ─────────────────────────────
    // 旧内部モデル（KurokoTitle / KurokoSegment）を組み立てた後、リーダ（KurokoTitleSource.AdaptVmTitle /
    // AdaptSegment）が復元できる clean モデル（KurokoVmTitle / KurokoNoneSegment）へ**同じ内容で写す**。
    // 内容は不変、キー・時刻表現（"c"文字列→ms(double)）だけを clean 化する。

    // 旧 KurokoTitle（DVD 由来の内部骨格）→ clean KurokoVmTitle（純論理）。
    private static KurokoVmTitle ToVmTitle(KurokoTitle kt)
    {
        double Ms(string c) => KurokoTime.Parse(c).TotalMilliseconds;
        var vt = new KurokoVmTitle
        {
            Palette = kt.Palette,
            UopControl = kt.UopControl,
            PgcCommands = kt.PgcCommands,
            Timing = new KurokoVmTiming
            {
                TotalMs = kt.Timing.TotalMs,
                FrameRate = kt.Timing.FrameRate,
                CellBaseFrame = kt.Timing.CellBaseFrame,
                ChapterFrame = kt.Timing.ChapterFrame,
            },
        };
        // 論理音声：Number=論理番号 / Source=物理サブストリームID / Language / AstCtl。
        // codec/channels/正準MkvTrack は seg 側にあるので title には出さない。
        foreach (var a in kt.Audio)
            vt.Audio.Add(new KurokoVmAudioLogical
            {
                Number = a.DvdStream + 1, // version 5：1 始まり（DVD の論理音声番号＋1）
                Source = a.Substream,
                Language = KurokoLanguage.Normalize(a.Language), // version 5：ISO 639-2 の 3 文字
                AstCtl = a.AstCtl,
            });
        // 論理字幕：Number / Source（代表物理ID） / SpstCtl / Language / Modes（表示モード別の物理ID）。
        // 正準 Track** は seg で解決するので title に出さない。
        foreach (var s in kt.Subtitle)
            vt.Subtitle.Add(new KurokoVmSubtitleLogical
            {
                Number = s.DvdStream + 1, // version 5：1 始まり
                Source = s.Substream,
                SpstCtl = s.SpstCtl,
                Language = KurokoLanguage.Normalize(s.Language),
                Modes = new KurokoVmSubModes
                {
                    Normal = s.Phys43,
                    Wide = s.PhysWide,
                    Letterbox = s.PhysLetterbox,
                    PanScan = s.PhysPanScan,
                },
            });
        foreach (var ch in kt.Chapters)
            vt.Chapters.Add(new KurokoVmChapter { Number = ch.Number, StartCell = ch.StartCell, StartMs = Ms(ch.StartTime) });
        static KurokoVmPlaylistItem ToVmPlaylistItem(KurokoSegmentRef r, System.Func<string, double> ms) => new KurokoVmPlaylistItem
        {
            Seg = r.Id,
            TitleCellStart = r.TitleCellStart,
            CellCount = r.CellCount,
            StartMs = ms(r.StartTime),
        };
        foreach (var r in kt.Segments) vt.Playlist.Add(ToVmPlaylistItem(r, Ms));
        // アングル（§10.1 angleType:"dvd"）。Angles があるときは Playlist は空（排他）。
        vt.AngleType = kt.AngleType;
        foreach (var a in kt.Angles)
            vt.Angles.Add(new KurokoVmAngle
            {
                Number = a.Number,
                Name = a.Name,
                Playlist = a.Segments.Select(r => ToVmPlaylistItem(r, Ms)).ToList(),
            });
        // プログラムの開始セル：version 5 は 0 始まり（内部の KurokoProgram は IFO と同じ 1 始まり）。
        foreach (var p in kt.Programs) vt.Programs.Add(new KurokoProgram { Pgn = p.Pgn, EntryCell = p.EntryCell - 1 });
        foreach (var c in kt.Cells)
            vt.Cells.Add(new KurokoVmCell
            {
                Index = c.Index,
                StartMs = Ms(c.StartTime),
                Still = c.Still,
                CommandNr = c.CommandNr,
                Seg = c.Segment,
                SegCell = c.SegCell,
                Angle = c.Angle,
            });
        return vt;
    }

    // 旧 KurokoSegment（DVD 由来のフラット）→ clean KurokoNoneSegment（メディア権威。DVD は none 土台を流用）。
    private static KurokoNoneSegment ToNoneSegment(KurokoSegment seg)
    {
        var v = seg.Video;
        var ns = new KurokoNoneSegment
        {
            Video = new KurokoNoneVideo
            {
                Codec = v.Codec,
                Mode = "transcode",
                PixelWidth = v.Width,
                PixelHeight = v.Height,
                // 表示画素（DAR 適用後）。専用の算出値は無いので符号化画素を流用する（リーダは PixelWidth/Height だけ使う）。
                DisplayWidth = v.Width,
                DisplayHeight = v.Height,
                Aspect = v.Aspect == "16:9" ? "16:9" : "4:3",
                FrameRate = v.Fps,
                Cadence = v.Interlaced ? "interlaced" : "progressive",
                Tff = v.Tff,
                InterlaceMode = v.InterlaceMode,
                FieldLayout = v.FieldLayout,
                InterlacedRanges = v.InterlacedRanges,
            },
            Timing = new KurokoNoneTiming { TotalMs = seg.Timing.TotalMs },
        };
        // DVD は構成一定なので音声は単一スパン [0..TotalMs]。各トラック＝旧 seg.audio（物理→実トラック）。
        var span = new KurokoNoneAudioSpan { StartMs = 0, EndMs = seg.Timing.TotalMs };
        foreach (var a in seg.Audio)
            span.Tracks.Add(new KurokoNoneAudioTrack
            {
                Track = a.MkvTrack,
                Source = a.Substream,
                Codec = a.Codec,
                Channels = a.Channels,
                DualMono = false,
                Name = null,
                Language = KurokoLanguage.Normalize(a.Language),
                // ラウドネス・メタデータ（§9.1）。メディアに帰属するので seg.json（物理トラック）側が権威。
                // エンベロープ実体は loudness/<source>.json（別ファイル）で、ここは参照のみ。
                Loudness = a.Loudness,
            });
        ns.Audio.Add(span);
        // 字幕（DVD サブピクチャ）は画像型。物理ID(source)→実トラック(track)。
        foreach (var s in seg.Subtitle)
            ns.Subtitle.Add(new KurokoNoneSubtitle { Source = s.Substream, Track = s.MkvTrack, Type = "image" });
        // メディア解像度パーツ（§8 fields×film 混在）。
        if (seg.Parts is { Count: > 0 })
            ns.Parts = seg.Parts.Select(p => new KurokoNonePart
            {
                File = p.File, DurationMs = p.DurationMs,
                PixelHeight = p.PixelHeight, InterlaceMode = p.InterlaceMode, FieldLayout = p.FieldLayout,
                Still = p.Still,
            }).ToList();
        return ns;
    }

    // 結果＝符号化済みセグメント（clean av.mkv パス＋seg.json モデル）。
    private sealed class SegEncodeResult
    {
        public string CleanMkv = "";                 // 単一 av.mkv（parts 無し時）
        public List<string> PartCleanMkvs = new();   // 解像度パーツ（av.pNN.mkv 順）。非空なら CleanMkv は使わない
        public KurokoSegment Seg = new();
        // 字幕（§11）：subs/img/<物理>/index.json＋PNG を krk へ書くための (krkパス, バイト列) 列。
        public List<(string Path, byte[] Data)> SubsFiles = new();
    }

    // 末尾ラン拡張で次セル先頭から読み足す上限バイト数。音声流れ込みは実測 ~250ms（多重化遅延）なので、
    // 4MiB（SD の VOB で数秒分）あれば十分に覆える。
    private const long TailPeekBytes = 4 << 20;

    // 複数 Stream を順に読む連結ストリーム（読み取り専用・順次）。セグメント末尾ラン拡張の
    // 「セル列＋次セル先頭のみ」入力に使う。所有権を持ち、Dispose で各ストリームを破棄する。
    private sealed class ConcatStream : Stream
    {
        private readonly Stream[] _parts;
        private int _idx;
        public ConcatStream(params Stream[] parts) { _parts = parts; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_idx < _parts.Length)
            {
                int n = _parts[_idx].Read(buffer, offset, count);
                if (n > 0) return n;
                _idx++;
            }
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var p in _parts) { try { p.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // 先頭 cap バイトだけ読める制限ストリーム（読み取り専用・順次）。所有権を持つ。
    // PS の全パックの SCR と各 PES の PTS/DTS に delta(90kHz, 符号付き) を加算しながら読み出すストリーム。
    // 書き換えの中身は RewriteTimestamps と同じで、こちらはファイルではなく読み出しの途中で当てる。
    // 用途：PTSリセットを跨ぐ「次セルの読み足し」を、現在のランと**時刻の上で地続き**にしてから ffmpeg へ渡す。
    // ffmpeg は連結しても時刻を振り直さないので（実測：38.239733 → 0.059667 のまま）、ここで揃えないと
    // atrim が届かず、セル末尾の音声が丸ごと落ちる。
    private sealed class PtsShiftStream : Stream
    {
        private const long Mask = 0x1FFFFFFFFL; // 33bit
        private readonly Stream _inner;
        private readonly long _delta90k;
        private readonly byte[] _sector = new byte[Sector];
        private int _have, _taken;

        public PtsShiftStream(Stream inner, long delta90k) { _inner = inner; _delta90k = delta90k; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_taken >= _have)
            {
                // 1セクタ単位で読み込む（DVD の PS はセクタ境界＝パック境界）。
                int read = 0, n;
                while (read < Sector && (n = _inner.Read(_sector, read, Sector - read)) > 0) read += n;
                if (read <= 0) return 0;
                _have = read; _taken = 0;
                ShiftSector(_sector, read, _delta90k);
            }
            int take = Math.Min(count, _have - _taken);
            Buffer.BlockCopy(_sector, _taken, buffer, offset, take);
            _taken += take;
            return take;
        }

        private static void ShiftSector(byte[] b, int len, long delta90k)
        {
            if (delta90k == 0) return;
            if (len < 14 || b[0] != 0 || b[1] != 0 || b[2] != 1 || b[3] != 0xBA) return; // pack_header でなければ触らない
            WriteScr(b, 4, (ReadScr(b, 4) + delta90k) & Mask);
            int off = 14 + (b[13] & 0x07);
            if (off + 6 <= len && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1 && b[off + 3] == 0xBB)
                off += 6 + ((b[off + 4] << 8) | b[off + 5]);
            while (off + 9 <= len && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1)
            {
                int sid = b[off + 3];
                int pesLen = (b[off + 4] << 8) | b[off + 5];
                int pesEnd = pesLen == 0 ? len : Math.Min(len, off + 6 + pesLen);
                if (sid == 0xBE || sid == 0xBF) { off = pesEnd; continue; } // padding / NAV(private_2)
                int ptsDts = (b[off + 7] >> 6) & 0x03;
                int q = off + 9;
                if ((ptsDts & 0x02) != 0 && q + 5 <= len) WriteTs(b, q, (ReadTs(b, q) + delta90k) & Mask);
                if ((ptsDts & 0x01) != 0 && q + 10 <= len) WriteTs(b, q + 5, (ReadTs(b, q + 5) + delta90k) & Mask);
                off = pesEnd;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    // 1セルの PS から音声（a:0）の最初/最後の PTS と 1 フレームの時間（90kHz）を測る。
    // PTSリセット境界で「このランの音声の続き」になるシフト量を出すのに使う。
    private static (long First90k, long Last90k, long FrameDur90k) ProbeAudioPts(
        string ffprobe, Stream cellPs, System.Threading.CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffprobe, "-v error -select_streams a:0 -show_entries packet=pts -of csv=p=0 -i pipe:0")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var feeder = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var buf = new byte[1 << 16]; int n; var stdin = p.StandardInput.BaseStream;
                while ((n = cellPs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    stdin.Write(buf, 0, n);
                }
                stdin.Flush();
            }
            catch { }
            finally { try { p.StandardInput.Close(); } catch { } }
        });
        long first = -1, last = -1;
        var deltas = new List<long>();
        string? line;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            if (!long.TryParse(line.Replace("\r", "").TrimEnd(',').Trim(), out var v)) continue;
            if (first < 0) first = v;
            if (last >= 0 && v > last) deltas.Add(v - last);
            if (v > last) last = v;
        }
        try { p.WaitForExit(); } catch { }
        try { feeder.Wait(1000); } catch { }
        long dur = 0;
        if (deltas.Count > 0) { deltas.Sort(); dur = deltas[deltas.Count / 2]; } // 中央値（外れ値に強い）
        return (first, last, dur);
    }

    // 複数のストリームを順に読む（ConcatStream の N 本版）。遅延で開くので、同時に開くのは1本だけ。
    private sealed class MultiConcatStream : Stream
    {
        private readonly List<Func<Stream>> _open;
        private int _i = -1;
        private Stream? _cur;

        public MultiConcatStream(List<Func<Stream>> open) { _open = open; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                if (_cur is null)
                {
                    if (++_i >= _open.Count) return 0;
                    _cur = _open[_i]();
                }
                int n = _cur.Read(buffer, offset, count);
                if (n > 0) return n;
                _cur.Dispose();
                _cur = null;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (disposing) { try { _cur?.Dispose(); } catch { } _cur = null; } base.Dispose(disposing); }
    }

    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private long _remain;
        public LimitedStream(Stream inner, long cap) { _inner = inner; _remain = cap; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remain <= 0) return 0;
            int n = _inner.Read(buffer, offset, (int)Math.Min(count, _remain));
            _remain -= n;
            return n;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _inner.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // 1セグメント（ソースタイトル tp のセル範囲 [cellStart..cellEnd]）を符号化して av.mkv＋seg.json を作る。
    // ロジックは旧 per-title 符号化と同一（ラン分割・IDR強制・RffFieldTime焼き込み・字幕写像）で、範囲と 0基準時刻に限定。
    // 符号化した中間 mkv の**音声の先頭**が 0 をカバーしているか測り、ずれていれば mkvmerge の --sync で戻す。
    // 音声は atrim=start=（パーツ先頭のソース秒）で切り、-output_ts_offset でそこが 0 になるので、正しい出力は
    // **先頭パケットが 0 以下**（Opus は pre-skip ぶん -6.5ms）になる。ところが matroska muxer は映像の先頭 DTS
    // 次第でタイムスタンプを正規化し、**音声だけを 0 より後ろへ押し出す**ことがある。
    // 実測（ある市販ディスク）：cell0/cell2/cell4 は -6.5ms のままだが、cell6 だけ +9.5ms になる。muxer のずらしを止めて
    // （-avoid_negative_ts disabled）測ると音声はどのセルでも -6.5ms で同一＝符号化側に差は無く、分岐しているのは
    // 符号化後の映像の先頭 DTS（cell0/4=0.010 に対し cell6=0.017。PTSリセット直後の VOB 先頭で GOP の
    // 立ち上がりが違う）。音声が 0 より後ろに出ると**セグメント境界にその分の穴**が空き、再生側が無音で埋めるため
    // 音が途切れる（実測 9.5ms）。ここで実測して打ち消す。映像には触らない（映像の時刻は --timestamps で焼き直す）。
    // expectedStartMs＝**事前スキャンから求めた「出力の音声先頭があるべき時刻」**。
    // 音声は atrim=start=（パーツ先頭のソース秒）で切り、-output_ts_offset でそこが 0 になるので、
    //   ・パーツ先頭より前にソース音声がある → あるべき先頭は 0（Opus は pre-skip ぶん負になる）
    //   ・パーツ先頭より後にしかソース音声が無い → あるべき先頭は「その差」＝正の値
    // となる。実測がこれより後ろなら、その差はすべて muxer のずらしなので戻す。
    // 大きさで足切りはしない（「何 ms までなら muxer のせい」という根拠の無いしきい値を置かないため）。
    private static string AudioSyncArgs(string ffprobe, string mkvPath, int firstAudioTrack, int audioCount,
        double expectedStartMs, StringBuilder log, string tag, System.Threading.CancellationToken ct)
    {
        if (audioCount <= 0) return "";
        double firstMs = ProbeFirstAudioPtsMs(ffprobe, mkvPath, ct);
        if (double.IsNaN(firstMs))
        {
            log.AppendLine($"{tag}: 音声先頭の測定に失敗（--sync なし）");
            return "";
        }
        double excess = firstMs - expectedStartMs;
        if (excess <= 0.5) return ""; // あるべき位置にある＝正常。触らない
        int shift = (int)Math.Round(excess);
        var sb = new StringBuilder();
        for (int i = 0; i < audioCount; i++) sb.Append($"--sync {firstAudioTrack + i}:{-shift} ");
        log.AppendLine($"{tag}: 音声先頭 +{firstMs:F1}ms（あるべき位置 {expectedStartMs:F1}ms）"
            + $" → muxer のずらし {excess:F1}ms を mkvmerge --sync {-shift}ms で戻す");
        return sb.ToString();
    }

    // 事前スキャン：このランのソース音声が最初に現れる時刻（秒）。取れなければ NaN。
    // 先頭側だけ読めば足りるので LimitedStream で頭を少しだけ渡す。
    private static double ProbeSourceFirstAudioSec(string ffprobe, DvdTitlePlayback tp, int cellFirstIdx, int cellLastIdx,
        System.Threading.CancellationToken ct)
    {
        try
        {
            using var ps = new LimitedStream(tp.OpenStream(cellFirstIdx, cellLastIdx), 4 << 20);
            long first = ProbeAudioPts(ffprobe, ps, ct).First90k;
            return first >= 0 ? first / 90000.0 : double.NaN;
        }
        catch { return double.NaN; }
    }

    // 出力の音声先頭があるべき時刻(ms)。ソース音声がパーツ先頭より後にしか無いときだけ正になる。
    // 事前スキャンが取れなかった場合は 0（＝ずれは全て muxer のせいとみなす）。DVD のセルは音声が
    // 映像より先行して格納されているのが普通なので、実際にはほぼ常に 0 になる。
    private static double ExpectedAudioStartMs(double srcFirstAudioSec, double partStartSec)
        => double.IsNaN(srcFirstAudioSec) ? 0 : Math.Max(0, (srcFirstAudioSec - partStartSec) * 1000.0);

    // mkv の音声(a:0)の先頭パケット時刻(ms)。取れなければ NaN。先頭 1 パケットだけ読む。
    private static double ProbeFirstAudioPtsMs(string ffprobe, string mkvPath, System.Threading.CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -select_streams a:0 -read_intervals \"%+#1\" -show_entries packet=pts_time -of csv=p=0 -i \"{mkvPath}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            string? line = p.StandardOutput.ReadLine();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (line is null) return double.NaN;
            line = line.Replace("\r", "").TrimEnd(',').Trim();
            return double.TryParse(line, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v * 1000.0 : double.NaN;
        }
        catch { return double.NaN; }
    }

    // 末尾読み足しの入力。PTSリセットを跨ぐとき（shift!=0）は時刻をずらして地続きにする。
    private static Stream TailSource(DvdTitlePlayback tp, int cellIndex, long shift90k)
        => shift90k == 0 ? tp.OpenStream(cellIndex, cellIndex)
                         : new PtsShiftStream(tp.OpenStream(cellIndex, cellIndex), shift90k);

    // ランの末尾で「次のセル」から音声を読み足すための計画。Next90k <= 0 なら読み足さない。
    // DVD はセル末尾の音声が次セルの先頭に入っているので（多重化の遅延 ~150-250ms）、ランの終わりで
    // 次セルの頭を読み足さないとその分が丸ごと欠ける。**セグメントの最後のランだけでなく、セグメント内部の
    // ラン境界（PTSリセット）にも要る**：内部境界で読み足していなかったため、そこに音声の穴が空いていた
    //（実測：ある krk タイトル1の cell3→cell4＝0:07:05.5 で 144ms。再生側がそこを無音で埋めるので、
    //  大きな音が鳴っている箇所だと途切れ＋ノイズとして聞こえる）。
    // 次セルが別の時刻基準で始まる（PTSリセット）ときは、ffmpeg が連結しても時刻を振り直さないため、
    // 読み足す PS の時刻を**このランの音声の続き**になるようシフトしてから渡す（Shift90k / PtsShiftStream）。
    // シフト量は音声の連続性で決める：shift =（このランの最終音声PTS + 1フレーム）− 次セルの先頭音声PTS。
    // 映像側の境界を基準にすると数十ms重なったり空いたりする（実測：ある市販ディスク cell4→cell6 で 83ms）。
    private static (long Next90k, long Shift90k) PlanTailExtend(
        DvdTitlePlayback tp, DvdH264Converter.Options opt, int lastCell, int nextCell, long runLastVideo90k,
        StringBuilder log, string idTag, System.Threading.CancellationToken ct)
    {
        if (nextCell >= tp.CellCount || runLastVideo90k < 0) return (-1, 0);
        long next90k = -1;
        try
        {
            using var pn = new LimitedStream(tp.OpenStream(nextCell, nextCell), TailPeekBytes);
            next90k = ProbeCellRff(opt.FfprobePath, pn, ct).FirstPts90k;
        }
        catch (Exception ex) { log.AppendLine($"seg {idTag}: 次セル先頭probe失敗（末尾拡張なし）: {ex.Message}"); }
        if (next90k < 0) return (-1, 0);
        if (next90k > runLastVideo90k) return (next90k, 0);   // 時刻が地続き＝そのまま読み足せる

        // next <= 現ラン末尾＝PTSリセット。音声の連続性でシフト量を決めて地続きにする。
        try
        {
            using var pc = tp.OpenStream(lastCell, lastCell);
            var cur = ProbeAudioPts(opt.FfprobePath, pc, ct);
            using var pn2 = new LimitedStream(tp.OpenStream(nextCell, nextCell), 1 << 20);
            var nxt = ProbeAudioPts(opt.FfprobePath, pn2, ct);
            if (cur.Last90k >= 0 && cur.FrameDur90k > 0 && nxt.First90k >= 0)
            {
                long shift = cur.Last90k + cur.FrameDur90k - nxt.First90k;
                long shifted = next90k + shift;
                // シフト後も「次セル先頭映像 > 現ラン末尾映像」でなければ拡張しない（安全側）。
                if (shifted > runLastVideo90k)
                {
                    log.AppendLine($"seg {idTag}: PTSリセット境界の末尾音声を読み足す cell{lastCell}→cell{nextCell} "
                        + $"curLastA={cur.Last90k} frameDur={cur.FrameDur90k} nextFirstA={nxt.First90k} "
                        + $"shift={shift}({shift / 90000.0:F3}s) 拾う長さ={(next90k - nxt.First90k) / 90000.0:F3}s");
                    return (shifted, shift);
                }
            }
        }
        catch (Exception ex) { log.AppendLine($"seg {idTag}: リセット境界の音声probe失敗（末尾拡張なし）: {ex.Message}"); }
        return (-1, 0);
    }

    // 指定アングルで cell の**次に再生される**セル index（無ければ CellCount）。アングルブロックのセルは
    // 同じ区間の別アングルが物理的に隣り合って並ぶので、単純な +1 では別アングルのセルに当たる。
    private static int NextPlayedCell(DvdTitlePlayback tp, int cell, int angle)
    {
        for (int i = cell + 1; i < tp.CellCount; i++)
            if (tp.Pgc.IsCellForAngle(i, angle)) return i;
        return tp.CellCount;
    }

    // angle＝このセグメントのセルが属するアングル番号（1始まり。0＝アングルブロックの外）。
    // 末尾の音声読み足しで「次に再生されるセル」を求めるのに使う。
    private static SegEncodeResult? EncodeSegment(DvdTitlePlayback tp, int cellStart, int cellEnd,
        DvdH264Converter.Options opt, bool interlaced, bool recompress, string work, string idTag,
        StringBuilder log, System.Threading.CancellationToken ct,
        Action<double, string>? segProgress = null, int angle = 0)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int rangeCount = cellEnd - cellStart + 1;

        // セグメント内進捗（0..1）＋フェーズ文言。呼び出し側が全体進捗（バイト重み）と状況表示へ写像する。
        // フェーズ配分は時間の実勢に合わせた概算重み（単調に滑らかに進むことが目的で厳密さは不要）：
        //   0.00-0.05 準備（フィールド順/fields判定/音声プラン） 0.05-0.18 RFFプローブ
        //   0.18-0.95 フレーム情報解析＋符号化 0.95-1.00 remux/PTS焼き直し・字幕・ラウドネス
        long segSrcBytes;
        using (var sb0 = tp.OpenStream(cellStart, cellEnd)) segSrcBytes = Math.Max(1, sb0.Length);
        void SP(double f, string t) => segProgress?.Invoke(Math.Clamp(f, 0, 1), t);
        const double PPrepEnd = 0.05, PProbeEnd = 0.18, PEncEnd = 0.95;

        bool tff = true;
        if (interlaced)
        {
            SP(0.005, "フィールド順判定中…");
            using (var psd = tp.OpenStream(cellStart, cellEnd))
                tff = DvdH264Converter.DetectTopFieldFirstFromStream(opt.FfprobePath, psd, ct, out _, out _);
        }

        // インターレース方式＝fields の判定（案B）。フレーム毎に「fields対象＝インターレース(prog=false)かつ fld==2」を数え、
        //  ・全フレームが fields対象 → fieldSep=true（セグメント全体を半高・倍フレームの単一 av.mkv）。
        //  ・fields対象と非対象（24p/RFF）が混在 → mixedParts=true（ケイデンス連続区間ごとに解像度パーツへ分割）。
        //  ・fields対象が無い（全 progressive/film）→ どちらも false（従来 weave 単一）。
        bool fieldsSel = interlaced && string.Equals(opt.InterlaceMode, "fields", StringComparison.OrdinalIgnoreCase);
        bool fieldSep = false, mixedParts = false;
        if (fieldsSel)
        {
            using (var psFs = tp.OpenStream(cellStart, cellEnd))
            {
                var pf = ProbeRunFrames(opt.FfprobePath, psFs, ct,
                    fed => SP(0.01 + 0.035 * Math.Min(1.0, (double)fed / segSrcBytes), $"fields判定中 {(int)(100.0 * Math.Min(1.0, (double)fed / segSrcBytes))}%"));
                var elig = ComputeFieldsEligible(pf.Progressive, pf.Fields, opt.FieldsMinInterlaceFrames);
                int nfe = 0, nother = 0, nrawCombed = 0;
                for (int k = 0; k < pf.Progressive.Count; k++)
                {
                    if (elig[k]) nfe++; else nother++;
                    if (!pf.Progressive[k] && pf.Fields[k] == 2) nrawCombed++;
                }
                fieldSep = nfe > 0 && nother == 0;
                mixedParts = nfe > 0 && nother > 0;
                log.AppendLine($"seg {idTag}: interlaceMode=fields 判定 frames={pf.Progressive.Count} fieldsEligible={nfe}(rawCombed={nrawCombed}) other={nother} → {(fieldSep ? "fields(半高・倍フレーム 単一)" : mixedParts ? "mixed(ケイデンス・パーツ分割)" : "weave フォールバック（テレシネ/全プログレッシブ）")}");
            }
        }
        // 一律 fields（fieldSep）は dual＝2映像トラック（top/bottom、各元フレームレート・同一PTS）で格納する。TS変換器と同形式で、
        // 再生・demux は seg.Video.FieldLayout=="dual" を見て bottom を第2デコーダへ振り分ける（parity=トラック番号で反転しない）。
        // 混在（mixedParts）はパーツ毎に単一トラック fields のまま（dual化は将来課題）。
        bool dualFields = fieldSep;

        SP(PPrepEnd - 0.005, "音声プラン解析中…");
        List<AudioPlan> audioPlan;
        using (var psA = tp.OpenStream(cellStart, cellStart))
            audioPlan = BuildAudioPlan(opt.FfprobePath, psA, recompress, opt.AudioBitrateK, ct, log, opt.AudioCodec, DownmixToStereo(opt));
        if (audioPlan.Count == 0 && rangeCount > 1)
            using (var psAll = tp.OpenStream(cellStart, cellEnd))
                audioPlan = BuildAudioPlan(opt.FfprobePath, psAll, recompress, opt.AudioBitrateK, ct, log, opt.AudioCodec, DownmixToStereo(opt));
        // §13.3：セグメントのトラックは「物理」（タイトル非依存）。VOB に実在する全音声を物理ID昇順で収録する
        //（BuildAudioPlan は OrigId 昇順）。論理番号（AST_CTL はタイトルPGCごとの表）は title.json 側で解決する。
        var physAttr = IfoAudioByPhys(tp); // 物理ID→(論理,属性)。言語等の参考メタ用

        var subPlan = BuildSubpicturePlan(tp);
        string? LangForPhys(int id) => subPlan
            .Where(s => s.Id43 == id || s.IdWide == id || s.IdLb == id || s.IdPs == id).Select(s => s.Lang).FirstOrDefault();
        var subSpus = new Dictionary<int, List<(long Pts90k, byte[] Data)>>();

        var cellFirst = new long[rangeCount];
        var cellLast = new long[rangeCount];
        var cellRun = new int[rangeCount];
        var cellFrames = new int[rangeCount];   // 各セルのソース実フレーム数（all-intra 判定用）
        var cellFieldSum = new int[rangeCount];  // 各セルの実フィールド数合計（RFF込み。疎セル判定＝映像の実表示尺）
        var runStarts = new List<int>();
        long prevLast = -1;
        long probeDoneBytes = 0;
        for (int lc = 0; lc < rangeCount; lc++)
        {
            ct.ThrowIfCancellationRequested();
            long first, last;
            using (var ps2 = tp.OpenStream(cellStart + lc, cellStart + lc))
            {
                long cellLen = Math.Max(1, ps2.Length);
                var r = ProbeCellRff(opt.FfprobePath, ps2, ct, fed =>
                {
                    double d = Math.Min(1.0, (probeDoneBytes + Math.Min(fed, cellLen)) / (double)segSrcBytes);
                    SP(PPrepEnd + (PProbeEnd - PPrepEnd) * d, $"RFFプローブ中 {(int)(100 * d)}%");
                });
                first = r.FirstPts90k; last = r.LastPts90k; cellFrames[lc] = r.FieldCounts.Count; cellFieldSum[lc] = r.FieldCounts.Sum(fc => fc & 0x7F);
                probeDoneBytes += cellLen;
            }
            cellFirst[lc] = first; cellLast[lc] = last;
            bool reset = prevLast >= 0 && first >= 0 && first < prevLast - 90000;
            if (lc == 0 || reset) runStarts.Add(lc);
            cellRun[lc] = runStarts.Count - 1;
            if (last >= 0) prevLast = last;
        }
        runStarts.Add(rangeCount);
        int runCount = runStarts.Count - 1;

        // 疎セル（静止画セル）の検出：IFO のセル尺が「映像フレームが張る概算尺」より明らかに大きい＝映像が疎。
        // ある市販ディスク T4 のように「映像1枚＋音声数秒」のセルが該当。ラン分割は映像PTSの後退でしか見ないため、静止画
        // セル（映像が毎回ほぼ同PTSへリセット＝後退にならない）を1ランにまとめてしまい、フレーム時間で焼くと
        // 映像も音声も先頭へ潰れる。疎セルを検出したら、後段で「各セルを durationMs=IFO尺のパーツ」として
        // 個別符号化し、再生側の既存パーツ連結 demux に載せる（映像・音声とも各セル時刻に並ぶ）。
        double fieldDurMsDet = tp.VideoAttr.IsPal ? 20.0 : 1001.0 / 60000.0 * 1000.0;
        var cellSparse = new bool[rangeCount];
        bool cellPartsMode = false;
        for (int lc = 0; lc < rangeCount; lc++)
        {
            double ifoMs = tp.Pgc.Cells[cellStart + lc].Duration.TotalMilliseconds;
            // 映像の実表示尺＝実フィールド数合計×フィールド長（RFF/3:2プルダウン込み）。フレーム数×2 だと RFF セルを
            // 過小評価して通常セルまで疎誤判定するため、必ず実フィールド数で見る。
            double videoMs = cellFieldSum[lc] * fieldDurMsDet;
            cellSparse[lc] = ifoMs > videoMs + 1000.0; // IFO尺が実映像尺より1秒超大きい＝映像が疎
            if (cellSparse[lc]) cellPartsMode = true;
        }

        // セグメント末尾ラン拡張の境界時刻：セル末尾の音声は DVD 多重化の遅延（実測 ~250ms）により
        // **次セルの先頭に流れ込んで**格納されている。セグメントをここで切ると末尾の音声が欠け、境界通過時に
        // 音声が先行する（次セグメント側の頭は atrim で自セグメント分だけを持つ）。次のタイトルセルが
        // PTS 連続なら、その先頭を TailPeekBytes だけ読み足して「境界までの音声」を採り、映像は境界でトリムする。
        // ラン内部のセル境界は連続読みなので元々途切れない。PTSリセット（別ラン相当）や最終セルでは拡張しない。
        // 「次のセル」はアングルを考慮して求める。アングルブロックのセルの**物理的な隣は同じ区間の別アングル**
        // なので、そのまま cellEnd+1 を見ると「次セル先頭PTS ≦ 現ラン末尾PTS」となって末尾拡張が働かず、
        // セル末尾の音声（DVD 多重化の遅延で次セルの先頭に入っている ~150-250ms）が丸ごと欠ける。
        // アングルブロック外のセグメント（angle=0）は、続くアングルブロックの先頭＝アングル1のセルが
        // 物理的な隣なので 1 として探す。
        int nextGlobal = NextPlayedCell(tp, cellEnd, angle >= 1 ? angle : 1);
        var segTail = PlanTailExtend(tp, opt, cellEnd, nextGlobal, cellLast[rangeCount - 1], log, idTag, ct);

        var aLangs = audioPlan.Select(p =>
            physAttr.TryGetValue(p.OrigId, out var pa) && pa.Attr.HasLanguage ? pa.Attr.LanguageCode : null).ToList();

        double fieldDurMs = tp.VideoAttr.IsPal ? 20.0 : 1001.0 / 60000.0 * 1000.0;
        var runFiles = new List<string>();                    // 非mixed：これらを concat して単一 av.mkv
        // 音声を通しで符号化するための、ラン毎の (セル範囲, 累積シフト)。PTSリセットのたびに
        // 「そのランの音声が前のランの続きになる」量を足し込み、セグメント全体を 1 本の時間軸にする。
        var audioRuns = new List<(int CellFirst, int CellLast, long Shift90k)>();
        long audioCumShift90k = 0;
        var parts = new List<(string CleanMkv, double DurMs, bool Fields, bool Still)>(); // mixed（案B）：clean 済みのパーツ（順序）。Still=疎セル
        var runBaseSec = new double[runCount];
        double seg0SrcAudioFirstSec = double.NaN;   // 先頭ランのソース音声開始（セグメント全体の --sync 判定に使う）
        var tsAllMs = new List<double>();
        var progAll = new List<bool>();
        // 元フレーム毎の source PTS と segment-through clean ms（mixed のセル時刻算出に使う。セルはパーツと非整合なため）。
        var frameSrcPtsG = new List<long>();
        var frameCleanG = new List<double>();
        // 各ランが frameSrcPtsG/frameCleanG のどこから始まるか（PTSリセットで source PTS がラン間で重複するため、
        // セル時刻は「そのセルが属するランのフレームスライス内だけ」で source PTS 一致を探す必要がある）。
        var runFrameStart = new List<int>();
        double cum = 0;
        bool anyFieldsPart = false;
        var cellPartStartSec = new double[rangeCount]; // cellPartsMode：各セルのセグメント通し開始秒（durationMs 累積）

        if (cellPartsMode)
        {
            // 疎セル（静止画セル）を含むセグメント：各セルを durationMs=IFO尺(疎)/映像尺(通常) の**パーツ**として
            // 個別符号化する。各セルを単独で符号化するので、セル境界の音声PTSリセットが原理的に消える（パーツ内は
            // 0基準・連続、再生側 demux が durationMs 累積でオフセット）。映像も各セル時刻に並ぶ。パーツ機構(seg.Parts)は
            // 元々 fields×film 混在用だが、再生側は durationMs で連結再生できるので流用する（再生エンジンの改修不要）。
            // per-cell 符号化の作法は WriteMenuPgc と同型（末尾音声読み足し・all-intra・remux・PTS焼き直し）。
            mixedParts = true;
            double partCum = 0;
            long encDoneBytesC = 0; // 進捗：符号化済みセルの元バイト累計
            for (int lc = 0; lc < rangeCount; lc++)
            {
                ct.ThrowIfCancellationRequested();
                cellPartStartSec[lc] = partCum;
                var cell = tp.Pgc.Cells[cellStart + lc];
                long cellLenC; using (var mLen = tp.OpenStream(cellStart + lc, cellStart + lc)) cellLenC = Math.Max(1, mLen.Length);
                long cFirst = cellFirst[lc] >= 0 ? cellFirst[lc] : 0;
                bool still = cell.StillTime != 0 || cellFrames[lc] <= 1; // 単フレーム/静止=all-intra（cutree劣化回避）

                // セル末尾の音声読み足し（多重化遅延で次セル先頭へ流れ込む分。EncodeSegment/WriteMenuPgc と同型）。
                double trimEndSec = 0;
                if (lc + 1 < rangeCount && cellLast[lc] >= 0)
                {
                    try
                    {
                        using var pn = new LimitedStream(tp.OpenStream(cellStart + lc + 1, cellStart + lc + 1), TailPeekBytes);
                        long next90 = ProbeCellRff(opt.FfprobePath, pn, ct).FirstPts90k;
                        if (next90 > cellLast[lc]) trimEndSec = next90 / 90000.0;
                    }
                    catch (Exception ex) { log.AppendLine($"seg {idTag} cell{lc}: 次セル先頭probe失敗（末尾拡張なし）: {ex.Message}"); }
                }
                bool tailExtend = trimEndSec > 0;
                // 音声末尾は、このパーツの**宣言尺**で切る（疎セルは IFO 尺、通常セルは実フィールド時間）。
                // 次セル先頭PTS（＝ソースPTS軸）と共用すると、テレシネぶん／疎セルぶん音声が合わなくなる。
                double cellDeclMs = cellSparse[lc] ? cell.Duration.TotalMilliseconds : cellFieldSum[lc] * fieldDurMs;
                double aTrimEndC = cFirst / 90000.0 + cellDeclMs / 1000.0;

                string rawMkv = Path.Combine(work, $"{idTag}_cp{lc}_raw.mkv");
                string cargs = BuildKurokoFfmpegArgs(opt, tp.VideoAttr, false, tff, rawMkv,
                    -cFirst / 90000.0, audioPlan, aLangs, System.Array.Empty<int>(), System.Array.Empty<string?>(), null,
                    allIntra: still, videoTrimEndSec: trimEndSec, audioTrimEndSec: aTrimEndC);
                using (var ps = tailExtend
                    ? (Stream)new ConcatStream(tp.OpenStream(cellStart + lc, cellStart + lc),
                                               new LimitedStream(tp.OpenStream(cellStart + lc + 1, cellStart + lc + 1), TailPeekBytes))
                    : tp.OpenStream(cellStart + lc, cellStart + lc))
                {
                    int rc = RunEncode(opt.FfmpegPath, cargs, ps, out string ce, ct, fed =>
                    {
                        double d = Math.Min(1.0, (encDoneBytesC + Math.Min(fed, cellLenC)) / (double)segSrcBytes);
                        SP(PProbeEnd + (PEncEnd - PProbeEnd) * d, $"符号化中（セル {lc + 1}/{rangeCount}） {(int)(100 * d)}%");
                    });
                    if (!File.Exists(rawMkv) || new FileInfo(rawMkv).Length == 0)
                    { log.AppendLine($"seg {idTag} cell{lc}: 符号化失敗 rc={rc}\n{Tail(ce)}"); return null; }
                }

                // フィールド時間で0起点クリーンPTSを作り、mkvmerge で焼き直す。
                List<byte> fldc; List<long> srcPtsc;
                using (var psR = tp.OpenStream(cellStart + lc, cellStart + lc))
                { var pr = ProbeRunFrames(opt.FfprobePath, psR, ct); fldc = pr.Fields; srcPtsc = pr.Pts90k; }
                int nEncC = ProbeVideoFrameCount(opt.FfprobePath, rawMkv);
                NoteFrameCountMismatch(log, $"seg {idTag} cell{lc}(符号化)", fldc.Count, nEncC);
                if (nEncC > 0)
                {
                    while (fldc.Count < nEncC) { fldc.Add(fldc.Count > 0 ? fldc[^1] : (byte)2); srcPtsc.Add(srcPtsc.Count > 0 ? srcPtsc[^1] : 0); }
                    if (fldc.Count > nEncC) { fldc.RemoveRange(nEncC, fldc.Count - nEncC); srcPtsc.RemoveRange(nEncC, srcPtsc.Count - nEncC); }
                }
                var frameCleanC = new List<double>(fldc.Count);
                double tMsC = 0;
                for (int i = 0; i < fldc.Count; i++) { frameCleanC.Add(tMsC); tMsC += fldc[i] * fieldDurMs; }
                double videoCleanEndMs = frameCleanC.Count > 0 ? tMsC : cell.Duration.TotalMilliseconds;

                SP(PProbeEnd + (PEncEnd - PProbeEnd) * Math.Min(1.0, (encDoneBytesC + cellLenC) / (double)segSrcBytes),
                   $"remux/PTS焼き直し中（セル {lc + 1}/{rangeCount}）…");
                string remuxC = Path.Combine(work, $"{idTag}_cp{lc}_rx.mkv");
                if (!MkvmergeConcat(opt, new[] { rawMkv }, remuxC, ct, out string rxErr))
                { log.AppendLine($"seg {idTag} cell{lc}: remux失敗\n{Tail(rxErr)}"); return null; }
                int nAvC = ProbeVideoFrameCount(opt.FfprobePath, remuxC);
                NoteFrameCountMismatch(log, $"seg {idTag} cell{lc}(PTS焼き)", frameCleanC.Count, nAvC);
                var tsAllC = new List<double>(frameCleanC);
                while (tsAllC.Count < nAvC) tsAllC.Add(tsAllC.Count > 0 ? tsAllC[^1] + fieldDurMs * 2 : 0);
                if (tsAllC.Count > nAvC) tsAllC.RemoveRange(nAvC, tsAllC.Count - nAvC);
                string tsFileC = Path.Combine(work, $"{idTag}_cp{lc}_ts.txt");
                { var sbts = new StringBuilder("# timestamp format v2\n"); foreach (var t in tsAllC) sbts.Append(t.ToString("F3", inv)).Append('\n'); File.WriteAllText(tsFileC, sbts.ToString()); }
                string cleanPartC = Path.Combine(work, $"{idTag}_cp{lc}.mkv");
                RunFfmpegNoInput(opt.MkvmergePath, $"-o \"{cleanPartC}\" {DvdH264Converter.MkvTimestampScaleArg} --timestamps \"0:{tsFileC}\" \"{remuxC}\"", ct, out string mErrC);
                if (!File.Exists(cleanPartC) || new FileInfo(cleanPartC).Length == 0)
                { log.AppendLine($"seg {idTag} cell{lc}: PTS焼き直し失敗\n{Tail(mErrC)}"); return null; }

                // durationMs：疎セルは IFO 尺（映像1枚を尺ぶん保持）、通常セルは映像尺。
                double partDurMs = cellSparse[lc] ? cell.Duration.TotalMilliseconds : videoCleanEndMs;
                parts.Add((cleanPartC, partDurMs, false, cellSparse[lc]));
                log.AppendLine($"seg {idTag} cell{lc}: パーツ化 sparse={cellSparse[lc]} frames={cellFrames[lc]} videoMs={videoCleanEndMs:F0} durMs={partDurMs:F0}");

                // 字幕（SPU）：セル内SPUを抽出し、パーツ通し時刻へ写像して subSpus に足す。
                var cellSpuC = new Dictionary<int, List<(long, byte[])>>();
                using (var pss = tp.OpenStream(cellStart + lc, cellStart + lc)) ExtractSubpictures(pss, cellSpuC);
                int nMapC = Math.Min(srcPtsc.Count, frameCleanC.Count);
                foreach (var kv in cellSpuC)
                {
                    if (!subSpus.TryGetValue(kv.Key, out var lst)) subSpus[kv.Key] = lst = new List<(long, byte[])>();
                    foreach (var (sp, data) in kv.Value)
                    {
                        double within = MapSourcePtsToCleanMs(sp, srcPtsc, frameCleanC, nMapC);
                        double globalMs = partCum * 1000.0 + Math.Max(0, within);
                        lst.Add(((long)Math.Round(globalMs / 1000.0 * 90000.0), data));
                    }
                }

                partCum += partDurMs / 1000.0;
                encDoneBytesC += cellLenC;
            }
            cum = partCum;
        }
        else
        {
        long encDoneBytesR = 0; // 進捗：符号化済みランの元バイト累計
        for (int r = 0; r < runCount; r++)
        {
            runFrameStart.Add(frameSrcPtsG.Count);
            ct.ThrowIfCancellationRequested();
            int a = runStarts[r], b = runStarts[r + 1] - 1; // ローカル（範囲内）index
            runBaseSec[r] = cum;
            long runFirst = cellFirst[a] >= 0 ? cellFirst[a] : 0;
            // 事前スキャン：このランのソース音声が最初に現れる時刻。出力の音声先頭が「あるべき位置」より
            // 後ろかどうかの判定に使う（AudioSyncArgs）。静止セル等で音声が遅れて始まる区間を、
            // muxer のずらしと取り違えないため。
            double runSrcAudioFirstSec = ProbeSourceFirstAudioSec(opt.FfprobePath, tp, cellStart + a, cellStart + b, ct);
            if (r == 0) seg0SrcAudioFirstSec = runSrcAudioFirstSec;
            // このランの「次のセル」から音声を読み足す（PlanTailExtend 参照）。最終ランはセグメントの外の
            // 次セル、それ以外は**次のランの先頭セル**。内部のラン境界でも読み足さないと音声に穴が空く。
            int runNextCell = r + 1 < runCount ? cellStart + runStarts[r + 1] : nextGlobal;
            var runTail = r + 1 < runCount
                ? PlanTailExtend(tp, opt, cellStart + b, runNextCell, cellLast[b], log, $"{idTag} run{r}", ct)
                : segTail;
            long tailNext90k = runTail.Next90k, tailShift90k = runTail.Shift90k;
            audioRuns.Add((cellStart + a, cellStart + b, audioCumShift90k));
            if (r + 1 < runCount) audioCumShift90k += runTail.Shift90k;   // 次のランへ持ち越す
            bool tailExtend = tailNext90k > 0;
            double trimEndSec = tailExtend ? tailNext90k / 90000.0 : 0;

            // 進捗：このランのフェーズ内配分（フレーム情報解析 15% / 符号化 85%）。ラン重み＝元バイト比。
            long runLen; using (var mLen = tp.OpenStream(cellStart + a, cellStart + b)) runLen = Math.Max(1, mLen.Length);
            string runTag = runCount > 1 ? $"（ラン {r + 1}/{runCount}）" : "";
            double runBase = PProbeEnd + (PEncEnd - PProbeEnd) * Math.Min(1.0, encDoneBytesR / (double)segSrcBytes);
            double runSpan = (PEncEnd - PProbeEnd) * Math.Min(1.0, runLen / (double)segSrcBytes);

            // ラン先頭でフレーム情報（フィールド数・progressive・source PTS）を取得。cadence 分割・パーツ尺・字幕写像に使う。
            List<byte> fld; List<bool> prg; List<long> srcPts;
            using (var psR = tp.OpenStream(cellStart + a, cellStart + b))
            {
                var pr = ProbeRunFrames(opt.FfprobePath, psR, ct, fed =>
                {
                    double ff = Math.Min(1.0, fed / (double)runLen);
                    SP(runBase + runSpan * 0.15 * ff, $"フレーム情報解析中{runTag} {(int)(100 * ff)}%");
                });
                fld = pr.Fields; prg = pr.Progressive; srcPts = pr.Pts90k;
            }
            int nfr = fld.Count;
            var frameCleanRun = new List<double>(nfr); // このランの元フレーム毎の segment-through clean ms（字幕写像）
            List<long> srcPtsRun = srcPts;

            if (!mixedParts)
            {
                // ===== 従来：ラン全体を1回符号化（fieldSep 一律 or weave 一律）→ runFiles（後で concat） =====
                string? forceKeys = b > a
                    ? string.Join(",", Enumerable.Range(a + 1, b - a).Where(k => cellFirst[k] >= 0)
                        .Select(k => (cellFirst[k] / 90000.0).ToString("F6", inv)))
                    : null;
                string runMkv = Path.Combine(work, $"{idTag}_r{r}.mkv");
                // 音声末尾は宣言尺（フィールド時間軸）で切る。ソースPTS軸の trimEndSec と共用すると
                // ソフトテレシネのぶんだけ音声が足りなくなる（パーツ側と同じ理由）。
                double runFieldMs = 0; for (int k = 0; k < nfr; k++) runFieldMs += fld[k] * fieldDurMs;
                // **最終ランだけ 1 フレームぶん余白を足す**。セグメントの宣言尺（segEndMs＝最後のフレーム時刻＋
                // 最後の間隔）とこのフィールド積み上げは別式なので 1 フィールドずれることがあり、その差だけ
                //音声が足りなくなる（実測：ある krk の S0003 で音声 27.227208s に対し宣言尺 27.243883s＝16.7ms 不足）。
                // 境界に穴が空くと再生側がそこを無音で埋めるため、音楽では一瞬の途切れとして聞こえる。
                // 余りは再生側がセグメント末尾でサンプル精度に切り詰めるので、多めに採って困らない。
                //
                // **内部のラン境界では余白を足さない**：ラン同士は ffmpeg の concat(-c copy) で直結され、
                // 再生側のトリムが一切かからない。長く採るとそのぶん次のランを丸ごと押しのけ、継ぎ目に
                // 波形のスプライス（バチッというノイズ）が出る（実測：ある krk の cell3→cell4 で 0:07:06）。
                // 内部境界は宣言尺ちょうどで切り、次のランと隙間なく直結させる。
                // **ランでは映像だけを符号化する**（audioPlan を空で渡す）。音声はラン境界で切らず、
                // セグメント全体を 1 回で符号化する（下の「音声を通しで符号化」）。ラン分割は映像の都合
                //（PTSリセット）で入るものなので、音声まで分ける必要は無い。分けて `concat -c copy` で
                // 直結すると、Opus は不連続の直後に pre-roll を必要とするため継ぎ目の数フレームが破綻し、
                // ほぼフルスケールの短いバーストになる（実測：ある krk の 0:07:06 で peak 29640／周囲は 12000 前後）。
                string args = BuildKurokoFfmpegArgs(opt, tp.VideoAttr, interlaced, tff, runMkv,
                    -runFirst / 90000.0, System.Array.Empty<AudioPlan>(), System.Array.Empty<string?>(),
                    System.Array.Empty<int>(), System.Array.Empty<string?>(), forceKeys,
                    videoTrimEndSec: trimEndSec, fieldSeparate: fieldSep, dualTracks: dualFields);
                using (var ps = tailExtend
                    ? (Stream)new ConcatStream(tp.OpenStream(cellStart + a, cellStart + b),
                                               new LimitedStream(TailSource(tp, runNextCell, tailShift90k), TailPeekBytes))
                    : tp.OpenStream(cellStart + a, cellStart + b))
                {
                    int rc = RunEncode(opt.FfmpegPath, args, ps, out string ffErr, ct, fed =>
                    {
                        double ff = Math.Min(1.0, fed / (double)runLen);
                        SP(runBase + runSpan * (0.15 + 0.85 * ff), $"符号化中{runTag} {(int)(100 * ff)}%");
                    });
                    if (!File.Exists(runMkv) || new FileInfo(runMkv).Length == 0)
                    { log.AppendLine($"seg {idTag} run{r}(cell {cellStart + a}-{cellStart + b}): ffmpeg 失敗 rc={rc}\n{Tail(ffErr)}"); return null; }
                }
                runFiles.Add(runMkv);
                int nEnc = ProbeVideoFrameCount(opt.FfprobePath, runMkv);
                if (nEnc > 0)
                {
                    // dual：v:0（top トラック）は元フレーム毎に1枚＝元フレーム数。単一トラック fields は倍フレームなので半分。
                    int target = (fieldSep && !dualFields) ? (nEnc + 1) / 2 : nEnc;
                    NoteFrameCountMismatch(log, $"seg {idTag} run{r}(符号化)", fld.Count, target);
                    while (fld.Count < target) { fld.Add(fld.Count > 0 ? fld[^1] : (byte)2); prg.Add(prg.Count > 0 ? prg[^1] : true); }
                    if (fld.Count > target) { fld.RemoveRange(target, fld.Count - target); prg.RemoveRange(target, prg.Count - target); }
                }
                double tMs = cum * 1000.0;
                for (int i = 0; i < fld.Count; i++)
                {
                    frameCleanRun.Add(tMs);
                    frameSrcPtsG.Add(i < srcPts.Count ? srcPts[i] : -1); frameCleanG.Add(tMs);
                    // dual：元フレーム毎に1個（フレーム時刻）を焼き、mkvmerge で top/bottom 両トラックへ同一適用（両者同一PTS）。
                    // 単一トラック fields：フィールド時刻で2個/元フレーム（倍レート1本）。
                    if (dualFields) { tsAllMs.Add(tMs); progAll.Add(false); }
                    else if (fieldSep) { tsAllMs.Add(tMs); progAll.Add(false); tsAllMs.Add(tMs + fieldDurMs); progAll.Add(false); }
                    else { tsAllMs.Add(tMs); progAll.Add(prg[i]); }
                    tMs += fld[i] * fieldDurMs;
                }
                cum = tMs / 1000.0;
            }
            else
            {
                // ===== 案B：ラン内をケイデンス連続区間（sub-run）へ分け、各区間を解像度パーツとして符号化 =====
                //   fields対象（interlaced かつ fld==2）区間＝半高・倍フレーム／それ以外＝フル高 weave。
                //   区間は -copyts 上の絶対ソース秒で trim（start/end）して切り出し、音声も tsOffset 由来の atrim で揃える。
                // テレシネ耐性＋短区間統合の fields対象判定（combed 点在・短ディゾルブを weave 化）。§8.1
                bool[] elig = ComputeFieldsEligible(prg, fld, opt.FieldsMinInterlaceFrames);
                // 進捗：サブパーツ総数（elig の遷移数）。各サブパーツはラン全体を読み直すため、
                // ラン内の符号化進捗＝(完了サブパーツ数＋当該サブパーツの供給割合)/総数 で進める。
                int subTotal = 0;
                { bool? cur = null; for (int k = 0; k < nfr; k++) { bool sf = fieldsSel && elig[k]; if (cur is null || sf != cur) { subTotal++; cur = sf; } } if (subTotal == 0) subTotal = 1; }
                int i = 0, subIdx = 0;
                while (i < nfr)
                {
                    bool subFields = fieldsSel && elig[i];
                    int j = i;
                    while (j < nfr && (fieldsSel && elig[j]) == subFields) j++;
                    bool lastSub = j >= nfr;
                    bool doTail = lastSub && tailExtend;
                    long subFirst90 = srcPts[i] >= 0 ? srcPts[i] : runFirst;
                    // 保険：ラン先頭パーツの基準PTSは cellFirst（ProbeCellRff の独立計測）と一致するはず。1秒以上
                    // 乖離していたら probe 側の異常なので cellFirst を採る。ここがずれると -output_ts_offset と
                    // 音声 atrim の基準だけが狂い、映像は mkvmerge で 0 基準へ焼かれるため**音声だけが丸ごとずれる**。
                    if (i == 0 && runFirst > 0 && Math.Abs(subFirst90 - runFirst) > 90000)
                    {
                        log.AppendLine($"seg {idTag} run{r}: パーツ0の基準PTS {subFirst90} が cell 先頭 {runFirst} と乖離 → cell 先頭を採用");
                        subFirst90 = runFirst;
                    }
                    double subStartSec = subFirst90 / 90000.0;
                    double subEndSec = (j < nfr && srcPts[j] >= 0) ? srcPts[j] / 90000.0 : 0; // 排他末尾（=次区間先頭）
                    double vTrimEnd = doTail ? trimEndSec : subEndSec;
                    long subEnd90 = subEndSec > 0 ? (long)Math.Round(subEndSec * 90000.0) : long.MaxValue;
                    // 区間内のセル先頭で IDR を強制（forceKeys。区間先頭より後・末尾より前のセル境界）。
                    string subForce = string.Join(",", Enumerable.Range(a, b - a + 1)
                        .Where(k => cellFirst[k] >= 0 && cellFirst[k] > subFirst90 && cellFirst[k] < subEnd90)
                        .Select(k => (cellFirst[k] / 90000.0).ToString("F6", inv)));
                    double vTrimStart = i > 0 ? subStartSec : 0; // ラン先頭区間は前を落とす必要が無い
                    // 音声の末尾は**宣言尺（フィールド時間軸）**で切る。映像 trim はソースPTS軸のまま
                    // （どのフレームを採るかの選択なので軸を変えてはいけない）だが、パーツの宣言尺は
                    // fld×fieldDurMs の積み上げで作るため、ソフトテレシネがあると 2 軸は一致しない。
                    // 同じ値を使っていたせいで 3:2 のパーツだけ音声がちょうど1フィールドぶん不足していた。
                    double subFieldMs = 0; for (int k = i; k < j; k++) subFieldMs += fld[k] * fieldDurMs;
                    // 上の run 側と同じ理由で 1 フレームぶん余白を足す（余りは再生側がサンプル精度で切る）。
                    double aTrimEnd = subStartSec + (subFieldMs + 2 * fieldDurMs) / 1000.0;

                    string partMkv = Path.Combine(work, $"{idTag}_r{r}p{subIdx}.mkv");
                    // インターレース・パーツは dual（2映像トラック top/bottom）。film パーツは単一トラック（フル高）。§8.1
                    string args = BuildKurokoFfmpegArgs(opt, tp.VideoAttr, interlaced, tff, partMkv,
                        -subStartSec, audioPlan, aLangs, System.Array.Empty<int>(), System.Array.Empty<string?>(),
                        string.IsNullOrEmpty(subForce) ? null : subForce,
                        videoTrimEndSec: vTrimEnd, audioTrimEndSec: aTrimEnd, fieldSeparate: subFields, videoTrimStartSec: vTrimStart,
                        dualTracks: subFields);
                    using (var ps = doTail
                        ? (Stream)new ConcatStream(tp.OpenStream(cellStart + a, cellStart + b),
                                                   new LimitedStream(TailSource(tp, runNextCell, tailShift90k), TailPeekBytes))
                        : tp.OpenStream(cellStart + a, cellStart + b))
                    {
                        int subNo = subIdx; // 進捗表示用（クロージャで固定）
                        int rc = RunEncode(opt.FfmpegPath, args, ps, out string ffErr, ct, fed =>
                        {
                            double ff = Math.Min(1.0, fed / (double)runLen);
                            double sf = Math.Min(1.0, (subNo + ff) / subTotal);
                            SP(runBase + runSpan * (0.15 + 0.85 * sf), $"符号化中{runTag}（パーツ {subNo + 1}/{subTotal}） {(int)(100 * ff)}%");
                        });
                        if (!File.Exists(partMkv) || new FileInfo(partMkv).Length == 0)
                        { log.AppendLine($"seg {idTag} run{r} part{subIdx}(frames {i}-{j}): ffmpeg 失敗 rc={rc}\n{Tail(ffErr)}"); return null; }
                    }
                    int nEncP = ProbeVideoFrameCount(opt.FfprobePath, partMkv);
                    // 映像が 1 枚も出なかったパーツは、mkvmerge で映像の時刻を焼けず、音声だけがソースの絶対時刻のまま残る
                    //（7 月の変換器で作った ある DVD 由来の krk の S0016/av.p00.mkv：1 フレームのパーツで映像 0 枚・
                    // 音声が 1835 秒から始まり、再生が止まった。再生側は範囲外の時刻を捨てて救済する）。黙って作らず必ず記録する。
                    if (nEncP <= 0 && j > i)
                        log.AppendLine($"警告: seg {idTag} run{r} part{subIdx}(frames {i}-{j}): 符号化された映像が 0 枚"
                            + "（このパーツは壊れています。音声の時刻が 0 起点になりません）");
                    var tsPart = new List<double>();
                    double tMs = 0, baseMs = cum * 1000.0;
                    for (int k = i; k < j; k++)
                    {
                        frameCleanRun.Add(baseMs + tMs);
                        frameSrcPtsG.Add(k < srcPts.Count ? srcPts[k] : -1); frameCleanG.Add(baseMs + tMs);
                        // dual：元フレーム毎に1個（フレーム時刻）を焼き、mkvmerge で top/bottom 両トラックへ同一適用（両者同一PTS）。
                        // film：元フレーム毎に1個（progAll=prg）。いずれも 1個/元フレーム（旧・単一トラック fields の 2個/元フレームは廃止）。
                        if (subFields) { tsPart.Add(tMs); tsAllMs.Add(baseMs + tMs); progAll.Add(false); }
                        else { tsPart.Add(tMs); tsAllMs.Add(baseMs + tMs); progAll.Add(prg[k]); }
                        tMs += fld[k] * fieldDurMs;
                    }
                    // 実符号化フレーム数へ合わせる（dual/film とも 1枚/元フレーム。過不足を末尾で調整。tsAllMs/progAll も同期）。
                    NoteFrameCountMismatch(log, $"seg {idTag} run{r} part{subIdx}(符号化)", tsPart.Count, nEncP);
                    while (nEncP > 0 && tsPart.Count < nEncP) { double d = tsPart.Count >= 2 ? tsPart[^1] - tsPart[^2] : fieldDurMs * 2; double add = tsPart.Count > 0 ? tsPart[^1] + d : 0; tsPart.Add(add); tsAllMs.Add(baseMs + add); progAll.Add(subFields ? false : (progAll.Count > 0 ? progAll[^1] : true)); }
                    while (nEncP > 0 && tsPart.Count > nEncP) { tsPart.RemoveAt(tsPart.Count - 1); tsAllMs.RemoveAt(tsAllMs.Count - 1); progAll.RemoveAt(progAll.Count - 1); }

                    string tsFileP = Path.Combine(work, $"{idTag}_r{r}p{subIdx}_ts.txt");
                    { var sbp = new StringBuilder("# timestamp format v2\n"); foreach (var t in tsPart) sbp.Append(t.ToString("F3", inv)).Append('\n'); File.WriteAllText(tsFileP, sbp.ToString()); }
                    string cleanPart = Path.Combine(work, $"{idTag}_r{r}p{subIdx}_clean.mkv");
                    // dual パーツは top(0)/bottom(1) 両映像トラックへ同一の frame-time PTS を焼く。
                    string tsArgP = subFields ? $"--timestamps \"0:{tsFileP}\" --timestamps \"1:{tsFileP}\"" : $"--timestamps \"0:{tsFileP}\"";
                    // 音声が 0 より後ろへ押し出されていたら戻す（AudioSyncArgs 参照）。トラック番号は映像の次から。
                    // パーツ先頭（subIdx>0）はラン途中なので、そこには必ずソース音声がある＝あるべき先頭は 0。
                    double expPartMs = subIdx == 0 ? ExpectedAudioStartMs(runSrcAudioFirstSec, subStartSec) : 0;
                    string syncP = AudioSyncArgs(opt.FfprobePath, partMkv, subFields ? 2 : 1, audioPlan.Count,
                        expPartMs, log, $"seg {idTag} run{r} part{subIdx}", ct);
                    RunFfmpegNoInput(opt.MkvmergePath, $"-o \"{cleanPart}\" {DvdH264Converter.MkvTimestampScaleArg} {tsArgP} {syncP}\"{partMkv}\"", ct, out string mErrP);
                    if (!File.Exists(cleanPart) || new FileInfo(cleanPart).Length == 0)
                    { log.AppendLine($"seg {idTag} run{r} part{subIdx}: mkvmerge 失敗\n{Tail(mErrP)}"); return null; }
                    double partDurMs = tsPart.Count >= 2 ? tsPart[^1] + (tsPart[^1] - tsPart[^2]) : tsPart.Count == 1 ? tsPart[^1] + fieldDurMs * 2 : 0;
                    parts.Add((cleanPart, partDurMs, subFields, false));
                    anyFieldsPart |= subFields;
                    cum += partDurMs / 1000.0;
                    log.AppendLine($"seg {idTag} run{r} part{subIdx}: frames {i}-{j} {(subFields ? "fields(半高)" : "weave(フル高)")} dur={partDurMs:F0}ms nEnc={nEncP}");
                    i = j; subIdx++;
                }
            }

            // 字幕：ラン単位で抽出し、このランのフレーム対応（srcPtsRun↔frameCleanRun）で segment-through clean ms へ写像。
            SP(runBase + runSpan, $"字幕抽出中{runTag}…");
            var runSub = new Dictionary<int, List<(long SourcePts90k, byte[] Data)>>();
            using (var psS = tp.OpenStream(cellStart + a, cellStart + b))
                ExtractSubpictures(psS, runSub);
            int nMap = Math.Min(srcPtsRun.Count, frameCleanRun.Count);
            foreach (var kv in runSub)
            {
                if (!subSpus.TryGetValue(kv.Key, out var lst)) subSpus[kv.Key] = lst = new List<(long, byte[])>();
                foreach (var (sp, data) in kv.Value)
                {
                    double cms = MapSourcePtsToCleanMs(sp, srcPtsRun, frameCleanRun, nMap);
                    lst.Add(((long)Math.Round(Math.Max(0, cms) / 1000.0 * 90000.0), data));
                }
            }
            encDoneBytesR += runLen;
        }
        }

        // 非mixed：runFiles を concat→単一 av.mkv→PTS焼き。mixed（案B）：各パーツが既に clean 済みなのでここはスキップ。
        string cleanMkv = "";
        if (!mixedParts)
        {
            SP(PEncEnd, "remux/PTS焼き直し中…");
            string mkvPath = Path.Combine(work, $"{idTag}.mkv");
            if (runFiles.Count == 1) File.Move(runFiles[0], mkvPath, overwrite: true);
            else
            {
                string listPath = Path.Combine(work, $"{idTag}_list.txt");
                File.WriteAllText(listPath, string.Join("\n", runFiles.Select(f => $"file '{f.Replace("\\", "/").Replace("'", "'\\''")}'")));
                string cargs = $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{listPath}\" -map 0 -c copy \"{mkvPath}\"";
                if (!RunFfmpegNoInput(opt.FfmpegPath, cargs, ct, out string cErr) || !File.Exists(mkvPath) || new FileInfo(mkvPath).Length == 0)
                { log.AppendLine($"seg {idTag}: concat 失敗\n{Tail(cErr)}"); return null; }
            }

            // 映像の枚数の最終の合わせ込み（音声の終端をこの尺に合わせるので、音声の符号化より前に行う）。
            int nAv = ProbeVideoFrameCount(opt.FfprobePath, mkvPath);
            NoteFrameCountMismatch(log, $"seg {idTag}(av.mkv)", tsAllMs.Count, nAv);
            if (nAv > 0 && tsAllMs.Count != nAv)
            {
                while (tsAllMs.Count < nAv) { tsAllMs.Add(tsAllMs.Count > 0 ? tsAllMs[^1] + fieldDurMs * 2 : 0); progAll.Add(progAll.Count > 0 ? progAll[^1] : true); }
                if (tsAllMs.Count > nAv) { tsAllMs.RemoveRange(nAv, tsAllMs.Count - nAv); progAll.RemoveRange(nAv, progAll.Count - nAv); }
            }

            // ── 音声を通しで符号化する（ラン境界で切らない）────────────────────────────
            // ラン分割は映像の都合（PTSリセット）で入るもので、音声を分ける必要は無い。分けて
            // `concat -c copy` で直結すると Opus が継ぎ目で破綻する（pre-roll が無いため。実測：
            // ある krk の 0:07:06 で peak 29640＝ほぼフルスケールの短いバースト）。
            // 各ランの PS を PtsShiftStream で「前のランの続き」の時刻へずらしながら連結し、
            // セグメント全体を 1 本の連続した時間軸として 1 回だけ符号化する。
            // 副次効果：音声単独の符号化なので、matroska muxer が映像の GOP 構造次第で音声を
            // ずらす現象（AudioSyncArgs）も起きない。
            string audioMkv = "";
            if (audioPlan.Count > 0 && audioRuns.Count > 0)
            {
                SP(PEncEnd, "音声を通しで符号化中…");
                long aFirst90k = cellFirst[0] >= 0 ? cellFirst[0] : 0;
                double aStartSec = aFirst90k / 90000.0;
                // 末尾はセグメントの宣言尺（下の segEndMs と同じ式＝焼く映像の最終フレーム時刻＋最終間隔）＋1フレームの
                // 余白。余りは再生側がセグメント末尾でサンプル精度に切り詰める。
                // **フィールド時間の総和（cellFieldSum）だけで決めてはいけない**：セル単独の解析では、セル末尾の GOP の
                // 数枚（次セル先頭の I を参照する B）が復号できずに数え落とされるが、符号化では末尾の読み足しで次セルの頭まで
                // 流すので復号でき、映像はその枚数ぶん長くなる（上の「末尾で調整」で時刻を補う）。総和で切ると、その長さ
                // だけ音声が足りず、次のセグメントへ移る直前に穴が空く（実測：ある DVD タイトル1 の S0014→S0015
                //＝1:41:05.9 で +2 枚＝67ms の無音）。読み足した次セルの頭に本物の音声があるので、映像の尺まで採ってよい。
                double segFieldMs = 0; foreach (var t in cellFieldSum) segFieldMs += t * fieldDurMs;
                double segVideoMs = tsAllMs.Count >= 2 ? tsAllMs[^1] + (tsAllMs[^1] - tsAllMs[^2])
                                  : tsAllMs.Count == 1 ? tsAllMs[^1] + fieldDurMs * 2
                                  : segFieldMs;
                double aEndSec = aStartSec + (Math.Max(segFieldMs, segVideoMs) + 2 * fieldDurMs) / 1000.0;
                if (segVideoMs > segFieldMs + 1)
                    log.AppendLine($"seg {idTag}: 音声の終端を映像の尺へ伸ばす（フィールド総和 {segFieldMs:F1}ms → 映像 {segVideoMs:F1}ms）");
                audioMkv = Path.Combine(work, $"{idTag}_audio.mka");
                string aargs = BuildKurokoFfmpegArgs(opt, tp.VideoAttr, interlaced, tff, audioMkv,
                    -aStartSec, audioPlan, aLangs, System.Array.Empty<int>(), System.Array.Empty<string?>(), null,
                    audioTrimEndSec: aEndSec, audioOnly: true);
                var opens = new List<Func<Stream>>();
                foreach (var ar in audioRuns)
                {
                    var cap = ar;
                    opens.Add(() => cap.Shift90k == 0
                        ? tp.OpenStream(cap.CellFirst, cap.CellLast)
                        : new PtsShiftStream(tp.OpenStream(cap.CellFirst, cap.CellLast), cap.Shift90k));
                }
                // **セグメント末尾の読み足しも音声側に要る**。セル末尾の音声は次セルの先頭に入っているので
                // （多重化の遅延 ~150-250ms）、最後に次セルの頭を足さないとセグメント末尾の音声が欠ける
                //（実測：ある krk の S0003＝38秒の境界で約 0.3 秒の無音になった）。
                // 映像側の読み足し（tailExtend）と同じ次セル・同じシフトを使うが、最後のランが既に
                // 累積シフトぶんずれているので、そのぶんを足した量でずらす。
                if (segTail.Next90k > 0 && nextGlobal < tp.CellCount)
                {
                    long tailCum = audioRuns[^1].Shift90k + segTail.Shift90k;
                    opens.Add(() => new LimitedStream(TailSource(tp, nextGlobal, tailCum), TailPeekBytes));
                }
                using (var aps = new MultiConcatStream(opens))
                {
                    int arc = RunEncode(opt.FfmpegPath, aargs, aps, out string aErr, ct, null);
                    if (!File.Exists(audioMkv) || new FileInfo(audioMkv).Length == 0)
                    { log.AppendLine($"seg {idTag}: 音声の通し符号化に失敗 rc={arc}\n{Tail(aErr)}"); return null; }
                }
                log.AppendLine($"seg {idTag}: 音声を通しで符号化（ラン{audioRuns.Count}本を連結・継ぎ目なし）"
                    + $" 開始={aStartSec:F3}s 終端={aEndSec:F3}s 累積シフト={audioCumShift90k / 90000.0:F3}s");
            }

            string tsFile = Path.Combine(work, $"{idTag}_ts.txt");
            {
                var sbts = new StringBuilder("# timestamp format v2\n");
                foreach (var t in tsAllMs) sbts.Append(t.ToString("F3", inv)).Append('\n');
                File.WriteAllText(tsFile, sbts.ToString());
            }
            cleanMkv = Path.Combine(work, $"{idTag}_clean.mkv");
            // dual：top(0)/bottom(1) 両映像トラックへ同一の frame-time PTS を焼く。
            string tsArg = dualFields ? $"--timestamps \"0:{tsFile}\" --timestamps \"1:{tsFile}\"" : $"--timestamps \"0:{tsFile}\"";
            // 音声は別ファイル（通し符号化）なので mkvmerge の入力として後ろに足す。mkvmerge のオプションは
            // **直後の入力ファイル**に効くので、--timestamps は映像側、--sync は音声側に付く。
            // 音声単独の符号化では muxer のずらしは起きないはずだが、念のため測って戻す（AudioSyncArgs）。
            double expSegMs = ExpectedAudioStartMs(seg0SrcAudioFirstSec, (cellFirst[0] >= 0 ? cellFirst[0] : 0) / 90000.0);
            string audioIn = "";
            if (!string.IsNullOrEmpty(audioMkv) && File.Exists(audioMkv))
            {
                string syncA = AudioSyncArgs(opt.FfprobePath, audioMkv, 0, audioPlan.Count,
                    expSegMs, log, $"seg {idTag}(音声)", ct);
                audioIn = $" {syncA}\"{audioMkv}\"";
            }
            RunFfmpegNoInput(opt.MkvmergePath, $"-o \"{cleanMkv}\" {DvdH264Converter.MkvTimestampScaleArg} {tsArg} \"{mkvPath}\"{audioIn}", ct, out string mErr);
            if (!File.Exists(cleanMkv) || new FileInfo(cleanMkv).Length == 0)
            { log.AppendLine($"seg {idTag}: mkvmerge PTS焼き直し 失敗\n{Tail(mErr)}"); return null; }
        }

        var subpIds = subSpus.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(k => k).ToList();
        // 字幕は av.mkv に入れない（§11）。SPU を復号して subs/img/<物理>/（cue毎PNG＋index.json）へ書く。
        // 座標は 720×(NTSC 480/PAL 576) の符号化グリッド（再生側は復号映像へ同座標で合成）。
        if (subpIds.Count > 0) SP(0.97, "字幕画像生成中…");
        var subsFiles = BuildSubsImages(idTag, subSpus, tp.Pgc.Palette.ToArray(),
            720, tp.VideoAttr.IsPal ? 576 : 480, LangForPhys, log);

        // セグメント内0基準のセル時刻。mixed はセルとパーツが非整合なので、元フレームの source PTS→clean ms 表から引く。
        double CellTimeSec(int lc)
        {
            if (lc < 0 || lc >= rangeCount) return 0;
            if (cellPartsMode) return cellPartStartSec[lc]; // 疎セル・パーツ化：durationMs 累積の通し開始秒
            if (mixedParts)
            {
                long cf = cellFirst[lc];
                if (cf < 0) return 0;
                // このセルが属するランのフレームスライス内だけで source PTS 一致を探す（PTSリセットで run 間の
                // source PTS が重複するため、全域検索だと後続ランのセルが先行ランのフレームへ誤マップして時刻が巻き戻る）。
                int crun = cellRun[lc];
                int lo = crun < runFrameStart.Count ? runFrameStart[crun] : 0;
                int hi = crun + 1 < runFrameStart.Count ? runFrameStart[crun + 1] : frameSrcPtsG.Count;
                for (int m = lo; m < hi; m++)
                    if (frameSrcPtsG[m] >= 0 && frameSrcPtsG[m] >= cf) return Math.Max(0, frameCleanG[m] / 1000.0);
                return hi > lo ? Math.Max(0, frameCleanG[hi - 1] / 1000.0) : (frameCleanG.Count > 0 ? frameCleanG[^1] / 1000.0 : 0);
            }
            int rr = cellRun[lc]; int a2 = runStarts[rr];
            double within = (cellFirst[lc] >= 0 && cellFirst[a2] >= 0) ? (cellFirst[lc] - cellFirst[a2]) / 90000.0 : 0;
            return Math.Max(0, runBaseSec[rr] + within);
        }

        // セグメント終端時刻＝次セグメントの開始オフセット。cum（フレーム数調整前）ではなく、実際に av.mkv へ
        // 焼いた tsAllMs（nAv 調整後）の最終フレームPTS＋最終フレーム表示時間（mkvmerge が付ける最終デルタ）から
        // 算出する。これで次セグメント先頭が実mkv終端と厳密に連続し、境界で PTS が重なって min-Due プールが1枚
        // 落とす（＝1フレーム飛び）のを防ぐ。
        double segEndMs = tsAllMs.Count >= 2 ? tsAllMs[^1] + (tsAllMs[^1] - tsAllMs[^2])
                        : tsAllMs.Count == 1 ? tsAllMs[^1] + fieldDurMs * 2
                        : cum * 1000.0;
        // fields セグメント（単一 fieldSep、または mixed で fields パーツを含む）は、seg.Video 要約を半高・倍フレーム・
        // interlaceMode=fields にする。再生側はこの要約で _fieldsMode/_fieldHeight を決め、フレーム高でパーツ毎に
        // fields展開/weave を自動判定する（24pパーツ=フル高→weave、29.97iパーツ=半高→展開）。
        bool fieldsSeg = fieldSep || anyFieldsPart;
        var seg = new KurokoSegment
        {
            Video = new KurokoVideo
            {
                Codec = KurokoVideoCodec(opt),
                Width = tp.VideoAttr.DisplayWidth,
                Height = fieldsSeg ? tp.VideoAttr.DisplayHeight / 2 : tp.VideoAttr.DisplayHeight,
                Aspect = tp.VideoAttr.AspectRatio == "16:9" ? "16:9" : "4:3",
                // fields は dual（一律=dualFields／混在=dualパーツ）で各トラック元フレームレート。weave も元レート。
                // → 常にフレームレート（旧・単一トラック fields の倍レートは廃止）。
                Fps = tp.VideoAttr.IsPal ? "25/1" : "30000/1001",
                Interlaced = interlaced,
                Tff = tff,
                InterlaceMode = fieldsSeg ? "fields" : "weave",
                // 一律fields も 混在の fields パーツも dual 格納。fieldsSeg（fields を含む）なら dual。
                FieldLayout = fieldsSeg ? "dual" : "single",
                InterlacedRanges = BuildInterlacedRanges(tsAllMs, progAll, fieldDurMs),
            },
            Timing = new KurokoTiming { FrameRate = tp.VideoAttr.IsPal ? "25/1" : "30000/1001", TotalMs = segEndMs },
        };
        // mixed（案B）：解像度パーツの一覧（順序・尺）。再生側 demux が av.pNN.mkv を連結する。
        if (mixedParts && parts.Count > 0)
            seg.Parts = parts.Select((p, i) => new KurokoSegmentPart
            {
                File = System.IO.Path.GetFileName(KurokoLayout.SegmentPartMkv(idTag, i)),
                DurationMs = p.DurMs,
                PixelHeight = p.Fields ? tp.VideoAttr.DisplayHeight / 2 : tp.VideoAttr.DisplayHeight,
                InterlaceMode = p.Fields ? "fields" : "weave",
                FieldLayout = p.Fields ? "dual" : "single", // interlace パーツ=dual(2トラック)／film パーツ=single
                Still = p.Still, // 疎セル（静止画）パーツ＝再生側で映像投入直後に即 Drain（§8.2）
            }).ToList();
        // §13.3：seg.json のトラックは物理のみ（substream→mkvTrack）。論理番号・アスペクト変種は title.json 側。
        bool segOpus = recompress && string.Equals(opt.AudioCodec, "opus", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < audioPlan.Count; i++)
        {
            var at = physAttr.TryGetValue(audioPlan[i].OrigId, out var pa) ? pa.Attr : null;
            var ka = new KurokoAudio
            {
                Substream = audioPlan[i].OrigId, // 物理サブストリームID
                MkvTrack = (dualFields ? 2 : 1) + i, // dual は映像2トラック（top/bottom）の後に音声

                // **再符号化するときだけ** Opus。コピー指定なら元のコーデック/チャンネル数がそのまま残る。
                Codec = segOpus ? "opus" : recompress ? "ac3" : (at is not null ? KurokoAudioCodec(at.CodingMode) : "ac3"),
                Channels = recompress ? OutAudioChannels(opt, segOpus, at?.Channels ?? 2) : (at?.Channels ?? 2),
                Language = aLangs[i], // 参考メタ（表示は title.json が正）
            };
            // ラウドネス・メタデータ（§9.1）。音声本体はフルレンジのまま（-drc_scale 0）で、dialnorm/DRC は
            // ここでメタとして保持し再生時に任意適用する。AC-3 のみ（DTS/LPCM/MPEG は未対応・保持しない）。
            if (at is null || KurokoAudioCodec(at.CodingMode) == "ac3")
            {
                SP(0.98, $"ラウドネス解析中（0x{audioPlan[i].OrigId:X2}）…"); // 元PSをもう1回全読みする
                ApplyLoudness(ka, tp, cellStart, cellEnd, audioPlan[i].OrigId, opt, log, ct);
            }
            seg.Audio.Add(ka);
        }
        for (int k = 0; k < subpIds.Count; k++)
            seg.Subtitle.Add(new KurokoSubtitle
            {
                Substream = subpIds[k], // 物理サブストリームID（0x20+n）
                MkvTrack = -1,          // 字幕は av.mkv 非同梱。実体は subs/img/<物理>/（§11）
                Language = LangForPhys(subpIds[k]),
            });
        for (int lc = 0; lc < rangeCount; lc++)
            seg.Cells.Add(new KurokoSegCell
            {
                StartInSegment = KurokoTime.Format(TimeSpan.FromSeconds(CellTimeSec(lc))),
                Duration = KurokoTime.Format(tp.Pgc.Cells[cellStart + lc].Duration),
            });

        return new SegEncodeResult
        {
            CleanMkv = cleanMkv,
            PartCleanMkvs = parts.Select(p => p.CleanMkv).ToList(),
            Seg = seg,
            SubsFiles = subsFiles,
        };
    }

    // subSpus（物理サブID → 時刻付きSPU列・セグメント内0基準90kHz）を復号し、subs/img/<物理>/index.json＋
    // cue毎PNG（パレット/コントラスト適用済み RGBA）へ写す（§11）。返り値は (krkパス, バイト列) 列（呼び出し側が writer へ）。
    private static List<(string Path, byte[] Data)> BuildSubsImages(
        string segId, Dictionary<int, List<(long Pts90k, byte[] Data)>> subSpus,
        uint[] palette, int canvasW, int canvasH, Func<int, string?> langForPhys, StringBuilder log)
    {
        var files = new List<(string, byte[])>();
        foreach (var kv in subSpus.Where(x => x.Value.Count > 0).OrderBy(x => x.Key))
        {
            int physId = kv.Key;
            var index = new KurokoSubsImage
            {
                Type = "image",
                Lang = KurokoLanguage.Normalize(langForPhys(physId)),
                Canvas = new KurokoSubsCanvas { Width = canvasW, Height = canvasH },
            };
            // DVD からの変換は **SPU の生データをそのまま格納**する（§11）。SPU 1つに時間変化（フェード・
            // CHG_COLCON による行範囲ごとの出し分け）が全て入っているので、状態ごとの PNG を書かずに済み、
            // データ量が1〜2桁小さく、かつ市販機と同じ見え方を完全に再現できる。表示に使うパレットは
            // title.json の CLUT（再生器が渡す）。PNG 表現は仕様上も有効なまま（DVD 以外の入力向け）。
            var decoded = new List<(double StartMs, double? EndMs, byte[] Spu)>();
            foreach (var (pts90k, data) in kv.Value.OrderBy(x => x.Pts90k))
            {
                // 表示区間（cue の開始/終了）は SPU の DCSQ から取る。描画自体は再生時に SPU から行う。
                var f = DvdSpuDecoder.DecodeSubpicture(data, palette);
                if (f is null || f.Width <= 0 || f.Height <= 0) continue;
                double spuMs = pts90k / 90.0;
                double startMs = spuMs + f.StartDelay.TotalMilliseconds;
                double? endMs = f.StopDelay == TimeSpan.MaxValue ? null : spuMs + f.StopDelay.TotalMilliseconds;
                decoded.Add((startMs, endMs, data));
            }
            if (decoded.Count == 0) continue;
            for (int i = 0; i < decoded.Count; i++)
            {
                var d = decoded[i];
                double end = d.EndMs ?? (i + 1 < decoded.Count ? decoded[i + 1].StartMs : d.StartMs + 5000.0);
                if (end < d.StartMs) end = d.StartMs;
                string spuName = $"{i:0000}.spu";
                files.Add((KurokoLayout.SubsImagePng(segId, physId, spuName), d.Spu));
                index.Cues.Add(new KurokoSubsCue
                {
                    StartMs = d.StartMs,
                    EndMs = end,
                    Spu = spuName,
                });
            }
            files.Add((KurokoLayout.SubsImageIndex(segId, physId), KurokoJson.Serialize(index)));
        }
        return files;
    }

    // ── メニュー実体生成（M1b-2） ────────────────────────────────────────────
    // 各メニュードメイン(VMGM/VTSnn)の各PGCについて、背景 video.mkv（映像＋ボタン図柄サブピクチャ）・任意
    // audio.mka・buttons.json（セル境界/静止/代表PCI）・palette.json を生成し writer へ追加する。
    // 命令のみ（セル無し）の PGC は背景が無いのでメディアを置かない（disc.json には命令が載っている）。
    // 診断(F)：各メニューPGCの映像アスペクトと SPST_CTL（表示モード別の物理サブピクチャID）をダンプする。
    // メニューのボタン図柄SPUを「表示モードで正しく選ぶ」実装の裏どり用。ワイド枠が 0x20 か 0x21 かを確認する。
    public static string DumpMenuSubctl(string isoPath)
    {
        var sb = new StringBuilder();
        using var disc = DvdDisc.Open(isoPath);
        void DumpDomain(string dom, IReadOnlyList<DvdMenuPgc> pgcs, Func<int, DvdTitlePlayback?> open)
        {
            for (int i = 0; i < pgcs.Count; i++)
            {
                int pgcNo = i + 1;
                DvdTitlePlayback? tp;
                try { tp = open(pgcs[i].Pgc.Number); } catch (Exception ex) { sb.AppendLine($"{dom}/PGC{pgcNo:00}: open失敗 {ex.Message}"); continue; }
                if (tp is null) { sb.AppendLine($"{dom}/PGC{pgcNo:00}: open=null"); continue; }
                sb.AppendLine($"{dom}/PGC{pgcNo:00}: cells={tp.CellCount} aspect={tp.VideoAttr.AspectRatio} subpStreams={tp.SubpictureStreams.Count} progEntry(prog->cell1based)=[{string.Join(",", tp.Pgc.ProgramEntryCells)}] cellStill=[{string.Join(",", tp.Pgc.Cells.Select(c => c.StillTime == 0xFF ? "inf" : c.StillTime.ToString()))}]");
                var ctl = tp.Pgc.SubpictureControl;
                for (int k = 0; k < ctl.Count; k++)
                {
                    var s = ctl[k];
                    if (!s.Available) continue;
                    sb.AppendLine($"    SPST_CTL #{k}: avail 43:0x{0x20 + s.N43:X2} wide:0x{0x20 + s.NWide:X2} lb:0x{0x20 + s.NLetterbox:X2} ps:0x{0x20 + s.NPanScan:X2}");
                }
            }
        }
        DumpDomain("VMGM", SafeMenuList(() => disc.GetVmgmPgcs()), n => disc.OpenVmgmMenuPgc(n));
        foreach (int vts in disc.Titles.Select(t => t.VtsNumber).Distinct().OrderBy(v => v))
        {
            int v = vts;
            DumpDomain($"VTS{v:00}", SafeMenuList(() => disc.GetMenuPgcs(v)), n => disc.OpenMenuPgc(v, n));
        }
        return sb.ToString();
    }

    // 診断：各タイトルのセルの StillTime（静止時間）/ CommandNr（セル命令番号）を列挙する。
    // 「タイトルに静止セル・セル命令があるか」を IFO から高速確認する（符号化なし）。
    public static string DumpTitleCells(string isoPath)
    {
        var sb = new StringBuilder();
        using var disc = DvdDisc.Open(isoPath);
        sb.AppendLine($"=== title cells: {System.IO.Path.GetFileName(isoPath)} titles={disc.Titles.Count} ===");
        foreach (var t in disc.Titles)
        {
            DvdTitlePlayback? tp;
            try { tp = disc.OpenTitle(t.TitleNumber); } catch (Exception ex) { sb.AppendLine($"T{t.TitleNumber}: open失敗 {ex.Message}"); continue; }
            var cells = tp.Pgc.Cells;
            int stillN = 0, cmdN = 0;
            for (int c = 0; c < cells.Count; c++)
            {
                if (cells[c].StillTime != 0) stillN++;
                if (cells[c].CommandNr != 0) cmdN++;
            }
            sb.AppendLine($"T{t.TitleNumber} (VTS{t.VtsNumber}.{t.VtsTitleNumber}): pgc={tp.Pgc.Number} cells={cells.Count} progEntry=[{string.Join(",", tp.Pgc.ProgramEntryCells)}] cellCmdTable={tp.Pgc.CellCommands.Count} still!=0:{stillN} cmd!=0:{cmdN} dur={tp.Pgc.PlaybackTime.ToTimeSpan()}");
            for (int c = 0; c < cells.Count; c++)
            {
                var cl = cells[c];
                string il = "";
                if ((cl.Category & 0x04) != 0) // インターリーブ：VOBUフィルタ後のサブレンジと実セクタ総数を出す
                {
                    var rr = tp.CellSectorRanges(c);
                    long tot = rr.Sum(r => r.Last - r.First + 1);
                    il = $" ILVU->{rr.Count}range {tot}sec [{string.Join(",", rr.Take(6).Select(r => $"{r.First}..{r.Last}"))}{(rr.Count > 6 ? ",…" : "")}]";
                }
                sb.AppendLine($"    cell{c}: vob={cl.VobId} cid={cl.CellId} cat=0x{cl.Category:X2} sec=[{cl.FirstSector}..{cl.LastSector}] ({cl.LastSector - cl.FirstSector + 1}sec) dur={cl.Duration} still={(cl.StillTime == 0xFF ? "inf" : cl.StillTime.ToString())} cmd={cl.CommandNr}{il}");
            }
        }
        return sb.ToString();
    }

    private static void WriteKurokoMenus(DvdDisc disc, KurokoZipWriter writer, DvdH264Converter.Options opt,
        string work, StringBuilder log, System.Threading.CancellationToken ct,
        IProgress<DvdH264Converter.Progress>? progress = null)
    {
        // 進捗：全ドメインの PGC 総数で 0.90..0.98 を等割り（メニューは小さいので個数割りで十分）。
        var domains = new List<(string Domain, IReadOnlyList<DvdMenuPgc> Pgcs, Func<int, DvdTitlePlayback?> Open)>
        { (KurokoLayout.MenuDomainVmgm, SafeMenuList(() => disc.GetVmgmPgcs()), pgcNo => disc.OpenVmgmMenuPgc(pgcNo)) };
        foreach (int vts in disc.Titles.Select(t => t.VtsNumber).Distinct().OrderBy(v => v))
        {
            int v = vts;
            domains.Add((KurokoLayout.MenuDomainVts(v), SafeMenuList(() => disc.GetMenuPgcs(v)), pgcNo => disc.OpenMenuPgc(v, pgcNo)));
        }
        int pgcTotal = Math.Max(1, domains.Sum(d => d.Pgcs.Count)), pgcDone = 0;
        foreach (var d in domains)
            WriteMenuDomain(writer, opt, work, log, ct, d.Domain, d.Pgcs, d.Open,
                pgcNo => progress?.Report(new DvdH264Converter.Progress(
                    0.90 + 0.08 * pgcDone++ / pgcTotal, $"メニュー {d.Domain}/PGC{pgcNo:00} を生成中…")));
    }

    private static IReadOnlyList<DvdMenuPgc> SafeMenuList(Func<IReadOnlyList<DvdMenuPgc>> f)
    { try { return f() ?? Array.Empty<DvdMenuPgc>(); } catch { return Array.Empty<DvdMenuPgc>(); } }

    private static void WriteMenuDomain(KurokoZipWriter writer, DvdH264Converter.Options opt, string work,
        StringBuilder log, System.Threading.CancellationToken ct, string domain,
        IReadOnlyList<DvdMenuPgc> pgcs, Func<int, DvdTitlePlayback?> open, Action<int>? onPgcStart = null)
    {
        for (int i = 0; i < pgcs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int pgcNo = i + 1; // disc.json の MenuPgcDir(domain, i+1) と一致（Pgc.Number も i+1）。
            onPgcStart?.Invoke(pgcNo);
            DvdTitlePlayback? tp;
            try { tp = open(pgcs[i].Pgc.Number); } catch { tp = null; }
            if (tp is null || tp.CellCount == 0) continue; // 命令のみPGC（背景なし）。
            try { WriteMenuPgc(writer, opt, work, log, ct, domain, pgcNo, (int)pgcs[i].Type, tp); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.AppendLine($"  {domain}/PGC{pgcNo:00}: メニュー生成失敗（スキップ）: {ex.Message}"); }
        }
    }

    // v3 メニュー（セル単位ファイル形式）。PGC を「1本の video.mkv＋buttons.json」ではなく、各セルを独立した
    // C<nn>.mkv（映像+音声）／C<nn>.spu（SPU図柄・任意）へ割り、per-VOBU の時刻付き PCI と全メタを menu.json へ書く。
    // v2 で起きた「メニューページ着地の時刻シーク丸めで1ページずれ・SPU多重化前後のハイライトずれ」を、
    // セル単位ファイル化で構造的に排除する（seek/丸め/SPU多重化なし。静止=Cell=1フレーム、連続モーションは
    // seamlessNext のセル群を再生側で playlist 連結）。目標＝内蔵ISOエンジンとのメニュー再生パリティ。
    private static void WriteMenuPgc(KurokoZipWriter writer, DvdH264Converter.Options opt, string work,
        StringBuilder log, System.Threading.CancellationToken ct, string domain, int pgcNo, int menuType, DvdTitlePlayback tp)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        int nCells = tp.CellCount;
        bool interlaced = string.Equals(opt.Deinterlace, "none", StringComparison.OrdinalIgnoreCase);
        double fieldDurMs = tp.VideoAttr.IsPal ? 20.0 : 1001.0 / 60000.0 * 1000.0; // 1フィールド時間

        bool tff = true;
        if (interlaced)
            using (var psd = tp.OpenStream(0, nCells - 1))
                tff = DvdH264Converter.DetectTopFieldFirstFromStream(opt.FfprobePath, psd, ct, out _, out _);

        // メニュー背景音声：タイトルと同じ BuildAudioPlan → IFO 論理音声順（AST_CTL）へ整列。
        bool menuAudioRecompress = string.Equals(opt.AudioMode, "recompress", StringComparison.OrdinalIgnoreCase);
        List<AudioPlan> audioPlan;
        using (var psa = tp.OpenStream(0, nCells - 1))
            audioPlan = BuildAudioPlan(opt.FfprobePath, psa, menuAudioRecompress, opt.AudioBitrateK, ct, log, opt.AudioCodec, DownmixToStereo(opt));
        var audioMap = OrderAudioPlanByIfo(tp, audioPlan, log);
        audioPlan = audioMap.Select(x => x.Plan).ToList();
        var aLangs = audioMap.Select(x => x.Attr is { HasLanguage: true } at ? at.LanguageCode : null).ToList();

        // menu.json の音声コーデック名は**実際に C<nn>.mkv へ入れたもの**にする。再符号化しない（audio=copy）ときは
        // 元の音声のまま入るので、Opus の設定だけで "opus" と書くと再生側が別のコーデックとして扱い、音声が途切れる。
        var firstMenuAttr = audioMap.Select(x => x.Attr).FirstOrDefault(a => a is not null);
        string audioCodec = menuAudioRecompress
            ? (string.Equals(opt.AudioCodec, "opus", StringComparison.OrdinalIgnoreCase) ? "opus" : "ac3")
            : (firstMenuAttr is not null ? KurokoAudioCodec(firstMenuAttr.CodingMode) : "ac3");
        string videoCodec = KurokoVideoCodec(opt);
        string aspect = tp.VideoAttr.AspectRatio == "16:9" ? "16:9" : "4:3";

        // ── menu.json のメタ（内蔵 DvdPgc の全項目を運ぶ） ─────────────────────────────
        var menu = new KurokoMenuV3
        {
            Domain = domain,
            Pgc = pgcNo,
            MenuType = menuType,
            UopControl = tp.Pgc.UopControl,
            NextPgc = tp.Pgc.NextPgcn,
            PrevPgc = tp.Pgc.PrevPgcn,
            GoUpPgc = tp.Pgc.GoUpPgcn,
            Palette = tp.Pgc.Palette.ToList(),
            Video = new KurokoMenuV3Video
            {
                Codec = videoCodec,
                Width = tp.VideoAttr.DisplayWidth,
                Height = tp.VideoAttr.DisplayHeight,
                Aspect = aspect,
                Interlaced = interlaced,
                Tff = tff,
            },
            ProgramMap = tp.Pgc.ProgramEntryCells.Select(c => c - 1).ToList(), // version 5：0 始まり
        };
        foreach (var ac in tp.Pgc.AudioControl)
            menu.Audio.Add(new KurokoMenuV3Audio { Available = ac.Available, Number = ac.Number, Codec = audioCodec });
        foreach (var sc in tp.Pgc.SubpictureControl)
            menu.SpstCtl.Add(new KurokoMenuV3SpstCtl { Available = sc.Available, N43 = sc.N43, Wide = sc.NWide, Lb = sc.NLetterbox, Ps = sc.NPanScan });
        // §20「VM 命令の JSON 表現」に従い { hex, asm } で持つ。
        static KurokoVmCommand VmCmd(byte[] c)
            => new() { Hex = DvdVmDisassembler.Hex(c), Asm = DvdVmDisassembler.ToMnemonic(c) };
        menu.Commands.Pre = tp.Pgc.PreCommands.Select(VmCmd).ToList();
        menu.Commands.Post = tp.Pgc.PostCommands.Select(VmCmd).ToList();
        menu.Commands.Cell = tp.Pgc.CellCommands.Select(VmCmd).ToList();

        // ── 各セルを独立ファイル C<nn>.mkv（映像+音声）へ。SPU は C<nn>.spu、PCI は menu.json へ ──
        for (int c = 0; c < nCells; c++)
        {
            ct.ThrowIfCancellationRequested();
            var cell = tp.Pgc.Cells[c];

            // ソースPTS基準（0起点化）。単一セルは PTS リセットを跨がないのでラン分割は不要。ついでにソース実フレーム数も取る。
            long cellFirst, cellLast; int srcFrameCount;
            using (var ps2 = tp.OpenStream(c, c))
            { var r = ProbeCellRff(opt.FfprobePath, ps2, ct); cellFirst = r.FirstPts90k; cellLast = r.LastPts90k; srcFrameCount = r.FieldCounts.Count; }
            long tsOff = cellFirst >= 0 ? cellFirst : 0;

            // all-intra（各セル独立Iフレーム）判定。IFO の静止フラグ（StillTime≠0）に加え、**実フレームが1枚のセル**も all-intra にする。
            // GOP＋cutree だと単フレーム（未来参照なし）が cutree に品質を回されず劣化する（音声つき1枚静止メニュー=StillTime0 が該当。
            // 音声なし静止=StillTime≠0 は元から all-intra なので劣化しない）。この分岐漏れを埋めて劣化度を揃える。
            // 本当のモーションメニュー（複数フレーム）は cutree 温存（still=false）＝効率維持。
            bool still = cell.StillTime != 0 || srcFrameCount <= 1;

            // セル末尾の音声読み足し（EncodeSegment と同型）。セル末尾ぶんの音声は DVD 多重化の遅延で
            // **次セルの先頭に流れ込んで**格納されており、tp.OpenStream(c,c) だけでは映像終端より手前で
            // 音声が切れる（境界に数十msのギャップ→大音量が跨ぐと変質して聞こえる）。次セル(c+1)が PTS 連続なら、
            // その先頭を TailPeekBytes だけ読み足して「境界までの音声」を採り、映像は境界でトリムする。頭刈り込み
            // （前セルからの流れ込み破棄）は atrim start=-tsOffsetSec で既に効いているので、これで境界のギャップを閉じる。
            double trimEndSec = 0;
            if (c + 1 < nCells && cellLast >= 0)
            {
                try
                {
                    using var pn = new LimitedStream(tp.OpenStream(c + 1, c + 1), TailPeekBytes);
                    long next90k = ProbeCellRff(opt.FfprobePath, pn, ct).FirstPts90k;
                    // next90k > cellLast のときだけ末尾拡張する（＝境界が現セルの映像最終PTSより後）。
                    // 「>=」だと、多ページ静止メニューのように全セルが同一VOBUを共有し next90k==cellLast==唯一の映像PTS
                    // になる場合に trim=end=<その映像PTS>（排他）が唯一のフレームを落とし、映像0フレームの空mkvになる。
                    if (next90k > cellLast) trimEndSec = next90k / 90000.0; // PTS 連続かつ映像最終より後のみ
                }
                catch (Exception ex) { log.AppendLine($"  {domain}/PGC{pgcNo:00} cell{c}: 次セル先頭probe失敗（末尾拡張なし）: {ex.Message}"); }
            }
            bool tailExtend = trimEndSec > 0;

            // 表示順フレームのフィールド数・ソースPTSから 0起点クリーンPTS列を作る（映像と SPU/PCI を同一軸に）。
            // **符号化より前**に採る：音声の atrim 末尾をフィールド時間軸（＝このセルの宣言尺）で決めるため。
            List<byte> fld; List<long> srcPts; List<bool> prg;
            using (var psR = tp.OpenStream(c, c))
            { var pr = ProbeRunFrames(opt.FfprobePath, psR, ct); fld = pr.Fields; srcPts = pr.Pts90k; prg = pr.Progressive; }
            double cellFieldMs = 0; foreach (var f in fld) cellFieldMs += f * fieldDurMs;
            double aTrimEndM = cellFieldMs > 0 ? tsOff / 90000.0 + cellFieldMs / 1000.0 : trimEndSec;

            string rawMkv = Path.Combine(work, $"menu_{domain}_{pgcNo:00}_c{c:00}_raw.mkv");
            string cargs = BuildKurokoFfmpegArgs(opt, tp.VideoAttr, interlaced, tff, rawMkv,
                -tsOff / 90000.0, audioPlan, aLangs, System.Array.Empty<int>(), System.Array.Empty<string?>(), null,
                allIntra: still, videoTrimEndSec: trimEndSec, audioTrimEndSec: aTrimEndM);
            using (var ps = tailExtend
                ? (Stream)new ConcatStream(tp.OpenStream(c, c),
                                           new LimitedStream(tp.OpenStream(c + 1, c + 1), TailPeekBytes))
                : tp.OpenStream(c, c))
            {
                int rc = RunEncode(opt.FfmpegPath, cargs, ps, out string ce, ct, null);
                if (!File.Exists(rawMkv) || new FileInfo(rawMkv).Length == 0)
                { log.AppendLine($"  {domain}/PGC{pgcNo:00} cell{c}: 符号化失敗 rc={rc}\n{Tail(ce)}"); return; }
            }

            int nEnc = ProbeVideoFrameCount(opt.FfprobePath, rawMkv);
            NoteFrameCountMismatch(log, $"  {domain}/PGC{pgcNo:00} cell{c}(符号化)", fld.Count, nEnc);
            if (nEnc > 0)
            {
                while (fld.Count < nEnc) { fld.Add(fld.Count > 0 ? fld[^1] : (byte)2); srcPts.Add(srcPts.Count > 0 ? srcPts[^1] : 0); }
                if (fld.Count > nEnc) { fld.RemoveRange(nEnc, fld.Count - nEnc); srcPts.RemoveRange(nEnc, srcPts.Count - nEnc); }
            }
            var frameCleanMs = new List<double>(fld.Count);
            double tMs = 0;
            for (int i = 0; i < fld.Count; i++) { frameCleanMs.Add(tMs); tMs += fld[i] * fieldDurMs; }
            double cellCleanEndMs = frameCleanMs.Count > 0 ? tMs : cell.Duration.TotalMilliseconds;

            // mkvmerge remux（単一フレーム等の不明タイムスタンプを 0起点の有効PTSへ振り直す。HEVC でも確実）
            // → クリーンPTS(フィールド時間)へ焼き直す（mkvmerge --timestamps）。
            string remux = Path.Combine(work, $"menu_{domain}_{pgcNo:00}_c{c:00}_rx.mkv");
            if (!MkvmergeConcat(opt, new[] { rawMkv }, remux, ct, out string rxErr))
            { log.AppendLine($"  {domain}/PGC{pgcNo:00} cell{c}: remux失敗\n{Tail(rxErr)}"); return; }
            int nAv = ProbeVideoFrameCount(opt.FfprobePath, remux);
            NoteFrameCountMismatch(log, $"  {domain}/PGC{pgcNo:00} cell{c}(PTS焼き)", frameCleanMs.Count, nAv);
            var tsAll = new List<double>(frameCleanMs);
            if (nAv > 0)
            {
                while (tsAll.Count < nAv) tsAll.Add(tsAll.Count > 0 ? tsAll[^1] + fieldDurMs * 2 : 0);
                if (tsAll.Count > nAv) tsAll.RemoveRange(nAv, tsAll.Count - nAv);
            }
            string tsFile = Path.Combine(work, $"menu_{domain}_{pgcNo:00}_c{c:00}_ts.txt");
            {
                var sbts = new StringBuilder("# timestamp format v2\n");
                foreach (var t in tsAll) sbts.Append(t.ToString("F3", inv)).Append('\n');
                File.WriteAllText(tsFile, sbts.ToString());
            }
            string cellMkv = Path.Combine(work, $"menu_{domain}_{pgcNo:00}_c{c:00}.mkv");
            RunFfmpegNoInput(opt.MkvmergePath, $"-o \"{cellMkv}\" {DvdH264Converter.MkvTimestampScaleArg} --timestamps \"0:{tsFile}\" \"{remux}\"", ct, out string mErr);
            if (!File.Exists(cellMkv) || new FileInfo(cellMkv).Length == 0)
            { log.AppendLine($"  {domain}/PGC{pgcNo:00} cell{c}: PTS焼き直し失敗\n{Tail(mErr)}"); return; }
            writer.AddFile(KurokoLayout.MenuCellAv(domain, pgcNo, c), cellMkv);

            int nMap = Math.Min(srcPts.Count, frameCleanMs.Count);

            // このセルのSPU（ボタン図柄）を C<nn>.spu へ外出し（物理サブID昇順・クリーンPTS焼き。多重化なし）。
            var cellSpu = new Dictionary<int, List<(long SourcePts90k, byte[] Data)>>();
            using (var pss = tp.OpenStream(c, c)) ExtractSubpictures(pss, cellSpu);
            var spuOut = new Dictionary<int, List<(long Pts90k, byte[] Data)>>();
            foreach (var kv in cellSpu)
                foreach (var (sp, data) in kv.Value)
                {
                    double cms = MapSourcePtsToCleanMs(sp, srcPts, frameCleanMs, nMap);
                    if (!spuOut.TryGetValue(kv.Key, out var lst)) spuOut[kv.Key] = lst = new List<(long, byte[])>();
                    lst.Add(((long)Math.Round(Math.Max(0, cms) / 1000.0 * 90000.0), data));
                }
            string spuName = "";
            if (spuOut.Any(kv => kv.Value.Count > 0))
            {
                string spuFile = Path.Combine(work, $"menu_{domain}_{pgcNo:00}_c{c:00}.spu");
                BuildSubsPs(spuOut, spuFile);
                writer.AddBytes(KurokoLayout.MenuCellSpu(domain, pgcNo, c), File.ReadAllBytes(spuFile));
                spuName = $"C{c:00}.spu";
            }

            // per-VOBU PCI（モーション中のハイライト更新）を時刻付き（fromMs..toMs）で収集。
            var mcell = new KurokoMenuV3Cell
            {
                Index = c,
                Still = cell.StillTime == 0xFF ? -1 : cell.StillTime,
                CellCommand = cell.CommandNr,
                DurationMs = cellCleanEndMs, // C<nn>.mkv の実尺（焼込みPTS基準）＝playlist offset・セル判定用
                SeamlessNext = (cell.Category & 0x08) != 0, // C_CAT seamless_play（次セルと継ぎ目なし）
                Static = srcFrameCount <= 1, // 実フレーム1枚＝静止セル。再生側は bob せず weave（内蔵ISOと同じ挙動）
                // 本当にインターレースな区間だけを bob するための情報（タイトル＝seg.json と同じ考え方）。
                // 時刻はセル内ローカル ms（C<nn>.mkv の PTS 基準）。フィルム（3:2）やプログレッシブのセルは空になる。
                // 従来はメニューを常に全 bob していたため、フィルム由来のモーションメニューがちらついていた。
                Cadence = MenuCadenceOf(prg),
                InterlacedRanges = BuildInterlacedRanges(frameCleanMs, prg, fieldDurMs) ?? new List<KurokoRange>(),
                Av = $"C{c:00}.mkv",
                Spu = spuName,
            };
            List<(long SrcPts90k, DvdNavPci Pci)> pcis;
            using (var psp = tp.OpenStream(c, c)) pcis = ExtractCellPcis(psp);
            for (int i = 0; i < pcis.Count; i++)
            {
                var (sp, pci) = pcis[i];
                double fromMs = sp >= 0 ? MapSourcePtsToCleanMs(sp, srcPts, frameCleanMs, nMap) : (i > 0 ? cellCleanEndMs : 0);
                double toMs = i + 1 < pcis.Count && pcis[i + 1].SrcPts90k >= 0
                    ? MapSourcePtsToCleanMs(pcis[i + 1].SrcPts90k, srcPts, frameCleanMs, nMap)
                    : Math.Max(cellCleanEndMs, cell.Duration.TotalMilliseconds);
                mcell.Pci.Add(BuildV3Pci(pci, fromMs, toMs));
            }
            menu.Cells.Add(mcell);
        }

        writer.AddBytes(KurokoLayout.MenuJson(domain, pgcNo), KurokoJson.Serialize(menu));

        int btnCells = menu.Cells.Count(x => x.Pci.Any(p => p.Buttons.Count > 0));
        log.AppendLine($"  {domain}/PGC{pgcNo:00}: v3 cells={nCells} btnCells={btnCells} still=[{string.Join(",", menu.Cells.Select(x => x.Still))}] audio={audioPlan.Count} spuCells={menu.Cells.Count(x => x.Spu.Length > 0)}");
    }

    // 内蔵 DvdNavPci を v3 の時刻付き PCI（fromMs..toMs の間だけ有効）へ写す。
    private static KurokoMenuV3Pci BuildV3Pci(DvdNavPci pci, double fromMs, double toMs)
    {
        var m = new KurokoMenuV3Pci
        {
            FromMs = fromMs,
            ToMs = toMs,
            HighlightStatus = pci.HighlightStatus,
            VobuUop = pci.VobuUopControl,
            ForcedSelect = pci.ForcedSelect,
            ForcedActivate = pci.ForcedActivate,
        };
        foreach (var b in pci.Buttons)
            m.Buttons.Add(new KurokoMenuV3Button
            {
                Number = b.Number,
                X0 = b.XStart, Y0 = b.YStart, X1 = b.XEnd, Y1 = b.YEnd,
                ColorGroup = b.ColorGroup, AutoAction = b.AutoAction,
                Up = b.Up, Down = b.Down, Left = b.Left, Right = b.Right,
                Command = new KurokoVmCommand { Hex = DvdVmDisassembler.Hex(b.Command), Asm = DvdVmDisassembler.ToMnemonic(b.Command) },
            });
        foreach (var g in pci.ColorGroups)
            m.ColorGroups.Add(new KurokoMenuV3ColorGroup
            {
                SelectColor = g.SelectColor.ToList(), SelectContrast = g.SelectContrast.ToList(),
                ActionColor = g.ActionColor.ToList(), ActionContrast = g.ActionContrast.ToList(),
            });
        return m;
    }

    // メニューセルPSを走査し、各VOBU先頭の NAV パック（PCI＝private_stream_2 の先頭0x00）を解析して、
    // その VOBU の表示開始に相当する「直後の映像PES のソースPTS(90k)」と対にして表示順で返す。
    // per-VOBU のハイライト更新（FOSL/色/ボタン矩形）を時刻付きで menu.json へ運ぶために使う。
    // 末尾で映像PTSに出会わなかった PCI は SrcPts90k=-1（呼び出し側でセル末時刻へ寄せる）。
    private static List<(long SrcPts90k, DvdNavPci Pci)> ExtractCellPcis(Stream ps)
    {
        var src = new BufferedStream(ps, 1 << 16);
        var result = new List<(long, DvdNavPci)>();
        byte[]? pendingPci = null; // 直近に読んだ PCI（次の映像PTSで表示開始時刻を確定）

        byte[]? ReadN(int n)
        {
            var buf = new byte[n]; int got = 0;
            while (got < n) { int r = src.Read(buf, got, n - got); if (r <= 0) return null; got += r; }
            return buf;
        }
        void SkipN(int n) { for (int i = 0; i < n; i++) if (src.ReadByte() < 0) return; }
        static long? PesPts(byte[] body)
        {
            if (body.Length < 9 || (body[0] & 0xC0) != 0x80) return null; // PES拡張の '10' マーカ
            if ((body[1] & 0x80) == 0) return null;                       // PTS_DTS_flags: PTSなし
            return ((long)(body[3] & 0x0E) << 29) | ((long)body[4] << 22) | ((long)(body[5] & 0xFE) << 14)
                 | ((long)body[6] << 7) | ((long)(body[7] & 0xFE) >> 1);
        }

        int b0 = src.ReadByte(), b1 = src.ReadByte(), b2 = src.ReadByte();
        while (b2 != -1)
        {
            if (!(b0 == 0 && b1 == 0 && b2 == 1)) { b0 = b1; b1 = b2; b2 = src.ReadByte(); continue; }
            int id = src.ReadByte();
            if (id < 0) break;
            if (id == 0xBA) { var h = ReadN(10); if (h == null) break; SkipN(h[9] & 0x07); }
            else if (id == 0xB9) break; // program end
            else if (id == 0xBF) // private_stream_2（NAV）。PCI は先頭バイト 0x00（DSI=0x01 は無視）。
            {
                int hi = src.ReadByte(), lo = src.ReadByte(); if (lo < 0) break;
                var body = ReadN((hi << 8) | lo); if (body == null) break;
                if (body.Length > 0 && body[0] == 0x00) pendingPci = body;
            }
            else if (id >= 0xE0 && id <= 0xEF) // 映像PES：PTS付きなら pending PCI の表示開始時刻を確定。
            {
                int hi = src.ReadByte(), lo = src.ReadByte(); if (lo < 0) break;
                var body = ReadN((hi << 8) | lo); if (body == null) break;
                if (pendingPci != null)
                {
                    long? pts = PesPts(body);
                    if (pts.HasValue)
                    {
                        var parsed = DvdNavPci.Parse(pendingPci);
                        if (parsed != null) result.Add((pts.Value, parsed));
                        pendingPci = null;
                    }
                }
            }
            else if (id >= 0xBB) { int hi = src.ReadByte(), lo = src.ReadByte(); if (lo < 0) break; SkipN((hi << 8) | lo); }
            b0 = src.ReadByte(); b1 = src.ReadByte(); b2 = src.ReadByte();
        }
        if (pendingPci != null) { var parsed = DvdNavPci.Parse(pendingPci); if (parsed != null) result.Add((-1, parsed)); }
        return result;
    }

    // mkv/動画の尺（秒）を ffprobe で取得。失敗時は 0。
    private static double ProbeDurationSec(string ffprobe, string path)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -show_entries format=duration -of default=nw=1:nk=1 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return double.TryParse(outp.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch { return 0; }
    }

    // progressive 以外（＝本当のインターレース）の連続フレームを、クリーンPTS(ms)の区間 [Start,End) として列挙。
    // 純 film（全 progressive）なら null。再生側はフレームの容器PTS(=実時間)がこの区間にあれば bob、無ければ weave。
    // メニューセルのケイデンス表記（seg.json の video.cadence と同じ語彙）。判定の実体は interlacedRanges で、
    // これは人が読んで分かるための記録。全数調査では手持ちディスクのメニューに interlaced/film/progressive が
    // 混在しており（セル内混在は 0.5% 未満の少数フレームのみ）、PGC 単位の真偽では表現できない。
    private static string MenuCadenceOf(List<bool> prog)
    {
        int p = 0, i = 0;
        foreach (var x in prog) { if (x) p++; else i++; }
        if (p + i == 0) return "progressive";
        if (i == 0) return "progressive";
        if (p == 0) return "interlaced";
        return "mixed";
    }

    private static List<KurokoRange>? BuildInterlacedRanges(List<double> tsMs, List<bool> prog, double fieldDurMs)
    {
        var ranges = new List<KurokoRange>();
        int n = System.Math.Min(tsMs.Count, prog.Count);
        int i = 0;
        while (i < n)
        {
            if (prog[i]) { i++; continue; }
            int j = i; while (j < n && !prog[j]) j++;
            double start = tsMs[i] - 0.5; // 端の境界フレーム取りこぼし回避に半msぶん広げる
            double end = (j < tsMs.Count ? tsMs[j] : tsMs[n - 1] + fieldDurMs * 2) + 0.5;
            ranges.Add(new KurokoRange { StartMs = start, EndMs = end });
            i = j;
        }
        return ranges.Count > 0 ? ranges : null;
    }

    // mkvmerge で複数 mkv を連結（append）して1本へ再muxする（1本でも remux）。
    // ffmpeg の concat(-c copy) と違い、タイムスタンプ不明のブロック（-output_ts_offset で僅かに負へ落ちた
    // 単一フレーム等）にも有効なPTSを振り直せる（HEVC はパーサ補完が効かず concat では
    // "Can't write packet with unknown timestamp" になる）。mkvmerge は警告で exit=1 を返すため、
    // 成否は出力ファイルの実体で判定する。
    private static bool MkvmergeConcat(DvdH264Converter.Options opt, IReadOnlyList<string> parts, string outPath,
        System.Threading.CancellationToken ct, out string stderr)
    {
        var a = new StringBuilder($"-o \"{outPath}\" {DvdH264Converter.MkvTimestampScaleArg} \"{parts[0]}\"");
        for (int i = 1; i < parts.Count; i++) a.Append($" + \"{parts[i]}\"");
        RunFfmpegNoInput(opt.MkvmergePath, a.ToString(), ct, out stderr);
        return File.Exists(outPath) && new FileInfo(outPath).Length > 0;
    }

    // 標準入力を使わない ffmpeg 実行（concat 等）。成功（exit 0）なら true。
    private static bool RunFfmpegNoInput(string exe, string args, System.Threading.CancellationToken ct, out string stderr)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            // stdout/stderr は必ず並行で吸い出す。逐次（stderr→stdout）だと、mkvmerge のように
            // 進捗を stdout へ大量出力するプロセスで stdout パイプが満杯→相手ブロック→デッドロックする。
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            stderr = errTask.GetAwaiter().GetResult();
            outTask.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { stderr = ex.Message; return false; }
    }

    // Kuroko メタデータ用の映像コーデック名（"h264"/"h265"/"av1" に正規化）。
    private static string KurokoVideoCodec(DvdH264Converter.Options opt)
        => KurokoVideoCodecs.Normalize(opt.VideoCodec);

    private static string KurokoAudioCodec(DvdAudioCodingMode m) => m switch
    {
        DvdAudioCodingMode.Ac3 => "ac3",
        DvdAudioCodingMode.Dts => "dts",
        DvdAudioCodingMode.Lpcm => "pcm",
        DvdAudioCodingMode.Mpeg1 or DvdAudioCodingMode.Mpeg2Ext => "mp2",
        _ => "ac3",
    };

    // Kuroko の av.mkv 用 ffmpeg 引数。BuildFfmpegArgs の matroska 版（-streamid/VOB特化を外し、
    // トラック言語メタデータを付ける。トラック順＝映像→音声(plan順)→字幕(id順)で MkvTrack と一致）。
    // videoTrimEndSec > 0：**ソースPTS軸**の境界（次セルの先頭映像PTS秒）。どのフレームを符号化するかの選択
    // なので、必ずソース軸で与える。入力を次セル先頭まで読み足した「セグメント末尾ラン拡張」で使う。
    // audioTrimEndSec > 0：**このパーツ／ランの宣言尺の終端**（開始ソース秒＋フィールド時間合計。疎セルは IFO 尺）。
    // 音声は「フレームが表示されている期間ぶん」が中に揃っている必要があるので、映像と同じ値を使ってはいけない
    //（宣言尺は fld×fieldDur の積み上げなので、ソフトテレシネがあるとソースPTS軸と一致しない。同値にしていた
    // 頃は 3:2 のパーツだけ音声がちょうど1フィールド不足した）。境界より後ろへ出っ張る分は再生側が切り詰める。
    // セル末尾の音声はDVD多重化の遅延で次セル先頭に流れ込んでいるため、読み足しは別途必要（EncodeSegment 参照）。
    // KUROKO.MANIFEST の comment 用：符号化パラメータを人間可読の1行にまとめる（ffmpeg/mkvmerge のパスは含めない）。
    // DVD/TS 両変換器が共用する。
    internal static string OptionsComment(DvdH264Converter.Options opt)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new List<string>
        {
            $"interlaceMode={opt.InterlaceMode}",
            $"videoCodec={opt.VideoCodec}",
            $"crf={opt.Crf}",
            $"keyint={opt.Keyint}",
            $"gop={opt.GopMode}",
            $"preset={opt.Preset}",
            $"deinterlace={opt.Deinterlace}",
            $"audioCodec={opt.AudioCodec}",
            $"audioBitrateKPerCh={opt.AudioBitrateK}",
            $"audioChannels={opt.AudioChannels}",
            $"audioMode={opt.AudioMode}",
            $"menuMode={opt.MenuMode}",
        };
        // fields のときだけ短区間統合閾値を記録（プログレッシブ隣接・この値未満の実インターレース区間を weave 化）。
        if (string.Equals(opt.InterlaceMode, "fields", StringComparison.OrdinalIgnoreCase))
            parts.Add($"fieldsMinInterlaceFrames={opt.FieldsMinInterlaceFrames}");
        if (opt.MaxSecondsPerCell > 0) parts.Add($"maxSecondsPerCell={opt.MaxSecondsPerCell.ToString(inv)}");
        return string.Join(", ", parts);
    }

    private static string BuildKurokoFfmpegArgs(DvdH264Converter.Options opt, DvdVideoAttr v, bool interlaced, bool tff,
        string outMkv, double tsOffsetSec, IReadOnlyList<AudioPlan> audioPlan, IReadOnlyList<string?> audioLangs,
        IReadOnlyList<int> subpIds, IReadOnlyList<string?> subLangs, string? forceKeyTimes, bool allIntra = false,
        double videoTrimEndSec = 0, double audioTrimEndSec = 0, bool fieldSeparate = false,
        double videoTrimStartSec = 0, bool dualTracks = false, bool audioOnly = false)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string aspect = v.AspectRatio == "16:9" ? "16:9" : "4:3";
        // dual：fields を top/bottom の半高2映像トラックへ分ける（filter_complex）。単一トラック separatefields（倍レート1本）と別。
        bool dual = fieldSeparate && dualTracks;
        var a = new StringBuilder();
        a.Append("-y -hide_banner -loglevel error -analyzeduration 100M -probesize 100M ");
        // 音声はフルダイナミックレンジで格納する（仕様 §14）。AC-3 デコード時に line-DRC(dynrng) を適用しない
        // （-drc_scale 0）。DRC/dialnorm は焼き込まず、別途メタとして保持し再生時に任意適用する。これで
        // 内蔵ISOエンジン（DRC無視のフルレンジ再生）と音量が一致し、聴取者の圧縮選択の自由も保つ。
        // -drc_scale は AC-3 デコーダのオプションなので -i の前に置く。DTS/LPCM/MPEG音声には無害。
        a.Append("-drc_scale 0 ");
        // テスト用：セグメント符号化の入力読込尺の上限（-i の前＝入力先頭からの尺で全セルに効く）。
        if (opt.MaxSecondsPerCell > 0) a.Append($"-t {opt.MaxSecondsPerCell.ToString(inv)} ");
        a.Append("-i pipe:0 ");
        // dual は filter_complex 出力（[vt]=top / [vb]=bottom）を先頭に map（映像=track0/1、音声=track2+）。
        // audioOnly＝音声だけを別パスで符号化する（下記）。映像は一切触らない。
        if (audioOnly) a.Append("-vn ");
        else a.Append(dual ? "-map \"[vt]\" -map \"[vb]\" " : "-map 0:v:0 ");
        for (int i = 0; i < audioPlan.Count; i++)
            a.Append($"-map 0:i:0x{audioPlan[i].OrigId:x}? ");
        for (int i = 0; i < subpIds.Count; i++)
            a.Append($"-map 0:i:0x{subpIds[i]:x}? ");
        a.Append("-fps_mode passthrough -copyts ");
        var vf = new List<string>();
        if (string.Equals(opt.Deinterlace, "ivtc", StringComparison.OrdinalIgnoreCase))
            vf.Add("fieldmatch,decimate");
        else if (!string.Equals(opt.Deinterlace, "none", StringComparison.OrdinalIgnoreCase))
            vf.Add($"{opt.Deinterlace.ToLowerInvariant()}=mode=send_field");
        // ケイデンス・パーツ／末尾ラン拡張のフレーム窓（絶対ソース秒。-copyts なので入力PTSは元PTS）。
        // start＝このパーツ先頭より前を落とす（前パーツ分）／end＝排他で次を落とす。音声側は atrim（下、tsOffset由来）で揃える。
        if (videoTrimStartSec > 0 || videoTrimEndSec > 0)
        {
            var tp = new List<string>();
            if (videoTrimStartSec > 0) tp.Add($"start={videoTrimStartSec.ToString("F6", inv)}");
            if (videoTrimEndSec > 0) tp.Add($"end={videoTrimEndSec.ToString("F6", inv)}");
            vf.Add($"trim={string.Join(":", tp)}");
        }
        // フィールド分離モード（interlace mode="fields"）：各フレームを T/B の半高さフレーム2枚（倍フレーム）へ分離し、
        // progressive として符号化する。setfield でフィールド順を明示（idet 検出の tff/bff）。trim の後に置き、境界
        // トリム後の各フレームを分離する。
        if (fieldSeparate && !dual)
            vf.Add($"setfield={(tff ? "tff" : "bff")},separatefields");
        if (audioOnly) { /* 映像フィルタは付けない */ }
        else if (dual)
        {
            // dual：共通前処理（deinterlace/trim 等の vf）後に split→field=top/bottom で半高2トラックへ。各トラックは
            // 元フレーム毎に1枚（元フレームレート）。field は type で明示抽出するので tff/bff の setfield は不要（TS変換器と同形）。
            string pre = vf.Count > 0 ? string.Join(",", vf) + "," : "";
            a.Append($"-filter_complex \"[0:v:0]{pre}split=2[sa][sb];[sa]field=type=top[vt];[sb]field=type=bottom[vb]\" ");
        }
        else if (vf.Count > 0)
            a.Append($"-vf {string.Join(",", vf)} ");
        bool av1 = KurokoVideoCodecs.IsAv1(opt.VideoCodec);
        // AV1 の全フレーム独立は毎フレームの強制で作るので、セル境界の強制は付けない（後の -force_key_frames が勝つ）。
        if (!audioOnly && !string.IsNullOrEmpty(forceKeyTimes) && !(av1 && allIntra))
            a.Append($"-force_key_frames {forceKeyTimes} ");
        // -forced-idr は x264/x265 の指定（AV1 は常にクローズド GOP でキーフレーム＝そこから復号できる）。
        if (!audioOnly && !av1 && string.Equals(opt.GopMode, "hybrid", StringComparison.OrdinalIgnoreCase))
            a.Append("-forced-idr 1 ");
        if (audioOnly) { /* 映像の符号化指定は付けない */ }
        else if (av1)
        {
            // AV1：H.265 と同じく weave フレームをプログレッシブとして符号化する（フィールド符号化の道具が無い）。
            // GOP は常にクローズド（KurokoVideoCodecs の注記）。
            KurokoVideoCodecs.AppendAv1Args(a, KurokoVideoCodecs.SvtPreset(opt.Preset), opt.Crf, aspect, opt.Keyint, allIntra);
        }
        else if (string.Equals(opt.VideoCodec, "h265", StringComparison.OrdinalIgnoreCase))
        {
            // H.265：フレーム/フィールド適応のインターレース符号化ツールが無いため、実インターレース
            //（interlaced=true）でも weave フレームを**プログレッシブとして**符号化する（フィールドの
            // 詰め替えは無損失。tff/ilme/ildct 相当は付けない）。krk メタデータ（video.interlaced/tff/
            // interlacedRanges）は従来どおり書かれ、再生側が bob 解除する（＝再生モデルは H.264 と同一）。
            a.Append($"-c:v libx265 -preset {opt.Preset} -crf {opt.Crf} -pix_fmt yuv420p -aspect {aspect} ");
            var hp = new List<string>();
            if (allIntra) { hp.Add("keyint=1"); hp.Add("scenecut=0"); } // 全フレーム独立Iフレーム（状態別静止メニュー用）
            else if (opt.Keyint > 0) { hp.Add($"keyint={opt.Keyint}"); hp.Add("scenecut=0"); }
            hp.Add("min-keyint=1");
            hp.Add(string.Equals(opt.GopMode, "closed", StringComparison.OrdinalIgnoreCase) ? "open-gop=0" : "open-gop=1");
            hp.Add("repeat-headers=1"); // 各IDRに VPS/SPS/PPS 在留（再生は extradata 不要の Annex-B 直食い）
            hp.Add("log-level=error");  // x265 の情報ログを抑止（stderr を ffmpeg のエラーに限定）
            a.Append($"-x265-params {string.Join(":", hp)} ");
        }
        else
        {
            // fields モードは半高さの progressive フレーム列なので ilme/tff は付けない（インターレース符号化しない）。
            string ilme = (interlaced && !fieldSeparate) ? "-flags +ilme+ildct " : "";
            a.Append($"-c:v libx264 -preset {opt.Preset} -crf {opt.Crf} -pix_fmt yuv420p -aspect {aspect} {ilme}");
            var xp = new List<string>();
            if (allIntra) { xp.Add("keyint=1"); xp.Add("scenecut=0"); } // 全フレーム独立Iフレーム（状態別静止メニュー用）
            else if (opt.Keyint > 0) { xp.Add($"keyint={opt.Keyint}"); xp.Add("scenecut=0"); }
            xp.Add("min-keyint=1");
            xp.Add(string.Equals(opt.GopMode, "closed", StringComparison.OrdinalIgnoreCase) ? "open-gop=0" : "open-gop=1");
            if (interlaced && !fieldSeparate) xp.Add(tff ? "tff=1" : "bff=1");
            xp.Add("repeat-headers=1");
            a.Append($"-x264-params {string.Join(":", xp)} ");
        }
        bool opus = string.Equals(opt.AudioCodec, "opus", StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < audioPlan.Count; i++)
        {
            if (audioPlan[i].Reencode)
            {
                // 出力チャンネル数はプラン側で決まる（Opus は設定次第で 2ch へ畳むか元のまま、AC-3 は常に 2ch）。
                int oc = audioPlan[i].Channels > 0 ? audioPlan[i].Channels : 2;
                a.Append(opus
                    ? $"-c:a:{i} libopus -b:a:{i} {audioPlan[i].BitrateK}k -ac:a:{i} {oc} "
                    : $"-c:a:{i} ac3 -b:a:{i} {audioPlan[i].BitrateK}k -ac:a:{i} {oc} ");
                // 音声先頭の刈り込み：セル/ラン先頭には前セルからの音声流れ込み（映像先頭より早いPTS）が普通にあり、
                // そのまま -output_ts_offset で負になると matroska muxer の avoid_negative_ts(auto) が**全ストリームを
                // 一括で正へシフト**する。後段の mkvmerge --timestamps は映像だけをクリーン軸へ焼き直すため、音声にだけ
                // シフトが残り「セグメントごとに一定量、音声コンテンツが遅れる」（v1=タイトル一本符号化では先頭にしか
                // 効かず無害だったが、v2 セグメント化で顕在化。8_h264/8_h265 の krk で実測 ~250ms）。映像開始前の音声を
                // サンプル精度で刈り込み、負を発生させない（流れ込み分は前セグメント側が持つ内容なので破棄が正しい）。
                // copy は filter 不可のため従来どおり（AC-3コピーは非既定。負→autoシフトの可能性が残る）。
                // audioTrimEndSec：宣言尺の終端（排他）。ソース軸の境界より後ろになることがあるが、その出っ張りは
                // 再生側がパーツ境界で切り詰めるので重複しない。逆に短く切ると境界に無音の穴が空く。
                if (tsOffsetSec < 0)
                    a.Append(audioTrimEndSec > 0
                        ? $"-filter:a:{i} atrim=start={(-tsOffsetSec).ToString("F6", inv)}:end={audioTrimEndSec.ToString("F6", inv)} "
                        : $"-filter:a:{i} atrim=start={(-tsOffsetSec).ToString("F6", inv)} ");
            }
            else
                a.Append($"-c:a:{i} copy ");
            if (audioLangs[i] is string al) a.Append($"-metadata:s:a:{i} language={al} ");
        }
        a.Append("-c:s copy ");
        for (int i = 0; i < subpIds.Count; i++)
            if (subLangs[i] is string sl) a.Append($"-metadata:s:s:{i} language={sl} ");
        if (tsOffsetSec != 0)
            a.Append($"-output_ts_offset {tsOffsetSec.ToString("F6", inv)} ");
        a.Append($"-f matroska \"{outMkv}\"");
        return a.ToString();
    }

    // 診断：タイトルの再生用プログラムストリーム（全セル連結）をファイルへダンプする。maxBytes>0 で先頭だけ。
    // 変換と同じ tp.OpenStream(0, last) を使うので、字幕など各サブストリームが変換入力と同一になる。
    public static string DumpTitlePs(string iso, int titleNumber, string outPs, long maxBytes = 0)
    {
        using var disc = DvdDisc.Open(iso);
        var tp = disc.OpenTitle(titleNumber);
        int last = tp.CellCount - 1;
        long written = 0;
        using (var ps = tp.OpenStream(0, last))
        using (var fs = File.Create(outPs))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = ps.Read(buf, 0, buf.Length)) > 0)
            {
                if (maxBytes > 0 && written + n > maxBytes) n = (int)(maxBytes - written);
                fs.Write(buf, 0, n);
                written += n;
                if (maxBytes > 0 && written >= maxBytes) break;
            }
        }
        return $"title {titleNumber} cells=0..{last} -> {outPs} ({written:N0} bytes)";
    }

    // 診断：1タイトルの字幕抽出→再パック→ffmpeg mux だけを実行し、物理サブストリーム別のSPU数/バイトと
    // 生成mkvのトラック別バイトを報告する（重い映像符号化を伴わず字幕パスのみ検証）。
    public static string SubtitleSelfTest(string iso, int titleNumber, string ffmpeg, string ffprobe, string outMkv)
    {
        var log = new StringBuilder();
        var ct0 = System.Threading.CancellationToken.None;
        using var disc = DvdDisc.Open(iso);
        var tp = disc.OpenTitle(titleNumber);
        int nCells = tp.CellCount; int last = nCells - 1;
        double fieldDurMs = tp.VideoAttr.IsPal ? 20.0 : 1001.0 / 60000.0 * 1000.0;
        // 実変換と同じ：セル走査→PTSリセットでラン分割→ラン毎にフレーム表(ソースPTS→クリーンms)を作り写像。
        var cellFirst = new long[nCells]; var runStarts = new List<int>(); long prevLast = -1;
        for (int k = 0; k < nCells; k++)
        {
            using var ps2 = tp.OpenStream(k, k);
            var r = ProbeCellRff(ffprobe, ps2, ct0); long first = r.FirstPts90k, lst2 = r.LastPts90k;
            cellFirst[k] = first;
            if (k == 0 || (prevLast >= 0 && first >= 0 && first < prevLast - 90000)) runStarts.Add(k);
            if (lst2 >= 0) prevLast = lst2;
        }
        runStarts.Add(nCells);
        int runCount = runStarts.Count - 1;
        var sink = new Dictionary<int, List<(long, byte[])>>();
        double cum = 0;
        for (int rr = 0; rr < runCount; rr++)
        {
            int a = runStarts[rr], b = runStarts[rr + 1] - 1;
            List<long> srcPts; List<byte> fld;
            using (var psR = tp.OpenStream(a, b)) { var pr = ProbeRunFrames(ffprobe, psR, ct0); srcPts = pr.Pts90k; fld = pr.Fields; }
            var frameCleanMs = new List<double>(fld.Count);
            double t = cum * 1000.0; for (int i = 0; i < fld.Count; i++) { frameCleanMs.Add(t); t += fld[i] * fieldDurMs; }
            double cum0 = (rr == 0) ? 0 : frameCleanMs[0] / 1000.0;
            cum = t / 1000.0;
            int nMap = Math.Min(srcPts.Count, frameCleanMs.Count);
            var raw = new Dictionary<int, List<(long SourcePts90k, byte[] Data)>>();
            using (var ps = tp.OpenStream(a, b)) ExtractSubpictures(ps, raw);
            long firstSp = raw.SelectMany(kv => kv.Value).Select(x => x.Item1).DefaultIfEmpty(-1).Min();
            log.AppendLine($"  [run{rr}] cells{a}-{b} cum0={frameCleanMs.FirstOrDefault() / 1000:F2} srcPts={srcPts.FirstOrDefault() / 90000.0:F2}..{(nMap > 0 ? srcPts[nMap - 1] / 90000.0 : 0):F2} fcmEnd={(nMap > 0 ? frameCleanMs[nMap - 1] / 1000 : 0):F2} firstSpu={firstSp / 90000.0:F2}->{(firstSp >= 0 ? MapSourcePtsToCleanMs(firstSp, srcPts, frameCleanMs, nMap) / 1000 : 0):F2}");
            foreach (var kv in raw)
            {
                if (!sink.TryGetValue(kv.Key, out var lst)) sink[kv.Key] = lst = new List<(long, byte[])>();
                foreach (var (sp, data) in kv.Value)
                    lst.Add(((long)Math.Round(Math.Max(0, MapSourcePtsToCleanMs(sp, srcPts, frameCleanMs, nMap)) / 1000.0 * 90000.0), data));
            }
        }
        log.AppendLine($"title {titleNumber}: cells=0..{last} runs={runCount} runStarts=[{string.Join(",", runStarts)}]");
        foreach (var sid in sink.Keys.OrderBy(k => k))
        {
            var times = sink[sid].Select(x => x.Item1 / 90000.0).ToList();
            log.AppendLine($"  抽出 0x{sid:X2}: SPU={sink[sid].Count} bytes={sink[sid].Sum(x => (long)x.Item2.Length):N0} first={(times.Count > 0 ? times.Min() : 0):F1}s last={(times.Count > 0 ? times.Max() : 0):F1}s");
        }
        var subpIds = sink.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(k => k).ToList();
        if (subpIds.Count == 0) { log.AppendLine("  字幕なし"); return log.ToString(); }
        // 診断：各SPUのDCSQ開始/停止遅延を出力（大きな開始遅延＝表示遅延が効いているか確認）。
        foreach (var sid in subpIds)
        {
            log.AppendLine($"  --- 0x{sid:X2} SPU別 clean時刻/DCSQ遅延 ---");
            foreach (var (pts, data) in sink[sid])
            {
                var (sd, pd) = ParseSpuDelays(data);
                double clean = pts / 90000.0; // sink はフレーム表で写像済みのクリーン時刻
                if (sd > 0.5 || clean > 625 && clean < 665) // 大遅延 or 631s周辺だけ表示
                    log.AppendLine($"    clean={clean:F3}s size={data.Length} startDelay={sd:F3}s stopDelay={(double.IsInfinity(pd) ? "∞" : pd.ToString("F3"))}");
            }
        }
        string subsPs = Path.ChangeExtension(outMkv, ".subs.ps");
        BuildSubsPs(sink, subsPs);
        var sa = new StringBuilder($"-y -hide_banner -loglevel error -i \"{subsPs}\" ");
        foreach (var sid in subpIds) sa.Append($"-map 0:i:0x{sid:x}? ");
        sa.Append($"-copyts -c:s copy -f matroska \"{outMkv}\"");
        RunFfmpegNoInput(ffmpeg, sa.ToString(), System.Threading.CancellationToken.None, out string err);
        log.AppendLine($"  subs.ps={new FileInfo(subsPs).Length:N0}B ffmpeg: {(File.Exists(outMkv) ? new FileInfo(outMkv).Length + "B" : "失敗\n" + Tail(err))}");
        return log.ToString();
    }

    // ソース字幕PTS を「ソース映像フレームPTS(表示順=昇順)→クリーン時刻(ms)」表で線形補間して写像する。
    // 映像フレームが無い区間(PTSギャップ)は両端のクリーン時刻間に潰れるので、字幕が映像のフレーム時刻軸に正しく乗る。
    private static double MapSourcePtsToCleanMs(long pts, List<long> srcPts, List<double> cleanMs, int n)
    {
        if (n <= 0) return 0;
        if (pts <= srcPts[0]) return cleanMs[0];
        if (pts >= srcPts[n - 1]) return cleanMs[n - 1];
        int lo = 0, hi = n - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (srcPts[mid] <= pts) lo = mid; else hi = mid - 1; }
        long p0 = srcPts[lo], p1 = srcPts[lo + 1];
        double c0 = cleanMs[lo], c1 = cleanMs[lo + 1];
        if (p1 <= p0) return c0;
        return c0 + (double)(pts - p0) / (p1 - p0) * (c1 - c0);
    }

    // 診断：SPUのDCSQを解釈して START_DISPLAY/STOP_DISPLAY の遅延（秒）を返す（DvdSpuDecoder と同等）。
    private static (double startDelay, double stopDelay) ParseSpuDelays(byte[] spu)
    {
        if (spu.Length < 6) return (0, double.PositiveInfinity);
        int size = (spu[0] << 8) | spu[1];
        int p = (spu[2] << 8) | spu[3];
        double start = 0, stop = double.PositiveInfinity;
        int guard = 0;
        while (p + 4 <= spu.Length && p + 4 <= size && guard++ < 64)
        {
            int delay = (spu[p] << 8) | spu[p + 1];
            int next = (spu[p + 2] << 8) | spu[p + 3];
            double when = delay * 1024.0 / 90000.0;
            int q = p + 4; bool end = false;
            while (q < spu.Length && q < size && !end)
            {
                int cmd = spu[q++];
                switch (cmd)
                {
                    case 0x00: case 0x01: start = when; break;
                    case 0x02: stop = when; break;
                    case 0x03: case 0x04: q += 2; break;
                    case 0x05: q += 6; break;
                    case 0x06: q += 4; break;
                    case 0x07: if (q + 2 <= spu.Length) { int len = (spu[q] << 8) | spu[q + 1]; q += Math.Max(2, len); } else end = true; break;
                    default: end = true; break;
                }
            }
            if (next == p || next < ((spu[2] << 8) | spu[3]) || next + 4 > size) break;
            p = next;
        }
        return (start, stop);
    }

    // 字幕サブピクチャ抽出：1ランの再生用PS（tp.OpenStream(a,b)）をストリーム解析し、private_stream_1（0xBD）の
    // substream 0x20-0x3F（サブピクチャ）の SPU を集める。ffmpeg の probesize 依存（2本目以降の字幕サブストリームが
    // 100MB以降に初出すると検出されず欠落する）を回避するため自前で全SPUを拾う。
    //
    // タイミング：ここでは各SPUに【ソースPES PTS】をそのまま付けて返す。クリーン時刻への写像は呼び出し側で
    // 「ソース映像フレームPTS→クリーン(tsAllMs)」対応表を使って行う（字幕PTSと映像フレーム計数の乖離＝PTSギャップや
    // 字幕/映像で基準が違う素材でも、映像と同じフレーム時刻軸に正しく乗る）。
    private static void ExtractSubpictures(Stream ps, Dictionary<int, List<(long SourcePts90k, byte[] Data)>> sink)
    {
        var src = new BufferedStream(ps, 1 << 20);
        var asmBuf = new Dictionary<int, List<byte>>();   // 組み立て中SPU
        var asmNeed = new Dictionary<int, int>();          // SPDSZ（SPU総バイト）
        var asmPts = new Dictionary<int, long>();          // SPU先頭PTS（ソース字幕PTS）

        void Emit(int sid)
        {
            if (!asmBuf.TryGetValue(sid, out var b) || b.Count == 0) return;
            int need = asmNeed.TryGetValue(sid, out var n) && n > 0 ? n : b.Count;
            int take = Math.Min(need, b.Count);
            var data = new byte[take];
            b.CopyTo(0, data, 0, take);
            long subPts = asmPts.TryGetValue(sid, out var p) ? p : 0;
            if (!sink.TryGetValue(sid, out var lst)) sink[sid] = lst = new List<(long, byte[])>();
            lst.Add((subPts, data));
            b.Clear(); asmNeed[sid] = 0;
        }

        void Feed(byte[] body)
        {
            if (body.Length < 4 || (body[0] & 0xC0) != 0x80) return; // PES拡張の '10' マーカ
            int flags = body[1];
            int hdrLen = body[2];
            int payOff = 3 + hdrLen;
            if (payOff >= body.Length) return;
            long? pts = null;
            if ((flags & 0x80) != 0 && payOff >= 8)
                pts = ((long)(body[3] & 0x0E) << 29) | ((long)body[4] << 22) | ((long)(body[5] & 0xFE) << 14)
                    | ((long)body[6] << 7) | ((long)(body[7] & 0xFE) >> 1);
            int sid = body[payOff];
            if (sid < 0x20 || sid > 0x3F) return; // サブピクチャのみ（音声0x80等は無視）
            int dataOff = payOff + 1;
            int dataLen = body.Length - dataOff;
            if (dataLen <= 0) return;

            if (pts.HasValue)
            {
                Emit(sid); // 直前の未完SPUがあれば確定（PTS付きPES＝新SPU開始の目印）
                var nb = new List<byte>(dataLen);
                for (int i = dataOff; i < body.Length; i++) nb.Add(body[i]);
                asmBuf[sid] = nb; asmPts[sid] = pts.Value;
                asmNeed[sid] = nb.Count >= 2 ? (nb[0] << 8) | nb[1] : 0;
            }
            else
            {
                if (!asmBuf.TryGetValue(sid, out var b)) return; // 開始PESを見ていない継続は捨てる
                for (int i = dataOff; i < body.Length; i++) b.Add(body[i]);
                if ((!asmNeed.TryGetValue(sid, out var nn) || nn == 0) && b.Count >= 2) asmNeed[sid] = (b[0] << 8) | b[1];
            }
            if (asmBuf.TryGetValue(sid, out var bb) && asmNeed.TryGetValue(sid, out var need2) && need2 > 0 && bb.Count >= need2)
                Emit(sid);
        }

        byte[]? ReadN(int n)
        {
            var buf = new byte[n]; int got = 0;
            while (got < n) { int r = src.Read(buf, got, n - got); if (r <= 0) return null; got += r; }
            return buf;
        }
        void SkipN(int n) { for (int i = 0; i < n; i++) if (src.ReadByte() < 0) return; }

        int b0 = src.ReadByte(), b1 = src.ReadByte(), b2 = src.ReadByte();
        while (b2 != -1)
        {
            if (!(b0 == 0 && b1 == 0 && b2 == 1)) { b0 = b1; b1 = b2; b2 = src.ReadByte(); continue; }
            int id = src.ReadByte();
            if (id < 0) break;
            if (id == 0xBA) { var h = ReadN(10); if (h == null) break; SkipN(h[9] & 0x07); }
            else if (id == 0xB9) break; // program end
            else if (id == 0xBD)
            {
                int hi = src.ReadByte(), lo = src.ReadByte(); if (lo < 0) break;
                var body = ReadN((hi << 8) | lo); if (body == null) break;
                Feed(body);
            }
            else if (id >= 0xBB) { int hi = src.ReadByte(), lo = src.ReadByte(); if (lo < 0) break; SkipN((hi << 8) | lo); }
            // それ以外（startcode誤検出など）は次の 00 00 01 へ。
            b0 = src.ReadByte(); b1 = src.ReadByte(); b2 = src.ReadByte();
        }
        foreach (var sid in new List<int>(asmBuf.Keys)) Emit(sid); // 末尾の未確定を確定
    }

    // 抽出したSPUを「字幕のみの小さなMPEG-PS」へ再パック（クリーンPTSを焼く）。全部小さいので ffmpeg は
    // 既定 probesize で全サブストリームを検出でき、-map 順で出力トラック順を制御できる。
    private static void BuildSubsPs(Dictionary<int, List<(long Pts90k, byte[] Data)>> sink, string outPath)
    {
        var all = new List<(long Pts, int Sid, byte[] Data)>();
        foreach (var kv in sink) foreach (var (pts, data) in kv.Value) all.Add((pts, kv.Key, data));
        all.Sort((x, y) => x.Pts != y.Pts ? x.Pts.CompareTo(y.Pts) : x.Sid.CompareTo(y.Sid));
        using var fs = File.Create(outPath);
        fs.Write(new byte[] { 0, 0, 1, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8 }); // SCR=0 パック
        foreach (var (pts, sid, data) in all)
        {
            int off = 0; bool first = true;
            while (off < data.Length)
            {
                int chunk = Math.Min(2020, data.Length - off);
                WritePrivate1Pes(fs, sid, data, off, chunk, first ? pts : (long?)null);
                off += chunk; first = false;
            }
        }
    }

    private static void WritePrivate1Pes(Stream fs, int sid, byte[] data, int off, int len, long? pts)
    {
        int hdrLen = pts.HasValue ? 5 : 0;
        int pesLen = 3 + hdrLen + 1 + len; // PES拡張(3)+PTS+substreamID(1)+payload
        Span<byte> h = stackalloc byte[9 + 5];
        h[0] = 0; h[1] = 0; h[2] = 1; h[3] = 0xBD;
        h[4] = (byte)(pesLen >> 8); h[5] = (byte)(pesLen & 0xFF);
        h[6] = 0x80; h[7] = pts.HasValue ? (byte)0x80 : (byte)0x00; h[8] = (byte)hdrLen;
        int hn = 9;
        if (pts.HasValue)
        {
            long p = pts.Value;
            h[hn++] = (byte)(0x21 | (((p >> 30) & 7) << 1));
            h[hn++] = (byte)((p >> 22) & 0xFF);
            h[hn++] = (byte)(0x01 | (((p >> 15) & 0x7F) << 1));
            h[hn++] = (byte)((p >> 7) & 0xFF);
            h[hn++] = (byte)(0x01 | ((p & 0x7F) << 1));
        }
        fs.Write(h.Slice(0, hn));
        fs.WriteByte((byte)sid);
        fs.Write(data, off, len);
    }

    // ── ffmpeg 引数（セル単位：映像→H.264、全音声コピー、字幕コピー）──
    private static string BuildFfmpegArgs(DvdH264Converter.Options opt, DvdVideoAttr v, bool interlaced, bool tff, string outPath, double outputTsOffsetSec = 0, IReadOnlyList<AudioPlan>? audioPlan = null, string? forceKeyTimes = null, IReadOnlyList<int>? subpictureIds = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string aspect = v.AspectRatio == "16:9" ? "16:9" : "4:3";
        var a = new StringBuilder();
        // 字幕は表示時のみ出現するため、ある程度のプローブで全ストリーム把握を狙う。
        a.Append("-y -hide_banner -loglevel error -analyzeduration 100M -probesize 100M ");
        // テスト用：セル毎の上限。-copyts では出力側 -t が元PTS基準になり cell1 以降で何も出ない。
        // 入力側(-i の前)へ置くと入力先頭からの読込尺になり全セルで効く。
        if (opt.MaxSecondsPerCell > 0) a.Append($"-t {opt.MaxSecondsPerCell.ToString(inv)} ");
        a.Append("-i pipe:0 ");
        a.Append("-map 0:v:0 ");
        // 音声は「サブストリームID昇順」で明示マップし、さらに -streamid で出力サブストリームIDを原本IDに固定する。
        //   背景：vob多重化器は音声を種別ごとに 0x80,0x81…(AC-3)/0x88…(DTS)/0xa0…(LPCM) と連番で振り直すため、
        //   原本が飛びID(0x80,0x82,0x83…)だと出力がズレ、温存IFOのAST_CTLと食い違う（音声2〜4が別トラックに化ける）。
        //   対策：① id昇順マップで出力順を一定化（チャンク間で 0x80=音声1 が一定）。
        //         ② -streamid <出力index>:<原本ID> で各音声の出力IDを原本IDへ固定。
        //   ②は ffmpeg-patch/ の mpegenc パッチ版なら効いて原本ID完全一致（音声1〜4OK）。未パッチ版でも -streamid は
        //   無視されるだけで無害＝後方互換（その場合は①により音声1のみ一致）。idが取れない時は従来 `-map 0:a`。
        var streamIds = new StringBuilder();
        var acodec = new StringBuilder();
        int nextOut = 1; // 次の出力stream index（0=映像、1.. が音声→字幕）
        if (audioPlan is { Count: > 0 })
        {
            int ai = 0;      // 音声内インデックス
            foreach (var pl in audioPlan)
            {
                a.Append($"-map 0:i:0x{pl.OrigId:x}? ");
                streamIds.Append($"-streamid {nextOut}:{pl.OutId} ");
                if (pl.Reencode)
                    acodec.Append($"-c:a:{ai} ac3 -b:a:{ai} {pl.BitrateK}k -ac:a:{ai} 2 ");
                else
                    acodec.Append($"-c:a:{ai} copy ");
                nextOut++; ai++;
            }
        }
        else
        {
            // 音声があれば全コピー、無ければ無視（? 付き）。メニューの純背景セルは音声が無いことがある。
            a.Append("-map 0:a? ");
            acodec.Append("-c:a copy ");
        }
        // 字幕（サブピクチャ）の substream ID を原本通りに保つ。`-map 0:s?` は ffmpeg の検出（＝出現）順で
        // マップするため、出現順が原本の substream ID 順と違うと mpegenc の自動採番（0x20,0x21…）で振り直され、
        // 温存IFOの PGC_SPST_CTL（字幕→実ID対応）と食い違って再生側で字幕が出なくなる（音声と同型）。
        // 対策：
        //   ・-map は 0x20〜0x3F を昇順に総当り（`?` で欠番スキップ）。タイトルはラン単位エンコードで run ごとに
        //     存在字幕が変わり得るため、ここで取りこぼさないことを優先する。連番IDなら自動採番が原本IDを再現する。
        //   ・字幕IDが probe 済み（タイトル）なら、各IDに -streamid で出力IDを原本IDへ固定（パッチ版mpegencが
        //     効けば非連番でも完全一致）。出力indexは「音声の次」から昇順（＝全字幕が当該runに揃う前提。揃わない
        //     runでは固定が外れ自動採番になるが、上の総当りマップで取りこぼしは無い＝最悪でも従来同等）。
        for (int sid = 0x20; sid <= 0x3F; sid++)
            a.Append($"-map 0:i:0x{sid:x}? ");
        if (subpictureIds is { Count: > 0 })
            for (int k = 0; k < subpictureIds.Count; k++)
                streamIds.Append($"-streamid {nextOut + k}:{subpictureIds[k]} ");
        a.Append(streamIds);
        // 元のフレーム数・PTSをそのまま保持（CFR再スタンプで重複フレーム挿入＝位置がIFO尺とズレるのを防ぐ）。
        a.Append("-fps_mode passthrough -copyts ");
        if (string.Equals(opt.Deinterlace, "ivtc", StringComparison.OrdinalIgnoreCase))
            a.Append("-vf fieldmatch,decimate "); // ソフト/ハードテレシネ解除→24p progressive
        else if (!string.Equals(opt.Deinterlace, "none", StringComparison.OrdinalIgnoreCase))
            a.Append($"-vf {opt.Deinterlace.ToLowerInvariant()}=mode=send_field ");
        string ilme = interlaced ? "-flags +ilme+ildct " : "";
        // ラン1パス符号化で内部セル先頭にキーフレームを強制（時刻は -copyts のソースPTS秒）。
        // closed: open-gop=0 なので強制キーは IDR。hybrid: open-gop=1 だが下の -forced-idr で強制キーだけ IDR に
        //   格上げ（内部の周期GOPはオープンのまま＝圧縮率はオープン相当、セル先頭だけ堅牢）。open: 非IDRのオープンI。
        if (!string.IsNullOrEmpty(forceKeyTimes))
            a.Append($"-force_key_frames {forceKeyTimes} ");
        if (string.Equals(opt.GopMode, "hybrid", StringComparison.OrdinalIgnoreCase))
            a.Append("-forced-idr 1 "); // 強制キーフレーム（セル境界）を IDR に格上げ
        a.Append($"-c:v libx264 -preset {opt.Preset} -crf {opt.Crf} -pix_fmt yuv420p -aspect {aspect} {ilme}");
        var xp = new List<string>();
        if (opt.Keyint > 0) { xp.Add($"keyint={opt.Keyint}"); xp.Add("scenecut=0"); }
        // min-keyint=1：セル境界で強制するキーフレームを、間隔が短くても確実に挿入させる（既定の min-keyint に
        // 阻まれて短セル先頭がキーフレームにならないのを防ぐ）。最大GOP長は keyint で決まるのでシーク助走には影響しない。
        xp.Add("min-keyint=1");
        xp.Add(string.Equals(opt.GopMode, "closed", StringComparison.OrdinalIgnoreCase) ? "open-gop=0" : "open-gop=1");
        if (interlaced) xp.Add(tff ? "tff=1" : "bff=1");
        xp.Add("repeat-headers=1");
        // Bフレームは有効（圧縮率のため）。再生側はフィールド数テーブルを「出力フレーム単位の累積」で
        // 使うので、デコーダのBフレーム並べ替え（avcodecが表示順で出力）と干渉しない。
        a.Append($"-x264-params {string.Join(":", xp)} ");
        a.Append(acodec);
        a.Append("-c:s copy ");
        // PTS連続化：リセット後のセルを前セルの続きへ振り直す（-copyts の元PTSに一律オフセットを足す。
        // 全ストリーム（映像/音声/字幕）同量シフトなのでA/V関係は不変）。0なら付けない（=従来と同一）。
        if (outputTsOffsetSec != 0)
            a.Append($"-output_ts_offset {outputTsOffsetSec.ToString("F6", inv)} ");
        a.Append($"-f vob \"{outPath}\"");
        return a.ToString();
    }

    // 出力 VOB をセクタ走査し、(各セクタ先頭の映像PES PTS) と (SPS=NALタイプ7 を含むセクタ) を記録する。
    private static (List<(long Sector, double PtsSec)> Pts, List<long> SpsSectors) ScanVideoPtsBySector(string vobPath)
    {
        var pts = new List<(long, double)>();
        var sps = new List<long>();
        using var fs = new FileStream(vobPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sec = new byte[Sector];
        long sectorIdx = 0;
        while (true)
        {
            int read = 0, n;
            while (read < Sector && (n = fs.Read(sec, read, Sector - read)) > 0) read += n;
            if (read <= 0) break;
            if (FindFirstVideoPts(sec, read) is double p) pts.Add((sectorIdx, p));
            if (SectorHasSps(sec, read)) sps.Add(sectorIdx);
            sectorIdx++;
        }
        return (pts, sps);
    }

    // セクタ内に H.264 SPS(NAL type 7) のスタートコードがあるか。
    private static bool SectorHasSps(byte[] b, int len)
    {
        for (int i = 0; i + 3 < len; i++)
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 && (b[i + 3] & 0x1F) == 7) return true;
        return false;
    }

    // 出力PTS(秒)に最も近いセクタを返す（ラン内はPTS単調増加なので二分探索）。
    private static long NearestPtsSector(List<(long Sector, double PtsSec)> pts, double target)
    {
        if (pts.Count == 0) return 0;
        int lo = 0, hi = pts.Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (pts[mid].PtsSec < target) lo = mid + 1; else hi = mid; }
        long best = pts[lo].Sector; double bd = Math.Abs(pts[lo].PtsSec - target);
        if (lo > 0 && Math.Abs(pts[lo - 1].PtsSec - target) < bd) best = pts[lo - 1].Sector;
        return best;
    }

    // ptsSec に最も近い SPS セクタへスナップ（強制IDRのセクタに合わせ、再生側がクリーンに先頭復号できる）。
    private static long SnapToSps(List<long> spsSectors, long ptsSec)
    {
        if (spsSectors.Count == 0) return ptsSec;
        int lo = 0, hi = spsSectors.Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (spsSectors[mid] < ptsSec) lo = mid + 1; else hi = mid; }
        long best = spsSectors[lo];
        if (lo > 0 && Math.Abs(spsSectors[lo - 1] - ptsSec) <= Math.Abs(best - ptsSec)) best = spsSectors[lo - 1];
        return best;
    }

    // ── 自前タイムライン制御：出力VOBの全パックの SCR / 各PESの PTS・DTS に delta(90kHz, 符号付き) を加算する。
    //   ffmpegの mux 先読み(preroll)を打ち消し、ラン境界をフレーム精度で連続化する（＝muxのタイムラインを自前で持つ）。
    //   PES/AC-3 等の枠組みは ffmpeg の正しい出力をそのまま使い、時刻だけ書き換える。
    private static void RewriteTimestamps(string vobPath, long delta90k)
    {
        if (delta90k == 0) return;
        const long Mask = 0x1FFFFFFFFL; // 33bit
        using var fs = new FileStream(vobPath, FileMode.Open, FileAccess.ReadWrite);
        var b = new byte[Sector];
        long pos = 0;
        while (true)
        {
            fs.Position = pos;
            int read = 0, n;
            while (read < Sector && (n = fs.Read(b, read, Sector - read)) > 0) read += n;
            if (read <= 0) break;
            if (read >= 14 && b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0xBA)
            {
                WriteScr(b, 4, (ReadScr(b, 4) + delta90k) & Mask);
                int off = 14 + (b[13] & 0x07);
                if (off + 6 <= read && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1 && b[off + 3] == 0xBB)
                    off += 6 + ((b[off + 4] << 8) | b[off + 5]);
                while (off + 9 <= read && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1)
                {
                    int sid = b[off + 3];
                    int pesLen = (b[off + 4] << 8) | b[off + 5];
                    int pesEnd = pesLen == 0 ? read : Math.Min(read, off + 6 + pesLen);
                    if (sid == 0xBE || sid == 0xBF) { off = pesEnd; continue; } // padding / NAV(private_2)
                    int ptsDts = (b[off + 7] >> 6) & 0x03;
                    int p = off + 9;
                    if ((ptsDts & 0x02) != 0 && p + 5 <= read) WriteTs(b, p, (ReadTs(b, p) + delta90k) & Mask);
                    if ((ptsDts & 0x01) != 0 && p + 10 <= read) WriteTs(b, p + 5, (ReadTs(b, p + 5) + delta90k) & Mask);
                    off = pesEnd;
                }
                fs.Position = pos;
                fs.Write(b, 0, read);
            }
            pos += Sector;
        }
    }

    // PES PTS/DTS（5バイト, 33bit）。
    private static long ReadTs(byte[] b, int o) =>
        ((long)(b[o] & 0x0E) << 29) | ((long)b[o + 1] << 22) | ((long)(b[o + 2] & 0xFE) << 14)
        | ((long)b[o + 3] << 7) | ((long)(b[o + 4] & 0xFE) >> 1);
    private static void WriteTs(byte[] b, int o, long v)
    {
        b[o] = (byte)((b[o] & 0xF1) | (int)((v >> 29) & 0x0E));       // 接頭辞(7-4)とmarker(0)保持、PTS[32..30]
        b[o + 1] = (byte)((v >> 22) & 0xFF);                          // [29..22]
        b[o + 2] = (byte)(((v >> 14) & 0xFE) | 0x01);                 // [21..15] + marker
        b[o + 3] = (byte)((v >> 7) & 0xFF);                           // [14..7]
        b[o + 4] = (byte)(((v << 1) & 0xFE) | 0x01);                  // [6..0] + marker
    }

    // pack_header SCR_base（6バイトのうち base 33bit。ext と marker は保持）。
    private static long ReadScr(byte[] b, int o) =>
        (((long)(b[o] >> 3) & 0x07) << 30) | (((long)b[o] & 0x03) << 28) | ((long)b[o + 1] << 20)
        | (((long)(b[o + 2] >> 3) & 0x1F) << 15) | (((long)b[o + 2] & 0x03) << 13) | ((long)b[o + 3] << 5)
        | ((long)(b[o + 3 + 1] >> 3) & 0x1F);
    private static void WriteScr(byte[] b, int o, long s)
    {
        b[o] = (byte)(0x40 | (int)(((s >> 30) & 0x07) << 3) | 0x04 | (int)((s >> 28) & 0x03));
        b[o + 1] = (byte)((s >> 20) & 0xFF);
        b[o + 2] = (byte)((int)(((s >> 15) & 0x1F) << 3) | 0x04 | (int)((s >> 13) & 0x03));
        b[o + 3] = (byte)((s >> 5) & 0xFF);
        b[o + 4] = (byte)((int)(((s >> 0) & 0x1F) << 3) | 0x04 | (b[o + 4] & 0x03)); // ext[8..7]保持
    }

    // セクタ内の最初の video PES(0x000001E0..EF)で PTS を持つものの PTS（秒）を返す。無ければ null。
    private static double? FindFirstVideoPts(byte[] b, int len)
    {
        for (int i = 0; i + 9 < len; i++)
        {
            if (b[i] != 0 || b[i + 1] != 0 || b[i + 2] != 1) continue;
            int sid = b[i + 3];
            if (sid < 0xE0 || sid > 0xEF) continue;
            // 00 00 01 E0 の後は PES_packet_length(2B)、その後に PES ヘッダ（フラグ等）。
            int p = i + 4 + 2;
            if (p + 3 > len) return null;
            int flags2 = b[p + 1];
            int ptsDts = (flags2 >> 6) & 0x03;
            if ((ptsDts & 0x02) != 0 && p + 3 + 5 <= len)
            {
                int o = p + 3;
                long ts = ((long)(b[o] & 0x0E) << 29) | ((long)b[o + 1] << 22) | ((long)(b[o + 2] & 0xFE) << 14)
                        | ((long)b[o + 3] << 7) | ((long)(b[o + 4] & 0xFE) >> 1);
                return ts / 90000.0;
            }
            // この video PES に PTS が無ければ同一セクタ内の次の start code を探す。
        }
        return null;
    }

    // PTS(秒) に対応する chunk 内セクタ（最初に pts 以上になるセクタ）。
    private static long SectorForPts(List<(long Sector, double PtsSec)> idx, double targetPts, long chunkSectors)
    {
        if (idx.Count == 0) return 0;
        if (targetPts <= idx[0].PtsSec) return 0;
        int lo = 0, hi = idx.Count - 1, ans = idx.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (idx[mid].PtsSec >= targetPts) { ans = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        return Math.Clamp(idx[ans].Sector, 0, Math.Max(0, chunkSectors - 1));
    }

    private static long RoundUp(long v) => (v + Sector - 1) / Sector * Sector;
    private static void W32Be(byte[] b, int o, long v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static string Tail(string s) => s.Length <= 1500 ? s : s.Substring(s.Length - 1500);

    // ── ffmpeg 実行（DvdH264Converter と同等。stdin へ PS を流し、ct で kill、進捗バイト通知）──
    private static int RunEncode(string exe, string args, Stream input, out string stderr,
        System.Threading.CancellationToken ct, Action<long>? onProgress)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var feeder = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var buf = new byte[1 << 16];
                int n; long fed = 0, last = 0;
                var stdin = p.StandardInput.BaseStream;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    stdin.Write(buf, 0, n);
                    fed += n;
                    if (fed - last >= (4 << 20)) { last = fed; onProgress?.Invoke(fed); }
                }
                stdin.Flush();
                onProgress?.Invoke(fed); // 最終値（4MB未満の小入力でも1回は通知＝進捗表示が出る）
            }
            catch { }
            finally { try { p.StandardInput.Close(); } catch { } }
        });
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        try { feeder.Wait(2000); } catch { }
        ct.ThrowIfCancellationRequested();
        stderr = err;
        return p.ExitCode;
    }

    // 元セルPSを ffprobe で走査し、音声サブストリームIDを昇順で返す（DVDのPSでは ffmpeg の stream `id` が
    // サブストリームID＝AC-3:0x80+ / DTS:0x88+ / LPCM:0xA0+ / MPEG音声 等）。これを `-map 0:i:<id>` の固定順に
    // 使い、全セルチャンクで音声の並び（=出力サブストリームID）を一定にする。取得不能・空なら空リスト。
    private static List<int> ProbeAudioStreamIds(string ffprobe, Stream cellPs, System.Threading.CancellationToken ct)
    {
        var ids = new List<int>();
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                "-v error -analyzeduration 100M -probesize 100M -select_streams a -show_entries stream=id -of csv=p=0 -i pipe:0")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var feeder = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var buf = new byte[1 << 16]; int n; var stdin = p.StandardInput.BaseStream;
                    while ((n = cellPs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ct.IsCancellationRequested) break;
                        stdin.Write(buf, 0, n);
                    }
                    stdin.Flush();
                }
                catch { }
                finally { try { p.StandardInput.Close(); } catch { } }
            });
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var s = line.Replace("\r", "").TrimEnd(',').Trim();
                if (s.Length == 0 || s == "N/A") continue;
                int id;
                bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id)
                    : int.TryParse(s, out id);
                if (ok && id > 0 && !ids.Contains(id)) ids.Add(id);
            }
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            try { feeder.Wait(2000); } catch { }
        }
        catch { ids.Clear(); }
        ids.Sort();
        return ids;
    }

    // IFO の論理字幕ストリーム＋PGC_SPST_CTL から、Kuroko 用の字幕プランを作る。1論理字幕はアスペクト別に
    // 最大4つの物理サブストリーム(4:3/ワイド/レターボックス/パン&スキャン、各 0x20+番号)を持つ。全変種を保持して
    // 将来のアスペクト切替に備える（ffprobe 物理プローブは probesize 依存で取りこぼし＆重複するので使わない）。
    // 返り値：論理番号順に (論理番号, 言語, 4モードの物理サブID。無い変種は -1)。
    private static List<(int Logical, string? Lang, int Id43, int IdWide, int IdLb, int IdPs)> BuildSubpicturePlan(DvdTitlePlayback tp)
    {
        var plan = new List<(int, string?, int, int, int, int)>();
        var ctl = tp.Pgc.SubpictureControl;
        bool anyAvail = false;
        for (int i = 0; i < tp.SubpictureStreams.Count; i++)
            if (i < ctl.Count && ctl[i].Available) { anyAvail = true; break; }
        for (int i = 0; i < tp.SubpictureStreams.Count; i++)
        {
            bool avail = i < ctl.Count && ctl[i].Available;
            if (anyAvail && !avail) continue;
            var s = tp.SubpictureStreams[i];
            string? lang = s.HasLanguage ? s.LanguageCode : null;
            if (avail)
            {
                var c = ctl[i];
                plan.Add((i, lang, 0x20 + c.N43, 0x20 + c.NWide, 0x20 + c.NLetterbox, 0x20 + c.NPanScan));
            }
            else
            {
                // 制御表が無いディスク：索引で1変種のみ（4:3扱い）。
                plan.Add((i, lang, 0x20 + i, -1, -1, -1));
            }
        }
        return plan;
    }

    // 元セルPSを ffprobe で走査し、字幕（サブピクチャ）substream ID を昇順で返す（DVDのPSでは ffmpeg の
    // stream `id` が substream ID＝0x20-0x3F）。これを `-map 0:i:<id>` + `-streamid` の固定に使い、出力の
    // 字幕IDを原本通りに保つ（保持IFOの PGC_SPST_CTL と一致させる）。取得不能・空なら空リスト。
    private static List<int> ProbeSubpictureStreamIds(string ffprobe, Stream cellPs, System.Threading.CancellationToken ct)
    {
        var ids = new List<int>();
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                "-v error -analyzeduration 100M -probesize 100M -select_streams s -show_entries stream=id -of csv=p=0 -i pipe:0")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var feeder = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var buf = new byte[1 << 16]; int n; var stdin = p.StandardInput.BaseStream;
                    while ((n = cellPs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ct.IsCancellationRequested) break;
                        stdin.Write(buf, 0, n);
                    }
                    stdin.Flush();
                }
                catch { }
                finally { try { p.StandardInput.Close(); } catch { } }
            });
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var s = line.Replace("\r", "").TrimEnd(',').Trim();
                if (s.Length == 0 || s == "N/A") continue;
                int id;
                bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id)
                    : int.TryParse(s, out id);
                if (ok && id >= 0x20 && id <= 0x3F && !ids.Contains(id)) ids.Add(id);
            }
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            try { feeder.Wait(2000); } catch { }
        }
        catch { ids.Clear(); }
        ids.Sort();
        return ids;
    }

    // ラウドネス・メタデータ（仕様 §9.1）を AC-3 の生ビットストリームから取り出して ka へ載せる。
    // 元 PS から当該サブストリームの AC-3 ES を -c copy で取り出し（＝無加工の原本ビット）、Ac3Parser で
    // dialnorm と各オーディオブロックの dynrng / 各フレームの compr を解析する。失敗しても変換は続行する
    // （メタが無い＝フルレンジ扱いで、仕様上は正当）。
    private static void ApplyLoudness(KurokoAudio ka, DvdTitlePlayback tp, int cellStart, int cellEnd, int origId,
        DvdH264Converter.Options opt, StringBuilder log, System.Threading.CancellationToken ct)
    {
        try
        {
            byte[] es;
            using (var ps = tp.OpenStream(cellStart, cellEnd))
            using (var ms = new MemoryStream())
            {
                string args = $"-v error -analyzeduration 100M -probesize 100M -i pipe:0 " +
                              $"-map 0:i:0x{origId:x}? -c copy -f ac3 pipe:1";
                if (!RunFfmpegToStream(opt.FfmpegPath, args, ps, ms, ct)) return;
                es = ms.ToArray();
            }
            if (es.Length < 8) return;
            // maxsec（試験用の尺制限）が効いているときは、格納される音声と同じ範囲だけを対象にする。
            var ld = Ac3Parser.Extract(es, opt.MaxSecondsPerCell > 0 ? opt.MaxSecondsPerCell * 1000.0 : 0);
            if (ld.DialnormDb is null && ld.LinePoints is null && ld.RfPoints is null) return; // メタ無し＝フルレンジ
            var env = new KurokoLoudnessFile { Type = "ac3" };
            if (ld.LinePoints is { Count: > 0 }) env.Drc = new KurokoDrc { Mode = "ac3-line", Points = ld.LinePoints };
            if (ld.RfPoints is { Count: > 0 }) env.DrcRf = new KurokoDrc { Mode = "ac3-rf", Points = ld.RfPoints };
            bool hasEnv = env.Drc is not null || env.DrcRf is not null;
            ka.Loudness = new KurokoLoudness
            {
                Type = "ac3",
                // AC-3 の dialnorm は「台詞レベルの測定値」なので DialogLevelDb（§9.1.1）。
                // DTS の DNG（直接当てるゲイン＝DialogGainDb）とは意味が異なるので混同しないこと。
                DialogLevelDb = ld.DialnormDb,
                File = hasEnv ? KurokoLayout.SegmentLoudnessName(origId) : null,
            };
            ka.LoudnessEnvelopes = hasEnv ? env : null;
            log.AppendLine($"  loudness 0x{origId:X}: type=ac3 dialnorm={ld.DialnormDb:F0}dB " +
                           $"drc={(env.Drc is null ? "無し(全ユニティ)" : env.Drc.Points.Count + "点")} " +
                           $"drcRf={(env.DrcRf is null ? "無し" : env.DrcRf.Points.Count + "点")} es={es.Length}B");
        }
        catch (Exception ex) { log.AppendLine($"  loudness 0x{origId:X}: 解析失敗（メタ無しで続行）: {ex.Message}"); }
    }

    // ffmpeg へ stdin から流し込み、stdout をそのまま dest へ受ける。
    private static bool RunFfmpegToStream(string ffmpeg, string args, Stream input, Stream dest,
        System.Threading.CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg, args)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var feeder = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var buf = new byte[1 << 16]; int n; var stdin = p.StandardInput.BaseStream;
                while ((n = input.Read(buf, 0, buf.Length)) > 0) { if (ct.IsCancellationRequested) break; stdin.Write(buf, 0, n); }
                stdin.Flush();
            }
            catch { }
            finally { try { p.StandardInput.Close(); } catch { } }
        });
        var errTask = System.Threading.Tasks.Task.Run(() => { try { return p.StandardError.ReadToEnd(); } catch { return ""; } });
        p.StandardOutput.BaseStream.CopyTo(dest);
        p.WaitForExit();
        try { feeder.Wait(2000); } catch { }
        try { errTask.Wait(2000); } catch { }
        return dest.Length > 0;
    }

    // 音声プラン（1サブストリーム分）。OrigId=入力で掴む原本サブストリームID、OutId=出力に書くID、
    // Reencode=AC-3再エンコードするか、BitrateK=その時のkbps。
    // Channels＝**出力**チャンネル数（Reencode=false のコピー時は元のまま＝0で「指定しない」）。
    internal readonly record struct AudioPlan(int OrigId, int OutId, bool Reencode, int BitrateK, int Channels = 0);

    // 再符号化時に 3ch 以上を 2ch へ畳むか（"source" 指定のときだけ元のチャンネル数を保つ）。
    internal static bool DownmixToStereo(DvdH264Converter.Options opt)
        => !string.Equals(opt.AudioChannels, "source", StringComparison.OrdinalIgnoreCase);

    // 出力チャンネル数。Opus は設定次第、AC-3 再圧縮は常に 2ch、コピーは元のまま。
    private static int OutAudioChannels(DvdH264Converter.Options opt, bool reencodeOpus, int srcCh)
    {
        int src = Math.Max(1, srcCh);
        if (!reencodeOpus) return 2;                                   // AC-3 再圧縮は常に 2ch
        return DownmixToStereo(opt) ? Math.Min(2, src) : src;
    }

    // 有効な AC-3 ビットレート(kbps)。最も近い値へ丸める。
    private static readonly int[] Ac3Rates = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640 };
    private static int NearestAc3(int kbps)
    {
        int best = Ac3Rates[0];
        foreach (var r in Ac3Rates) if (System.Math.Abs(r - kbps) < System.Math.Abs(best - kbps)) best = r;
        return best;
    }

    // 元セルPSを ffprobe で走査し、各音声サブストリームの (id, codec, channels, bit_rate) から方針どおりの
    // AudioPlan（DVDストリーム順＝サブストリームIDの下位3bit順）を作る。recompress=false なら全コピー。方針：
    //   AC-3 多ch(>2) → AC-3 2ch ダウンミックス（ch当たりbps維持＝総bps×2/ch を最近接AC-3値へ丸め）。OutId据置。
    //   AC-3 2ch以下 → コピー。
    //   DTS / LPCM → AC-3 2ch（既定192k）。OutId=0x80+(原番号)=基底だけAC-3へ差替（番号はVTS内一意なので衝突無し）。
    //   その他(MPEG音声等) → コピー。
    // perChK＝1チャンネルあたりの kbps。stereoDownmix＝Opus 再符号化時に 3ch 以上を 2ch へ畳むか。
    private static List<AudioPlan> BuildAudioPlan(string ffprobe, Stream cellPs, bool recompress, int perChK,
        System.Threading.CancellationToken ct, StringBuilder log, string audioCodec = "ac3", bool stereoDownmix = true)
    {
        var plans = new List<AudioPlan>();
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                "-v error -analyzeduration 100M -probesize 100M -select_streams a -show_entries stream=id,codec_name,channels,bit_rate -of compact=p=0:nk=0 -i pipe:0")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var feeder = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var buf = new byte[1 << 16]; int n; var stdin = p.StandardInput.BaseStream;
                    while ((n = cellPs.Read(buf, 0, buf.Length)) > 0) { if (ct.IsCancellationRequested) break; stdin.Write(buf, 0, n); }
                    stdin.Flush();
                }
                catch { }
                finally { try { p.StandardInput.Close(); } catch { } }
            });
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                // compact 形式：id=0x80|codec_name=ac3|channels=6|bit_rate=384000
                var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tok in line.Split('|'))
                {
                    int eq = tok.IndexOf('=');
                    if (eq > 0) kv[tok.Substring(0, eq).Trim()] = tok.Substring(eq + 1).Trim();
                }
                if (!kv.TryGetValue("id", out var ids)) continue;
                int id = ids.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(ids.AsSpan(2), System.Globalization.NumberStyles.HexNumber)
                    : (int.TryParse(ids, out var d) ? d : 0);
                if (id <= 0) continue;
                string codec = kv.GetValueOrDefault("codec_name", "").ToLowerInvariant();
                int ch = int.TryParse(kv.GetValueOrDefault("channels", ""), out var c) ? c : 0;
                int srcBrK = int.TryParse(kv.GetValueOrDefault("bit_rate", ""), out var b) && b > 0 ? (int)(b / 1000) : 0;

                if (plans.Exists(x => x.OrigId == id)) continue;
                plans.Add(DecideAudio(id, codec, ch, srcBrK, perChK, recompress, audioCodec,
                    stereoDownmix ? Math.Min(2, Math.Max(1, ch)) : Math.Max(1, ch), log));
            }
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            try { feeder.Wait(2000); } catch { }
        }
        catch (Exception ex) { log.AppendLine($"  audio plan 失敗: {ex.Message}"); plans.Clear(); }
        // 並びは暫定（ID昇順）。Kuroko 経路の論理順は OrderAudioPlanByIfo が AST_CTL 基準で決める
        //（旧来の「ID下位3bit昇順＝論理順」は AST_CTL 非恒等のディスクでトラックが入れ替わるため廃止）。
        plans.Sort((x, y) => x.OrigId.CompareTo(y.OrigId));
        return plans;
    }

    // probe結果（VOB実測の物理サブストリーム）を IFO の論理音声順に並べ替える（Kuroko 用）。
    // DVD の論理音声番号（SPRM[1]/SetSTN が参照）と物理サブストリームIDの対応は PGC_AST_CTL が決める
    //（恒等とは限らない。例：ある市販ディスク T1 は 論理1=0x83(5.1ch en)、論理3=0x81(2ch解説)）。物理IDの
    // 下位3bit を論理番号とみなすとこうしたディスクで内容と番号が食い違う。内蔵ISOエンジンは
    // AST_CTL[i].Number で解決しており（DvdIsoAnalyzer.MapIfoAudio と同じ）、それに揃える。
    // 返り値[k]＝mkv音声トラック k+1 として符号化する論理ストリーム（Plan=符号化計画、Logical=論理番号、
    // AstNum=AST_CTL 実番号（無ければ-1）、Attr=IFO属性（言語/コーデック/ch。IFO未宣言なら null））。
    // VOB に実在しない宣言ストリームは除外し、IFO 未宣言の実在ストリームは従来順で末尾に足す。
    private static List<(AudioPlan Plan, int Logical, int AstNum, DvdAudioAttr? Attr)> OrderAudioPlanByIfo(
        DvdTitlePlayback tp, List<AudioPlan> probed, StringBuilder log)
    {
        var result = new List<(AudioPlan, int, int, DvdAudioAttr?)>();
        var used = new HashSet<int>();
        for (int i = 0; i < tp.AudioStreams.Count; i++)
        {
            int id = AudioPhysId(tp, i, out int astNum);
            int idx = id >= 0 ? probed.FindIndex(x => x.OrigId == id) : -1;
            if (idx < 0)
            {
                log.AppendLine($"  audio 論理{i}: 0x{id:X}(astCtl={astNum}) がVOBに無い→除外");
                continue;
            }
            if (!used.Add(id))
            {
                log.AppendLine($"  audio 論理{i}: 0x{id:X} は他の論理ストリームに割当済み（AST_CTL重複）→除外");
                continue;
            }
            result.Add((probed[idx], i, astNum, tp.AudioStreams[i]));
        }
        // IFO 未宣言だが VOB に実在するストリーム（変則ディスク）：下位3bit を論理番号として末尾へ（従来動作）。
        foreach (var pl in probed.Where(x => !used.Contains(x.OrigId)).OrderBy(x => x.OrigId & 0x07))
        {
            log.AppendLine($"  audio 0x{pl.OrigId:X}: IFO未宣言→論理{pl.OrigId & 0x07}として末尾に追加");
            result.Add((pl, pl.OrigId & 0x07, -1, null));
        }
        return result;
    }

    // 論理音声 i の物理サブストリームID（コーデック基底 AC3=0x80/DTS=0x88/LPCM=0xA0/MPEG=0xC0 ＋ AST_CTL 実番号。
    // 制御表が無い/不可なら論理index＝規格の既定。内蔵ISOエンジン BuildAudioTracks と同じ解決）。
    // astNum: AST_CTL の実番号（無ければ -1）。戻り値 -1=未知コーデック。
    private static int AudioPhysId(DvdTitlePlayback tp, int i, out int astNum)
    {
        astNum = -1;
        var a = tp.AudioStreams[i];
        int baseId = a.CodingMode switch
        {
            DvdAudioCodingMode.Ac3 => 0x80,
            DvdAudioCodingMode.Dts => 0x88,
            DvdAudioCodingMode.Lpcm => 0xA0,
            // MPEG audio は private_stream_1 ではなく本来のストリームID。ffmpeg(mpegps) は 0x1C0-0x1DF を割り当てる
            // （seg 側 OrigId・-map 0:i:0x1c0 と一致）。0xC0 だと seg の物理ID(0x1C0)と食い違い title.json 論理音声が
            // 空になり無音になる。
            DvdAudioCodingMode.Mpeg1 or DvdAudioCodingMode.Mpeg2Ext => 0x1C0,
            _ => -1,
        };
        if (baseId < 0) return -1;
        var ctl = tp.Pgc.AudioControl;
        bool avail = i < ctl.Count && ctl[i].Available;
        if (avail) astNum = ctl[i].Number;
        return baseId + (avail ? ctl[i].Number : i);
    }

    // IFO から「物理サブストリームID → (論理番号, 属性)」（音声）。セグメント（物理トラック。§13.3）の参考メタと、
    // title.json（論理→物理。§13.4）の生成に使う。重複IDは先勝ち。
    private static Dictionary<int, (int Logical, DvdAudioAttr Attr)> IfoAudioByPhys(DvdTitlePlayback tp)
    {
        var map = new Dictionary<int, (int, DvdAudioAttr)>();
        for (int i = 0; i < tp.AudioStreams.Count; i++)
        {
            int id = AudioPhysId(tp, i, out _);
            if (id >= 0 && !map.ContainsKey(id)) map[id] = (i, tp.AudioStreams[i]);
        }
        return map;
    }

    // recompress 時、AC-3 は全て 2ch・指定ビットレート(perChK×2 を AC-3 有効値へ丸め)。
    // AC-3 2ch(以下)は、元ビットレートが指定値より大きい時のみ再圧縮（小さい/不明ならコピー＝劣化させない）。
    // perChK＝**1チャンネルあたり**の kbps。outCh＝Opus の出力チャンネル数（AC-3 は常に 2ch）。
    // **コピー指定が最優先**：AudioCodec=opus であってもコピーならコピーする（旧実装は opus 判定が先で、
    // コピーを選んでも Opus へ再符号化していた）。
    private static AudioPlan DecideAudio(int id, string codec, int ch, int srcBrK, int perChK, bool recompress,
        string audioCodec, int outCh, StringBuilder log)
    {
        if (!recompress)
        {
            log.AppendLine($"  audio 0x{id:X}: {codec} {ch}ch → コピー（無劣化）");
            return new AudioPlan(id, id, false, 0);
        }
        if (string.Equals(audioCodec, "opus", StringComparison.OrdinalIgnoreCase))
        {
            // Kuroko-Opus：全音声を Opus へ再符号化。ビットレートは perChK×出力ch（Opusは任意値可）。
            // OutId は Kuroko(av.mkv) では未使用のため id 据置。
            int oc = Math.Max(1, Math.Min(outCh, Math.Max(1, ch)));
            int total = perChK * oc;
            log.AppendLine($"  audio 0x{id:X}: {codec} {ch}ch → Opus {oc}ch {total}k（{perChK}k/ch）");
            return new AudioPlan(id, id, true, total, oc);
        }
        int rate = NearestAc3(perChK * 2);

        // DTS / LPCM → AC-3 2ch（基底だけAC-3へ差替、番号=原ID-原基底は据置）。
        if (codec is "dts" or "dca")
        {
            int outId = 0x80 + (id - 0x88);
            log.AppendLine($"  audio 0x{id:X}: DTS {ch}ch → AC-3 2ch {rate}k (out 0x{outId:X})");
            return new AudioPlan(id, outId, true, rate, 2);
        }
        if (codec is "pcm_dvd" or "pcm_s16be" or "pcm_s24be" or "pcm_s20be")
        {
            int outId = 0x80 + (id - 0xA0);
            log.AppendLine($"  audio 0x{id:X}: LPCM {ch}ch → AC-3 2ch {rate}k (out 0x{outId:X})");
            return new AudioPlan(id, outId, true, rate, 2);
        }
        if (codec == "ac3")
        {
            if (ch > 2)
            {
                log.AppendLine($"  audio 0x{id:X}: AC-3 {ch}ch → 2ch {rate}k");
                return new AudioPlan(id, id, true, rate, 2);
            }
            // 2ch以下：元bpsが指定値より大きければ再圧縮、それ以外はコピー。
            if (srcBrK > rate)
            {
                log.AppendLine($"  audio 0x{id:X}: AC-3 {ch}ch {srcBrK}k → 2ch {rate}k（再圧縮）");
                return new AudioPlan(id, id, true, rate, 2);
            }
            log.AppendLine($"  audio 0x{id:X}: AC-3 {ch}ch {(srcBrK > 0 ? srcBrK + "k" : "bps?")} → コピー（≤{rate}k）");
            return new AudioPlan(id, id, false, 0);
        }
        log.AppendLine($"  audio 0x{id:X}: {codec} → コピー（対象外）");
        return new AudioPlan(id, id, false, 0);
    }

    // 温存IFOの音声属性(0x204+i*8)を方針どおり書き換える（DTS/LPCM→AC-3、AC-3多ch→2ch）。
    // byte0 bit7-5=コーデング(0=AC3,4=LPCM,6=DTS)、byte1 bit2-0=ch-1, bit5-4=サンプルレート, bit7-6=量子化。
    private static void PatchAudioAttrsToAc3Stereo(byte[] ifo, StringBuilder log)
    {
        if (ifo.Length < 0x204) return;
        int count = System.Math.Min(8, (ifo[0x202] << 8) | ifo[0x203]);
        for (int i = 0; i < count; i++)
        {
            int o = 0x204 + i * 8;
            if (o + 2 > ifo.Length) break;
            int a0 = ifo[o], a1 = ifo[o + 1];
            int coding = (a0 >> 5) & 0x07;
            int channels = (a1 & 0x07) + 1;
            if (coding == 6 || coding == 4) // DTS / LPCM → AC-3 2ch 48k
            {
                ifo[o] = (byte)(a0 & 0x1F);          // bits7-5=0(AC-3)、言語等は保持
                ifo[o + 1] = (byte)((a1 & 0x08) | 0x01); // ch=2(001), sr=48k, 量子化=0, 予約bit3保持
                log.AppendLine($"  ifo audio[{i}]: {(coding == 6 ? "DTS" : "LPCM")} → AC-3 2ch");
            }
            else if (coding == 0 && channels > 2) // AC-3 多ch → 2ch
            {
                ifo[o + 1] = (byte)((a1 & 0xF8) | 0x01);
                log.AppendLine($"  ifo audio[{i}]: AC-3 {channels}ch → 2ch");
            }
        }
    }

    // ラン(PTS連続な複数セル)のソースPSを ffprobe で復号し、各フレームの (生PTS90kHz, フィールド数=2+repeat_pict,
    // progressive=interlaced_frame==0) を【表示順(PTS昇順)】で返す。Kuroko(案C) の容器PTS焼き直し用の「正しい
    // フィールド数列」と「interlaced 区間」を、最終 av.mkv のフレームと同じ並び・粒度で得るために使う
    //（av.mkv の容器デルタは不規則で round 誤判定するため、ソースの repeat_pict が正）。
    private static (List<long> Pts90k, List<byte> Fields, List<bool> Progressive) ProbeRunFrames(
        string ffprobe, Stream runPs, System.Threading.CancellationToken ct, Action<long>? onFed = null)
    {
        var rows = new List<(long pts, byte fields, bool prog)>();
        var psi = new ProcessStartInfo(ffprobe,
            "-v error -select_streams v:0 -show_entries frame=best_effort_timestamp,repeat_pict,interlaced_frame -of csv=p=0 -i pipe:0")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var feeder = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var buf = new byte[1 << 16]; int n; long fed = 0, last = 0; var stdin = p.StandardInput.BaseStream;
                while ((n = runPs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    stdin.Write(buf, 0, n);
                    fed += n;
                    if (fed - last >= (4 << 20)) { last = fed; onFed?.Invoke(fed); } // 進捗（供給済みバイト）
                }
                stdin.Flush();
                onFed?.Invoke(fed);
            }
            catch { }
            finally { try { p.StandardInput.Close(); } catch { } }
        });
        long seq = 0; string? line;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            var parts = line.Replace("\r", "").TrimEnd(',').Split(',');
            if (parts.Length < 3) continue;
            string sPts = parts[0].Trim();
            int a = int.TryParse(parts[1].Trim(), out var av) ? av : 0;
            int b = int.TryParse(parts[2].Trim(), out var bv) ? bv : 0;
            int inter, rp;
            if (b > 1) { inter = a; rp = b; } else if (a > 1) { rp = a; inter = b; } else { inter = a; rp = b; }
            int fields = Math.Clamp(2 + rp, 1, 7);
            long pts = (sPts != "N/A" && long.TryParse(sPts, out var pv)) ? pv : long.MinValue;
            rows.Add((pts, (byte)fields, inter == 0)); // long.MinValue = PTS 未取得（後で近傍から補完）
            seq++;
        }
        // PTS 未取得（ffprobe が N/A を返すフレーム。ラン末尾など）の補完。**フレーム連番を入れてはいけない**：
        // この後の PTS 昇順ソートで連番（小さい値）が先頭へ飛び出し、srcPts[0] が「ラン先頭フレームの PTS」で
        // なくなる。呼び出し側は srcPts[0] を -output_ts_offset と音声 atrim の基準に使うため、映像だけが
        // mkvmerge --timestamps で 0 基準へ焼き直され、**音声がセグメント先頭時刻ぶん丸ごと遅れる**
        // （ある市販ディスク タイトル1 の S0002＝93.4秒。ラン末尾フレームの連番 124794 が 1.3866 秒として採用されていた）。
        // デコード順の直前フレームの直後（+1）へ置き、並び順が変わらないようにする。
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].pts != long.MinValue) continue;
            long fill;
            if (i > 0) fill = rows[i - 1].pts + 1;                  // 直前は補完済み＝必ず既知
            else
            {
                int j = 1; while (j < rows.Count && rows[j].pts == long.MinValue) j++;
                fill = j < rows.Count ? rows[j].pts - j : 0;         // 先頭が未取得：直後の既知から前へ詰める
            }
            rows[i] = (fill, rows[i].fields, rows[i].prog);
        }
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        try { feeder.Wait(2000); } catch { }
        var order = Enumerable.Range(0, rows.Count).ToList();
        order.Sort((x, y) => rows[x].pts != rows[y].pts ? rows[x].pts.CompareTo(rows[y].pts) : x.CompareTo(y)); // 表示順(PTS昇順)・安定
        var pl = new List<long>(rows.Count); var fl = new List<byte>(rows.Count); var prl = new List<bool>(rows.Count);
        foreach (var i in order) { pl.Add(rows[i].pts); fl.Add(rows[i].fields); prl.Add(rows[i].prog); }
        return (pl, fl, prl);
    }

    // テレシネ耐性＋短区間統合の「fields対象（＝真のインターレース）」判定。
    //   raw[i] = interlaced_frame かつ fld==2（combed で RFF なし）。
    //   raw が連続する run（実インターレース小区間）が、
    //     ・長さ < minInterlaceFrames（フレーム）で、かつ
    //     ・プログレッシブ（film/weave）フレームに隣接する
    //   場合は fields(dual) にせず weave（フィルム）へ降格する。用途：
    //     ・ソフトテレシネの combed（run長1 で点在。例 ある市販ディスク T15）を毎フレーム分割しない。
    //     ・フィルム中の短いディゾルブ/フェード遷移（~0.5秒の実インターレース。例 ある市販ディスク）を統合し、
    //       パーツ数と再生側の境界処理を減らす（その区間は weave＝軽い櫛が出るトレードオフ）。
    //   真の 29.97i は run が長く持続する（導入部の連続インターレース・純インターレース素材）ので降格されない。
    //   minInterlaceFrames=0 は降格しない（全ての実インターレースを fields 化）。
    private static bool[] ComputeFieldsEligible(IReadOnlyList<bool> prg, IReadOnlyList<byte> fld, int minInterlaceFrames)
    {
        int n = prg.Count;
        var elig = new bool[n];
        for (int i = 0; i < n; i++) elig[i] = !prg[i] && fld[i] == 2;
        if (minInterlaceFrames <= 0) return elig;

        int p = 0;
        while (p < n)
        {
            if (!elig[p]) { p++; continue; }
            int q = p; while (q < n && elig[q]) q++;   // [p,q) は実インターレースの連続 run
            int runLen = q - p;
            // プログレッシブ（film）に隣接する短区間のみ対象（純インターレース中の一時的な短区間は対象外）。
            bool nearProgressive = (p > 0 && prg[p - 1]) || (q < n && prg[q]);
            if (runLen < minInterlaceFrames && nearProgressive)
                for (int k = p; k < q; k++) elig[k] = false; // 短い実インターレース区間 → フィルム(weave)へ降格
            p = q;
        }
        return elig;
    }

    // 動画ファイルの映像パケット数（=フレーム数）。mkvmerge のタイムスタンプ数を一致させる検算用。
    // 時刻リストの数と、実際に符号化されたフレーム数の食い違いを記録する。
    // 時刻は**位置で対応付ける**（i 番目の時刻＝i 番目のフレーム）ので、途中で1枚でも欠けると
    // それ以降のフレームが丸ごと1フレームぶん前後した時刻を持つ。過不足は末尾で調整するため
    // 絵は出てしまい、記録しないと誰も気づけない（再生側で「krk の時刻がおかしい」を推測する羽目になる）。
    private static void NoteFrameCountMismatch(StringBuilder log, string where, int expected, int actual)
    {
        if (actual <= 0 || expected == actual) return;
        int diff = actual - expected;
        bool big = Math.Abs(diff) >= 3;   // 末尾GOPの端数を超える食い違い＝ソース由来の取りこぼしを疑う
        log.AppendLine($"{(big ? "警告: " : "")}{where}: 時刻の数 {expected} と実符号化フレーム数 {actual} が "
            + $"{diff:+#;-#;0} 枚食い違う（末尾で調整）"
            + (big ? " ← 途中で欠けていると、以降の時刻が1フレームぶんずれます" : ""));
    }

    private static int ProbeVideoFrameCount(string ffprobe, string path)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd(); p.WaitForExit();
            return int.TryParse(outp.Trim(), out var n) ? n : 0;
        }
        catch { return 0; }
    }

    // 元セルPSを ffprobe で復号し、表示順の各フレームの (フィールド数=2+repeat_pict) と
    // 疎な (フレーム番号, 生PTS90kHz) アンカー、先頭フレームの生PTS(90kHz)を返す。
    // アンカーは seek 時の起点フレーム特定に使う。FirstPts は変換後PTSとのオフセット補正に使う。
    private static (List<byte> FieldCounts, List<(int Local, long Pts90k)> Anchors, long FirstPts90k, long LastPts90k) ProbeCellRff(
        string ffprobe, Stream cellPs, System.Threading.CancellationToken ct, Action<long>? onFed = null)
    {
        var fc = new List<byte>();
        var anchors = new List<(int, long)>();
        var psi = new ProcessStartInfo(ffprobe,
            "-v error -select_streams v:0 -show_entries frame=pts,repeat_pict,interlaced_frame -of csv=p=0 -i pipe:0")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var feeder = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var buf = new byte[1 << 16]; int n; long fed = 0, last = 0; var stdin = p.StandardInput.BaseStream;
                while ((n = cellPs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    stdin.Write(buf, 0, n);
                    fed += n;
                    if (fed - last >= (4 << 20)) { last = fed; onFed?.Invoke(fed); } // 進捗（供給済みバイト）
                }
                stdin.Flush();
                onFed?.Invoke(fed);
            }
            catch { }
            finally { try { p.StandardInput.Close(); } catch { } }
        });
        int lastAnchor = -1000;
        long firstPts = -1;
        long lastPts = -1;
        string? line;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            // ffprobe CSV の列は要求順でなく正規順＝(pts, interlaced_frame, repeat_pict)。CR/末尾カンマ除去。
            var parts = line.Replace("\r", "").TrimEnd(',').Split(',');
            if (parts.Length < 3) continue;
            string sPts = parts[0].Trim();
            int a = int.TryParse(parts[1].Trim(), out var av) ? av : 0;
            int b = int.TryParse(parts[2].Trim(), out var bv) ? bv : 0;
            // interlaced_frame は 0/1、repeat_pict は 0..4。値で判別（順序が違うビルドにも頑健）。
            int inter, rp;
            if (b > 1) { inter = a; rp = b; }
            else if (a > 1) { rp = a; inter = b; }
            else { inter = a; rp = b; } // 両方0/1 → 正規順 (interlaced, repeat)
            long pts = 0; bool havePts = sPts != "N/A" && sPts.Length > 0 && long.TryParse(sPts, out pts);
            if (havePts && firstPts < 0) firstPts = pts;
            if (havePts) lastPts = pts;
            int fields = Math.Clamp(2 + rp, 1, 7);
            // bit7=プログレッシブ（interlaced_frame=0）。再生側は weave 表示してbobのちらつきを避ける。
            byte packed = (byte)(fields | (inter == 0 ? 0x80 : 0));
            int local = fc.Count;
            fc.Add(packed);
            if (havePts && local - lastAnchor >= 30) { anchors.Add((local, pts)); lastAnchor = local; }
        }
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        try { feeder.Wait(2000); } catch { }
        return (fc, anchors, firstPts, lastPts);
    }

    // 出力チャンク(VOB)先頭の最初の映像PTS(90kHz)を返す（無ければ-1）。チャンク先頭PTSの実測に使う。
    // ※ frame=pts は出力H.264で1フレームおきに N/A になるため、確実な「パケットPTS（=コンテナの格納PTS。
    //    再生側が読む値）の最初の非N/A」を採る。最初のパケット（IDR）はPTSを持つので先頭で確定する。
    private static long ProbeFirstVideoPts90k(string ffprobe, string path)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -select_streams v:0 -show_entries packet=pts -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            long first = -1;
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var s = line.Replace("\r", "").TrimEnd(',').Trim();
                if (s.Length > 0 && s != "N/A" && long.TryParse(s, out var v)) { first = v; break; }
            }
            try { if (!p.HasExited) p.Kill(true); } catch { } // 残りの出力は不要
            p.WaitForExit();
            return first;
        }
        catch { return -1; }
    }

    private static bool DetectTopFieldFirst(string ffmpeg, string srcIso)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg,
                $"-hide_banner -i \"{srcIso}\" -map 0:v:0 -vf idet -frames:v 200 -an -f null -")
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            int tff = 0, bff = 0;
            foreach (var line in err.Split('\n'))
            {
                if (line.IndexOf("Multi frame detection", StringComparison.Ordinal) < 0) continue;
                tff = ParseAfter(line, "TFF:"); bff = ParseAfter(line, "BFF:");
            }
            return tff >= bff;
        }
        catch { return true; }
    }

    private static int ParseAfter(string line, string key)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return 0;
        i += key.Length;
        while (i < line.Length && line[i] == ' ') i++;
        int j = i;
        while (j < line.Length && char.IsDigit(line[j])) j++;
        return j > i && int.TryParse(line.Substring(i, j - i), out var v) ? v : 0;
    }
}
