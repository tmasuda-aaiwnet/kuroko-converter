using System;
using System.ComponentModel;
using System.IO;

namespace Kuroko.Converter;

public enum JobState { Pending, Running, Done, Failed }

// バッチ変換の1行。グリッドへ表示専用でバインドする（セルは編集不可、表示のみ）。
public sealed class ConvJob : INotifyPropertyChanged
{
    private string _source = "";
    // 変換元（フルパス）。変更すると変換後名（Output）も連動する。
    public string Source
    {
        get => _source;
        set { if (_source == value) return; _source = value; Raise(nameof(Source)); Raise(nameof(Output)); }
    }

    private string _suffix = "";
    // 変換後サフィックス。変換後ファイル名の拡張子(.krk)の直前に挿入する（空可）。UI のテキストで指定。
    public string Suffix
    {
        get => _suffix;
        set { value ??= ""; if (_suffix == value) return; _suffix = value; Raise(nameof(Suffix)); Raise(nameof(Output)); }
    }

    // 変換後ファイル名：変換元と同じフォルダに、変換元ファイル名の拡張子を .krk へ変えたもの。
    // 拡張子(.krk)の直前に「変換後サフィックス」を挿入する（例：movie.iso ＋ "_h265" → movie_h265.krk。空なら movie.krk）。
    public string Output
    {
        get
        {
            try
            {
                var dir = Path.GetDirectoryName(_source) ?? "";
                return Path.Combine(dir, Path.GetFileNameWithoutExtension(_source) + _suffix + ".krk");
            }
            catch { return ""; }
        }
    }

    private JobState _state = JobState.Pending;
    public JobState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State)); Raise(nameof(StateText)); Raise(nameof(CanEdit)); Raise(nameof(CanReset));
        }
    }

    private int _percent;
    // 変換中の進捗(0..100)。状態表示「変換中(xx%)」に反映。
    public int Percent
    {
        get => _percent;
        set { if (_percent == value) return; _percent = value; if (_state == JobState.Running) Raise(nameof(StateText)); }
    }

    public string StateText => _state switch
    {
        JobState.Pending => "開始待ち",
        JobState.Running => $"変換中({_percent}%)",
        JobState.Done => "変換終了",
        JobState.Failed => "変換失敗",
        _ => "",
    };

    private DateTime? _start, _end;
    public DateTime? Start { get => _start; set { _start = value; Raise(nameof(StartText)); Raise(nameof(ElapsedText)); } }
    public DateTime? End { get => _end; set { _end = value; Raise(nameof(EndText)); Raise(nameof(ElapsedText)); } }
    public string StartText => _start?.ToString("HH:mm:ss") ?? "";
    public string EndText => _end?.ToString("HH:mm:ss") ?? "";

    // 処理時間：終了済みなら 終了−開始、処理中なら 現在−開始（タイマで毎秒更新）。未開始は空。
    public string ElapsedText
    {
        get
        {
            if (_start is not { } s) return "";
            var end = _end ?? DateTime.Now;
            var t = end - s;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    // 実行中の行は編集（削除/移動/元変更）不可。完了/失敗は「開始待ちへ戻す」可。
    public bool CanEdit => _state != JobState.Running;
    public bool CanReset => _state is JobState.Done or JobState.Failed;

    // 処理中の経過時間ライブ更新用（UIタイマから呼ぶ）。
    public void Tick() { if (_state == JobState.Running) Raise(nameof(ElapsedText)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
