using System.Diagnostics;
using System.IO;
using System.Text;

namespace Kuroko.Converter;

// 変換の共通部品：変換オプション・進捗・外部の ffmpeg を標準入力つきで走らせる処理など。
// DVD・BD・TS から krk への変換（DvdH264FullConverter・BdNoneConverter・TsNoneConverter）が使う。
internal static class DvdH264Converter
{
    // 変換オプション。
    //  Deinterlace: "none"=インターレースのまま(再生側でボブ・デインターレース) / "bwdif" / "yadif"=変換時にプログレッシブ化。
    //  Keyint: IDR/キーフレーム間隔(フレーム)。既定60。0=libx264 既定(約250=長めGOP・高画質/小サイズ)。
    //  GopMode: "hybrid"=オープンGOP＋セル先頭のみIDR(既定。圧縮率はほぼオープンで、チャプター/セル頭は堅牢) /
    //    "closed"=全GOP IDR(最堅牢・やや大) / "open"=純オープンGOP(やや高圧縮、セル先頭も非IDR)。
    //  Preset/Crf: x264 のプリセットと CRF。
    //  AudioMode: "copy"=音声無劣化コピー / "recompress"=AC-3 2ch へ再圧縮
    //    （AC-3多chは2chダウンミックス、DTS/LPCMはAC-3 2ch へ変換。出力ビットレートは AudioBitrateK）。
    //  AudioBitrateK: 再圧縮時の AC-3/Opus 2ch 出力ビットレート(kbps)。AC-3 は有効値へ最近接丸め。既定128。
    //  AudioCodec（Kuroko のみ）: "opus"=全音声を Opus 2ch へ再符号化（既定。同梱ffmpegのネイティブOpusで再生）/
    //    "ac3"=従来どおり AC-3（AudioMode に従いコピー/再圧縮）。
    internal sealed record Options(
        int Crf = KurokoVideoCodecs.DefaultAv1Crf,
        int Keyint = 60,
        string GopMode = "hybrid",
        string Deinterlace = "none",
        string Preset = "medium",
        double MaxSecondsPerCell = 0,
        string FfmpegPath = "ffmpeg",
        string FfprobePath = "ffprobe",
        // Kuroko(.krk) のみ使用：av.mkv の映像PTSを RffFieldTime(テレシネ加味の実時間)へ焼き直す remux に使う
        // mkvmerge(MKVToolNix)。既定は同梱想定パス。
        string MkvmergePath = "mkvmerge",                 // 既定は PATH。画面・CLI は ToolPaths の既定を渡す
        string AudioMode = "copy",
        // 音声ビットレート＝**1チャンネルあたり**の kbps。実際の指定値は「これ×出力チャンネル数」になる
        //（Opus 2ch なら ×2、5.1ch 保持なら ×6）。AC-3 は常に 2ch なので ×2 を AC-3 の有効値へ丸める。
        int AudioBitrateK = 32,
        string AudioCodec = "opus",
        // 再符号化時の出力チャンネル数。"stereo"(既定)=3ch以上は 2ch へダウンミックス／"source"=元のまま。
        // コピー時は元のまま（この設定は無視）。
        string AudioChannels = "stereo",
        // MenuMode: "keep"=メニューは元のMPEG-2のまま（既定） / "recompress"=モーションメニューの映像をH.264・
        //   音声をAC-3 2chへ再圧縮（静止セルはverbatim）。映像/音声の設定（Crf/Preset/AudioBitrateK等）を共用。
        string MenuMode = "keep",
        // VideoCodec（Kuroko のみ）: "av1"(既定)=SVT-AV1 / "h265"=libx265 / "h264"=libx264。タイトル/メニューの両方に適用。
        //   H.265 にはフレーム/フィールド適応のインターレース符号化ツールが無いため、実インターレース
        //   （Deinterlace=none）は weave フレームを**プログレッシブとして**符号化する（フィールド情報は
        //   無損失に保持され、再生側が従来どおり InterlacedRanges/Tff で bob 解除する）。
        //   拡張DVD(.iso) 形式出力は常に H.264（本設定は無視）。
        string VideoCodec = "av1",
        // InterlaceMode（Kuroko のみ）: インターレース素材の格納方式。動作は常に「インターレース維持」。
        //   "fields"(既定)=T/B を各々「半高さ・倍フレームレート」の progressive フレームとして符号化
        //     （separatefields）。動きのある実インターレースで櫛が消え圧縮が効く。再生側は各半高フレームを
        //     1フィールドとして全高へ展開＋bob。セグメントが純インターレース（progressive/RFF 無し）のときだけ適用し、
        //     film/混在セグメントは自動的に "weave" へフォールバックする。
        //   "weave"=T/B を1枚(フル高)へ織り込んで符号化（従来）。再生側で1行おきに分離して bob。
        //   拡張DVD(.iso) 形式出力・メニューは常に weave（本設定は無視）。
        string InterlaceMode = "fields",
        // FieldsMinInterlaceFrames（Kuroko・InterlaceMode=fields のみ）: プログレッシブ（film/weave）に隣接し、
        //   連続フレーム数がこの値**未満**の実インターレース小区間は、fields(dual) にせず weave 扱いにする。
        //   ソフトテレシネの combed（1フレームで点在）や、フィルム中の短いディゾルブ/フェード遷移（~0.5秒）を
        //   毎回パーツ分割せず統合するための閾値。既定30（≒1秒。29.97fps）。0=降格しない（全ての実インターレースを
        //   fields 化。※テレシネ櫛が毎フレーム分割され過分割になり得るので非推奨）。単位＝フレーム。
        int FieldsMinInterlaceFrames = 30);

