using System.Globalization;
using System.IO;
using System.Text;
using Kuroko.Media.Dvd;
using Kuroko.Media.Krk;

namespace Kuroko.Converter;

// Blu-ray の ISO（BDMV）を コントローラ "none" の Kuroko(.krk) へ変換する。仕様は docs/kuroko-format-spec.md。
//
// タイトルの集合は **ISO 再生と同じもの**（BdmvTitleSet.Build）を使うので、変換前後で「どれがタイトルか」が
// 食い違わない。構成は BdKurokoPlanner が先に全部決める（§計画）。ここはその計画どおりに符号化して書くだけ。
//
//  ・セグメント＝クリップの IN/OUT 区間。同じ範囲を指す参照は 1 本を共有する
//    （本編クリップを複数タイトル・複数アングルから共有＝BD と同じ節約が効く）。
//  ・アングルは §10.1 の angles[]（angleType="bd"）。尺・章・音声・字幕はタイトルで共通。
//  ・映像ケイデンスは **clpi の ProgramInfo が宣言**している（1080p23.976 / 1080i29.97 等）。
//    放送 TS と違ってソースを再走査して repeat_pict を数える必要が無い。
//  ・字幕（PGS）は**復号しない**。表示 1 回ぶんの生バイト列を subs/img/<pid>/<n>.pgs へ入れる（§11）。
//  ・音声は Opus のみ（2ch ダウンミックス／チャンネル数維持）。コピーは非対応＝変換失敗。
internal static class BdNoneConverter
{
    // ISO の中のクリップは ffmpeg へ**標準入力で流す**（UDF 上のファイルなのでパスが無い）。
    // m2ts は 192 バイト単位（TP_extra_header 付き）だが ffmpeg の mpegts demuxer がそのまま扱える。
    private const string PipeInput = "-i pipe:0";

