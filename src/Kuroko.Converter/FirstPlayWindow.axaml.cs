using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Kuroko.Converter;

// 既存の .krk にファーストプレイ（仕様の disc.json の firstPlayback）を設定する。
//   ①ファイルを指定 → ②タイトル一覧から選ぶ → ③そのタイトルのアングル/音声/字幕を選ぶ → ④krk へ書き込む。
// ComboBox には文字列だけを入れ、番号は控えのリスト（_angles/_audios/_subtitles）で解決する。
public partial class FirstPlayWindow : Window
{
    private KurokoFirstPlayEditor.Document? _doc;
    private List<KurokoFirstPlayEditor.Choice> _angles = new();
    private List<KurokoFirstPlayEditor.Choice> _audios = new();
    private List<KurokoFirstPlayEditor.Choice> _subtitles = new();
    private bool _busy;

    public FirstPlayWindow()
    {
        InitializeComponent();
    }

    // ① ファイルを指定する。
    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = await Dialogs.OpenFile(this, "krk を選ぶ", Dialogs.Type("Kuroko (.krk)", "*.krk"));
        if (path is not null) await LoadAsync(path);
    }

    // ② krk を読み、タイトル一覧を出す（現在のファーストプレイがあれば初期選択に反映）。
    private async Task LoadAsync(string path)
    {
        SetBusy(true, $"[{Path.GetFileName(path)}] を読み込み中…");
        try
        {
            _doc = await Task.Run(() => KurokoFirstPlayEditor.Read(path));
            PathText.Text = path;
            TitleCombo.ItemsSource = _doc.Titles.Select(t => t.Label).ToList();

            if (_doc.Titles.Count == 0)
            {
                CurrentText.Text = "";
                StatusText.Text = "この krk にはタイトルがありません。";
                return;
            }

            var cur = _doc.Current;
            int idx = cur is not null ? _doc.Titles.FindIndex(t => t.Id == cur.Title) : -1;
            TitleCombo.SelectedIndex = idx >= 0 ? idx : 0;   // ここで③のリストが埋まる

            CurrentText.Text = cur is null
                ? "現在の設定：未設定（既定＝タイトル1・アングル1・音声1・字幕なし で開始）"
                : $"現在の設定：タイトル{cur.Title}・アングル{cur.Angle}・音声{cur.Audio}・"
                  + (cur.Subtitle >= 1 ? $"字幕{cur.Subtitle}" : "字幕なし");
            StatusText.Text = $"タイトル {_doc.Titles.Count} 件（controller={_doc.Controller} / version={_doc.Version}）。";
        }
        catch (Exception ex)
        {
            _doc = null;
            StatusText.Text = "読み込みに失敗しました: " + ex.Message;
            await Dialogs.Show(this, "krk を読み込めません", ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    // ③ 選んだタイトルのアングル・音声・字幕を出す。
    private void TitleCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int ti = TitleCombo.SelectedIndex;
        if (_doc is null || ti < 0 || ti >= _doc.Titles.Count) return;
        var t = _doc.Titles[ti];

        _angles = t.Angles.ToList();
        _audios = t.Audio.ToList();
        _subtitles = t.Subtitle.ToList();

        Fill(AngleCombo, _angles);
        Fill(AudioCombo, _audios);
        Fill(SubtitleCombo, _subtitles);

        // 現在のファーストプレイと同じタイトルなら、その値を初期選択にする。違うタイトルなら既定。
        var cur = _doc.Current;
        bool same = cur is not null && cur.Title == t.Id;
        AngleCombo.SelectedIndex = IndexOf(_angles, same ? cur!.Angle : 1, fallback: 0);
        AudioCombo.SelectedIndex = IndexOf(_audios, same ? cur!.Audio : 1, fallback: 0);
        SubtitleCombo.SelectedIndex = IndexOf(_subtitles, same ? cur!.Subtitle : 0, fallback: 0);
    }

    private static void Fill(ComboBox box, List<KurokoFirstPlayEditor.Choice> items)
    {
        box.ItemsSource = items.Select(c => c.Label).ToList();
        box.IsEnabled = items.Count > 1;   // 選択肢が1つ（アングル無し等）なら触らせない
    }

    private static int IndexOf(List<KurokoFirstPlayEditor.Choice> items, int number, int fallback)
    {
        int i = items.FindIndex(c => c.Number == number);
        return i >= 0 ? i : fallback;
    }

    // ④ krk へ書き込む。
    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _doc is null) return;
        int ti = TitleCombo.SelectedIndex;
        if (ti < 0 || ti >= _doc.Titles.Count) return;

        var fp = new KurokoFirstPlayback
        {
            Title = _doc.Titles[ti].Id,
            Angle = Pick(_angles, AngleCombo.SelectedIndex, 1),
            Audio = Pick(_audios, AudioCombo.SelectedIndex, 1),
            Subtitle = Pick(_subtitles, SubtitleCombo.SelectedIndex, 0),
        };
        await WriteAsync(fp,
            $"タイトル{fp.Title}・アングル{fp.Angle}・音声{fp.Audio}・"
            + (fp.Subtitle >= 1 ? $"字幕{fp.Subtitle}" : "字幕なし"));
    }

    // ファーストプレイの記載を消す（既定へ戻す）。
    private async void Clear_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _doc is null) return;
        if (!await Dialogs.Confirm(this, "ファーストプレイを削除", "この krk からファーストプレイの記載を削除します。よろしいですか？"))
            return;
        await WriteAsync(null, "未設定（既定）");
    }

    private async Task WriteAsync(KurokoFirstPlayback? fp, string summary)
    {
        string path = _doc!.Path;
        SetBusy(true, $"[{Path.GetFileName(path)}] を書き込み中…（krk 全体を詰め直します）");
        var progress = new Progress<double>(v => Bar.Value = v);
        try
        {
            string bak = await Task.Run(() => KurokoFirstPlayEditor.Write(path, fp, progress, CancellationToken.None));
            StatusText.Text = $"書き込み完了：{summary}（元ファイルは {Path.GetFileName(bak)} に退避）";
            await LoadAsync(path);   // 書き込んだ内容を読み直して表示を更新する
        }
        catch (Exception ex)
        {
            StatusText.Text = "書き込みに失敗しました: " + ex.Message;
            await Dialogs.Show(this, "ファーストプレイを書き込めません", ex.Message);
        }
        finally
        {
            SetBusy(false, null);
            Bar.Value = 0;
        }
    }

    private static int Pick(List<KurokoFirstPlayEditor.Choice> items, int index, int fallback)
        => index >= 0 && index < items.Count ? items[index].Number : fallback;

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        BrowseButton.IsEnabled = !busy;
        ChoicePanel.IsEnabled = !busy && _doc is { Titles.Count: > 0 };
        SaveButton.IsEnabled = !busy && _doc is { Titles.Count: > 0 };
        ClearButton.IsEnabled = !busy && _doc?.Current is not null;
        CloseButton.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }
}
