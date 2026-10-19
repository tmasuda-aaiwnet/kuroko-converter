using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Kuroko.Media.Dvd;
using Kuroko.Media.Krk;

namespace Kuroko.Converter;

// MPEG-2 TS（放送・配信）／H.264 TS（AVCHD 等）を コントローラ "none" の Kuroko(.krk) へ変換する
// （映像＋音声。字幕は対象外）。仕様は docs/kuroko-format-spec.md 第I部。
//
//  ・サービス（プログラム）ごとに 1 タイトル。同一映像を指すミラーは 1 タイトルに集約。単一プログラムは 1 タイトル。
//  ・title.json は純粋に論理（番号・言語・プレイリスト・source 写像キー）。メディア属性は seg.json 帰属。
//  ・映像ケイデンスを保持：
//      - インターレース（29.97i/50i）：weave（H.265）/インターレース符号化（H.264）＋interlacedRanges、再生側 bob。
//      - 24p ソフトテレシネ（3:2）：フレーム別フィールド数(=2+repeat_pict)のクリーン時刻を mkvmerge --timestamps で焼込み。
//      - プログレッシブ（59.94p/29.97p/25p/50p/24p）：実フレームレートの CFR。
//  ・音声構成（デュアルモノ↔ステレオ等）の変化点でセグメント分割し、各セグメントを単一構成に保つ
//    （全音声トラックの変化点の和で切る）。デュアルモノは seg.audio[].dualMono（真偽）で持つ。
//  ・**チャンネル数の変化（例：2ch→5.1ch）は「別トラック＋スパン」で表す**（セグメントは切らない）。
//    Matroska の Channels は TrackEntry の属性で、Opus はさらにチャンネルマッピングが CodecPrivate に
//    入るため、1本のトラックの途中では変えられない。seg.json は「時刻スパン×トラック集合」（仕様§7）
//    なので、区間ごとに別トラックを置いてスパンで指し分ける。「Opus 2ch」選択時は出力が常に 2ch なので
//    分割不要（この処理は「チャンネル数維持」のときだけ働く）。
internal static class TsNoneConverter
{
    private sealed class TsAudio
    {
        public int Index; public int Pid = -1; public string Codec = ""; public int Channels; public string? Lang; public string? Name;
        // この音声の「出力トラック」の並び。既定は全長1本だが、チャンネル数維持のときは
        // **実フレームのチャンネル数が変わる点で分割**して複数本になる（§下の AudioRun 参照）。
        public List<AudioRun> Runs = new();
    }

    // 音声の1出力トラック分。StartMs/EndMs は**入力音声の先頭を0とした時刻**（ffmpeg の atrim と同じ軸）。
    // EndMs=double.MaxValue は「最後まで」。Channels は出力チャンネル数、OutIndex は出力音声トラックの通し番号。
    //
    // なぜ分割するか：Matroska の Channels は TrackEntry の属性で、さらに Opus はチャンネル数と
    // チャンネルマッピングが CodecPrivate(OpusHead) に入るため、**1本のトラックの途中で 2ch→5.1ch には
    // できない**。ffmpeg も復号器の初期レイアウトでエンコーダを構成するので、途中から来る 5.1 は黙って
    // 畳まれる（実測：ステレオで始まり12.5秒後に5.1になる素材は -ac 無しでも opus,2ch になった）。
    // Kuroko の seg.json は「時刻スパン×トラック集合」（仕様§7）なので、区間ごとに別トラックを置き、
    // スパンで指し分ければ元のチャンネル数を保てる。
    private sealed class AudioRun
    {
        public double StartMs; public double EndMs = double.MaxValue; public int Channels; public int OutIndex;
    }

    private sealed class TsService
    {
        public int ProgramNum;
        public string? Name;
        public int VideoIndex = -1;
        public string VideoCodec = "";
        public int Width, Height;
        public string Aspect = "16:9";
        public string Fps = "30000/1001";
        public string FieldOrder = "";
        public List<TsAudio> Audios = new();
        // ARIB 字幕（ffprobe の arib_caption）。PID と言語。docs/kuroko-format-spec.md §11.1・§12。
        public List<(int Index, int Pid, string? Lang)> Captions = new();
    }

    // 1 セグメントのエンコード結果（メディア＋seg.json＋尺）。
    private sealed class SegResult
    {
        public string CleanMkv = ""; public KurokoNoneSegment Seg = new(); public double TotalMs;
        // 字幕（subs/text/<source>.json）。source・言語・名前・中身。
        public List<(int Source, string? Lang, string Name, byte[] Json)> TextSubs = new();
    }