    public static string ConvertBdToKuroko(string isoPath, string outKrk, DvdH264Converter.Options opt,
        IProgress<DvdH264Converter.Progress>? progress = null, CancellationToken ct = default)
    {
        var log = new StringBuilder();
        var startAt = DateTime.Now;
        log.AppendLine($"start: {startAt:yyyy-MM-dd HH:mm:ss}");
        try
        {
            if (!File.Exists(isoPath)) return "ERROR: 入力が見つかりません: " + isoPath;
            if (Reject(opt) is string why) return log + "\nERROR: " + why;
            // 暗号化（AACS）されたままの Blu-ray は断る（解除はしない）。
            if (EncryptedInputCheck.CheckBdIso(isoPath) is { } enc) return log + "\nERROR: " + enc.Message("この変換器", "変換");

            using var udf = new UdfReader(isoPath);
            var plan = BdKurokoPlanner.Build(udf);
            if (plan.Titles.Count == 0) return log + "\nERROR: 変換できるタイトルがありません";
            log.Append(BdKurokoPlanner.Describe(plan));

            string work = Path.Combine(Path.GetTempPath(), "vuecove_bdnone_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var writer = new KurokoZipWriter();
                writer.AddBytes(KurokoLayout.ManifestFile, KurokoJson.Serialize(new KurokoManifest
                {
                    Controller = KurokoLayout.ControllerNone,
                    // アングルを持つタイトルがあれば 4（§10.1）。無ければ 3 のままでよい。
                    Version = KurokoLayout.CleanLayoutVersion, // version 5（公開仕様 1.0）
                    // createdBy は ASCII に保つ。manifest は他が全て ASCII なので、ここに 1 文字でも
                    // 非ASCII があると、UTF-8 と判別できないエディタでその文字だけ化ける
                    // （controller="none" は別キーで持っているので "→none" は重複でもある）。
                    CreatedBy = "Kuroko Converter (BD)",
                    Comment = DvdH264FullConverter.OptionsComment(opt),
                }));

                // 進捗の重みはセグメントの**符号化する尺**。1 本だけ 2 時間・他は 2 分、という偏りが普通なので
                // 本数で等分すると本編に入った瞬間に止まって見える。書き出しに 0.97..1.0 を残す。
                double totalMs = Math.Max(1, plan.Segments.Sum(s => s.DurationMs));
                double done = 0;
                TruncatedAudioSegments.Clear();
                var segInfo = new Dictionary<string, SegResult>(StringComparer.Ordinal);
                foreach (var seg in plan.Segments)
                {
                    ct.ThrowIfCancellationRequested();
                    double baseF = 0.97 * done / totalMs, spanF = 0.97 * seg.DurationMs / totalMs;
                    var segCap = seg;
                    void Report(double f, string phase) => progress?.Report(new DvdH264Converter.Progress(
                        Math.Clamp(baseF + spanF * Math.Clamp(f, 0, 1), 0, 0.97),
                        $"{segCap.SegId} ({segCap.ClipId}): {phase}"));

                    var r = EncodeSegment(udf, seg, opt, work, writer, ct, log, Report);
                    if (r is null) return log + $"\nERROR: セグメント {seg.SegId}（クリップ {seg.ClipId}）の変換に失敗しました";
                    segInfo[seg.SegId] = r;
                    done += seg.DurationMs;
                }

                var disc = new KurokoNoneDisc();
                foreach (var t in plan.Titles)
                {
                    var title = BuildTitle(t, segInfo);
                    writer.AddBytes(KurokoLayout.TitleJson(t.Id), KurokoJson.Serialize(title));
                    disc.Titles.Add(new KurokoNoneTitleEntry { Id = t.Id, Name = title.Name, DurationMs = title.Timing.TotalMs, Video = $"VIDEO/T{t.Id:00}" });
                    log.AppendLine($"title{t.Id}: {title.Name} dur={title.Timing.TotalMs / 1000.0:F1}s"
                        + $"（計画 {t.TotalMs / 1000.0:F1}s） 章{title.Chapters.Count}"
                        + $" 音声{title.Audio.Count} 字幕{title.Subtitle.Count}"
                        + (t.AngleType is null ? "" : $" angleType={t.AngleType} アングル{title.Angles.Count}"));
                }

                writer.AddBytes(KurokoLayout.DiscJson, KurokoJson.Serialize(disc));
                progress?.Report(new DvdH264Converter.Progress(0.98, "書き出し中"));
                writer.Write(outKrk);
                progress?.Report(new DvdH264Converter.Progress(1.0, "完了"));
                if (TruncatedAudioSegments.Count > 0)
                {
                    log.AppendLine($"※ 音声が途中で切れたセグメントが {TruncatedAudioSegments.Count} 件あります（下記）。"
                        + "該当トラックはその位置から無音になります。");
                    foreach (var t in TruncatedAudioSegments) log.AppendLine("   " + t);
                }
                log.AppendLine($"done: {outKrk} titles={disc.Titles.Count} segments={plan.Segments.Count}"
                    + $" size={(File.Exists(outKrk) ? new FileInfo(outKrk).Length : 0)}");
                log.AppendLine($"end: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (所要 {(DateTime.Now - startAt).TotalSeconds:F1}s)");
                return log.ToString();
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
        catch (OperationCanceledException) { return log + "\nCANCELLED"; }
        catch (Exception ex) { return log + "\nERROR: " + ex; }
    }

    // 対応できない設定は**黙って別物を作らず**変換失敗にする。
    private static string? Reject(DvdH264Converter.Options opt)
    {
        if (string.Equals(opt.AudioMode, "copy", StringComparison.OrdinalIgnoreCase))
            return "BD からの変換は音声コピー（無劣化）に対応していません。Opus 2ch もしくは Opus（チャンネル数維持）を選んでください";
        if (!string.Equals(opt.AudioCodec, "opus", StringComparison.OrdinalIgnoreCase))
            return $"BD からの変換は音声 {opt.AudioCodec} に対応していません。Opus を選んでください";
        return null;
    }

    // ── セグメント（＝クリップの IN/OUT 区間）1 本 ─────────────────────────────
    // 符号化結果と、title.json を組むのに要る論理情報（PID → トラック/source の対応）。
    private sealed class SegResult
    {
        public double TotalMs;
        public readonly Dictionary<int, int> AudioTrackByPid = new();   // PID → av.mkv のトラック番号
        public readonly Dictionary<int, string?> LangByPid = new();     // PID → 言語（音声・字幕とも）
        public readonly HashSet<int> PgPids = new();                    // 実際に cue を書けた字幕 PID
    }

    private static SegResult? EncodeSegment(UdfReader udf, BdPlanSegment seg, DvdH264Converter.Options opt,
        string work, KurokoZipWriter writer, CancellationToken ct, StringBuilder log, Action<double, string> report)
    {
        // セグメント内進捗：字幕抽出 0..0.10／符号化 ..0.95／seg.json ..1.0。
        const double PSubs = 0.10, PEnc = 0.95;
        var inv = CultureInfo.InvariantCulture;
        var ent = udf.FindEntry($"BDMV/STREAM/{seg.ClipId}.m2ts");
        if (ent is null) { log.AppendLine($"{seg.SegId}: クリップ {seg.ClipId} が見つかりません"); return null; }

        var video = seg.Video;
        if (video is null) { log.AppendLine($"{seg.SegId}: 映像ストリームがありません"); return null; }
        // インターレース方式 fields（T/B を半高2トラックへ分離）は BD 経路では未実装。選ばれていても
        // weave で格納するので、**黙って違うものを作らない**ようにここで明示する。
        if (video.Interlaced && string.Equals(opt.InterlaceMode, "fields", StringComparison.OrdinalIgnoreCase))
            log.AppendLine($"{seg.SegId}: インターレース源だが fields 格納は BD 経路では未実装 → weave で格納する");
        var audios = seg.Audio.ToList();
        var pgs = seg.Pg.ToList();
        var result = new SegResult { TotalMs = seg.DurationMs };

        // クリップの提示区間。IN/OUT がこれと一致していればトリム不要＝丸ごと符号化できる。
        var (clipStart45k, clipEnd45k) = BdmvPlaylist.ReadClipStcRange(udf, seg.ClipId);
        long origin90k = clipStart45k * 2;
        double trimStartSec = Math.Max(0, (double)seg.In90k - clipStart45k) / 45000.0;
        bool trimEnd = clipEnd45k > 0 && seg.Out90k + 45 < clipEnd45k;   // 1ms 余裕
        double durSec = (double)(seg.Out90k - seg.In90k) / 45000.0;
        // MaxSecondsPerCell>0 は動作確認用の尺制限（全長を待たずに一通り出力を作る）。
        if (opt.MaxSecondsPerCell > 0 && durSec > opt.MaxSecondsPerCell)
        { durSec = opt.MaxSecondsPerCell; trimEnd = true; }
        double limitMs = Math.Min(seg.DurationMs, durSec * 1000.0);

        // 1) クリップを 1 回だけ走査して、字幕（PGS）の生データと**映像の元 PTS** を同時に取り出す。
        // 映像 PTS を取るのは、ffmpeg の matroska muxer が書き込み時に**時刻を 1ms へ丸めてしまう**ため。
        // 丸まった後では戻せないので、ソース（90kHz）の時刻をここで押さえ、後で mkvmerge で焼き直す。
        // 字幕が無いクリップでも走査する（映像 PTS が要る）。
        var srcPts90k = new List<long>();
        {
            report(0, pgs.Count > 0 ? "字幕・時刻の抽出" : "時刻の抽出");
            var counts = new Dictionary<int, int>();
            int multiDs = 0;
            var indexes = new Dictionary<int, KurokoSubsImage>();
            foreach (var p in pgs)
                indexes[p.Pid] = new KurokoSubsImage
                {
                    Type = "image", Lang = KurokoLanguage.Normalize(p.Language),
                    Canvas = new KurokoSubsCanvas { Width = video.Size.W, Height = video.Size.H },
                };
            // 尺制限時は走査も比例して打ち切る（そうしないと制限しても 40GB を舐め切ってしまう）。
            long scanMax = limitMs < seg.DurationMs - 1
                ? (long)(ent.Length * Math.Min(1.0, (trimStartSec * 1000.0 + limitMs) / Math.Max(1, seg.DurationMs)) * 1.2)
                : 0;
            using (var s = udf.OpenRead(ent))
                BdPgsExtractor.ExtractMany(s, pgs.Select(p => p.Pid).ToList(), origin90k, (pid, cue) =>
                {
                    // トリムぶんを引いてセグメント 0 基準へ。区間の外へ出た cue は捨てる。
                    double st = cue.StartMs - trimStartSec * 1000.0, en = cue.EndMs - trimStartSec * 1000.0;
                    if (en <= 0 || st >= limitMs) return;
                    int n = counts.TryGetValue(pid, out var c) ? c + 1 : 1;
                    counts[pid] = n;
                    // cue 内の Display Set 数。2 以上＝パレット更新（フェード・色替え）を含む。生データには
                    // DS ごとの表示時刻が無い（時刻は捨てた PES ヘッダにあった）ので、再生側は最終状態を
                    // cue 全体に出す。実際に出る盤があるか把握するために数えて出す。
                    int ds = CountDisplaySets(cue.Data);
                    if (ds > 1) multiDs++;
                    string name = $"{n:0000}.pgs";
                    writer.AddBytes(KurokoLayout.SubsImagePng(seg.SegId, pid, name), cue.Data);
                    indexes[pid].Cues.Add(new KurokoSubsCue
                    { StartMs = Math.Max(0, st), EndMs = Math.Min(limitMs, en), Pgs = name });
                }, scanMax, ct, video.Pid, v => srcPts90k.Add(v));
            foreach (var (pid, idx) in indexes)
            {
                if (idx.Cues.Count == 0) continue;                        // このクリップに実体が無い言語
                writer.AddBytes(KurokoLayout.SubsImageIndex(seg.SegId, pid), KurokoJson.Serialize(idx));
                result.PgPids.Add(pid);
                result.LangByPid[pid] = idx.Lang;
            }
            log.AppendLine($"{seg.SegId}: 字幕 {result.PgPids.Count}/{pgs.Count} 本 "
                + string.Join(" ", result.PgPids.OrderBy(x => x).Select(x => $"0x{x:X4}:{counts[x]}"))
                + (multiDs > 0 ? $"  ※パレット更新を含む cue {multiDs} 件（再生は最終状態のみ）" : ""));
        }

        // 2) 映像と音声を **別々の ffmpeg で** 符号化する。
        //
        // 1 回の ffmpeg に映像と音声 11 本をまとめると、**音声が 1 本だけ途中で黙って止まる**ことがある
        //（実測：BD_003 のタイトル3。TrueHD 由来の 1 本が 574〜713 秒で終わり、位置は実行ごとに変わる。
        //   ffmpeg は -loglevel warning でもエラーも警告も出さず、終了コードは 0）。
        // 切り分けの結果、**「映像符号化が遅い」かつ「長時間」の両方が揃ったときだけ**起きると分かった：
        //   音声のみ（15分/全長）… 正常   映像あり preset=ultrafast（全長）… 正常
        //   映像あり preset=medium（15分）… 正常   映像あり preset=medium（全長）… 再現
        // 映像が遅いほど音声は先へ進んだまま出力待ちで溜まり続けるので、「速度差 × 時間」が効く形になる。
        // 原因は ffmpeg の内部にあってこちらからは直せないため、**映像と音声の結合そのものを断つ**。
        // DVD 変換器が音声を別パスで通し符号化しているのと同じ作法。代償はクリップをもう 1 回読むこと。
        //
        // **PID で map してはいけない**（-map i:0x…）。ffmpeg は BD の TrueHD の PID を **2 本**
        //（TrueHD 本体と、同じ PID に多重された互換 AC-3 コア）として見せるので、-map i: は両方に当たる。
        // すると出力の本数が増え、`-c:a:N` / `-metadata:s:a:N` は**出力の並び**に付くので 1 本ずつずれる。
        // 実測（ある市販ディスク）：9 本のつもりが 11 本になり、言語が後ろへずれた上に、指定の余った末尾 2 本は
        // matroska の既定コーデック（vorbis）で符号化されていた。mkv トラック8 は jpn 表示なのに中身は
        // ces（ISO の PID 0x1105 と相関 +0.968／日本語 0x1107 とは −0.075）だった。
        //
        // そこで **ffprobe で実際のストリーム番号を引き**、`-map 0:<番号>` で 1 本ずつ選ぶ。
        // 引けなかったときだけ従来どおり PID で map する（少なくとも今までと同じ結果になる）。
        report(PSubs, "符号化中 0%");
        string avMkv = Path.Combine(work, seg.SegId + ".mkv");        // 映像のみ
        string audioMka = Path.Combine(work, seg.SegId + ".audio.mka"); // 音声のみ
        var streamIndexByPid = ProbeStreamIndexByPid(udf, ent, opt.FfprobePath, opt.FfmpegPath, work, seg.SegId, log);
        string MapArg(int pid) => streamIndexByPid.TryGetValue(pid, out int si)
            ? $"-map 0:{si} "
            : $"-map i:0x{pid:X4} ";
        var a = new StringBuilder();
        a.Append("-y -hide_banner -loglevel error -analyzeduration 100M -probesize 100M ");
        a.Append(PipeInput).Append(' ');
        // -ss は入力の**後**に置く（パイプは seek できないので復号して読み飛ばす）。ある市販ディスク のように
        // IN＝presentation_start なら 0 になり、余計な読み飛ばしは起きない。
        if (trimStartSec > 0.001) a.Append($"-ss {trimStartSec.ToString("F3", inv)} ");
        if (trimEnd) a.Append($"-t {durSec.ToString("F3", inv)} ");
        a.Append(MapArg(video.Pid));
        a.Append("-fps_mode passthrough -an ");
        AppendVideoArgs(a, opt, video);
        a.Append($"-f matroska \"{avMkv}\"");
        // 0 起点をそろえる（TsNoneConverter.BaseAlignOutput の注記）。音声も読ませて捨てる。
        string limitArgs = (trimStartSec > 0.001 ? $"-ss {trimStartSec.ToString("F3", inv)} " : "")
                         + (trimEnd ? $"-t {durSec.ToString("F3", inv)} " : "");
        a.Append(TsNoneConverter.BaseAlignOutput(
            audios.Where(x => streamIndexByPid.ContainsKey(x.Pid)).Select(x => streamIndexByPid[x.Pid]).Distinct(), limitArgs));

        // 音声側（別プロセス）。トラック番号は mkvmerge で映像の後ろに付くので 1 から。
        var aa = new StringBuilder();
        aa.Append("-y -hide_banner -loglevel error -analyzeduration 100M -probesize 100M ");
        aa.Append(PipeInput).Append(' ');
        if (trimStartSec > 0.001) aa.Append($"-ss {trimStartSec.ToString("F3", inv)} ");
        if (trimEnd) aa.Append($"-t {durSec.ToString("F3", inv)} ");
        aa.Append("-vn -sn ");

        // どの候補も復号できなかった音声は、**無音のトラック**として作る（トラックの並びと本数を保つため。
        // ここで抜くと、以降の番号付けと title.json の音声番号がずれる）。lavfi の anullsrc を入力に足し、
        // 入力側の -t で尺を切る（lavfi は無限に出るので必ず区切る）。
        var silent = audios.Where(x => !streamIndexByPid.ContainsKey(x.Pid)).ToList();
        int lavfiInput = 1;                                // 0 は本体（パイプ）
        var silentInputIndex = new Dictionary<int, int>();  // PID → 入力番号
        foreach (var au in silent)
        {
            int ch = DvdH264FullConverter.DownmixToStereo(opt) ? Math.Min(2, au.Channels) : Math.Max(1, au.Channels);
            string layout = ch >= 2 ? "stereo" : "mono";
            aa.Append($"-f lavfi -t {durSec.ToString("F3", inv)} -i anullsrc=channel_layout={layout}:sample_rate=48000 ");
            silentInputIndex[au.Pid] = lavfiInput++;
        }

        foreach (var au in audios)
        {
            if (silentInputIndex.TryGetValue(au.Pid, out int inIdx)) aa.Append($"-map {inIdx}:a:0 ");
            else aa.Append(MapArg(au.Pid));
        }

        int track = 1;                                     // 映像が 0、音声は 1 から
        for (int i = 0; i < audios.Count; i++)
        {
            var au = audios[i];
            int chOut = DvdH264FullConverter.DownmixToStereo(opt) ? Math.Min(2, au.Channels) : Math.Max(1, au.Channels);
            aa.Append($"-c:a:{i} libopus -b:a:{i} {opt.AudioBitrateK * chOut}k -ac:a:{i} {chOut} ");
            if (!string.IsNullOrEmpty(au.Language)) aa.Append($"-metadata:s:a:{i} language={au.Language} ");
            result.AudioTrackByPid[au.Pid] = track++;
            result.LangByPid[au.Pid] = au.Language;
        }
        if (silent.Count > 0)
            log.AppendLine($"{seg.SegId}: 復号できない音声 {silent.Count} 本を無音トラックにする（"
                + string.Join(" ", silent.Select(x => $"0x{x.Pid:X4}")) + "）");
        aa.Append($"-f matroska \"{audioMka}\"");
        // 0 起点をそろえる（同上）。映像も読ませて捨てる。
        if (streamIndexByPid.TryGetValue(video.Pid, out int videoIdx))
            aa.Append(TsNoneConverter.BaseAlignOutput(new[] { videoIdx }, limitArgs));

        long clipLen = Math.Max(1, ent.Length);
        double PMid = PSubs + (PEnc - PSubs) * 0.75;   // 映像で 3/4、音声で 1/4（音声は符号化が軽い）

        int rc;
        string encErr;
        using (var s = udf.OpenRead(ent))
            rc = DvdH264Converter.RunEncode(opt.FfmpegPath, a.ToString(), s, out encErr, ct,
                fed =>
                {
                    double f = Math.Min(1.0, fed / (double)clipLen);
                    report(PSubs + (PMid - PSubs) * f, $"映像を符号化中 {(int)(100 * f)}%");
                });
        if (rc != 0 || !FileOk(avMkv))
        { log.AppendLine($"{seg.SegId}: ffmpeg 失敗（映像）\n{Tail(encErr)}"); return null; }

        string audErr = "";
        if (audios.Count > 0)
        {
            using var s2 = udf.OpenRead(ent);
            int arc = DvdH264Converter.RunEncode(opt.FfmpegPath, aa.ToString(), s2, out audErr, ct,
                fed =>
                {
                    double f = Math.Min(1.0, fed / (double)clipLen);
                    report(PMid + (PEnc - PMid) * f, $"音声を符号化中 {(int)(100 * f)}%");
                });
            if (arc != 0 || !FileOk(audioMka))
            { log.AppendLine($"{seg.SegId}: ffmpeg 失敗（音声）\n{Tail(audErr)}"); return null; }
        }

        // **成功（rc=0）でも stderr は残す。** ffmpeg は「一部の出力ストリームだけ落ちた」ようなときでも
        // 0 で終了することがあり、黙って通ると壊れた krk ができる（実測：BD_003 の TrueHD 音声が 10 分で
        // 切れたのに、ログにはエラーが 1 行も残っていなかった）。-loglevel error なので平常時は空。
        if (!string.IsNullOrWhiteSpace(encErr))
            log.AppendLine($"{seg.SegId}: ffmpeg の出力（映像・終了コード 0）\n{Tail(encErr)}");
        if (!string.IsNullOrWhiteSpace(audErr))
            log.AppendLine($"{seg.SegId}: ffmpeg の出力（音声・終了コード 0）\n{Tail(audErr)}");

        // 全音声トラックが最後まで出ているかを検証する。1 本だけ途中で終わっていても ffmpeg は 0 を返すので、
        // ここで捕まえないと気づけない。**見つけても変換は止めない**：セグメントを失敗にすると変換全体が
        // 中止になり（呼び出し側が ERROR で返す）、他が無事でも何も得られなくなる。代わりに変換ログへ
        // 記録し、完了行の直前にも一覧で出す。
        if (audios.Count > 0)
            VerifyAudioTrackSpans(opt.FfprobePath, audioMka, audios.Count, seg.SegId, log, TruncatedAudioSegments);

        // 2b) mkvmerge でタイムスタンプを細かく入れ直す。
        // ffmpeg の matroska muxer は TimestampScale を選べず、**既定の 1ms へ丸めて書く**
        //（23.976fps の 41.7083ms が 41/42ms に化ける）。スケールを細かくするだけでは
        // **一度丸めた値は戻らない**ので、走査で押さえた**ソースの 90kHz PTS をそのまま焼き直す**。
        // 宣言フレームレートからグリッドを作る手もあるが、それは CFR を仮定することになる。
        // 元の時刻を使えば、CFR でない盤・プルダウンの載った盤でもそのまま正しい。
        List<double>? times = null;                 // 焼き直した映像の時刻（3) の尺計算でも使う）
        {
            string remuxed = Path.Combine(work, seg.SegId + ".clean.mkv");
            var mk = new StringBuilder($"-o \"{remuxed}\" {DvdH264Converter.MkvTimestampScaleArg}");
            int nf = ProbeFrameCount(opt.FfprobePath, avMkv);
            times = BuildSourceTimestamps(srcPts90k, origin90k, trimStartSec, limitMs, nf);
            if (times is not null)
            {
                string tsFile = Path.Combine(work, seg.SegId + ".ts.txt");
                var tsb = new StringBuilder("# timestamp format v2\n");
                foreach (var t in times) tsb.Append(t.ToString("F6", inv)).Append('\n');
                File.WriteAllText(tsFile, tsb.ToString());
                mk.Append($" --timestamps \"0:{tsFile}\"");
                log.AppendLine($"{seg.SegId}: 元 PTS を焼き直す（{times.Count} フレーム / 末尾 {times[^1]:F3}ms）");
            }
            else
                log.AppendLine($"{seg.SegId}: 元 PTS を使えないため丸めのまま再格納する"
                    + $"（拾えた PTS {srcPts90k.Count} 件 / 符号化フレーム {nf} 枚）");
            // 音声のずらし量は、焼き直す前の映像の頭を測ってから決める（AudioShiftMs の注記）。
            string audioSync = "";
            if (times is not null && audios.Count > 0 && FileOk(audioMka))
            {
                double vHeadMs = ProbeStartTimeMs(opt.FfprobePath, avMkv, "v:0");
                double shift = AudioShiftMs(times[0], vHeadMs);
                if (Math.Abs(shift) >= 1.0)
                {
                    var sy = new StringBuilder();
                    for (int i = 0; i < audios.Count; i++) sy.Append($" --sync {i}:{(int)Math.Round(shift)}");
                    audioSync = sy.ToString();
                }
                log.AppendLine($"{seg.SegId}: 音声の基準合わせ：映像の頭 ffmpeg={vHeadMs:F1}ms → 元 PTS={times[0]:F1}ms"
                    + $"（音声を {shift:+0.0;-0.0;0}ms ずらす{(audioSync.Length == 0 ? "＝不要" : "")}）");
            }
            mk.Append($" \"{avMkv}\"");
            // 音声は別ファイル（別パスで符号化した）ので、mkvmerge の入力として後ろに足す。
            // mkvmerge のオプションは**直後の入力ファイル**に効くので、--timestamps は映像側、--sync は音声側に付く
            //（音声ファイルの中の番号＝0 から）。
            if (audios.Count > 0 && FileOk(audioMka)) mk.Append(audioSync).Append($" \"{audioMka}\"");
            if (RunTool(opt.MkvmergePath, mk.ToString(), ct, out string mErr) && FileOk(remuxed))
            {
                // 成功でも出力は残す（ffmpeg と同じ理由。警告だけ出して一部を落とす可能性がある）。
                if (!string.IsNullOrWhiteSpace(mErr))
                    log.AppendLine($"{seg.SegId}: mkvmerge の出力（成功）\n{Tail(mErr)}");
                try { File.Delete(avMkv); } catch { }
                try { if (audios.Count > 0) File.Delete(audioMka); } catch { }
                avMkv = remuxed;
            }
            else if (audios.Count > 0 && FileOk(audioMka))
            {
                // 音声は別ファイルなので、ここで失敗すると**音声が丸ごと落ちる**。時刻の焼き直しを諦めて
                // 素の結合だけやり直す（精度は 1ms のままになるが、音声は失わない）。
                log.AppendLine($"{seg.SegId}: mkvmerge の再格納に失敗（時刻の焼き直しを諦めて結合のみ再試行）\n{Tail(mErr)}");
                string plain = Path.Combine(work, seg.SegId + ".join.mkv");
                string joinArgs = $"-o \"{plain}\" {DvdH264Converter.MkvTimestampScaleArg} \"{avMkv}\" \"{audioMka}\"";
                if (RunTool(opt.MkvmergePath, joinArgs, ct, out string jErr) && FileOk(plain))
                {
                    try { File.Delete(avMkv); } catch { }
                    try { File.Delete(audioMka); } catch { }
                    avMkv = plain;
                    times = null;   // 焼き直していないので、尺は容器から測り直す
                }
                else
                { log.AppendLine($"{seg.SegId}: 音声の結合にも失敗しました\n{Tail(jErr)}"); return null; }
            }
            else
                log.AppendLine($"{seg.SegId}: mkvmerge の再格納に失敗（精度 1ms のまま続行）\n{Tail(mErr)}");
        }

        // 3) seg.json（メディア属性・0 基準タイミング）。尺は**符号化結果の実測**を使う（宣言尺との
        //    わずかな差や、尺制限での打ち切りをそのまま反映する。タイトルの連結位置もこれを積む）。
        //
        // 尺は**映像の時間軸**で測る。容器の duration（format=duration）は映像と音声の長い方で、
        // Opus は映像より 20ms 前後長くなる。それを連結オフセットに使うと、**セグメント境界ごとに
        // その差だけ空きが生じ、繰り返し参照で積み上がる**（実測：音声のある S0002 を 301 回並べる
        // タイトル1 が 7.7 秒過大。音声の無い S0008 を 451 回並べるタイトル4 はずれ無し）。
        report(PEnc, "seg.json");
        double measured = 0;
        if (times is { Count: > 1 })
            measured = times[^1] + (times[^1] - times[^2]);      // 最終フレームの表示終わり＝映像の実尺
        if (measured <= 0) measured = ProbeDurationMs(opt.FfprobePath, avMkv);
        if (measured > 0) result.TotalMs = measured;
        else result.TotalMs = limitMs;
        seg = seg with { DurationMs = result.TotalMs };
        var segJson = new KurokoNoneSegment
        {
            Video = new KurokoNoneVideo
            {
                // 旧：h264 以外は h265 として扱っていた（AV1 を足したので正規化をそろえる。BD の既定は h265）。
                Codec = string.IsNullOrWhiteSpace(opt.VideoCodec) ? "av1" : KurokoVideoCodecs.Normalize(opt.VideoCodec),
                Mode = "transcode",
                PixelWidth = video.Size.W, PixelHeight = video.Size.H,
                DisplayWidth = video.Size.W, DisplayHeight = video.Size.H,   // BD は正方画素（SAR 1:1）
                Aspect = video.Aspect,
                FrameRate = video.FrameRate ?? "24000/1001",
                // ケイデンスは clpi の宣言そのもの。BD の film は 23.976p で**素のまま**入っているので
                // ソフトテレシネ（RFF）を解く必要が無く、インターレースは 1080i/480i/576i の宣言で確定する。
                Cadence = video.Interlaced ? "interlaced" : "progressive",
                Tff = true,
                InterlaceMode = "weave",
                FieldLayout = "single",
                InterlacedRanges = video.Interlaced
                    ? new List<KurokoRange> { new() { StartMs = -1, EndMs = seg.DurationMs + 1 } } : null,
            },
            Timing = new KurokoNoneTiming { TotalMs = seg.DurationMs },
        };
        // 音声は 1 スパン（BD のクリップは途中で構成が変わらない。放送 TS と違い番組境界が無い）。
        if (audios.Count > 0)
        {
            var span = new KurokoNoneAudioSpan { StartMs = 0, EndMs = seg.DurationMs };
            foreach (var au in audios)
                span.Tracks.Add(new KurokoNoneAudioTrack
                {
                    Track = result.AudioTrackByPid[au.Pid],
                    Source = au.Pid,                       // source ＝ **PID**（物理キー。title.json から引く）
                    Codec = "opus",
                    Channels = DvdH264FullConverter.DownmixToStereo(opt) ? Math.Min(2, au.Channels) : Math.Max(1, au.Channels),
                    Language = KurokoLanguage.Normalize(au.Language),
                });
            segJson.Audio.Add(span);
        }
        foreach (var pid in result.PgPids.OrderBy(x => x))
            segJson.Subtitle.Add(new KurokoNoneSubtitle { Source = pid, Track = -1, Type = "image" });

        writer.AddFile(KurokoLayout.SegmentAvMkv(seg.SegId), avMkv);
        writer.AddBytes(KurokoLayout.SegmentJson(seg.SegId), KurokoJson.Serialize(segJson));
        report(1.0, "完了");
        return result;
    }

    private static void AppendVideoArgs(StringBuilder a, DvdH264Converter.Options opt, BdClipStream video)
    {
        bool av1 = KurokoVideoCodecs.IsAv1(opt.VideoCodec);
        bool h265 = !av1 && !string.Equals(opt.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase);
        int keyint = opt.Keyint > 0 ? opt.Keyint : 300;
        bool closed = string.Equals(opt.GopMode, "closed", StringComparison.OrdinalIgnoreCase);
        bool deint = !string.Equals(opt.Deinterlace, "none", StringComparison.OrdinalIgnoreCase);
        if (deint && video.Interlaced) a.Append($"-vf {opt.Deinterlace.ToLowerInvariant()}=mode=send_field ");
        if (av1)
            KurokoVideoCodecs.AppendAv1Args(a, KurokoVideoCodecs.SvtPreset(opt.Preset), opt.Crf, video.Aspect, keyint, allIntra: false);
        else if (h265)
        {
            a.Append($"-c:v libx265 -preset {opt.Preset} -crf {opt.Crf} -pix_fmt yuv420p -aspect {video.Aspect} ");
            a.Append($"-x265-params keyint={keyint}:scenecut=0:min-keyint=1:"
                + (closed ? "open-gop=0" : "open-gop=1") + ":repeat-headers=1:log-level=error ");
        }
        else
        {
            // 実インターレースを維持するときだけインターレース符号化（H.265 にはこのツールが無いので weave のまま）。
            string ilme = video.Interlaced && !deint ? "-flags +ilme+ildct " : "";
            a.Append($"-c:v libx264 -preset {opt.Preset} -crf {opt.Crf} -pix_fmt yuv420p -aspect {video.Aspect} {ilme}");
            a.Append($"-x264-params keyint={keyint}:scenecut=0:min-keyint=1:"
                + (closed ? "open-gop=0" : "open-gop=1") + ":repeat-headers=1"
                + (video.Interlaced && !deint ? ":tff=1" : "") + " ");
        }
    }

    // ── title.json（純粋に論理。メディア属性は seg.json 側）─────────────────────
    private static KurokoNoneTitle BuildTitle(BdPlanTitle t, Dictionary<string, SegResult> segInfo)
    {
        var title = new KurokoNoneTitle { Id = t.Id, Name = t.Name };

        // 連結位置は**符号化結果の実尺**を積み直す（計画の StartMs は BDMV の宣言尺。両者の差が
        // 積み上がると後ろのセグメントで音ズレになる）。アングル間の尺は計画時に一致を確認済み。
        List<KurokoNonePlaylistItem> Build(List<BdPlanPlaylistItem> src, out double total)
        {
            var list = new List<KurokoNonePlaylistItem>(src.Count);
            double at = 0;
            foreach (var p in src)
            {
                list.Add(new KurokoNonePlaylistItem { Seg = p.SegId, StartMs = at });
                at += segInfo.TryGetValue(p.SegId, out var r) ? r.TotalMs : 0;
            }
            total = at;
            return list;
        }

        // アングルは §10.1。Angles と Playlist は排他なので、アングル 1 本なら Playlist に入れる。
        double totalMs = 0;
        if (t.AngleType is not null && t.Angles.Count > 1)
        {
            title.AngleType = t.AngleType;
            foreach (var ang in t.Angles)
            {
                title.Angles.Add(new KurokoNoneAngle { Number = ang.Number, Playlist = Build(ang.Playlist, out var m) });
                totalMs = Math.Max(totalMs, m);
            }
        }
        else if (t.Angles.Count > 0)
            title.Playlist.AddRange(Build(t.Angles[0].Playlist, out totalMs));
        title.Timing.TotalMs = totalMs;

        // 章は PlayListMark 由来。実尺より後ろに落ちたものは載せない（尺制限で切ったとき等）。
        foreach (var ms in t.ChapterMs)
            if (ms < totalMs)
                title.Chapters.Add(new KurokoNoneChapter { Number = title.Chapters.Count + 1, StartMs = ms });

        // 論理音声／字幕は STN_table の並び（＝BD の選択メニューの並び）。source は PID で、seg.json 側の
        // source と突き合わせて実トラックへ解決する（2 段解決）。そのセグメントに無ければその区間は出ない。
        var segs = (title.Angles.Count > 0 ? title.Angles[0].Playlist : title.Playlist)
            .Select(p => p.Seg).Distinct().Select(id => segInfo.GetValueOrDefault(id)).Where(x => x is not null).ToList();
        for (int i = 0; i < t.Audio.Count; i++)
        {
            var e = t.Audio[i];
            title.Audio.Add(new KurokoNoneAudioLogical
            {
                Number = e.Number > 0 ? e.Number : i + 1,
                Source = e.Pid,
                Language = KurokoLanguage.Normalize(e.Language ?? segs.Select(s => s!.LangByPid.GetValueOrDefault(e.Pid)).FirstOrDefault(x => x is not null)),
            });
        }
        foreach (var e in t.Pg)
        {
            // どのセグメントにも実体が無い字幕は載せない（選べるのに絶対出ない項目を作らない）。
            // 落としたぶんは詰めて 1..n に振り直す（STN の番号をそのまま使うと欠番になる）。
            if (!segs.Any(s => s!.PgPids.Contains(e.Pid))) continue;
            title.Subtitle.Add(new KurokoNoneSubtitleLogical
            {
                Number = title.Subtitle.Count + 1,
                Type = "image",
                Source = e.Pid,
                Language = KurokoLanguage.Normalize(e.Language ?? segs.Select(s => s!.LangByPid.GetValueOrDefault(e.Pid)).FirstOrDefault(x => x is not null)),
            });
        }
        return title;
    }

    // 走査で拾ったソースの映像 PTS（90kHz・復号順）から、mkvmerge へ渡すタイムスタンプ列（ms・表示順）を作る。
    // 使えないと判断したら null（呼び出し側は丸めのまま続行する）。
    //
    //  ・PES は復号順に出てくるので**表示順へ並べ替える**（B フレームがあるため）。
    //  ・符号化した範囲だけに絞る（-ss / -t / 尺制限）。時刻はセグメント 0 基準へ寄せる。
    //  ・**符号化フレーム数と一致しなければ使わない**。数が食い違ったまま焼くと全フレームがずれるので、
    //    黙って当てずに丸めのまま残すほうが安全（ログに両方の数を出す）。
    private static List<double>? BuildSourceTimestamps(List<long> srcPts90k, long origin90k,
        double trimStartSec, double limitMs, int encodedFrames)
    {
        if (srcPts90k.Count == 0 || encodedFrames <= 0) return null;
        double head = trimStartSec * 1000.0;
        var rel = new List<double>(srcPts90k.Count);
        foreach (var v in srcPts90k)
        {
            double ms = (v - origin90k) / 90.0 - head;
            if (ms >= -0.5) rel.Add(ms);
        }
        rel.Sort();                                  // 復号順 → 表示順
        if (rel.Count < encodedFrames) return null;  // 足りない＝取りこぼしがある
        rel.RemoveRange(encodedFrames, rel.Count - encodedFrames);   // -t の打ち切りぶんを落とす
        if (rel[0] < -0.5) return null;
        rel[0] = Math.Max(0, rel[0]);
        for (int i = 1; i < rel.Count; i++)
            if (rel[i] <= rel[i - 1]) return null;   // 単調増加でなければ信用しない
        // 尺が想定から大きく外れていたら使わない（PTS ラップや別ストリームの混入を弾く）。
        if (limitMs > 0 && rel[^1] > limitMs + 1000.0) return null;
        return rel;
    }

    // mkv の映像フレーム数（パケット数）。取れなければ 0。
    // クリップの先頭を切り出して ffprobe にかけ、**PID → 実ストリーム番号**を引く。
    // 同じ PID に複数のストリームがぶら下がるとき（BD の TrueHD＝本体＋AC-3 コア）は
    // **最初の 1 本**を採る。ffprobe が使えない・PID が拾えないときは空の辞書を返し、
    // 呼び出し側は従来の PID 指定へ落ちる。
    private static Dictionary<int, int> ProbeStreamIndexByPid(
        UdfReader udf, UdfEntry ent, string ffprobe, string ffmpeg, string work, string segId, StringBuilder log)
    {
        var map = new Dictionary<int, int>();
        var candidates = new Dictionary<int, List<(int Index, string Codec)>>();
        string probePath = Path.Combine(work, segId + ".probe.m2ts");
        try
        {
            // 先頭 64MB（38Mbps なら約 13 秒）。BD の音声は連続して載るので、これで全本が現れる。
            const long HeadBytes = 64L << 20;
            using (var src = udf.OpenRead(ent))
            using (var dst = File.Create(probePath))
            {
                var buf = new byte[1 << 20];
                long left = HeadBytes;
                while (left > 0)
                {
                    int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    dst.Write(buf, 0, n);
                    left -= n;
                }
            }

            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe,
                "-v error -analyzeduration 100M -probesize 100M "
                + $"-show_entries stream=index,id,codec_type,codec_name -of csv=p=0 \"{probePath}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string outText = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();

            foreach (var line in outText.Split('\n'))
            {
                // index,codec_name,codec_type,id  の並び（-show_entries の指定順ではなく定義順で出る）
                var c = line.Trim().Split(',');
                if (c.Length < 4) continue;
                if (!int.TryParse(c[0], out int index)) continue;
                string idText = c[^1].Trim();
                if (!idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(idText.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out int pid)) continue;
                // **同じ PID に複数のストリームが出ることがある。** BD の TrueHD は「TrueHD 本体」と
                // 「同じ PID に多重された互換 AC-3 コア」の 2 本として見える。候補を順に控えておく。
                if (!candidates.TryGetValue(pid, out var list)) candidates[pid] = list = new List<(int, string)>();
                list.Add((index, c.Length >= 2 ? c[1].Trim() : ""));
            }

            // 候補が 1 本だけならそのまま。複数あるときは**先頭から順に実際に復号できるかを確かめ**、
            // 最初に通ったものを採る（無条件に先頭を採ると、復号できない形式を選んでしまう）。
            // 1 本も通らなかった PID は map に入れず、呼び出し側が無音のトラックを作る。
            foreach (var kv in candidates)
            {
                var list = kv.Value;
                if (list.Count == 1) { map[kv.Key] = list[0].Index; continue; }

                int chosen = -1;
                var tried = new List<string>();
                foreach (var (idx, codec) in list)
                {
                    bool ok = CanDecodeStream(ffmpeg, probePath, work, idx);
                    tried.Add($"{idx}:{codec}{(ok ? "=OK" : "=NG")}");
                    if (ok) { chosen = idx; break; }
                }
                if (chosen >= 0) map[kv.Key] = chosen;
                log.AppendLine($"{segId}: PID 0x{kv.Key:X4} に {list.Count} 本 — {string.Join(" ", tried)}"
                    + (chosen >= 0 ? $" → {chosen} を使う" : " → **どれも復号できないので無音にする**"));
            }

            log.AppendLine($"{segId}: ストリーム番号を確定 {map.Count} 本 "
                + string.Join(" ", map.OrderBy(x => x.Value).Select(x => $"0x{x.Key:X4}=>{x.Value}")));
        }
        catch (Exception ex)
        {
            log.AppendLine($"{segId}: ストリーム番号の取得に失敗（PID 指定へ）: {ex.Message}");
            map.Clear();
        }
        finally
        {
            try { if (File.Exists(probePath)) File.Delete(probePath); } catch { }
        }
        return map;
    }

    // このストリームを実際に復号できるか。先頭の probe ファイルから数秒だけ復号して、PCM が出るかで判断する。
    // 「同じ PID に複数のストリームが出る」ときの選択に使う（TrueHD 本体 と 互換 AC-3 コアなど）。
    private static bool CanDecodeStream(string ffmpeg, string probePath, string work, int index)
    {
        string wav = Path.Combine(work, $"decodetest_{index}.wav");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg,
                $"-y -v error -i \"{probePath}\" -map 0:{index} -t 3 -ac 2 -f wav \"{wav}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            // WAV ヘッダだけ（44B）では「復号できた」と言えない。1 秒ぶん（48kHz/2ch/16bit≒192KB）の
            // 1 割も出ていなければ失敗とみなす。
            return p.ExitCode == 0 && File.Exists(wav) && new FileInfo(wav).Length > 20000;
        }
        catch { return false; }
        finally { try { if (File.Exists(wav)) File.Delete(wav); } catch { } }
    }

    // 全音声トラックが最後まで出ているかを検証する。
    // ffmpeg は一部の出力ストリームだけ落としても終了コード 0 を返すことがあり、黙って通ると壊れた krk が
    // できる（実測：BD_003 の TrueHD 音声だけ 607 秒で切れ、他 10 本は 9,326 秒あった）。
    //
    // 判定は「**末尾 30 秒にパケットがあるか**」。全パケットを数えると 2.6 時間の素材で数百万パケットになり
    // 変換時間に響くので、-read_intervals で末尾だけを読む。BD の音声はクリップ全長にわたって載るので、
    // 末尾に 1 つも無ければ途中で落ちたと判断してよい。
    // 音声が途中で切れたセグメントの記録（完了時にまとめて出す）。
    private static readonly List<string> TruncatedAudioSegments = new();

    // 他の変換器（TsNoneConverter）から使うための入口。記録だけして一覧には積まない。
    internal static void VerifyAudioTrackSpansForLog(string ffprobe, string path, int audioCount, string tag, StringBuilder log)
        => VerifyAudioTrackSpans(ffprobe, path, audioCount, tag, log, new List<string>());

    private static void VerifyAudioTrackSpans(string ffprobe, string path, int audioCount, string segId,
        StringBuilder log, List<string> truncated)
    {
        if (audioCount <= 0) return;
        double dur = ProbeFormatDuration(ffprobe, path);
        if (dur <= 40.0) return;   // 短すぎる素材は末尾窓が取れないので見ない

        var seen = ProbeStreamsInTailWindow(ffprobe, path, dur - 30.0);
        if (seen.Count == 0) { log.AppendLine($"{segId}: 音声トラックの検証を省略（末尾のパケットを読めません）"); return; }

        // 検証するのは**音声だけのファイル**なので、ストリーム番号は 0 から始まる（映像が 0 番ではない）。
        // ここを 1 始まりにしていて、常に最後の 1 本を「無い」と誤検出していた。
        // 利用者向けの表示は 1 始まり（トラック1 = ストリーム0）に直して出す。
        var missing = new List<string>();
        for (int i = 0; i < audioCount; i++)
            if (!seen.Contains(i)) missing.Add($"トラック{i + 1}");
        if (missing.Count == 0) return;

        string detail = $"{segId}: **音声が途中で終わっています** — 全長 {dur:F1}s の末尾 30 秒に "
            + $"{string.Join(" ", missing)} のパケットがありません（音声 {audioCount} 本中 {missing.Count} 本）";
        log.AppendLine(detail
            + "。ffmpeg が一部のストリームだけ落として終了コード 0 を返した可能性があります。上の ffmpeg 出力を確認してください。");
        truncated.Add(detail);
    }

    private static double ProbeFormatDuration(string ffprobe, string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe,
                $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            return double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
        }
        catch { return 0.0; }
    }

