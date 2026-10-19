using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kuroko.Media.Dvd;
using Kuroko.Media.Krk;

namespace Kuroko.Converter;

// Kuroko Converter の CLI（変換と、調べるための診断の命令）。画面（Kuroko.Converter）は引数があればここへ渡す。
// 結果は引数で指定したファイル（レポート等）へ書く。画面を持たないので、エラーは標準エラーへも出す。
internal static class ConverterCli
{
    // SVT-AV1 の情報ログ（起動のたびに十数行）を止める。ffmpeg の子プロセスへ引き継がれる（エラーは出る）。
    public static void PrepareEnvironment() => Environment.SetEnvironmentVariable("SVT_LOG", "1");

    // 処理したら終了コードを返す（0＝成功・1＝不明な命令・2＝エラー）。
    public static int Run(string[] args)
    {
        PrepareEnvironment();
        // 端末へ出す文字は UTF-8（Windows の既定のコードページのままだと、日本語のメッセージが化ける）。
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        try
        {
            switch (args[0])
            {
                // Kuroko(.krk) 出力：タイトルを av.mkv＋title.json 化し .krk へ（M1a：タイトルのみ）。
                // --dvd-convert-kuroko <in.iso> <out.krk> <report.txt> [key=value ...]
                // 進捗は <report.txt>.progress.txt へ「fraction TAB status」で逐次追記（GUI と同じ IProgress の検証用）。
                case "--dvd-convert-kuroko" when args.Length >= 4:
                    File.WriteAllText(args[3], DvdH264FullConverter.ConvertToKuroko(args[1], args[2], ParseOptions(args, 4),
                        new FileTraceProgress(args[3] + ".progress.txt")));
                    return 0;

                // 放送/配信 TS → コントローラ none の Kuroko(.krk)（サービス=タイトル。映像＋音声。字幕は対象外）。
                // --ts-convert-none <in.ts> <out.krk> <report.txt> [key=value ...]
                // 進捗は <report.txt>.progress.txt へ「fraction TAB status」で逐次追記（DVD版と同じ IProgress の検証用）。
                case "--ts-convert-none" when args.Length >= 4:
                    File.WriteAllText(args[3], TsNoneConverter.ConvertTsToKuroko(args[1], args[2], ParseOptions(args, 4),
                        new FileTraceProgress(args[3] + ".progress.txt")));
                    return 0;

                // 診断：ARIB 字幕の往復テスト（読み解き→krk のテキスト型→組み立て直し→読み解きで元に戻るか）。
                // --arib-caption-selftest <in.ts> <字幕の PID（0x130 等）> <out.txt>
                case "--arib-caption-dump" when args.Length >= 5:
                    File.WriteAllText(args[3], AribCaptionSelfTest.Dump(args[1], Convert.ToInt32(args[2][2..], 16), int.Parse(args[4])));
                    return 0;
                case "--arib-caption-selftest" when args.Length >= 4:
                    File.WriteAllText(args[3], AribCaptionSelfTest.Run(args[1],
                        args[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(args[2][2..], 16) : int.Parse(args[2])));
                    return 0;

                // 診断：EIT/PMT 番組タイムライン（event_id・番組名・開始時刻・音声コンポーネントの名称/言語/デュアルモノ）を
                // ダンプ。--ts-program-analyze <in.ts> <out.txt> [maxsec]
                case "--ts-program-analyze" when args.Length >= 3:
                {
                    double msec = args.Length >= 4 && double.TryParse(args[3], out var mm) ? mm : 0;
                    var tls = TsProgramAnalyzer.Analyze(args[1], msec);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("DEBUG: " + TsProgramAnalyzer.LastDebug);
                    foreach (var tl in tls)
                    {
                        sb.AppendLine($"program {tl.ProgramNumber}: audioPids=[{string.Join(",", tl.AudioPids)}] events={tl.Events.Count}");
                        foreach (var pid in tl.AudioPids)
                            sb.AppendLine($"  PID {pid}: tag={(tl.ComponentTagByPid.TryGetValue(pid, out var tg) ? tg.ToString() : "-")} lang={(tl.LanguageByPid.TryGetValue(pid, out var lg) ? lg : "-")} dualMonoRanges={string.Join(",", tl.DualMonoRangesFor(pid).Select(r => $"[{r.StartMs:F0}-{r.EndMs:F0}]"))}");
                        foreach (var e in tl.Events)
                        {
                            sb.AppendLine($"  event {e.EventId} [{e.StartMs:F0}-{e.EndMs:F0}]ms start={e.StartTime:yyyy-MM-dd HH:mm:ss} name=\"{e.Name}\"");
                            foreach (var a in e.Audio)
                                sb.AppendLine($"      audio pid={a.Pid} tag={a.ComponentTag} type=0x{(a.ComponentType ?? 0):X2} dualMono={a.IsDualMono} lang={a.Language} name=\"{a.Name}\"");
                        }
                    }
                    File.WriteAllText(args[2], sb.ToString());
                    return 0;
                }

                // 診断(F)：各メニューPGCの映像アスペクトと SPST_CTL（表示モード別物理サブID）をダンプ。--menu-subctl <in.iso> <out.txt>
                case "--menu-subctl" when args.Length >= 3:
                    File.WriteAllText(args[2], DvdH264FullConverter.DumpMenuSubctl(args[1]));
                    return 0;

                // 診断：各タイトルのセル StillTime/CommandNr を列挙（静止セル・セル命令の有無確認）。--title-cells <in.iso> <out.txt>
                case "--title-cells" when args.Length >= 3:
                    File.WriteAllText(args[2], DvdH264FullConverter.DumpTitleCells(args[1]));
                    return 0;

                // 診断：OpenTitle が返す再生対象（尺・章・セル・PRE）を吐く。章メニューからの JumpVTS_PTT で
                // 総尺が本編でなく部分尺になる不具合の切り分け用。--title-info <in.iso> <title> <out.txt>
                case "--title-info" when args.Length >= 4:
                {
                    int titleNo = int.Parse(args[2]);
                    using var disc = DvdDisc.Open(args[1]);
                    var tp = disc.OpenTitle(titleNo);
                    var sb = new System.Text.StringBuilder();
                    var te = disc.Titles.FirstOrDefault(t => t.TitleNumber == titleNo);
                    sb.AppendLine($"title {titleNo} (VTS{te?.VtsNumber}/T{te?.VtsTitleNumber}) OpenTitle.Duration={tp.Duration} pgcCells={tp.Pgc.Cells.Count} pgc.Duration={tp.Pgc.Duration}");
                    sb.AppendLine($"progEntryCells(1based)=[{string.Join(",", tp.Pgc.ProgramEntryCells)}]");
                    sb.AppendLine("cells: " + string.Join(" ", tp.Pgc.Cells.Select((c, i) => $"[{i}]{c.Duration:mm\\:ss\\.ff}")));
                    sb.AppendLine("chapters:");
                    foreach (var ch in tp.Chapters)
                        sb.AppendLine($"  #{ch.Number} startCell={ch.StartCellIndex} startTime={ch.StartTime}");
                    sb.AppendLine($"PRE ({tp.Pgc.PreCommands.Count}):");
                    for (int i = 0; i < tp.Pgc.PreCommands.Count; i++)
                        sb.AppendLine($"  [{i + 1}] {DvdVmDisassembler.Hex(tp.Pgc.PreCommands[i])}  {DvdVmDisassembler.ToMnemonic(tp.Pgc.PreCommands[i])}");
                    File.WriteAllText(args[3], sb.ToString());
                    return 0;
                }

                // 診断（M4a）：共有run分割/dedup 計画（セグメント一覧＋タイトル別プレイリスト）。--segment-plan <in.iso> <out.txt>
                case "--segment-plan" when args.Length >= 3:
                {
                    using var disc = DvdDisc.Open(args[1]);
                    File.WriteAllText(args[2], KurokoSegmentPlanner.Dump(disc));
                    return 0;
                }

                // 診断（M4b）：v2 krk のタイトルをプレイリスト demux で読み、PTS連続性/境界キーフレーム/統一トラックを検証。
                // --playlist-probe <in.krk> <title> <ffmpegDir> <out.txt>
                case "--playlist-probe" when args.Length >= 5:
                {
                    // 第6引数でアングル指定（既定 1。§10.1）。
                    int probeAngle = args.Length >= 6 && int.TryParse(args[5], out var pa) ? pa : 1;
                    File.WriteAllText(args[4], PlaylistProbe(args[1], int.Parse(args[2]), args[3], probeAngle));
                    return 0;
                }

                // 診断：タイトルの1セルの MPEG-2 PS を書き出す（インターリーブVOBUフィルタ後）。--title-cell-es <in.iso> <title> <cell> <out.mpg>
                case "--title-cell-es" when args.Length >= 5:
                {
                    int titleNo = int.Parse(args[2]); int cell = int.Parse(args[3]);
                    using var disc = DvdDisc.Open(args[1]);
                    var tp = disc.OpenTitle(titleNo);
                    using var s = tp.OpenStream(cell, cell);
                    using var fs = File.Create(args[4]);
                    s.CopyTo(fs);
                    return 0;
                }

                // 診断：メニューPGCの各セルのプログラムストリーム(ES)を書き出す。--menu-pgc-es <in.iso> <vts> <pgc> <outdir>
                case "--menu-pgc-es" when args.Length >= 5:
                {
                    int vts = int.Parse(args[2]); int pgcNo = int.Parse(args[3]); string outdir = args[4];
                    Directory.CreateDirectory(outdir);
                    using var disc = DvdDisc.Open(args[1]);
                    var tp = disc.OpenMenuPgc(vts, pgcNo);
                    int n = tp?.CellCount ?? 0;
                    for (int c = 0; c < n; c++)
                        using (var s = tp!.OpenStream(c, c))
                        using (var fs = File.Create(Path.Combine(outdir, $"cell_{c:00}.mpg")))
                            s.CopyTo(fs);
                    // PGC全体（本番変換器が符号化する OpenStream(0,n-1)）も書き出す。
                    if (n > 0)
                        using (var sf = tp!.OpenStream(0, n - 1))
                        using (var ff2 = File.Create(Path.Combine(outdir, "full.mpg")))
                            sf.CopyTo(ff2);
                    File.WriteAllText(Path.Combine(outdir, "info.txt"), $"vts={vts} pgc={pgcNo} cells={n}");
                    return 0;
                }

                // 診断：メニューPGCのループ/多ページ挙動解析用に、PGCのPlaybackTime/リンク/セル(Still/CommandNr)/
                // Pre/Post/Cellコマンド（逆アセンブル）をダンプ。--menu-pgc-cmds <in.iso> <vts> <pgc> <out.txt>
                case "--menu-pgc-cmds" when args.Length >= 5:
                {
                    int vts = int.Parse(args[2]); int pgcNo = int.Parse(args[3]);
                    using var disc = DvdDisc.Open(args[1]);
                    var tp = disc.OpenMenuPgc(vts, pgcNo);
                    var sb = new System.Text.StringBuilder();
                    if (tp is null) { sb.AppendLine("(pgc null)"); }
                    else
                    {
                        var pgc = tp.Pgc;
                        sb.AppendLine($"VTS{vts} PGC#{pgcNo} playbackTime={pgc.Duration} uop=0x{pgc.UopControl:X8} next={pgc.NextPgcn} prev={pgc.PrevPgcn} goUp={pgc.GoUpPgcn}");
                        sb.AppendLine($"progEntryCells(1based)=[{string.Join(",", pgc.ProgramEntryCells)}]");
                        for (int c = 0; c < pgc.Cells.Count; c++)
                        {
                            var cell = pgc.Cells[c];
                            string still = cell.StillTime == 0xFF ? "inf" : cell.StillTime.ToString();
                            sb.AppendLine($"  cell[{c}] still={still} cmdNr={cell.CommandNr} time={cell.Duration}");
                        }
                        void DumpCmds(string label, System.Collections.Generic.IReadOnlyList<byte[]> cmds)
                        {
                            sb.AppendLine($"{label} ({cmds.Count}):");
                            for (int i = 0; i < cmds.Count; i++)
                                sb.AppendLine($"  [{i + 1}] {DvdVmDisassembler.Hex(cmds[i])}  {DvdVmDisassembler.ToMnemonic(cmds[i])}");
                        }
                        DumpCmds("PRE", pgc.PreCommands);
                        DumpCmds("POST", pgc.PostCommands);
                        DumpCmds("CELL", pgc.CellCommands);
                    }
                    File.WriteAllText(args[4], sb.ToString());
                    return 0;
                }

                // 診断：メニューPGCの指定セルのボタンPCI（各ボタンの上下左右/自動実行/VM命令）をダンプ。
                // --menu-buttons <in.iso> <vts> <pgc> <cell> <out.txt>
                case "--menu-buttons" when args.Length >= 6:
                {
                    int vts = int.Parse(args[2]); int pgcNo = int.Parse(args[3]); int cell = int.Parse(args[4]);
                    using var disc = DvdDisc.Open(args[1]);
                    var tp = disc.OpenMenuPgc(vts, pgcNo);
                    var sb = new System.Text.StringBuilder();
                    if (tp is null) sb.AppendLine("(pgc null)");
                    else
                    {
                        using var ps = tp.OpenStream(cell, cell);
                        // NAVパック（private_stream_2=0xBF、substream 0x00=PCI）を手動抽出して解析する
                        //（DvdPsDemux は App 側で変換器から参照できないため）。
                        DvdNavPci? pci = null;
                        var sec = new byte[2048];
                        for (int s = 0; s < 3000 && pci is null; s++)
                        {
                            int got = 0, n; while (got < sec.Length && (n = ps.Read(sec, got, sec.Length - got)) > 0) got += n;
                            if (got <= 0) break;
                            for (int i = 0; i + 6 < got; i++)
                            {
                                if (sec[i] != 0 || sec[i + 1] != 0 || sec[i + 2] != 1 || sec[i + 3] != 0xBF) continue;
                                int len = (sec[i + 4] << 8) | sec[i + 5];
                                int po = i + 6;
                                if (po + len > got || len < 1) continue;
                                var payload = new byte[len];
                                System.Array.Copy(sec, po, payload, 0, len);
                                if (DvdNavPci.Parse(payload) is { HasButtons: true } np) { pci = np; break; }
                            }
                        }
                        if (pci is null) sb.AppendLine($"VTS{vts} PGC#{pgcNo} cell{cell}: (no PCI with buttons)");
                        else
                        {
                            sb.AppendLine($"VTS{vts} PGC#{pgcNo} cell{cell}: buttons={pci.ButtonCount} forcedSel={pci.ForcedSelect} forcedAct={pci.ForcedActivate}");
                            foreach (var btn in pci.Buttons)
                                sb.AppendLine($"  btn#{btn.Number} up={btn.Up} down={btn.Down} left={btn.Left} right={btn.Right} auto={btn.AutoAction} " +
                                              $"rect=({btn.XStart},{btn.YStart})-({btn.XEnd},{btn.YEnd}) cmd={DvdVmDisassembler.Hex(btn.Command)}  {DvdVmDisassembler.ToMnemonic(btn.Command)}");
                        }
                    }
                    File.WriteAllText(args[5], sb.ToString());
                    return 0;
                }

                // ISO情報：タイトル/メニューの容量・コーデック・ストリーム別バイト数を出力。--dvd-info <in.iso> <out.txt>
                case "--dvd-info" when args.Length >= 3:
                    File.WriteAllText(args[2], DvdIsoAnalyzer.Analyze(args[1], null, System.Threading.CancellationToken.None));
                    return 0;

                // 診断：DVD-Video ISO から disc.json（FirstPlay/メニュー/VM命令）を生成して出力（符号化なし・高速）。
                // --krk-discjson <in.iso> <out.txt>
                case "--krk-discjson" when args.Length >= 3:
                    using (var disc = DvdDisc.Open(args[1]))
                        File.WriteAllBytes(args[2], KurokoJson.Serialize(KurokoDiscBuilder.BuildDisc(disc)));
                    return 0;

                // 暗号化されたままの入力かを調べる（変換の前に自動で行うものと同じ判定）。
                // --check-encryption <in.iso|in.ts|in.m2ts|in.vob> <out.txt>。ISO は BDMV があれば BD、無ければ DVD として見る。
                case "--check-encryption" when args.Length >= 3:
                {
                    string inPath = args[1], ext = Path.GetExtension(inPath).ToLowerInvariant();
                    string result;
                    if (ext == ".vob")
                        using (var vs = File.OpenRead(inPath)) result = EncryptedInputCheck.VobScrambled(vs) ? "暗号化（CSS）されている" : "暗号化されていない";
                    else if (ext == ".m2ts")
                        using (var ms = File.OpenRead(inPath)) result = EncryptedInputCheck.M2tsEncrypted(ms) ? "暗号化（AACS）されている" : "暗号化されていない";
                    else if (ext == ".ts")
                        result = EncryptedInputCheck.CheckTs(inPath) ?? "スクランブルされていない";
                    else
                    {
                        bool bd;
                        try { using var u = new UdfReader(inPath); bd = u.FindEntry("BDMV") is not null; }
                        catch { bd = false; }
                        result = (bd ? EncryptedInputCheck.CheckBdIso(inPath) : EncryptedInputCheck.CheckDvdIso(inPath))
                                 ?? $"暗号化されていない（{(bd ? "Blu-ray" : "DVD")}）";
                    }
                    File.WriteAllText(args[2], result);
                    return 0;
                }

                // 診断：krk 内の1ファイルを取り出す（ZIP/旧ISO 両対応）。--krk-cat <in.krk> <innerPath> <out>
                case "--krk-cat" when args.Length >= 4:
                    using (var iso = KurokoContainerReader.Open(args[1]))
                    {
                        if (iso.FindEntry(args[2]) is not KrkEntry fe)
                            throw new FileNotFoundException($"krk 内にありません: {args[2]}");
                        File.WriteAllBytes(args[3], iso.ReadAllBytes(fe));
                    }
                    return 0;

                // 診断：AC-3 の各オーディオブロック（256サンプル）の dynrng とゲイン(dB)を CSV で出力する。
                // ffmpeg の実測ゲイン（-drc_scale 0 と 1 の復号PCMの比）と突き合わせて値を検証するのに使う。
                // --ac3-drc-dump <es> <out.csv>
                case "--ac3-drc-dump" when args.Length >= 3:
                {
                    var data = File.ReadAllBytes(args[1]);
                    var sb = new System.Text.StringBuilder("blockIndex,timeMs,dynrngRaw,gainDb\n");
                    int pos = 0; long sampleIndex = 0; int curRaw = 0; long blockNo = 0;
                    while (pos + 6 < data.Length)
                    {
                        if (!(data[pos] == 0x0B && data[pos + 1] == 0x77)) { pos++; continue; }
                        var fr = Ac3Parser.ParseFrame(data, pos);
                        if (fr is null) { pos += 2; continue; }
                        for (int b = 0; b < 6; b++)
                        {
                            if (fr.Dynrng[b] is int dv) curRaw = dv;
                            double tMs = (sampleIndex + b * 256) * 1000.0 / fr.SampleRate;
                            sb.Append(blockNo).Append(',').Append(tMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                              .Append(',').Append(curRaw).Append(',')
                              .Append(Ac3Parser.DynrngToDb(curRaw).ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                            blockNo++;
                        }
                        sampleIndex += 1536;
                        pos += fr.FrameBytes;
                    }
                    File.WriteAllText(args[2], sb.ToString());
                    return 0;
                }

                // 診断：AC-3 の生ESを完全走査（ビット割当まで解いて仮数を読み飛ばす）し、パーサの健全性
                // （6ブロック解析後にフレーム末尾へ着地するか）と dialnorm/dynrng を検証。--ac3-parse <es> <out.txt>
                case "--ac3-parse" when args.Length >= 3:
                {
                    var data = File.ReadAllBytes(args[1]);
                    var sb = new System.Text.StringBuilder();
                    int pos = 0, ok = 0, fail = 0, blocks = 0, nonUnity = 0;
                    var gaps = new List<int>();
                    double? dn = null;
                    int shown = 0;
                    while (pos + 6 < data.Length)
                    {
                        if (!(data[pos] == 0x0B && data[pos + 1] == 0x77)) { pos++; continue; }
                        var fr = Ac3Parser.ParseFrame(data, pos);
                        if (fr is null) { fail++; pos += 2; continue; }
                        ok++;
                        dn ??= Ac3Parser.DialnormToDb(fr.DialnormRaw);
                        // 着地誤差：6ブロック後の位置とフレーム末尾(crc2 の 16bit を除く)の差。
                        int gap = fr.FrameBits - 16 - fr.EndBitPos;
                        gaps.Add(gap);
                        for (int b = 0; b < 6; b++)
                            if (fr.Dynrng[b] is int dv) { blocks++; if (dv != 0) nonUnity++; }
                        if (shown < 5)
                        {
                            sb.AppendLine($"frame@{pos}: bytes={fr.FrameBytes} dialnorm={fr.DialnormRaw}({Ac3Parser.DialnormToDb(fr.DialnormRaw):F0}dB) " +
                                          $"endBit={fr.EndBitPos}/{fr.FrameBits} gap={gap} dynrng=[{string.Join(",", fr.Dynrng.Select(x => x is null ? "-" : x.ToString()))}]");
                            shown++;
                        }
                        pos += fr.FrameBytes;
                    }
                    gaps.Sort();
                    int neg = gaps.Count(x => x < 0);
                    sb.AppendLine($"frames ok={ok} fail={fail} dialnorm={dn:F0}dB");
                    sb.AppendLine($"dynrng blocks送出={blocks} 非ユニティ={nonUnity}");
                    sb.AppendLine($"着地gap(bit): min={(gaps.Count > 0 ? gaps[0] : 0)} p50={(gaps.Count > 0 ? gaps[gaps.Count / 2] : 0)} " +
                                  $"max={(gaps.Count > 0 ? gaps[^1] : 0)} 負(オーバーラン)={neg}");
                    sb.AppendLine(neg == 0 && gaps.Count > 0 && gaps[^1] < 200
                        ? "=> 健全：全フレームがフレーム内に収まり、末尾へ密着して着地"
                        : "=> 破綻：ビット割当/構文解析にズレあり");
                    File.WriteAllText(args[2], sb.ToString());
                    return 0;
                }

                // 診断：AC-3/DTS の生ESを直接パースし DRC メタデータ記録の有無を判定。--audio-drc <es> <ac3|dts>
                case "--audio-drc" when args.Length >= 3:
                    File.WriteAllText(Path.ChangeExtension(args[1], ".drc.txt"),
                        AudioDrcScanner.Scan(args[1], string.Equals(args[2], "dts", StringComparison.OrdinalIgnoreCase)));
                    return 0;

                // 診断：ISO/krk の一覧用サマリー（形式/コーデック/サイズ集計）を1件出力。
                // --info-summary <in.iso|in.krk> <out.txt> [ffprobe.exe]
                case "--info-summary" when args.Length >= 3:
                {
                    var s = args[1].EndsWith(".krk", StringComparison.OrdinalIgnoreCase)
                        ? KurokoInfoAnalyzer.AnalyzeSummary(args[1], args.Length >= 4 ? args[3] : null, System.Threading.CancellationToken.None)
                        : DvdIsoAnalyzer.AnalyzeSummary(args[1], null, System.Threading.CancellationToken.None);
                    File.WriteAllText(args[2], s.ToString());
                    return 0;
                }

                // krk情報：タイトル/映像・音声・字幕・サイズを出力。--krk-info <in.krk> <out.txt> [ffprobe.exe]
                case "--krk-info" when args.Length >= 3:
                    File.WriteAllText(args[2], KurokoInfoAnalyzer.Analyze(args[1], args.Length >= 4 ? args[3] : null, System.Threading.CancellationToken.None));
                    return 0;

                // 診断：字幕抽出→再パック→mux のみ検証。--dvd-subtest <in.iso> <title> <out.mkv> <ffmpeg> <ffprobe> <report.txt>
                case "--dvd-subtest" when args.Length >= 7:
                    File.WriteAllText(args[6], DvdH264FullConverter.SubtitleSelfTest(args[1], int.Parse(args[2]), args[4], args[5], args[3]));
                    return 0;

                // 診断：タイトルの再生用PSをダンプ。--dvd-dumpps <in.iso> <title> <out.ps> [maxBytes]
                case "--dvd-dumpps" when args.Length >= 4:
                    Console.WriteLine(DvdH264FullConverter.DumpTitlePs(args[1], int.Parse(args[2]), args[3],
                        args.Length >= 5 ? long.Parse(args[4]) : 0));
                    return 0;

                // 診断：ISO 内のファイルを取り出す。--udf-extract <in.iso> <ISO内パス> <out>
                case "--udf-extract" when args.Length >= 4:
                {
                    using var udf = new UdfReader(args[1]);
                    var ent = udf.FindEntry(args[2]) ?? throw new FileNotFoundException(args[2]);
                    File.WriteAllBytes(args[3], udf.ReadAllBytes(ent));
                    return 0;
                }

                // 診断：ISO を UDF として読み、ファイル階層を出す（純UDF＝Blu-ray の ISO 確認用）。
                // --udf-list <in.iso> <out.txt> [最大表示数]
                case "--udf-list" when args.Length >= 3:
                {
                    int max = args.Length >= 4 && int.TryParse(args[3], out var mv) ? mv : 400;
                    File.WriteAllText(args[2], UdfListReport.Run(args[1], max));
                    return 0;
                }

                // 診断：BD のタイトル（連結＋STC書換後）の一部を切り出す。ffprobe で外部検証するため。
                // --bd-dump <in.iso> <out.m2ts> <開始バイト|boundary> <バイト数> [タイトル添字]
                // タイトル添字は BdmvTitleSet.Build の並び順（0 始まり）。省略時は本編推定。
                case "--bd-dump" when args.Length >= 5:
                {
                    using var udf = new UdfReader(args[1]);
                    var titles = BdmvTitleSet.Build(udf);
                    var t = (args.Length >= 6 && int.TryParse(args[5], out var tix) && tix >= 0 && tix < titles.Count
                            ? titles[tix]
                            : BdmvTitleSet.PickMain(titles))
                        ?? throw new System.InvalidOperationException("BD: タイトルが決まりません。");
                    long clip0 = udf.FindEntry($"BDMV/STREAM/{t.ClipIds[0]}.m2ts")?.Length ?? 0;
                    long from = args[3] == "boundary" ? clip0 : long.Parse(args[3]);
                    long bytes = long.Parse(args[4]);
                    using var st = BdTitleStream.CreateFactory(args[1], t)();
                    st.Position = from / 192 * 192;
                    using var fo = File.Create(args[2]);
                    var buf = new byte[1 << 20];
                    long done = 0;
                    while (done < bytes)
                    {
                        int n = st.Read(buf, 0, (int)System.Math.Min(buf.Length, bytes - done));
                        if (n <= 0) break;
                        fo.Write(buf, 0, n);
                        done += n;
                    }
                    return 0;
                }

                // 診断：BD → Kuroko(none) の変換計画を出す（符号化はしない）。--bd-plan <in.iso> <out.txt>
                case "--bd-plan" when args.Length >= 3:
                {
                    using var udf = new UdfReader(args[1]);
                    var plan = BdKurokoPlanner.Build(udf);
                    File.WriteAllText(args[2], BdKurokoPlanner.Describe(plan));
                    return 0;
                }

                // Blu-ray ISO → コントローラ none の Kuroko(.krk)（タイトル集合は ISO 再生と同じ。アングル対応）。
                // --bd-convert-none <in.iso> <out.krk> <report.txt> [key=value ...]
                // 進捗は <report.txt>.progress.txt へ「fraction TAB status」で逐次追記（TS/DVD 版と同じ）。
                case "--bd-convert-none" when args.Length >= 4:
                    File.WriteAllText(args[3], BdNoneConverter.ConvertBdToKuroko(args[1], args[2], ParseOptions(args, 4),
                        new FileTraceProgress(args[3] + ".progress.txt")));
                    return 0;

                // 診断：BD の PGS 字幕を生のまま取り出す。--bd-pgs <in.iso> <clipId> <PID(16進)> <出力先> [最大MB]
                case "--bd-pgs" when args.Length >= 5:
                {
                    using var udf = new UdfReader(args[1]);
                    string clipId = args[2];
                    int pid = System.Convert.ToInt32(args[3], 16);
                    var ent = udf.FindEntry($"BDMV/STREAM/{clipId}.m2ts") ?? throw new FileNotFoundException(clipId);
                    var (s45, _) = BdmvPlaylist.ReadClipStcRange(udf, clipId);
                    long maxBytes = args.Length >= 6 && long.TryParse(args[5], out var mb) ? mb * 1024 * 1024 : 0;
                    using var st = udf.OpenRead(ent);
                    var cues = BdPgsExtractor.Extract(st, pid, s45 >= 0 ? s45 * 2 : 0, maxBytes);
                    Directory.CreateDirectory(args[4]);
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"clip={clipId} pid=0x{pid:X4} cues={cues.Count} 走査={(maxBytes > 0 ? maxBytes / 1048576 + "MB" : "全体")}");
                    for (int i = 0; i < cues.Count; i++)
                    {
                        string name = $"{i + 1:0000}.pgs";
                        File.WriteAllBytes(Path.Combine(args[4], name), cues[i].Data);
                        if (i < 12)
                            sb.AppendLine($"  {name} {System.TimeSpan.FromMilliseconds(cues[i].StartMs):hh\\:mm\\:ss\\.fff}"
                                + $" 〜 {System.TimeSpan.FromMilliseconds(cues[i].EndMs):hh\\:mm\\:ss\\.fff} {cues[i].Data.Length:N0} bytes");
                    }
                    if (cues.Count > 0)
                        sb.AppendLine($"  合計 {cues.Sum(c => (long)c.Data.Length):N0} bytes / 平均 {cues.Average(c => c.Data.Length):F0} bytes");
                    File.WriteAllText(Path.Combine(args[4], "report.txt"), sb.ToString());
                    return 0;
                }

                default:
                    Console.Error.WriteLine($"不明な命令です：{args[0]}（引数なしで起動すると画面が開きます）");
                    return 1;
            }
        }
        catch (System.Exception ex)
        {
            // ログ基盤が無いので report 引数（あれば末尾の .txt）へ書き、標準エラーにも出す。
            string? report = System.Array.Find(args, a => a.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase));
            if (report is not null) { try { File.WriteAllText(report, "ERROR: " + ex); } catch { } }
            Console.Error.WriteLine("ERROR: " + ex);
            return 2;
        }
    }

    // key=value 引数（crf/keyint/gop/deint/preset/maxsec/imode/fields-min-frames/ffmpeg/ffprobe 等）を Options へ。
    // v2 プレイリスト demux をヘッドレス検証：全パケットを読み、映像PTSの単調性・境界キーフレーム・トラック別集計を返す。
    private static string PlaylistProbe(string krk, int titleNo, string ffmpegDir, int angle = 1)
    {
        var sb = new System.Text.StringBuilder();
        using var src = KurokoTitleSource.Open(krk);
        var (angleCount, angleType, _) = src.ReadTitleAngles(titleNo);
        var title = src.ReadTitle(titleNo, angle);
        var segOffs = title.Segments.Select(s => $"{s.Id}@{(long)KurokoTime.Parse(s.StartTime).TotalMilliseconds}ms(cells {s.TitleCellStart}..{s.TitleCellStart + s.CellCount - 1})").ToList();
        sb.AppendLine($"title{titleNo} angle{angle}/{angleCount}(type={angleType ?? "-"}): segments={title.Segments.Count} [{string.Join(", ", segOffs)}]");
        sb.AppendLine($"canonical tracks: video=0 audio=[{string.Join(",", title.Audio.Select(a => a.MkvTrack))}] sub=[{string.Join(",", title.Subtitle.Select(s => s.MkvTrack))}]");
        // セル（先頭12個）：アングルブロックの兄弟セルは開始時刻を共有し、尺もブロックの尺になる（§17.1）。
        sb.AppendLine("cells (first 12): " + string.Join(", ", title.Cells.Take(12).Select(c =>
            $"#{c.Index}{(c.Angle > 0 ? $"a{c.Angle}" : "")}@{(long)KurokoTime.Parse(c.StartTime).TotalMilliseconds}+{(long)KurokoTime.Parse(c.Duration).TotalMilliseconds}ms/{c.Segment}")));

        using var dem = src.OpenTitleMedia(titleNo, ffmpegDir, angle);
        var cnt = new Dictionary<int, int>();
        var minp = new Dictionary<int, long>();
        var maxp = new Dictionary<int, long>();
        long lastVid = -1; int vidBack = 0; var vidKeys = new List<long>();
        int audCanon = title.Audio.Count > 0 ? title.Audio[0].MkvTrack : 1; // 音声トラック1の連続性を見る
        long lastAud = -1; var audGaps = new List<string>();
        int total = 0;
        while (dem.ReadPacket(out var p) && total < 5000000)
        {
            total++;
            cnt[p.StreamIndex] = cnt.TryGetValue(p.StreamIndex, out var c) ? c + 1 : 1;
            if ((long)p.PtsMs >= 0)
            {
                if (!minp.TryGetValue(p.StreamIndex, out var mn) || (long)p.PtsMs < mn) minp[p.StreamIndex] = (long)p.PtsMs;
                if (!maxp.TryGetValue(p.StreamIndex, out var mx) || (long)p.PtsMs > mx) maxp[p.StreamIndex] = (long)p.PtsMs;
                if (p.StreamIndex == 0)
                {
                    if (lastVid >= 0 && (long)p.PtsMs < lastVid - 5) vidBack++;
                    lastVid = (long)p.PtsMs;
                    if (p.KeyFrame) vidKeys.Add((long)p.PtsMs);
                }
                else if (p.StreamIndex == audCanon)
                {
                    if (lastAud >= 0)
                    {
                        long d = (long)p.PtsMs - lastAud;
                        if (d > 60 || d < 0) audGaps.Add($"{lastAud}->{(long)p.PtsMs}(Δ{d})"); // 通常20-40ms。逸脱＝境界の隙間/重なり
                    }
                    lastAud = (long)p.PtsMs;
                }
            }
        }
        sb.AppendLine($"packets total={total}  video PTS backward-jumps={vidBack}");
        foreach (var kv in cnt.OrderBy(k => k.Key))
            sb.AppendLine($"  stream{kv.Key}: packets={kv.Value} pts=[{(minp.TryGetValue(kv.Key, out var a) ? a : -1)}..{(maxp.TryGetValue(kv.Key, out var b) ? b : -1)}]ms");
        sb.AppendLine($"video keyframes (ms, first 40): {string.Join(",", vidKeys.Take(40))}");
        sb.AppendLine($"audio(track {audCanon}) PTS discontinuities (>60ms or backward): {(audGaps.Count == 0 ? "none" : string.Join(" ", audGaps.Take(20)))}");
        return sb.ToString();
    }

    // CLI 用の進捗トレース：同期追記（Progress<T> はスレッドプール経由で順序が乱れるため使わない）。
    private sealed class FileTraceProgress : System.IProgress<DvdH264Converter.Progress>
    {
        private readonly string _path;
        public FileTraceProgress(string path) { _path = path; try { File.Delete(path); } catch { } }
        public void Report(DvdH264Converter.Progress p)
        { try { File.AppendAllText(_path, $"{p.Fraction:F4}\t{p.Status}\n"); } catch { } }
    }

    private static DvdH264Converter.Options ParseOptions(string[] args, int start)
    {
        var kv = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        for (int i = start; i < args.Length; i++)
        {
            int eq = args[i].IndexOf('=');
            if (eq > 0) kv[args[i].Substring(0, eq)] = args[i].Substring(eq + 1);
        }
        string Get(string k, string def) => kv.TryGetValue(k, out var v) ? v : def;
        return new DvdH264Converter.Options(
            // CRF の既定は x264/x265 が 20、AV1 は目盛りが違うので 32（KurokoVideoCodecs.DefaultAv1Crf）。
            Crf: int.TryParse(Get("crf", KurokoVideoCodecs.IsAv1(Get("vcodec", "av1")) ? KurokoVideoCodecs.DefaultAv1Crf.ToString() : "20"), out var c) ? c : 20,
            Keyint: int.TryParse(Get("keyint", "60"), out var k) ? k : 60,
            GopMode: Get("gop", "hybrid"),
            Deinterlace: Get("deint", "none"),
            Preset: Get("preset", "medium"),
            MaxSecondsPerCell: double.TryParse(Get("maxsec", "0"), out var m) ? m : 0,
            FfmpegPath: Get("ffmpeg", ToolPaths.DefaultFfmpeg()),
            FfprobePath: Get("ffprobe", ToolPaths.FfprobeNextTo(Get("ffmpeg", ToolPaths.DefaultFfmpeg()))),
            MkvmergePath: Get("mkvmerge", ToolPaths.DefaultMkvmerge()),
            AudioMode: Get("audio", "recompress"),   // 画面と同じ既定（copy は TS の AAC で ffmpeg が失敗することがある）
            // abr は **1チャンネルあたり** の kbps（出力ch数倍したものが実際の指定値）。
            AudioBitrateK: int.TryParse(Get("abr", "32"), out var abr) ? abr : 32,
            AudioCodec: Get("acodec", "opus"),
            AudioChannels: Get("achan", "stereo"),
            MenuMode: Get("menu", "recompress"),
            VideoCodec: Get("vcodec", "av1"),
            InterlaceMode: Get("imode", "fields"),
            FieldsMinInterlaceFrames: int.TryParse(Get("fields-min-frames", "30"), out var fmi) ? fmi : 30);
    }

}
