using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Avalonia.Controls;

namespace Kuroko.Converter;

// ISO/krk情報（一覧）：指定フォルダー直下の .iso/.krk をファイル名の自然順で列挙し、上から順に
// サマリー（形式/コーデック/タイトル・メニュー別サイズ）を収集して一覧へ表示する。
// ISO は VOB 実走査のため1件が重い。ウィンドウを閉じると処理を中断する。
public partial class InfoListWindow : Window
{
    private readonly string _dir;
    private readonly string _ffprobePath;
    private readonly ObservableCollection<InfoRow> _rows = new();
    private readonly CancellationTokenSource _cts = new();

    public InfoListWindow() : this("", "ffprobe") { } // 画面の設計ツール用

    public InfoListWindow(string dir, string ffprobePath)
    {
        InitializeComponent();
        _dir = dir;
        _ffprobePath = ffprobePath;
        Title = $"ISO/krk情報（一覧） - {dir}";
        ListGrid.ItemsSource = _rows;
        Opened += async (_, _) => { if (_dir.Length > 0) await RunAsync(); };
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task RunAsync()
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(_dir)
                .Where(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".krk", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, NaturalNameComparer.Instance)
                .ToList();
        }
        catch (Exception ex)
        {
            BarText.Text = $"フォルダーを読めません: {ex.Message}";
            return;
        }

        foreach (var f in files)
            _rows.Add(new InfoRow { FullPath = f, FileName = Path.GetFileName(f) });
        Bar.Maximum = Math.Max(1, files.Count);
        BarText.Text = $"0/{files.Count}";
        if (files.Count == 0) { BarText.Text = "ISO/krk ファイルがありません。"; return; }

        var ct = _cts.Token;
        int done = 0;
        foreach (var row in _rows)
        {
            if (ct.IsCancellationRequested) return;
            BarText.Text = $"{done}/{files.Count}  {row.FileName} を解析中…";
            try
            {
                bool isKrk = row.FullPath.EndsWith(".krk", StringComparison.OrdinalIgnoreCase);
                var s = await Task.Run(() => isKrk
                    ? KurokoInfoAnalyzer.AnalyzeSummary(row.FullPath, _ffprobePath, ct)
                    : DvdIsoAnalyzer.AnalyzeSummary(row.FullPath, null, ct), ct);
                row.Apply(s);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                row.Format = "読取エラー";
                row.VideoCodec = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
            }
            done++;
            Bar.Value = done;
        }
        BarText.Text = $"{files.Count}/{files.Count} 完了";
    }

    // ファイル名の自然順（数値部分を数値として比較："2.iso" < "10.iso"）。大文字小文字は無視。
    private sealed class NaturalNameComparer : IComparer<string?>
    {
        public static readonly NaturalNameComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var nx = x.AsSpan(si, i - si).TrimStart('0');
                    var ny = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (nx.Length != ny.Length) return nx.Length - ny.Length;
                    int c = nx.CompareTo(ny, StringComparison.Ordinal);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (x.Length - i) - (y.Length - j);
        }
    }
}

// 1ファイル分の行（表示は整形済み文字列。処理完了時に埋める）。
public sealed class InfoRow : INotifyPropertyChanged
{
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";

    private string _format = "", _videoCodec = "", _audioCodec = "", _fileSize = "";
    private string _titleCount = "", _titleVideo = "", _titleAudio = "", _titleSub = "";
    private string _menuCount = "", _menuVideo = "", _menuAudio = "", _menuSub = "";

    public string Format { get => _format; set { _format = value; Raise(nameof(Format)); } }
    public string VideoCodec { get => _videoCodec; set { _videoCodec = value; Raise(nameof(VideoCodec)); } }
    public string AudioCodec { get => _audioCodec; set { _audioCodec = value; Raise(nameof(AudioCodec)); } }
    public string FileSize { get => _fileSize; set { _fileSize = value; Raise(nameof(FileSize)); } }
    public string TitleCount { get => _titleCount; set { _titleCount = value; Raise(nameof(TitleCount)); } }
    public string TitleVideo { get => _titleVideo; set { _titleVideo = value; Raise(nameof(TitleVideo)); } }
    public string TitleAudio { get => _titleAudio; set { _titleAudio = value; Raise(nameof(TitleAudio)); } }
    public string TitleSub { get => _titleSub; set { _titleSub = value; Raise(nameof(TitleSub)); } }
    public string MenuCount { get => _menuCount; set { _menuCount = value; Raise(nameof(MenuCount)); } }
    public string MenuVideo { get => _menuVideo; set { _menuVideo = value; Raise(nameof(MenuVideo)); } }
    public string MenuAudio { get => _menuAudio; set { _menuAudio = value; Raise(nameof(MenuAudio)); } }
    public string MenuSub { get => _menuSub; set { _menuSub = value; Raise(nameof(MenuSub)); } }

    internal void Apply(MediaInfoSummary s)
    {
        Format = s.Format;
        VideoCodec = s.VideoCodec;
        AudioCodec = s.AudioCodec;
        FileSize = s.FileSize.ToString("N0");
        TitleCount = s.TitleCount.ToString();
        TitleVideo = s.TitleVideo.ToString("N0");
        TitleAudio = s.TitleAudio.ToString("N0");
        TitleSub = s.TitleSub.ToString("N0");
        MenuCount = s.MenuCount.ToString();
        MenuVideo = s.MenuVideo.ToString("N0");
        MenuAudio = s.MenuAudio.ToString("N0");
        MenuSub = s.MenuSub.ToString("N0");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