    // 末尾の窓（fromSec 以降）に現れたストリーム番号の集合。
    private static HashSet<int> ProbeStreamsInTailWindow(string ffprobe, string path, double fromSec)
    {
        var seen = new HashSet<int>();
        try
        {
            string from = fromSec.ToString("F3", CultureInfo.InvariantCulture);
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe,
                $"-v error -read_intervals {from}%+40 -show_entries packet=stream_index -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            foreach (var line in o.Split('\n'))
                if (int.TryParse(line.Trim().TrimEnd(','), out int idx)) seen.Add(idx);
        }
        catch
        {
        }
        return seen;
    }

    // 別パスで符号化した音声を、映像の焼き直しに合わせてずらす量（ms）。
    // 映像だけ・音声だけの各 ffmpeg は、どちらも**入力の先頭（全ストリームの最小 start_time）**を 0 として時刻を付ける
    //（-ss/-t も同じ）ので、ここまでは映像と音声の基準が同じ。ただしこれは、各実行が相手側のストリームも読ませて
    // 捨てているから成り立つ（TsNoneConverter.BaseAlignOutput。ffmpeg は使うストリームの先頭だけで 0 を決める）。ところが映像は、その後 mkvmerge で**元の 90kHz PTS
    //（クリップの開始＝clpi の presentation start 基準）**へ焼き直す。2 つの基準が違えば（クリップの先頭で音声や
    // 字幕が映像より先に始まる盤）、映像だけがその差ぶん動き、音声とずれる。
    // 映像の頭が「ffmpeg の基準で vHeadMs」から「焼き直し後の firstTimeMs」へ動くので、音声も同じだけ動かす。
    // 実測：ある Blu-ray（全ストリームが同じ時刻から始まる盤）は差 0。放送 TS で同じ形の取り違え（b117fb5）があり、
    // 音声が 570ms 遅れていたので、BD も DVD と同様に**測って戻す**形にしておく。
    internal static double AudioShiftMs(double firstTimeMs, double vHeadMs) => firstTimeMs - vHeadMs;

