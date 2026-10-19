using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Kuroko.Converter;

// 一括変換の画面：変換待ちの一覧（追加・削除・並べ替え・元の変更・開始待ちへ戻す）、全行共通のパラメータ、
// 外部の ffmpeg・mkvmerge の場所、レポート・進捗。情報・音声定義の復旧・ファーストプレイ設定もここから開く。
public partial class MainWindow : Window
{
    private bool _running;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _infoCts;
    private readonly ObservableCollection<ConvJob> _jobs = new();
    private readonly DispatcherTimer _elapsedTimer;

    private static readonly Avalonia.Platform.Storage.FilePickerFileType SourceType
        = Dialogs.Type("DVD-Video / Blu-ray ISO / 放送TS", "*.iso", "*.ts");
    private static readonly Avalonia.Platform.Storage.FilePickerFileType KrkType = Dialogs.Type("Kuroko (.krk)", "*.krk");

    public MainWindow()
    {
        InitializeComponent();
        // ffmpeg・mkvmerge は同梱しない。前に使った場所、無ければ PATH 等から探した場所を既定にする（ToolPaths）。
        FfmpegText.Text = ToolPaths.DefaultFfmpeg();
        MkvmergeText.Text = ToolPaths.DefaultMkvmerge();
        JobList.ItemsSource = _jobs;
        // 1チャンネルあたりの kbps。実際の指定値は「これ×出力チャンネル数」。
        AudioBitrateCombo.ItemsSource = new[] { 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        AudioBitrateCombo.SelectedItem = 32;
        ApplyCodecChoices(); // プリセット・GOP の選択肢を既定のコーデック（AV1）に合わせる
        // 処理中行の「処理時間」を毎秒更新する。
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => { foreach (var j in _jobs) j.Tick(); };
        _elapsedTimer.Start();
    }

    private static string TagOf(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    // 変換後サフィックスの変更を全ジョブの変換後ファイル名へ反映する。
    private void SuffixText_TextChanged(object? sender, TextChangedEventArgs e)
    {
        string suffix = SuffixText.Text ?? "";
        foreach (var j in _jobs) j.Suffix = suffix;
    }

    // 映像コーデックの切替：CRF の目盛りが AV1（0〜63）と x264/x265 で違うので、CRF が既定値のままなら
    // 切替先の既定値へ合わせる（利用者が変えた値はそのまま）。
    private void VideoCodecCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CrfText is null) return;   // 初期化中
        ApplyCodecChoices();
        bool av1 = TagOf(VideoCodecCombo, "av1") == "av1";
        string x26Default = "20", av1Default = KurokoVideoCodecs.DefaultAv1Crf.ToString();
        string cur = CrfText.Text?.Trim() ?? "";
        if (av1 && cur == x26Default) CrfText.Text = av1Default;
        else if (!av1 && cur == av1Default) CrfText.Text = x26Default;
    }

    // x264/x265 のプリセット（画面に出すもの）。AV1 のときは SVT-AV1 の段階 0〜13 を出し、この名前に当たる段階に名前を添える。
    private static readonly string[] X26Presets = { "ultrafast", "veryfast", "fast", "medium", "slow", "veryslow" };
    private bool? _av1Choices;          // 今の選択肢が AV1 用か（null＝まだ作っていない）
    private string _x26Gop = "hybrid";  // AV1 にする前の GOP の選択（x264/x265 に戻したときに戻す）