    // Matroska の TimestampScale。**既定は 1,000,000ns＝1ms** で、フレーム時刻がミリ秒へ丸められる。
    // 23.976fps の 41.7083ms は 41/42ms に化け、24p を 60Hz へ出すときの 2:3 判定が丸め誤差で反転する
    // （実測：カデンス乱れが約4倍）。1,000ns＝1μs にすると丸めが 1000 分の 1 になり、容量増は約 0.3%。
    // 90kHz ちょうど（11111.11…ns）は整数にできないので、それより細かい 1μs を採る。
    internal const string MkvTimestampScaleArg = "--timestamp-scale 1000";

    // 進捗通知（GUI 用）。Fraction=0..1、Status=表示文。
    internal readonly record struct Progress(double Fraction, string Status);

    // 入力ストリームを ffmpeg の標準入力へ流しながらエンコードする。ffmpeg が -t で早期終了して
    // stdin を閉じたら、書き込み例外を握りつぶして読み出しを止める（途中打ち切り＝正常）。
    // onProgress: このセルで送出した累積バイト数を ~1MB ごとに通知。ct: 中止時は ffmpeg を kill。
    internal static int RunEncode(string exe, string args, Stream input, out string stderr,
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
                int n;
                long fed = 0, lastReport = 0;
                var stdin = p.StandardInput.BaseStream;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ct.IsCancellationRequested) break;
                    stdin.Write(buf, 0, n);
                    fed += n;
                    if (fed - lastReport >= (1 << 20)) { lastReport = fed; onProgress?.Invoke(fed); }
                }
                stdin.Flush();
                onProgress?.Invoke(fed);
            }
            catch { /* ffmpeg が stdin を閉じた（-t 早期終了等）。打ち切りは正常。 */ }
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

    // ソース映像のフィールド順(TFF/BFF)を ffprobe の top_field_first（MPEG-2 が符号化した実フィールド順フラグ）で
    // 判定する。元タイトルのPSを stdin に流し、インターレースフレームの top_field_first を多数決（1=TFF,0=BFF）。
    // ※ ffmpeg は DVD-Video の ISO（UDF/ISO9660）を直接開けないため、ISOパスを渡してはいけない。必ず DvdCore 由来のPS。
    // ※ idet（動き推定＋要約行）はビルド差・パイプ早期終了で要約が出ず検出ゼロ→既定TFFに化けることがあるため使わない。
    //    top_field_first はソースの宣言フラグそのもので確実。検出ゼロ（不明）は true(TFF) を既定。
    internal static bool DetectTopFieldFirstFromStream(string ffprobe, System.IO.Stream ps,
        System.Threading.CancellationToken ct, out int tffN, out int bffN)
    {
        tffN = 0; bffN = 0;
        try
        {
            var psi = new ProcessStartInfo(ffprobe,
                "-v error -select_streams v:0 -show_entries frame=interlaced_frame,top_field_first -of default=noprint_wrappers=1 -i pipe:0")
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
                    var buf = new byte[1 << 16]; int n; var stdin = p.StandardInput.BaseStream;
                    while ((n = ps.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ct.IsCancellationRequested) break;
                        stdin.Write(buf, 0, n);
                    }
                    stdin.Flush();
                }
                catch { /* ffprobe が先に閉じると書き込みが切れる＝正常 */ }
                finally { try { p.StandardInput.Close(); } catch { } }
            });
            // key=value 行が（フレームごとに）interlaced_frame → top_field_first の順で出る。インターレース時のみ計上。
            bool curInterlaced = false;
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("interlaced_frame=", StringComparison.Ordinal))
                    curInterlaced = line.EndsWith("=1", StringComparison.Ordinal);
                else if (line.StartsWith("top_field_first=", StringComparison.Ordinal))
                {
                    if (!curInterlaced) continue;
                    if (line.EndsWith("=1", StringComparison.Ordinal)) tffN++; else bffN++;
                    if (tffN + bffN >= 400) break; // 十分なサンプルで打ち切り
                }
            }
            try { p.Kill(true); } catch { }
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            try { feeder.Wait(2000); } catch { }
            return tffN >= bffN; // 同数/不明は TFF を既定
        }
        catch { return true; }
    }

}