    // CLI/GUI から呼ぶエントリ。戻り値はレポート文字列（末尾に "done:" を含めば成功）。
    public static string ConvertTsToKuroko(string tsPath, string outKrk, DvdH264Converter.Options opt,
        IProgress<DvdH264Converter.Progress>? progress = null, CancellationToken ct = default)
    {
        var log = new StringBuilder();
        var startAt = DateTime.Now;
        log.AppendLine($"start: {startAt:yyyy-MM-dd HH:mm:ss}");
        try
        {
            if (!File.Exists(tsPath)) return "ERROR: 入力が見つかりません: " + tsPath;
            // スクランブルされたままの TS は断る（解除はしない）。
            if (EncryptedInputCheck.CheckTs(tsPath) is string enc) return "ERROR: " + enc;
            var services = ProbeServices(opt.FfprobePath, tsPath, log);
            if (services.Count == 0) return log + "\nERROR: 映像のあるサービスが見つかりません";
            log.AppendLine($"ts: services={services.Count} [{string.Join(", ", services.Select(s => $"{s.ProgramNum}:{s.Name}"))}]");
            // 番組タイムライン（events・音声名称・言語・デュアルモノ区間）を EIT/PMT/TOT＋実ビットストリームから抽出。
            List<TsProgramTimeline> progTls;
            try { progTls = TsProgramAnalyzer.Analyze(tsPath, opt.MaxSecondsPerCell, ct); }
            catch { progTls = new(); }
            log.AppendLine($"program-analyze: {TsProgramAnalyzer.LastDebug.Split('\n')[0]}");

            string work = Path.Combine(Path.GetTempPath(), "vuecove_tsnone_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var writer = new KurokoZipWriter();
                writer.AddBytes(KurokoLayout.ManifestFile, KurokoJson.Serialize(new KurokoManifest
                  // createdBy は ASCII に保つ（BdNoneConverter と同じ理由）。
                { Controller = KurokoLayout.ControllerNone, Version = KurokoLayout.CleanLayoutVersion, CreatedBy = "Kuroko Converter (TS)",
                  Comment = DvdH264FullConverter.OptionsComment(opt) }));

                // タイトル表示名：SDT の service_name は ARIB 符号で ffprobe が正しく復号できず文字化けするため、
                // 実用上は録画ファイル名を基にする（複数サービス時はサービス番号を付す）。
                string baseName = Path.GetFileNameWithoutExtension(tsPath);
                var disc = new KurokoNoneDisc();
                int titleNo = 1, segCounter = 1;
                for (int si = 0; si < services.Count; si++)
                {
                    ct.ThrowIfCancellationRequested();
                    var svc = services[si];
                    // セグメント（サービス）内進捗（0..1＋フェーズ文言）→ 全体進捗（0..0.97）へ写像。書き出しに 0.97..1.0 を残す。
                    // 複数サービスは等分（TSはサービスが実ファイル内で多重化されており元バイトの単純な範囲重みが取れないため）。
                    double segBase = 0.97 * si / services.Count;
                    double segSpan = 0.97 / services.Count;
                    int siCap = si; var svcCap = svc;
                    Action<double, string> segReport = (f, phase) => progress?.Report(new DvdH264Converter.Progress(
                        Math.Clamp(segBase + segSpan * Math.Clamp(f, 0, 1), 0, 0.97),
                        services.Count > 1 ? $"サービス{svcCap.ProgramNum} ({siCap + 1}/{services.Count}): {phase}" : phase));
                    segReport(0, "エンコード開始");
                    // 番組タイムライン（このサービス）。音声構成（本数・デュアルモノ・名称）は event 境界で変わる。
                    var tl = progTls.FirstOrDefault(t => t.ProgramNumber == svc.ProgramNum);
                    // 論理音声の言語（title.audio の union 用。安定なので先頭イベント/PMT から）。
                    for (int t = 0; t < svc.Audios.Count; t++)
                    {
                        int pid = svc.Audios[t].Pid;
                        if (tl is not null && pid >= 0 && string.IsNullOrEmpty(svc.Audios[t].Lang))
                            svc.Audios[t].Lang = tl.AudioAt(pid, 0)?.Language ?? (tl.LanguageByPid.TryGetValue(pid, out var lgp) ? lgp : null);
                    }

                    string segId = $"S{segCounter:0000}"; segCounter++;
                    var r = EncodeService(opt, tsPath, svc, segId, work, opt.MaxSecondsPerCell, tl, ct, log, segReport);
                    if (r is null) { log.AppendLine($"service {svc.ProgramNum}: 変換失敗（スキップ）"); continue; }
                    writer.AddFile(KurokoLayout.SegmentAvMkv(segId), r.CleanMkv);
                    writer.AddBytes(KurokoLayout.SegmentJson(segId), KurokoJson.Serialize(r.Seg));
                    foreach (var ts in r.TextSubs) writer.AddBytes(KurokoLayout.SubsTextJson(segId, ts.Source), ts.Json);
                    var segs = new List<(string Id, SegResult R)> { (segId, r) };

                    var (title, titleMs) = BuildTitleNone(titleNo,
                        services.Count > 1 ? $"{baseName} (#{svc.ProgramNum})" : baseName, segs, svc, tl);
                    writer.AddBytes(KurokoLayout.TitleJson(titleNo), KurokoJson.Serialize(title));
                    disc.Titles.Add(new KurokoNoneTitleEntry
                    {
                        Id = titleNo,
                        Name = title.Name,
                        DurationMs = titleMs,
                        Video = $"VIDEO/T{titleNo:00}",
                    });
                    bool anyDm = r.Seg.Audio.Any(sp => sp.Tracks.Any(tr => tr.DualMono));
                    int maxTr = r.Seg.Audio.Count == 0 ? 0 : r.Seg.Audio.Max(sp => sp.Tracks.Count);
                    log.AppendLine($"title{titleNo}: service={svc.ProgramNum} dur={titleMs / 1000.0:F1}s spans={r.Seg.Audio.Count} maxTracks={maxTr} dualMono={anyDm}");
                    titleNo++;
                }
                if (disc.Titles.Count == 0) return log + "\nERROR: 変換できたタイトルがありません";

                writer.AddBytes(KurokoLayout.DiscJson, KurokoJson.Serialize(disc));
                progress?.Report(new DvdH264Converter.Progress(0.98, "書き出し中"));
                writer.Write(outKrk);
                progress?.Report(new DvdH264Converter.Progress(1.0, "完了"));
                log.AppendLine($"done: {outKrk} titles={disc.Titles.Count} size={(File.Exists(outKrk) ? new FileInfo(outKrk).Length : 0)}");
                log.AppendLine($"end: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (所要 {(DateTime.Now - startAt).TotalSeconds:F1}s)");
                return log.ToString();
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
        catch (OperationCanceledException) { return log + "\nCANCELLED"; }
        catch (Exception ex) { return log + "\nERROR: " + ex; }
    }

    // 1サービスを av.mkv へエンコードし、ケイデンスを焼き込んで clean seg を返す（失敗=null）。音声では分割しない。
    // maxSec>0 なら先頭からその秒数だけ（0=全長）。tl＝番組タイムライン（音声スパン組立に使う）。
    private static SegResult? EncodeService(
        DvdH264Converter.Options opt, string ts, TsService svc, string segId, string work,
        double maxSec, TsProgramTimeline? tl, CancellationToken ct, StringBuilder log,
        Action<double, string>? sp = null)
    {
        // セグメント内進捗（0..1）：エンコード 0..0.75（ffmpeg -progress で滑らか）／フレーム・フィールド解析 ..0.90／
        // タイミング焼き込み(mkvmerge) ..0.97／音声構成 ..1.0。
        void SP(double f, string t) => sp?.Invoke(Math.Clamp(f, 0, 1), t);
        const double PEnc = 0.75, PProbe = 0.90, PMkv = 0.97;
        var inv = CultureInfo.InvariantCulture;
        bool h265 = string.Equals(opt.VideoCodec, "h265", StringComparison.OrdinalIgnoreCase);
        bool deintApplied = !string.Equals(opt.Deinterlace, "none", StringComparison.OrdinalIgnoreCase);
        bool interlacedSrc = svc.FieldOrder is "tt" or "tb" or "bb" or "bt";
        bool tff = svc.FieldOrder is "tt" or "tb";
        double ssSec = 0;
        double encDur = maxSec > 0 ? maxSec : 0; // 0=全長（-t を付けない）

        bool pal = svc.Fps.StartsWith("25") || svc.Fps.StartsWith("50");
        double fieldDurMs = pal ? 20.0 : 1001.0 / 60000.0 * 1000.0;

        // ── ケイデンス判定 ─────────────────────────────────────────
        // interlaced：field-time（weave/インタレ符号化）。soft-telecine：field-time（プログレッシブ符号化）。
        // progressive：実フレームレートの CFR。deinterlace 選択時は CFR プログレッシブ。
        string cadence;
        bool encodeInterlaced;
        List<double> tsMs; List<bool> progAll; double totalMs; int nAv;

        // 1) 映像＋音声を raw.mkv へ。encodeInterlaced はまず field_order から仮決め（soft-telecine は後で false 化）。
        //    先に fields を見て soft-telecine/progressive を切り分けたいが、エンコード自体は encodeInterlaced に依存する
        //    ため、interlacedSrc のみを weave 符号化の条件とする（telecine/progressive は progressive 符号化）。
        // インターレース方式＝fields：実インターレース源を T/B の半高さ倍フレームへ分離し progressive 符号化する
        //（放送インターレースは RFF を持たないため純インターレースとして安全に分離できる）。deinterlace 選択時は不可。
        bool fieldSep = interlacedSrc && !deintApplied
            && string.Equals(opt.InterlaceMode, "fields", StringComparison.OrdinalIgnoreCase);
        encodeInterlaced = interlacedSrc && !deintApplied && !fieldSep;
        string rawMkv = Path.Combine(work, segId + "_raw.mkv");
        // 音声コピー（AAC無劣化）は、AAC extradata（サンプルレート）取得にシーク可能なファイル入力が要るため pipe 不可。
        // その場合のみファイル入力＋フェーズ単位進捗にフォールバックする。再エンコード時は pipe＋供給バイトで滑らかに。
        bool audioCopy = IsCopy(opt);
        // 音声の出力トラック割り（チャンネル数維持のときだけ、変化点でトラックを分ける）。
        SP(0, "音声チャンネル解析");
        PlanAudioOutputs(opt, ts, svc, encDur, ct, log);
        string audioMka = Path.Combine(work, segId + "_audio.mka");
        double PMid = PEnc * 0.75;   // 映像で 3/4、音声で 1/4（音声は符号化が軽い）

        // 映像パスと音声パスを別々の ffmpeg で走らせる（EncodePart 参照）。
        int rc; string encErr;
        int RunPart(string a2, double from, double to, string label, out string err)
        {
            if (audioCopy)
            {
                SP(from, label);
                return RunFfmpegNoInput(opt.FfmpegPath, a2, ct, out err) ? 0 : 1;
            }
            // DVDと同じ「供給バイト数」方式：TSを ffmpeg の標準入力へ流し、給餌済みバイト÷ファイル長を写像
            //（RunEncode が ~1MB ごとに累積供給バイトを通知。-t 早期終了で stdin が閉じても途中打ち切り＝正常）。
            SP(from, label + " 0%");
            long tsLen = Math.Max(1, new FileInfo(ts).Length);
            using var tsStream = File.OpenRead(ts);
            return DvdH264Converter.RunEncode(opt.FfmpegPath, a2, tsStream, out err, ct,
                fed => { double ff = Math.Min(1.0, fed / (double)tsLen); SP(from + (to - from) * ff, $"{label} {(int)(100 * ff)}%"); });
        }

        string vArgs = BuildFfmpegArgs(opt, ts, svc, rawMkv, encodeInterlaced, tff, h265, ssSec, encDur, fieldSep,
            pipeInput: !audioCopy, part: EncodePart.VideoOnly);
        rc = RunPart(vArgs, 0, PMid, "映像を符号化中", out encErr);
        if (rc != 0 || !FileOk(rawMkv))
        { log.AppendLine($"service {svc.ProgramNum}: ffmpeg 失敗（映像）\n{Tail(encErr)}"); return null; }
        if (!string.IsNullOrWhiteSpace(encErr))
            log.AppendLine($"service {svc.ProgramNum}: ffmpeg の出力（映像・終了コード 0）\n{Tail(encErr)}");

        int audioOuts = AudioOutCount(svc);
        string audErr = "";
        if (audioOuts > 0)
        {
            string aArgs = BuildFfmpegArgs(opt, ts, svc, audioMka, encodeInterlaced, tff, h265, ssSec, encDur, fieldSep,
                pipeInput: !audioCopy, part: EncodePart.AudioOnly);
            int arc = RunPart(aArgs, PMid, PEnc, "音声を符号化中", out audErr);
            if (arc != 0 || !FileOk(audioMka))
            { log.AppendLine($"service {svc.ProgramNum}: ffmpeg 失敗（音声）\n{Tail(audErr)}"); return null; }
            if (!string.IsNullOrWhiteSpace(audErr))
                log.AppendLine($"service {svc.ProgramNum}: ffmpeg の出力（音声・終了コード 0）\n{Tail(audErr)}");
        }
        // 全音声トラックが最後まで出ているかを検証する（BdNoneConverter と同じ理由。1 本だけ途中で
        // 終わっていても ffmpeg は 0 を返す）。見つけても変換は止めず、ログへ記録するだけにする。
        if (audioOuts > 0 && FileOk(audioMka))
            BdNoneConverter.VerifyAudioTrackSpansForLog(opt.FfprobePath, audioMka, audioOuts,
                $"service {svc.ProgramNum}", log);

        SP(PEnc, "フレーム解析");
        nAv = ProbeVideoFrameCount(opt.FfprobePath, rawMkv);

        if (deintApplied)
        {
            double frameMs = pal ? 20.0 : 1001.0 / 30000.0 * 1000.0;
            tsMs = new List<double>(nAv); progAll = new List<bool>(nAv);
            for (int i = 0; i < nAv; i++) { tsMs.Add(i * frameMs); progAll.Add(true); }
            totalMs = nAv * frameMs; cadence = "progressive";
        }
        else if (interlacedSrc)
        {
            if (fieldSep) // dual：各トラック=元フレーム数の 29.97・半高。tsMs はレンジ判定用（全域インターレース）。
            {
                int nTop = ProbeVideoFrameCount(opt.FfprobePath, rawMkv); // track0(top) のフレーム数＝元フレーム数
                double frameMs = pal ? 40.0 : 1001.0 / 30000.0 * 1000.0;
                tsMs = new List<double>(nTop); progAll = new List<bool>(nTop);
                for (int i = 0; i < nTop; i++) { tsMs.Add(i * frameMs); progAll.Add(false); }
                totalMs = nTop > 0 ? nTop * frameMs : 0;
            }
            else
                (tsMs, progAll, totalMs) = FieldTimeline(opt, ts, svc, ssSec, encDur, nAv, fieldDurMs, encodeInterlaced, ct);
            cadence = "interlaced";
        }
        else
        {
            // プログレッシブ源：fields を見て soft-telecine（repeat_pict 有り）か genuine progressive かを判定。
            var (fields, prog) = ProbeVideoFields(opt.FfprobePath, ts, svc.VideoIndex, ssSec, encDur, ct);
            bool telecine = fields.Any(f => f != 2);
            if (telecine)
            {
                tsMs = new List<double>(fields.Count); progAll = new List<bool>(fields.Count);
                double t = 0;
                for (int i = 0; i < fields.Count; i++) { tsMs.Add(t); progAll.Add(prog[i]); t += fields[i] * fieldDurMs; }
                FitToFrames(tsMs, progAll, nAv, fieldDurMs);
                totalMs = tsMs.Count >= 2 ? tsMs[^1] + (tsMs[^1] - tsMs[^2]) : tsMs.Count == 1 ? tsMs[^1] + fieldDurMs * 2 : 0;
                cadence = "soft-telecine";
            }
            else
            {
                double frameMs = FrameMs(svc.Fps);
                tsMs = new List<double>(nAv); progAll = new List<bool>(nAv);
                for (int i = 0; i < nAv; i++) { tsMs.Add(i * frameMs); progAll.Add(true); }
                totalMs = nAv * frameMs; cadence = "progressive";
            }
        }

        // 2) タイムスタンプv2 を作り、mkvmerge --timestamps で av.mkv のPTSへ焼き込む。
        SP(PProbe, "タイミング焼き込み");
        double vStartMs = ProbeStartTimeMs(opt.FfprobePath, rawMkv, "v:0");
        string cleanMkv = Path.Combine(work, segId + "_clean.mkv");
        if (fieldSep) // dual：track0=top / track1=bottom。**両トラックとも同一PTS（i*frameMs）**を焼く（再生は完全一致でペア化）。
        {                // 時間順（tff で top/bottom どちらが先か）と半フィールドの表示ずれは再生側で付ける（PTSには含めない）。
            double frameMs = pal ? 40.0 : 1001.0 / 30000.0 * 1000.0;
            var sb = new StringBuilder("# timestamp format v2\n");
            for (int i = 0; i < tsMs.Count; i++) sb.Append((i * frameMs).ToString("F3", inv)).Append('\n');
            string tsF = Path.Combine(work, segId + "_ts2.txt");
            File.WriteAllText(tsF, sb.ToString());
            var m = new StringBuilder($"-o \"{cleanMkv}\" {DvdH264Converter.MkvTimestampScaleArg} --timestamps \"0:{tsF}\" --timestamps \"1:{tsF}\"");
            m.Append($" \"{rawMkv}\"");
            // 音声は別ファイル（別パスで符号化した）。mkvmerge の入力として後ろに足し、映像を 0 へ振り直した
            // ぶんだけ前へずらす（AudioSyncArgs の注記）。
            if (audioOuts > 0 && FileOk(audioMka)) m.Append(AudioSyncArgs(audioOuts, vStartMs)).Append($" \"{audioMka}\"");
            RunFfmpegNoInput(opt.MkvmergePath, m.ToString(), ct, out string mErrD);
            if (!FileOk(cleanMkv)) { log.AppendLine($"service {svc.ProgramNum}: mkvmerge(dual) PTS焼き直し 失敗\n{Tail(mErrD)}"); return null; }
        }
        else
        {
            string tsFile = Path.Combine(work, segId + "_ts.txt");
            var sbts = new StringBuilder("# timestamp format v2\n");
            foreach (var v in tsMs) sbts.Append(v.ToString("F3", inv)).Append('\n');
            File.WriteAllText(tsFile, sbts.ToString());
            // mkvmerge --timestamps は映像(track0)を field-time で 0 基準へ再タイムする。音声は据え置きなので映像シフト分を --sync で揃える。
            var mkvArgs = new StringBuilder($"-o \"{cleanMkv}\" {DvdH264Converter.MkvTimestampScaleArg} --timestamps \"0:{tsFile}\"");
            mkvArgs.Append($" \"{rawMkv}\"");
            if (audioOuts > 0 && FileOk(audioMka)) mkvArgs.Append(AudioSyncArgs(audioOuts, vStartMs)).Append($" \"{audioMka}\"");
            RunFfmpegNoInput(opt.MkvmergePath, mkvArgs.ToString(), ct, out string mErr);
            if (!FileOk(cleanMkv)) { log.AppendLine($"service {svc.ProgramNum}: mkvmerge PTS焼き直し 失敗\n{Tail(mErr)}"); return null; }
        }

        // 3) seg.json（メディア属性・0基準タイミング）。
        var (dw, dh) = DisplaySize(svc.Width, svc.Height, svc.Aspect);
        bool hasInterlace = cadence == "interlaced" || cadence == "soft-telecine";
        var seg = new KurokoNoneSegment
        {
            Video = new KurokoNoneVideo
            {
                Codec = KurokoVideoCodecs.Normalize(opt.VideoCodec), Mode = "transcode",
                // fields dual：av.mkv は 2映像トラック（half高・各29.97）。PixelHeight は半高、FrameRate は各トラックの実レート（倍にしない）。
                PixelWidth = svc.Width, PixelHeight = fieldSep ? svc.Height / 2 : svc.Height,
                DisplayWidth = dw, DisplayHeight = fieldSep ? dh / 2 : dh,
                Aspect = svc.Aspect == "4:3" ? "4:3" : "16:9",
                FrameRate = svc.Fps,
                Cadence = cadence, Tff = tff,
                InterlaceMode = fieldSep ? "fields" : "weave",
                FieldLayout = fieldSep ? "dual" : "single",
                InterlacedRanges = hasInterlace ? BuildInterlacedRanges(tsMs, progAll, fieldDurMs) : null,
            },
            Timing = new KurokoNoneTiming { TotalMs = totalMs },
        };
        SP(PMkv, "音声構成");
        // dual は映像2トラック→音声は track2+。vStartMs は raw.mkv の映像先頭ぶん（mkvmerge --sync で
        // 音声を前へずらした量）。ラン境界は入力音声先頭基準なので、同じ量を引いて出力時刻軸へ揃える。
        seg.Audio = BuildAudioSpans(opt, svc, tl, totalMs, fieldSep ? 2 : 1, vStartMs > 1 ? vStartMs : 0);
        var result = new SegResult { CleanMkv = cleanMkv, Seg = seg, TotalMs = totalMs };
        if (svc.Captions.Count > 0)
        {
            SP(PMkv, "字幕を取り出し中");
            try { BuildCaptions(opt, ts, svc, vStartMs, totalMs, result, log, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log.AppendLine($"service {svc.ProgramNum}: 字幕の取り出しに失敗（字幕なしで続ける）: {ex.Message}"); }
        }
        SP(1.0, "完了");
        return result;
    }

    // ARIB 字幕をテキスト型（§11.1、charset "arib"）へ写す。時刻は krk の映像と同じ 0 基準＝**krk の映像の 0 秒目
    //（最初に復号できた絵）の元の PTS** を引く。これは ffprobe で直接取る（ffmpeg の起点＋映像の頭からの計算は、
    // muxer の丸めで数 ms ずれる。実測：ある放送の TSで 8ms）。取れなければその計算で代える。
    private static void BuildCaptions(DvdH264Converter.Options opt, string ts, TsService svc, double vStartMs, double totalMs,
        SegResult result, StringBuilder log, CancellationToken ct)
    {
        double originMs = ProbeFirstVideoFramePtsMs(opt.FfprobePath, ts, svc.VideoIndex);
        string originNote = "最初の絵の PTS";
        if (double.IsNaN(originMs))
        {
            double minStart = double.MaxValue;
            foreach (int idx in new[] { svc.VideoIndex }.Concat(svc.Audios.Select(a => a.Index)))
            {
                double st = ProbeStartTimeMs(opt.FfprobePath, ts, idx.ToString(CultureInfo.InvariantCulture));
                if (st > 0) minStart = Math.Min(minStart, st);
            }
            if (minStart == double.MaxValue) { log.AppendLine($"service {svc.ProgramNum}: 字幕：時刻の基準が取れないので字幕なし"); return; }
            originMs = minStart + vStartMs;
            originNote = "起点＋映像の頭";
        }
        long origin90k = (long)Math.Round(originMs * 90.0);
        foreach (var cap in svc.Captions)
        {
            var pesList = new List<(long Pts, byte[] Data)>();
            using (var fs = File.OpenRead(ts))
                AribCaptionExtractor.Extract(fs, cap.Pid, (pts, data) => pesList.Add((pts, data)), ct);
            for (int lang = 1; lang <= 2; lang++)
            {
                var dec = new AribCaptionDecoder(lang);
                var conv = new AribCaptionToKuroko();
                // 時刻は届いた順に連続値へ直す（26.5 時間ごとの PTS のラップを跨ぐ長時間録画のため）。
                var unwrap = new AribCaptionExtractor.Unwrapper(origin90k);
                foreach (var (pts, data) in pesList)
                {
                    long cont = unwrap.Next(pts);
                    var c = dec.Decode(data);
                    if (c is null) continue;
                    double ms = (cont - origin90k) / 90.0;
                    conv.Add(ms, c);
                }
                string? langCode = dec.Languages[lang - 1] ?? (lang == 1 ? cap.Lang : null);
                if (lang == 2 && dec.Languages[1] is null) break; // 第 2 言語なし
                var text = conv.Build(totalMs, langCode, lang == 1 ? "字幕" : "字幕（第2言語）");
                if (text.Cues.Count == 0) continue;
                int source = cap.Pid + (lang - 1) * 0x10000;
                result.TextSubs.Add((source, langCode, text.Name ?? "字幕", KurokoJson.Serialize(text)));
                result.Seg.Subtitle.Add(new KurokoNoneSubtitle { Source = source, Track = -1, Type = "text" });
                log.AppendLine($"service {svc.ProgramNum}: 字幕 PID=0x{cap.Pid:X4} 言語{lang}（{langCode}）：PES {pesList.Count} 件 → 表示 {conv.CueCount} 回／DRCS {conv.DrcsCount} 字"
                    + $"（時刻の基準 {originMs:F1}ms＝{originNote}）");
            }
        }
    }

    // インターレース源のフィールド時刻列（field-time モデル）。probe と encode のフレーム微差を吸収する。
    private static (List<double> TsMs, List<bool> Prog, double TotalMs) FieldTimeline(
        DvdH264Converter.Options opt, string ts, TsService svc, double ssSec, double durSec, int nAv,
        double fieldDurMs, bool encodeInterlaced, CancellationToken ct)
    {
        var (fields, prog) = ProbeVideoFields(opt.FfprobePath, ts, svc.VideoIndex, ssSec, durSec, ct);
        var tsMs = new List<double>(fields.Count); var progAll = new List<bool>(fields.Count);
        double t = 0;
        for (int i = 0; i < fields.Count; i++) { tsMs.Add(t); progAll.Add(prog[i]); t += fields[i] * fieldDurMs; }
        FitToFrames(tsMs, progAll, nAv, fieldDurMs);
        double totalMs = tsMs.Count >= 2 ? tsMs[^1] + (tsMs[^1] - tsMs[^2]) : tsMs.Count == 1 ? tsMs[^1] + fieldDurMs * 2 : 0;
        return (tsMs, progAll, totalMs);
    }

    private static void FitToFrames(List<double> tsMs, List<bool> progAll, int nAv, double fieldDurMs)
    {
        if (nAv <= 0) return;
        while (tsMs.Count < nAv) { tsMs.Add(tsMs.Count > 0 ? tsMs[^1] + fieldDurMs * 2 : 0); progAll.Add(progAll.Count > 0 ? progAll[^1] : true); }
        if (tsMs.Count > nAv) { tsMs.RemoveRange(nAv, tsMs.Count - nAv); progAll.RemoveRange(nAv, progAll.Count - nAv); }
    }

    // none の title.json（論理・複数セグメントのプレイリスト）。物理は seg.json 側。論理↔物理は source 恒等。
    private static (KurokoNoneTitle Title, double TotalMs) BuildTitleNone(
        int id, string name, List<(string Id, SegResult R)> segs, TsService svc, TsProgramTimeline? tl)
    {
        var title = new KurokoNoneTitle { Id = id, Name = name };
        double off = 0;
        foreach (var (segId, r) in segs)
        {
            title.Playlist.Add(new KurokoNonePlaylistItem { Seg = segId, StartMs = off });
            off += r.TotalMs;
        }
        title.Timing.TotalMs = off;
        // 論理音声（union）：全セグメント・全スパンに現れる音声 source を集約（number/source/language のみ）。
        // 位置ごとの実メニューは seg.json のスパンから引くので、これは要約（file-list/info 用）。
        var seen = new SortedDictionary<int, KurokoNoneAudioLogical>();
        foreach (var (_, r) in segs)
            foreach (var sp in r.Seg.Audio)
                foreach (var tr in sp.Tracks)
                    if (!seen.ContainsKey(tr.Source))
                        seen[tr.Source] = new KurokoNoneAudioLogical { Number = tr.Source + 1, Source = tr.Source, Language = KurokoLanguage.Normalize(tr.Language) };
        title.Audio.AddRange(seen.Values);
        // 論理字幕（テキスト型の ARIB 字幕）。番号＝並び順、source＝PID（§12）。
        var seenSub = new SortedDictionary<int, KurokoNoneSubtitleLogical>();
        foreach (var (_, r) in segs)
            foreach (var ts in r.TextSubs)
                if (!seenSub.ContainsKey(ts.Source))
                    seenSub[ts.Source] = new KurokoNoneSubtitleLogical { Source = ts.Source, Type = "text", Language = KurokoLanguage.Normalize(ts.Lang), Name = ts.Name };
        int subNo = 1;
        foreach (var sl in seenSub.Values) { sl.Number = subNo++; title.Subtitle.Add(sl); }
        // 番組（events）。録画区間 [0, off] に収まるものを時刻付きで。表示名は「現在位置の event 名→title.name」。
        if (tl is not null)
            foreach (var e in tl.Events)
            {
                if (e.EndMs <= 0 || e.StartMs >= off) continue;
                title.Events.Add(new KurokoNoneEvent
                {
                    StartMs = Math.Max(0, e.StartMs), EndMs = Math.Min(off, e.EndMs),
                    Name = e.Name, EventId = e.EventId,
                    StartTime = e.StartTime?.ToString("yyyy-MM-ddTHH:mm:ss"),
                });
            }
        return (title, off);
    }

    // 出力音声コーデック：copy=元コーデック／opus／ac3。
    private static string AudioOutCodec(DvdH264Converter.Options opt, TsAudio a)
        => string.Equals(opt.AudioMode, "copy", StringComparison.OrdinalIgnoreCase) ? NormAudioCodec(a.Codec) : "opus";

    // 出力音声チャンネル：コピーは元のまま。再符号化は設定どおり（"stereo"=3ch以上を 2ch へ／"source"=元のまま）。
    private static int AudioOutChannels(DvdH264Converter.Options opt, int srcCh)
    {
        int src = Math.Max(1, srcCh);
        if (string.Equals(opt.AudioMode, "copy", StringComparison.OrdinalIgnoreCase)) return src;
        return DvdH264FullConverter.DownmixToStereo(opt) ? Math.Min(2, src) : src;
    }

    // 出力音声トラックの総本数（分割ぶんを含む）。
    private static int AudioOutCount(TsService svc) => svc.Audios.Sum(a => Math.Max(1, a.Runs.Count));

    private static bool IsCopy(DvdH264Converter.Options opt)
        => string.Equals(opt.AudioMode, "copy", StringComparison.OrdinalIgnoreCase);

    // 各音声の出力トラック（Runs）を決める。
    //  ・コピー／2chダウンミックス … 全長1本（チャンネル数は途中で変わっても出力は一定なので分割不要）
    //  ・チャンネル数維持          … 実フレームのチャンネル数が変わる点で分割し、区間ごとに1本
    // 出力トラックの通し番号(OutIndex)は「音声の並び順 × その音声のラン順」で 0 から振る（＝ffmpeg へ
    // -map する順序、＝av.mkv の音声トラックの並び）。
    private static void PlanAudioOutputs(DvdH264Converter.Options opt, string ts, TsService svc,
        double durSec, CancellationToken ct, StringBuilder log)
    {
        bool keepChannels = !IsCopy(opt) && !DvdH264FullConverter.DownmixToStereo(opt);
        int outIndex = 0;
        foreach (var a in svc.Audios)
        {
            a.Runs.Clear();
            List<AudioRun> runs;
            if (!keepChannels)
                runs = new List<AudioRun> { new() { StartMs = 0, EndMs = double.MaxValue, Channels = AudioOutChannels(opt, a.Channels) } };
            else
            {
                runs = ProbeAudioChannelRuns(opt.FfprobePath, ts, a.Index, durSec, ct);
                if (runs.Count == 0)
                    runs = new List<AudioRun> { new() { StartMs = 0, EndMs = double.MaxValue, Channels = Math.Max(1, a.Channels) } };
            }
            foreach (var r in runs) { r.OutIndex = outIndex++; a.Runs.Add(r); }
            if (a.Runs.Count > 1)
                log.AppendLine($"  audio idx={a.Index}: チャンネル数が変化 → {a.Runs.Count}本に分割 "
                    + string.Join(" / ", a.Runs.Select(r => $"[{r.StartMs:F0}〜{(r.EndMs == double.MaxValue ? "終" : r.EndMs.ToString("F0"))}]{r.Channels}ch")));
        }
    }

    // 実フレームを走査してチャンネル数の変化区間を得る（時刻は**この音声の先頭フレームを0**とした ms）。
    // ストリーム見出しの channels は先頭フレームで決まるため、途中で 5.1 になる素材を取りこぼす。
    // 短すぎる区間（切替前後の1〜2フレーム等）は直前へ吸収して、無意味な細切れを避ける。
    private static List<AudioRun> ProbeAudioChannelRuns(string ffprobe, string ts, int streamIndex,
        double durSec, CancellationToken ct)
    {
        const double MinRunMs = 500.0;
        var runs = new List<AudioRun>();
        try
        {
            var inv = CultureInfo.InvariantCulture;
            string ri = durSec > 0 ? $"-read_intervals %+{durSec.ToString(inv)} " : "";
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -select_streams {streamIndex} {ri}-show_entries frame=pts_time,channels -of csv=p=0 -i \"{ts}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
            double first = double.NaN, lastMs = 0;
            string? line;
            while ((line = p.StandardOutput.ReadLine()) is not null)
            {
                var f = line.Split(',');
                if (f.Length < 2 || !double.TryParse(f[0], NumberStyles.Float, inv, out double pts)
                    || !int.TryParse(f[1], out int ch) || ch <= 0) continue;
                if (double.IsNaN(first)) first = pts;
                double ms = (pts - first) * 1000.0;
                lastMs = ms;
                if (runs.Count == 0) runs.Add(new AudioRun { StartMs = 0, Channels = ch });
                else if (runs[^1].Channels != ch) { runs[^1].EndMs = ms; runs.Add(new AudioRun { StartMs = ms, Channels = ch }); }
            }
            p.WaitForExit();
            if (runs.Count > 0) runs[^1].EndMs = double.MaxValue;
            // 短い区間を潰す（直前があれば直前へ、無ければ次へ吸収）。
            for (int i = runs.Count - 1; i >= 0; i--)
            {
                double len = (runs[i].EndMs == double.MaxValue ? lastMs : runs[i].EndMs) - runs[i].StartMs;
                if (len >= MinRunMs || runs.Count == 1) continue;
                if (i > 0) { runs[i - 1].EndMs = runs[i].EndMs; runs.RemoveAt(i); }
                else { runs[i + 1].StartMs = runs[i].StartMs; runs.RemoveAt(i); }
            }
            if (runs.Count > 0) { runs[0].StartMs = 0; runs[^1].EndMs = double.MaxValue; }
        }
        catch { runs.Clear(); }
        return runs;
    }

    private static string NormAudioCodec(string c) => c switch
    {
        "aac" => "aac", "ac3" or "eac3" => "ac3", "opus" => "opus",
        "mp2" or "mp1" or "mp3" => "mp2", "dts" => "dts", _ => c,
    };

    // ffmpeg 引数（ファイル入力。映像1＋音声N を map、映像はケイデンス保持で符号化、音声は選択方式で）。
    // ssSec>0 で先頭から入力シーク、durSec>0 で出力尺を制限（構成変化でのセグメント分割に使う）。
    // 符号化の対象。映像と音声は**別々の ffmpeg で**符号化する（BdNoneConverter と同じ理由。
    // 1 回にまとめると、映像符号化が遅く素材が長いときに音声が 1 本だけ黙って途中で止まることがある。
    // 実測は BD_003 で確認したが、原因は ffmpeg 内部なので TS でも同じ条件が揃えば起こり得る）。
    private enum EncodePart { VideoOnly, AudioOnly }

    private static string BuildFfmpegArgs(DvdH264Converter.Options opt, string ts, TsService svc, string outMkv,
        bool encodeInterlaced, bool tff, bool h265, double ssSec, double durSec, bool fieldSeparate = false,
        bool pipeInput = true, EncodePart part = EncodePart.VideoOnly)
    {
        bool wantVideo = part == EncodePart.VideoOnly;
        bool wantAudio = part == EncodePart.AudioOnly;
        var inv = CultureInfo.InvariantCulture;
        string aspect = svc.Aspect == "4:3" ? "4:3" : "16:9";
        var a = new StringBuilder();
        a.Append("-y -hide_banner -loglevel error -analyzeduration 100M -probesize 100M ");
        if (ssSec > 0) a.Append($"-ss {ssSec.ToString(inv)} ");
        // 入力：音声再エンコード時は標準入力（pipe:0）へ流し、DVDと同じ「供給バイト数」で進捗算出（RunEncode が給餌・計数）。
        // 音声コピー時は AAC の extradata（サンプルレート）取得にシーク可能なファイル入力が要るため -i ファイルにする。
        a.Append(pipeInput ? "-i pipe:0 " : $"-i \"{ts}\" ");
        if (durSec > 0) a.Append($"-t {durSec.ToString(inv)} ");
        bool deint = !string.Equals(opt.Deinterlace, "none", StringComparison.OrdinalIgnoreCase);
        if (!wantVideo) a.Append("-vn -sn ");
        else if (fieldSeparate && !deint)
        {
            // fields dual：T/B を**2映像トラック**へ分離する。track0=top（偶数ライン）／track1=bottom（奇数ライン）。
            // どちらも 29.97・半高の progressive。時間順（どちらが先か＝tff）は再生側で PTS に付ける（ここでは分離のみ）。
            // パリティ＝トラック番号で確定＝デコーダ並び替え/PTS推測に依存せず反転しない。
            a.Append($"-filter_complex \"[0:{svc.VideoIndex}]field=type=top[vt];[0:{svc.VideoIndex}]field=type=bottom[vb]\" ");
            a.Append("-map \"[vt]\" -map \"[vb]\" ");
        }
        else
            a.Append($"-map 0:{svc.VideoIndex} ");
        // 音声は「ラン（出力トラック）」ごとに map する。同じ入力ストリームを複数回 map してよい。
        if (wantAudio)
            foreach (var au in svc.Audios) foreach (var _ in au.Runs) a.Append($"-map 0:{au.Index} ");
        if (wantVideo)
        {
            a.Append("-fps_mode passthrough ");
            if (deint)
                a.Append($"-vf {opt.Deinterlace.ToLowerInvariant()}=mode=send_field ");
        }

        int keyint = opt.Keyint > 0 ? opt.Keyint : 300;
        bool closed = string.Equals(opt.GopMode, "closed", StringComparison.OrdinalIgnoreCase);
        if (!wantVideo) { /* 音声パスでは映像の符号化指定は付けない */ }
        else if (KurokoVideoCodecs.IsAv1(opt.VideoCodec))
            KurokoVideoCodecs.AppendAv1Args(a, KurokoVideoCodecs.SvtPreset(opt.Preset), opt.Crf, aspect, keyint, allIntra: false);
        else if (h265)
        {
            a.Append($"-c:v libx265 -preset {opt.Preset} -crf {opt.Crf} -pix_fmt yuv420p -aspect {aspect} ");
            var hp = new List<string> { $"keyint={keyint}", "scenecut=0", "min-keyint=1",
                closed ? "open-gop=0" : "open-gop=1", "repeat-headers=1", "log-level=error" };
            a.Append($"-x265-params {string.Join(":", hp)} ");
        }
        else
        {
            string ilme = encodeInterlaced ? "-flags +ilme+ildct " : "";
            a.Append($"-c:v libx264 -preset {opt.Preset} -crf {opt.Crf} -pix_fmt yuv420p -aspect {aspect} {ilme}");
            var xp = new List<string> { $"keyint={keyint}", "scenecut=0", "min-keyint=1", closed ? "open-gop=0" : "open-gop=1", "repeat-headers=1" };
            if (encodeInterlaced) xp.Add(tff ? "tff=1" : "bff=1");
            a.Append($"-x264-params {string.Join(":", xp)} ");
        }

        bool copy = IsCopy(opt);
        if (wantAudio)
        foreach (var au in svc.Audios)
        {
            foreach (var run in au.Runs)
            {
                int i = run.OutIndex;
                if (copy) a.Append($"-c:a:{i} copy ");
                else
                {
                    // 再符号化は Opus のみ。出力チャンネル数は**明示**する（付けないと ffmpeg が復号器の
                    // 初期レイアウトで決めるため、途中から 5.1 になる素材が 2ch のまま出てしまう）。
                    // ビットレートは 1ch あたりの指定 × 出力ch。
                    a.Append($"-c:a:{i} libopus -b:a:{i} {opt.AudioBitrateK * run.Channels}k -ac:a:{i} {run.Channels} ");
                    // 分割しているときは、そのトラックが担当する区間だけを残す（時刻は保つ＝atrim は PTS を
                    // 付け替えない。av.mkv 上でも本来の位置から始まる）。
                    if (au.Runs.Count > 1)
                        a.Append(run.EndMs == double.MaxValue
                            ? $"-filter:a:{i} atrim=start={(run.StartMs / 1000.0).ToString("F3", inv)} "
                            : $"-filter:a:{i} atrim=start={(run.StartMs / 1000.0).ToString("F3", inv)}:end={(run.EndMs / 1000.0).ToString("F3", inv)} ");
                }
                if (!string.IsNullOrEmpty(au.Lang)) a.Append($"-metadata:s:a:{i} language={au.Lang} ");
            }
        }
        a.Append($"-f matroska \"{outMkv}\"");
        // 0 起点をそろえる（BaseAlignOutput の注記）。映像の実行には音声を、音声の実行には映像を読ませて捨てる。
        var others = wantVideo ? svc.Audios.Select(x => x.Index).Distinct().ToList()
                               : (svc.VideoIndex >= 0 ? new List<int> { svc.VideoIndex } : new List<int>());
        a.Append(BaseAlignOutput(others, durSec > 0 ? $"-t {durSec.ToString(inv)} " : ""));
        return a.ToString();
    }

    // 映像だけ・音声だけの各 ffmpeg の**時刻の 0 起点をそろえる**ための 2 つ目の出力（読ませて捨てる）。
    // ffmpeg は時刻を 0 へ振り直すとき、入力全体ではなく**その実行で使うストリームの先頭**を 0 にする
    //（使わないストリームは読み捨て扱いになり、開始時刻の計算から外れる。-copyts -start_at_zero でも同じ）。
    // 映像だけの実行は映像の頭、音声だけの実行は音声の頭が 0 になり、音声が映像より先に始まる録画では
    // その差だけ音声が遅れた krk になっていた（ある放送の TS で 326ms。b117fb5 の別パス化から。
    // 0.75.0 の --sync の修正で直したのは別の 567ms ぶん）。そこで相手側のストリームも map し、
    // **複製して捨てる**（-c copy -f null。復号しないので軽い）。こうすると両方の実行で使うストリームが
    // 同じになり、0 起点がそろう（AudioSyncArgs／BD の AudioShiftMs の前提＝「全ストリームの最小が 0」が成り立つ）。
    // limit は本出力と同じ長さ指定（付けないと、捨てる側の出力のために入力の最後まで読み続ける）。
    internal static string BaseAlignOutput(IEnumerable<int> streamIndices, string limit)
    {
        var sb = new StringBuilder();
        foreach (int i in streamIndices) if (i >= 0) sb.Append($" -map 0:{i}");
        if (sb.Length == 0) return "";
        return $"{sb} -c copy {limit}-f null -";
    }

    // ── ffprobe：サービス（プログラム）とストリームの列挙 ─────────────────────────
    private static List<TsService> ProbeServices(string ffprobe, string ts, StringBuilder log)
    {
        var result = new List<TsService>();
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                "-v error -show_programs -show_entries " +
                "program=program_num:program_tags=service_name:" +
                "stream=index,id,codec_type,codec_name,width,height,field_order,channels,r_frame_rate,display_aspect_ratio:" +
                "stream_tags=language -of json \"" + ts + "\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string json = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd(); p.WaitForExit();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("programs", out var programs)) return result;

            var seenVideo = new HashSet<int>();
            foreach (var prog in programs.EnumerateArray())
            {
                var svc = new TsService();
                svc.ProgramNum = prog.TryGetProperty("program_num", out var pn) && pn.TryGetInt32(out var pnv) ? pnv : 0;
                if (prog.TryGetProperty("tags", out var ptags) && ptags.TryGetProperty("service_name", out var sn))
                    svc.Name = sn.GetString();
                if (!prog.TryGetProperty("streams", out var streams)) continue;
                foreach (var s in streams.EnumerateArray())
                {
                    string type = s.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() ?? "" : "";
                    int idx = s.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var ixv) ? ixv : -1;
                    if (type == "video")
                    {
                        int w = s.TryGetProperty("width", out var wv) && wv.TryGetInt32(out var wi) ? wi : 0;
                        int h = s.TryGetProperty("height", out var hv) && hv.TryGetInt32(out var hi) ? hi : 0;
                        if (svc.VideoIndex < 0 && w > 0 && h > 0)
                        {
                            svc.VideoIndex = idx; svc.Width = w; svc.Height = h;
                            svc.VideoCodec = s.TryGetProperty("codec_name", out var vc) ? vc.GetString() ?? "" : "";
                            svc.FieldOrder = s.TryGetProperty("field_order", out var fo) ? fo.GetString() ?? "" : "";
                            if (s.TryGetProperty("r_frame_rate", out var rf) && rf.GetString() is string rfr && rfr.Contains('/')) svc.Fps = rfr;
                            if (s.TryGetProperty("display_aspect_ratio", out var da) && da.GetString() is string dar && dar.Contains(':')) svc.Aspect = dar;
                        }
                    }
                    else if (type == "audio")
                    {
                        if (idx < 0) continue;
                        int ch = s.TryGetProperty("channels", out var cv) && cv.TryGetInt32(out var ci) ? ci : 0;
                        // ARIB のデュアルモノ等は channels=0 と報告される（ffprobe が channel_configuration=0 を数えられない）。
                        // 実体（パケット）があれば 2ch として採用し、宣言のみで実体の無い空スロットだけ除外する。
                        if (ch <= 0)
                        {
                            if (!AudioHasData(ffprobe, ts, idx)) continue;
                            ch = 2;
                        }
                        var au = new TsAudio { Index = idx, Channels = ch,
                            Codec = s.TryGetProperty("codec_name", out var ac) ? ac.GetString() ?? "" : "" };
                        // PID（ffprobe の id は "0x110" 等）。TsProgramAnalyzer の PID と対応づける。
                        if (s.TryGetProperty("id", out var idv) && idv.GetString() is string idstr)
                            au.Pid = idstr.StartsWith("0x") && int.TryParse(idstr.AsSpan(2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pv) ? pv
                                   : int.TryParse(idstr, out var pv2) ? pv2 : -1;
                        if (s.TryGetProperty("tags", out var stags) && stags.TryGetProperty("language", out var lg)) au.Lang = lg.GetString();
                        svc.Audios.Add(au);
                    }
                    else if (type == "subtitle" && s.TryGetProperty("codec_name", out var scn) && scn.GetString() == "arib_caption")
                    {
                        int spid = -1;
                        if (s.TryGetProperty("id", out var sidv) && sidv.GetString() is string sid)
                            spid = sid.StartsWith("0x") && int.TryParse(sid.AsSpan(2), System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sp1) ? sp1
                                 : int.TryParse(sid, out var sp2) ? sp2 : -1;
                        string? slang = s.TryGetProperty("tags", out var sst) && sst.TryGetProperty("language", out var sl) ? sl.GetString() : null;
                        if (idx >= 0 && spid >= 0) svc.Captions.Add((idx, spid, slang));
                    }
                }
                if (svc.VideoIndex < 0) continue;
                if (!seenVideo.Add(svc.VideoIndex)) continue;
                result.Add(svc);
            }
        }
        catch (Exception ex) { log.AppendLine("service 解析失敗: " + ex.Message); }
        return result;
    }

    // ── ffprobe：指定映像ストリームのフレーム別 (フィールド数=2+repeat_pict, progressive) を表示順で ──
    private static (List<byte> Fields, List<bool> Progressive) ProbeVideoFields(
        string ffprobe, string ts, int videoIndex, double ssSec, double durSec, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<(long pts, byte fields, bool prog)>();
        string ri = durSec > 0 ? $"-read_intervals {ssSec.ToString(inv)}%+{durSec.ToString(inv)} "
                  : ssSec > 0 ? $"-read_intervals {ssSec.ToString(inv)}% " : "";
        var psi = new ProcessStartInfo(ffprobe,
            $"-v error {ri}-select_streams {videoIndex} -show_entries frame=best_effort_timestamp,repeat_pict,interlaced_frame -of csv=p=0 \"{ts}\"")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
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
            rows.Add((pts == long.MinValue ? seq : pts, (byte)fields, inter == 0));
            seq++;
        }
        p.StandardError.ReadToEnd(); p.WaitForExit();
        var order = Enumerable.Range(0, rows.Count).ToList();
        order.Sort((x, y) => rows[x].pts != rows[y].pts ? rows[x].pts.CompareTo(rows[y].pts) : x.CompareTo(y));
        var fl = new List<byte>(rows.Count); var prl = new List<bool>(rows.Count);
        foreach (var i in order) { fl.Add(rows[i].fields); prl.Add(rows[i].prog); }
        return (fl, prl);
    }

    // ── 音声スパン組立 ─────────────────────────────
    // seg.audio ＝「時刻スパン×トラック集合」。境界＝event（present 変化＝番組切替）。各スパンの tracks は、その時刻で
    // 実データを持つ音声トラックの集合（本数・デュアルモノ・名称が区間ごとに変わるのを表現）。tl 無しは単一スパン。
    private static List<KurokoNoneAudioSpan> BuildAudioSpans(
        DvdH264Converter.Options opt, TsService svc, TsProgramTimeline? tl, double totalMs,
        int videoTracks = 1, double audioShiftMs = 0)
    {
        var bounds = new SortedSet<double> { 0, totalMs };
        if (tl is not null) foreach (var e in tl.Events) if (e.StartMs > 1 && e.StartMs < totalMs) bounds.Add(e.StartMs);
        // チャンネル数の変化点もスパン境界にする（区間ごとに別トラックを指すため）。
        foreach (var a in svc.Audios)
            foreach (var r in a.Runs)
            {
                double b = r.StartMs - audioShiftMs;
                if (b > 1 && b < totalMs) bounds.Add(b);
            }
        var bl = bounds.ToList();
        var spans = new List<KurokoNoneAudioSpan>();
        for (int i = 0; i + 1 < bl.Count; i++)
        {
            double s = bl[i], e2 = bl[i + 1], mid = (s + e2) / 2;
            var span = new KurokoNoneAudioSpan { StartMs = s, EndMs = e2 };
            for (int t = 0; t < svc.Audios.Count; t++)
            {
                var a = svc.Audios[t];
                int pid = a.Pid;
                // このトラックが mid で実データを持つか（データ範囲が mid をカバー）。tl 無し/範囲不明なら常に存在。
                if (tl is not null && pid >= 0 && tl.AudioDataRange.TryGetValue(pid, out var dr)
                    && !(mid >= dr.First - 1 && mid <= dr.Last + 1)) continue;
                bool dual = tl is not null && pid >= 0 && tl.BitstreamDualMono.TryGetValue(pid, out var rr)
                            && rr.Any(r => mid >= r.StartMs && mid < r.EndMs);
                // この時刻を担当するラン（＝出力トラック）。分割していなければ常に唯一のラン。
                var run = a.Runs.FirstOrDefault(r => mid >= r.StartMs - audioShiftMs - 1 && mid < r.EndMs - audioShiftMs + 1)
                          ?? a.Runs.LastOrDefault();
                if (run is null) continue;
                var comp = tl?.AudioAt(pid, mid);
                span.Tracks.Add(new KurokoNoneAudioTrack
                {
                    Track = videoTracks + run.OutIndex, Source = t, // dual は映像2トラックの後（track2+）
                    Codec = AudioOutCodec(opt, a), Channels = run.Channels,
                    DualMono = dual,
                    Name = string.IsNullOrWhiteSpace(comp?.Name) ? null : comp!.Name,
                    Language = KurokoLanguage.Normalize(comp?.Language ?? a.Lang),
                });
            }
            // 構成が同じ隣接スパンは統合。
            if (spans.Count > 0 && SameConfig(spans[^1], span)) spans[^1].EndMs = span.EndMs;
            else spans.Add(span);
        }
        return spans;
    }

    private static bool SameConfig(KurokoNoneAudioSpan a, KurokoNoneAudioSpan b)
    {
        if (a.Tracks.Count != b.Tracks.Count) return false;
        for (int i = 0; i < a.Tracks.Count; i++)
        {
            var x = a.Tracks[i]; var y = b.Tracks[i];
            if (x.Track != y.Track || x.Source != y.Source || x.DualMono != y.DualMono
                || !string.Equals(x.Name, y.Name, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // progressive 以外（＝本当のインターレース）の連続フレームを ms 区間へ（再生側 bob 判定用）。
    private static List<KurokoRange>? BuildInterlacedRanges(List<double> tsMs, List<bool> prog, double fieldDurMs)
    {
        var ranges = new List<KurokoRange>();
        int n = Math.Min(tsMs.Count, prog.Count);
        int i = 0;
        while (i < n)
        {
            if (prog[i]) { i++; continue; }
            int j = i; while (j < n && !prog[j]) j++;
            double start = tsMs[i] - 0.5;
            double end = (j < tsMs.Count ? tsMs[j] : tsMs[n - 1] + fieldDurMs * 2) + 0.5;
            ranges.Add(new KurokoRange { StartMs = start, EndMs = end });
            i = j;
        }
        return ranges.Count > 0 ? ranges : null;
    }

    // ── 小物 ─────────────────────────────────────────────
    private static (double Num, double Den) ParseFps(string s)
    {
        var p = s.Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(p[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d > 0) return (n, d);
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) && f > 0) return (f, 1);
        return (30000, 1001);
    }

    private static double FrameMs(string fps) { var (n, d) = ParseFps(fps); return n > 0 ? 1000.0 * d / n : 1001.0 / 30000.0 * 1000.0; }

    private static (int W, int H) DisplaySize(int pw, int ph, string aspect)
    {
        var a = aspect.Split(':');
        if (a.Length == 2 && int.TryParse(a[0], out var aw) && int.TryParse(a[1], out var ah) && ah > 0 && ph > 0)
            return ((int)Math.Round(ph * (double)aw / ah), ph);
        return (pw, ph);
    }

    // 指定ストリームが先頭 30 秒に実パケットを持つか（channels=0 のデュアルモノ等が空スロットでないことの確認）。
    private static bool AudioHasData(string ffprobe, string ts, int idx)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -read_intervals %+30 -select_streams {idx} -count_packets -show_entries stream=nb_read_packets -of csv=p=0 \"{ts}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd(); p.WaitForExit();
            // nb_read_packets は複数行で出ることがある。いずれかの行が正の整数なら実体あり。
            foreach (var line in outp.Split('\n'))
                if (int.TryParse(line.Trim(), out var n) && n > 0) return true;
            return false;
        }
        catch { return false; }
    }

    // 音声ファイル（別パスで符号化した audio.mka）を、映像を 0 へ振り直したぶんだけ前へずらす mkvmerge の指定。
    // 映像だけ・音声だけの各 ffmpeg は、どちらも**入力 TS の先頭（全ストリームの最小 start_time＝ふつうは音声の頭）**を
    // 0 として時刻を付けるので、映像の頭は vStartMs（例 567ms）、音声の頭は 0 になる。映像は --timestamps で 0 基準に
    // 振り直すので、音声も同じだけ前へずらして元の関係を保つ。
    // **--sync は直後の入力ファイルのトラック番号に掛かる**ので、audio.mka の直前に、そのファイルの中の番号（0 から）で
    // 付けること。別パス化（b117fb5）の際に映像ファイルの前へ「トラック 2・3」として付けたままになっていて空振りし、
    // 音声が vStartMs ぶん遅れた krk ができていた（ある放送の TS で 567ms。音声波形と輝度の相互相関で実測）。
    private static string AudioSyncArgs(int audioTracks, double vStartMs)
    {
        if (vStartMs <= 1) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < audioTracks; i++) sb.Append($" --sync {i}:{-(int)Math.Round(vStartMs)}");
        return sb.ToString();
    }

    // 最初に復号できる映像の絵の元の PTS（ms）。krk の映像の 0 秒目に当たる。取れなければ NaN。
    private static double ProbeFirstVideoFramePtsMs(string ffprobe, string path, int videoIndex)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -select_streams {videoIndex} -show_entries frame=best_effort_timestamp_time -read_intervals %+5 -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            foreach (var line in outp.Split('\n'))
                if (double.TryParse(line.Trim().TrimEnd(','), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v * 1000.0;
            return double.NaN;
        }
        catch { return double.NaN; }
    }

    // ストリームの start_time（ms）。失敗/不明は 0。
    private static double ProbeStartTimeMs(string ffprobe, string path, string stream)
    {
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                $"-v error -select_streams {stream} -show_entries stream=start_time -of csv=p=0 \"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            foreach (var line in outp.Split('\n'))
                if (double.TryParse(line.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) return v * 1000.0;
            return 0;
        }
        catch { return 0; }
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

    private static bool RunFfmpegNoInput(string exe, string args, CancellationToken ct, out string stderr)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
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

    private static bool FileOk(string p) => File.Exists(p) && new FileInfo(p).Length > 0;
    private static string Tail(string s, int n = 800) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(s.Length - n));
}