    // プリセット・GOP の選択肢を映像のコーデックに合わせて作り直す。
    //   AV1：プリセットは SVT-AV1 の段階 0〜13（例「6（medium 相当・既定）」）、GOP はクローズドだけ（AV1 は常にクローズド）。
    //   H.264・H.265：プリセットは x264/x265 の名前、GOP はオープン（セル先頭だけクローズド）とクローズド。
    // 切り替えたときは、プリセットはいちばん近い値を選び直す。
    private void ApplyCodecChoices()
    {
        bool av1 = TagOf(VideoCodecCombo, "av1") == "av1";
        if (_av1Choices == av1) return;
        int svt = KurokoVideoCodecs.SvtPreset(TagOf(PresetCombo, "medium")); // 名前・数字のどちらでも段階になる
        if (av1)
        {
            var items = Enumerable.Range(0, 14).Select(n =>
            {
                string? name = X26Presets.FirstOrDefault(p => KurokoVideoCodecs.SvtPreset(p) == n);
                string label = name is null ? $"{n}" : name == "medium" ? $"{n}（medium 相当・既定）" : $"{n}（{name} 相当）";
                return new ComboBoxItem { Tag = n.ToString(), Content = label };
            }).ToList();
            PresetCombo.ItemsSource = items;
            PresetCombo.SelectedIndex = svt;
            if (_av1Choices is not null) _x26Gop = TagOf(GopCombo, _x26Gop);
            GopCombo.ItemsSource = new[] { new ComboBoxItem { Tag = "closed", Content = "クローズドGOP（AV1 は常にクローズド）" } };
            GopCombo.SelectedIndex = 0;
        }
        else
        {
            PresetCombo.ItemsSource = X26Presets.Select(p => new ComboBoxItem { Tag = p, Content = p }).ToList();
            // いちばん近い名前（同じ近さなら遅いほう）。
            PresetCombo.SelectedIndex = Enumerable.Range(0, X26Presets.Length)
                .OrderBy(i => Math.Abs(KurokoVideoCodecs.SvtPreset(X26Presets[i]) - svt)).ThenByDescending(i => i).First();
            GopCombo.ItemsSource = new[]
            {
                new ComboBoxItem { Tag = "hybrid", Content = "オープンGOP（セル先頭GOPのみクローズドGOP）" },
                new ComboBoxItem { Tag = "closed", Content = "クローズドGOP（シーク堅牢）" },
            };
            GopCombo.SelectedIndex = _x26Gop == "closed" ? 1 : 0;
        }
        _av1Choices = av1;
    }

    private string FfprobePath()
        => ToolPaths.FfprobeNextTo(string.IsNullOrWhiteSpace(FfmpegText.Text) ? "ffmpeg" : FfmpegText.Text.Trim());