    // ストリームの start_time（ms）。取れなければ 0。
    private static double ProbeStartTimeMs(string ffprobe, string path, string stream)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe,
                $"-v error -select_streams {stream} -show_entries stream=start_time -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            var line = o.Trim().Split('\n')[0].Trim();
            return double.TryParse(line, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec)
                ? sec * 1000.0 : 0.0;
        }
        catch { return 0.0; }
    }

    private static int ProbeFrameCount(string ffprobe, string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe,
                $"-v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            return int.TryParse(o.Trim(), out int n) ? n : 0;
        }
        catch { return 0; }
    }

    // 外部ツールを入力無しで実行する（mkvmerge 用）。
    private static bool RunTool(string exe, string args, CancellationToken ct, out string stderr)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            var errTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            stderr = errTask.GetAwaiter().GetResult();
            ct.ThrowIfCancellationRequested();
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { stderr = ex.Message; return false; }
    }

    // cue に入っている Display Set の数（END セグメント 0x80 の数）。
    private static int CountDisplaySets(byte[] b)
    {
        int i = 0, n = 0;
        while (i + 3 <= b.Length)
        {
            int len = (b[i + 1] << 8) | b[i + 2];
            if (i + 3 + len > b.Length) break;
            if (b[i] == 0x80) n++;
            i += 3 + len;
        }
        return n;
    }

    // 符号化結果の尺（ms）。取れなければ 0。
    private static double ProbeDurationMs(string ffprobe, string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ffprobe,
                $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            return double.TryParse(outp.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v * 1000.0 : 0;
        }
        catch { return 0; }
    }

    private static bool FileOk(string p) => File.Exists(p) && new FileInfo(p).Length > 0;
    private static string Tail(string s, int n = 800) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[^n..]);
}