    // ===== キュー編集 =====

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var f in await Dialogs.OpenFiles(this, "変換する ISO・TS を選ぶ", true, SourceType))
            _jobs.Add(new ConvJob { Source = f, Suffix = SuffixText.Text ?? "" }); // 状態は既定で「開始待ち」
    }

    // ISO/krk情報（一覧）：フォルダーを指定し、直下の .iso/.krk を別ウィンドウで順次解析して一覧表示する。
    private async void InfoList_Click(object? sender, RoutedEventArgs e)
    {
        var dir = await Dialogs.OpenFolder(this, "ISO/krk のあるフォルダーを選択");
        if (dir is null) return;
        new InfoListWindow(dir, FfprobePath()).Show(this);
    }

    // 音声定義の復旧：音声が鳴らない .krk（seg.json の audio.tracks が空）を、内蔵 av.mkv を ffprobe して
    // 音声定義（seg.json/title.json）を再構成し書き戻す。ffmpeg 再エンコード不要。元は .bak へ退避。
    private async void RecoverAudio_Click(object? sender, RoutedEventArgs e)
    {
        if (_running || _infoCts != null) return;
        var src = await Dialogs.OpenFile(this, "音声定義を復旧する krk を選ぶ", KrkType);
        if (src is null) return;
        string fpInfo = FfprobePath();

        _infoCts = new CancellationTokenSource();
        var ct = _infoCts.Token;
        SetInfoBusy(true);
        StatusText.Text = $"[{Path.GetFileName(src)}] 音声定義を復旧中…";
        try
        {
            string report = await Task.Run(() => KurokoAudioRecovery.Recover(src, fpInfo, ct), ct);
            StatusText.Text = $"[{Path.GetFileName(src)}] 復旧処理 完了";
            new ReportWindow(report, Path.GetFileNameWithoutExtension(src) + ".recover.txt").Show(this);
        }
        catch (OperationCanceledException) { StatusText.Text = "復旧を中止しました。"; }
        catch (Exception ex)
        {
            StatusText.Text = "復旧に失敗しました: " + ex.Message;
            await Dialogs.Show(this, "音声定義の復旧に失敗しました", ex.ToString());
        }
        finally { SetInfoBusy(false); }
    }

    // ファーストプレイ設定：.krk を指定して、メディアプレーヤーとして再生したときの開始状態を設定する。
    private async void FirstPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (_running || _infoCts != null) return;
        await new FirstPlayWindow().ShowDialog(this);
    }

    // ISO/krk情報：ISO（DVD-Video）または .krk（Kuroko）を指定して情報を解析・表示する。
    // ISO は VOB を実走査するため重い（非同期＋進捗、中止可）。krk は JSON を読むだけで軽い。
    private async void Info_Click(object? sender, RoutedEventArgs e)
    {
        if (_running || _infoCts != null) return;
        var src = await Dialogs.OpenFile(this, "情報を見る ISO・krk を選ぶ", Dialogs.Type("DVD-Video ISO / Kuroko (.krk)", "*.iso", "*.krk"));
        if (src is null) return;
        bool isKrk = src.EndsWith(".krk", StringComparison.OrdinalIgnoreCase);
        string fpInfo = FfprobePath();

        _infoCts = new CancellationTokenSource();
        var ct = _infoCts.Token;
        SetInfoBusy(true);
        StatusText.Text = $"[{Path.GetFileName(src)}] 解析中…";
        var progress = new Progress<(double frac, string status)>(p =>
        {
            Progress.Value = p.frac;
            StatusText.Text = $"[{Path.GetFileName(src)}] {p.status}  {p.frac * 100:F0}%";
        });
        try
        {
            string report = isKrk
                ? await Task.Run(() => KurokoInfoAnalyzer.Analyze(src, fpInfo, ct), ct)
                : await Task.Run(() => DvdIsoAnalyzer.Analyze(src, progress, ct), ct);
            StatusText.Text = $"[{Path.GetFileName(src)}] 解析完了";
            new ReportWindow(report, Path.GetFileNameWithoutExtension(src) + ".info.txt").Show(this);
        }
        catch (OperationCanceledException) { StatusText.Text = "解析を中止しました。"; }
        catch (Exception ex)
        {
            StatusText.Text = "解析に失敗しました: " + ex.Message;
            await Dialogs.Show(this, "ISO/krk情報の解析に失敗しました", ex.ToString());
        }
        finally { SetInfoBusy(false); }
    }

    private void SetInfoBusy(bool busy)
    {
        if (!busy) { _infoCts?.Dispose(); _infoCts = null; }
        InfoButton.IsEnabled = AddButton.IsEnabled = RunButton.IsEnabled = RecoverAudioButton.IsEnabled = !busy;
        CancelButton.IsVisible = busy || _running;
        Progress.Value = 0;
    }

    // 変換元のダブルクリック：別のファイルを選んで差し替える。
    private async void SourceCell_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ConvJob job || !job.CanEdit) return;
        e.Handled = true;
        var f = await Dialogs.OpenFile(this, "変換元を選び直す", SourceType);
        if (f is null || string.Equals(f, job.Source, StringComparison.OrdinalIgnoreCase)) return;
        job.Source = f;      // 変換後名は連動して変わる
        ResetJob(job);       // 元が変わったので「開始待ち」に戻す
    }

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ConvJob job && job.CanEdit) _jobs.Remove(job);
    }

    private void MoveUp_Click(object? sender, RoutedEventArgs e) => Move(sender, -1);
    private void MoveDown_Click(object? sender, RoutedEventArgs e) => Move(sender, +1);

    // 上下移動。処理中行以外なら自由に動かせる（処理中行自体はボタン無効で動かせない）。
    private void Move(object? sender, int dir)
    {
        if ((sender as Control)?.DataContext is not ConvJob job || !job.CanEdit) return;
        int i = _jobs.IndexOf(job), j = i + dir;
        if (i < 0 || j < 0 || j >= _jobs.Count) return;
        _jobs.Move(i, j);
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ConvJob job && job.CanReset) ResetJob(job);
    }

    private static void ResetJob(ConvJob job)
    {
        job.State = JobState.Pending;
        job.Start = null;
        job.End = null;
        job.Percent = 0;
    }

    // ===== 実行 =====

    private async void FfmpegBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var f = await Dialogs.OpenFile(this, "ffmpeg を選ぶ", Dialogs.Executable("ffmpeg"));
        if (f is not null) FfmpegText.Text = f;
    }

    private async void MkvmergeBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var f = await Dialogs.OpenFile(this, "mkvmerge を選ぶ", Dialogs.Executable("mkvmerge"));
        if (f is not null) MkvmergeText.Text = f;
    }

    private void SetRunning(bool running)
    {
        _running = running;
        // パラメータ／ffmpeg・mkvmerge／既存出力の扱いはバッチ中固定（全行へ同一適用）。一覧と「追加」は実行中も操作可。
        CrfText.IsEnabled = KeyintText.IsEnabled = InterlaceModeCombo.IsEnabled = GopCombo.IsEnabled = !running;
        PresetCombo.IsEnabled = AudioCombo.IsEnabled = FfmpegText.IsEnabled = FfmpegBrowseButton.IsEnabled = !running;
        MkvmergeText.IsEnabled = MkvmergeBrowseButton.IsEnabled = !running;
        ExistCombo.IsEnabled = VideoCodecCombo.IsEnabled = SuffixText.IsEnabled = MaxSecText.IsEnabled = !running;
        FieldsMinFramesText.IsEnabled = AudioBitrateCombo.IsEnabled = !running;
        RunButton.IsVisible = !running;
        CancelButton.IsVisible = running;
    }

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (_jobs.All(j => j.State != JobState.Pending))
        {
            StatusText.Text = "「開始待ち」の項目がありません。";
            return;
        }

        // パラメータは実行開始時に確定し、バッチ全体へ同一適用。
        int crf = int.TryParse(CrfText.Text, out var c) ? c : 20;
        int keyint = int.TryParse(KeyintText.Text, out var k) ? k : 60;
        // フィールド分離時：プログレッシブ隣接・この値未満の実インターレース小区間を Weave 化（既定30フレーム）。
        int fieldsMinFrames = int.TryParse(FieldsMinFramesText.Text?.Trim(), out var fmi) && fmi >= 0 ? fmi : 30;
        string interlaceMode = TagOf(InterlaceModeCombo, "fields");
        string gopMode = TagOf(GopCombo, "hybrid");
        string preset = TagOf(PresetCombo, "medium");
        // 音声は常に Opus へ再符号化（2ch へのダウンミックスか、チャンネル数の維持）。
        string audioChannels = TagOf(AudioCombo, "opus") == "opus-src" ? "source" : "stereo";
        int audioBitrateK = AudioBitrateCombo.SelectedItem is int abr ? abr : 32;
        string videoCodec = TagOf(VideoCodecCombo, "av1");
        string ff = string.IsNullOrWhiteSpace(FfmpegText.Text) ? "ffmpeg" : FfmpegText.Text.Trim();
        string fp = ToolPaths.FfprobeNextTo(ff);
        string mm = string.IsNullOrWhiteSpace(MkvmergeText.Text) ? ToolPaths.DefaultMkvmerge() : MkvmergeText.Text.Trim();
        ToolPaths.Remember(ff, mm); // 次に開いたときの既定にする
        // 先頭からの変換時間（秒）。空欄＝無制限。
        double maxSec = double.TryParse(MaxSecText.Text?.Trim(), out var ms) && ms > 0 ? ms : 0;
        var opt = new DvdH264Converter.Options(
            Crf: crf, Keyint: keyint, GopMode: gopMode, Deinterlace: "none",
            Preset: preset, MaxSecondsPerCell: maxSec, FfmpegPath: ff, FfprobePath: fp, MkvmergePath: mm,
            AudioMode: "recompress", AudioBitrateK: audioBitrateK,
            AudioCodec: "opus", AudioChannels: audioChannels,
            VideoCodec: videoCodec, InterlaceMode: interlaceMode,
            FieldsMinInterlaceFrames: fieldsMinFrames,
            MenuMode: "recompress");
        bool skipExisting = TagOf(ExistCombo, "overwrite") == "skip";

        SetRunning(true);
        bool cancelled = false;
        try
        {
            // 上から「開始待ち」を探して順に処理。完了/失敗は飛ばす。
            while (true)
            {
                var job = _jobs.FirstOrDefault(j => j.State == JobState.Pending);
                if (job is null) break;

                string outPath = job.Output;
                job.Start = DateTime.Now;
                job.End = null;
                job.Percent = 0;
                job.State = JobState.Running;

                if (string.IsNullOrEmpty(job.Source) || !File.Exists(job.Source))
                {
                    job.End = DateTime.Now;
                    job.State = JobState.Failed;
                    LogText.Text = "入力ファイルが見つかりません: " + job.Source;
                    continue;
                }
                if (skipExisting && File.Exists(outPath))
                {
                    job.End = DateTime.Now;
                    job.State = JobState.Done; // 既存ありはスキップして「変換終了」扱い
                    StatusText.Text = "既存のためスキップ: " + outPath;
                    continue;
                }

                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                var progress = new Progress<DvdH264Converter.Progress>(p =>
                {
                    job.Percent = (int)Math.Round(p.Fraction * 100);
                    Progress.Value = p.Fraction;
                    StatusText.Text = $"[{Path.GetFileName(job.Source)}] {p.Status}  {p.Fraction * 100:F0}%";
                });
                Progress.Value = 0;
                LogText.Text = "";
                StatusText.Text = $"[{Path.GetFileName(job.Source)}] 開始しています…";

                string report = "";
                try
                {
                    // 入力の種別で変換器を選ぶ。Blu-ray も拡張子は .iso なので中身で判別する。
                    bool isTs = job.Source.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
                    bool isBd = !isTs && BdmvTitleSet.IsBlurayIso(job.Source);
                    report = isTs
                        ? await Task.Run(() => TsNoneConverter.ConvertTsToKuroko(job.Source, outPath, opt, progress, ct), ct)
                        : isBd
                        ? await Task.Run(() => BdNoneConverter.ConvertBdToKuroko(job.Source, outPath, opt, progress, ct), ct)
                        : await Task.Run(() => DvdH264FullConverter.ConvertToKuroko(job.Source, outPath, opt, progress, ct), ct);
                    // 変換器はキャンセルを内部で握って文字列（CANCELLED）を返すことがある。その場合も全体中止として扱う。
                    ct.ThrowIfCancellationRequested();
                    job.End = DateTime.Now;
                    if (report.Contains("done:")) { job.Percent = 100; job.State = JobState.Done; }
                    else job.State = JobState.Failed;
                }
                catch (OperationCanceledException)
                {
                    // 全体中止：処理中だった行は「開始待ち」へ戻す。途中出力は消す。
                    ResetJob(job);
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    job.End = DateTime.Now;
                    job.State = JobState.Failed;
                    report = ex.ToString();
                }
                finally
                {
                    _cts?.Dispose();
                    _cts = null;
                    if (!string.IsNullOrEmpty(report))
                    {
                        LogText.Text = report;
                        // 各ファイルのレポートを出力の隣に .convert.log で残す。
                        try { File.WriteAllText(Path.ChangeExtension(outPath, ".convert.log"), report); } catch { }
                    }
                }
            }
        }
        finally { SetRunning(false); }

        StatusText.Text = cancelled
            ? "中止しました。"
            : $"バッチ完了：成功 {_jobs.Count(j => j.State == JobState.Done)} / 失敗 {_jobs.Count(j => j.State == JobState.Failed)}";
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_running) { _cts?.Cancel(); StatusText.Text = "中止しています…"; }
        else if (_infoCts != null) { _infoCts.Cancel(); StatusText.Text = "解析を中止しています…"; }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_running || _infoCts != null) { e.Cancel = true; return; } // 変換中/解析中は閉じない
        _elapsedTimer.Stop();
        base.OnClosing(e);
    }
}
