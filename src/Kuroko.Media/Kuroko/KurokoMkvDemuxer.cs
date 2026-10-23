using System.IO;

namespace Kuroko.Media.Krk;

// 再生側 demux の抽象（v1＝単一 av.mkv の KurokoMkvDemuxer、v2＝セグメント連結の KurokoPlaylistDemuxer、
// メニュー＝単一 video.mkv）。エンジンはこの I/F 越しにパケットを読む。
internal interface IKurokoDemuxer : IDisposable
{
    bool ReadPacket(out KurokoMkvDemuxer.MkvPacket packet);
    bool SeekMs(int streamIndex, long ms);
    // 直近の下位ストリーム読み取りが例外（接続断＝SMB放置等）で失敗したか。
    // 上位（KurokoPlaybackEngine）は EOF 時にこれを見て「本当の終端」か「断」かを識別し、断なら再オープンする。
    bool LastReadThrew => false;
    // 直近の読みが「**末尾に達していないのに 0 バイト**」だったか。例外を投げない種類の異常（ファイル共有のセッションが
    // 正常終了に見える等）は EOF と区別がつかないので、読み位置がまだ末尾でなければ本物の終端ではないと判定する。
    // 上位は LastReadThrew と同じく再オープンする。
    bool LastReadUnexpectedEof => false;
    // このセグメント（＝プレイリスト連結時の SegmentIndex）が「疎セル＝静止画パーツ」か。上位（KurokoPlaybackEngine）は
    // true のパーツの映像パケットを投入した直後にデコーダを即 Drain し、保持中の1枚を排出する（§8.2）。単一 mkv 等は false。
    bool IsStillSegment(int segmentIndex) => false;
    // このセグメントが fields dual（top/bottom の2映像トラック）パーツか（§8.1）。単一 mkv 等は false。
    bool IsDualSegment(int segmentIndex) => false;
    // このセグメントの**タイトル通し時刻での終端**（＝次セグメントの開始）。最後／単一 mkv は無限大。
    // 上位はこれで境界の音声を時刻に合わせる（宣言尺を超える PCM を捨て、足りなければ無音で詰める）。
    double SegmentEndMs(int segmentIndex) => double.MaxValue;
    // 指定ストリームの AVCodecParameters と同じ形の入れ物（音声・映像のデコーダをコンテナモードで作るため。詳細は実装側の注釈）。
    // 取得できない実装は IntPtr.Zero を返してよい（従来どおり extradata 無しで作られる）。
    IntPtr TryGetCodecPar(int streamIndex) => IntPtr.Zero;
}

// .krk 内 av.mkv を再生するための Matroska デマルチプレクサ。自前の MatroskaReader（Kuroko SDK）で読む。
// パケットの並び・時刻（CodecDelay を引く・レーシングの割り振り）・キー・中身・シーク後の着地は ffmpeg と同じになるよう合わせてある。
// 以前は同梱 LGPL ffmpeg（avformat）に custom AVIO で Stream を食わせる経路も持っていた（環境変数 VUECOVE_MANAGED_DEMUX=0）。
// 再生器に LGPL を同梱しない方針で 2026-10-23 に外した（Vuecove の docs/no-lgpl-playback-plan.md §4.50）。
internal sealed class KurokoMkvDemuxer : IDisposable, IKurokoDemuxer
{
    // 以前は ffmpeg の共有ライブラリを読み込んでいた。今は何もしない（呼び手との互換のため残す）。
    public static bool TryLoad(string? ffmpegDir, out string error)
    {
        error = "";
        return true;
    }

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Kuroko.Matroska.MatroskaReader _mkv;
    private readonly Dictionary<int, IntPtr> _managedPar = new();
    private bool _disposed;

    public KurokoMkvDemuxer(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _mkv = new Kuroko.Matroska.MatroskaReader(stream, leaveOpen: true);
    }

    // 直近の下位読み取りが例外（接続断）で落ちたか。ReadPacket 成功時に false へ戻す。
    public bool LastReadThrew { get; private set; }

    // 直近の下位読み取りが「末尾未到達なのに 0 バイト」だったか（＝本物の終端ではない）。同上。
    public bool LastReadUnexpectedEof { get; private set; }

    // SegmentIndex＝プレイリスト連結時の現セグメント（＝メニューではセル）番号。上位が境界を検出して
    // Opus のギャップレス処理（デコーダリセット＋pre-skip 破棄）を行うのに使う。単一 mkv では常に 0。
    // PtsMs/DtsMs は **ms（小数あり）**。TimestampScale=1μs の krk でサブms を保つため double にする
    // （1ms の旧 krk では従来と同じ整数値になる）。無ければ -1。
    // HasPts/HasDts＝時刻を持っていたか。`PtsMs < 0` で時刻の有無を判定してはいけない：
    // Opus の pre-skip（312サンプル＝6.5ms）でストリーム先頭のパケットは **-6.5ms という実在の時刻**を持つ。
    // 負を「時刻無し」と見なすと、プレイリスト連結でこのパケットだけオフセットを失う（§18）。
    public readonly record struct MkvPacket(int StreamIndex, double PtsMs, double DtsMs, bool KeyFrame, byte[] Data,
        int SegmentIndex = 0, bool HasPts = true, bool HasDts = true);

    // 次のパケットを返す。EOF で false。pts/dts は ms（無ければ -1）。
    public bool ReadPacket(out MkvPacket packet)
    {
        packet = default;
        if (_disposed) return false;
        LastReadThrew = false;
        LastReadUnexpectedEof = false;
        Kuroko.Matroska.MatroskaFrame f;
        try
        {
            if (!_mkv.ReadFrame(out f))
            {
                LastReadUnexpectedEof = _mkv.EndedEarly;
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException or InvalidDataException)
        {
            LastReadThrew = true;
            return false;
        }
        double ms = f.HasTimestamp ? f.Timestamp * (_mkv.TimestampScaleNs / 1e6) : -1;
        // DTS は PTS と同じ（再生器は DTS を使わない。ffmpeg は B フレームのある映像で推定値を入れていた）
        packet = new MkvPacket(f.TrackIndex, ms, ms, f.KeyFrame, f.Data, 0, HasPts: f.HasTimestamp, HasDts: f.HasTimestamp);
        return true;
    }

    // 指定ストリームの AVCodecParameters と同じ形の入れ物を返す（無ければ IntPtr.Zero）。
    // **音声デコーダを「コンテナモード」で作るために要る**：Opus はチャンネル数とチャンネルマッピングが
    // CodecPrivate(OpusHead) にあり、これをデコーダへ転写しないと既定（ステレオ・mapping family 0）
    // に固定され、5.1ch のマルチストリーム Opus を正しく復号できない。
    // **この Demuxer が生きている間だけ有効**（Dispose で解放する）。
    public IntPtr TryGetCodecPar(int streamIndex)
    {
        if (_disposed || streamIndex < 0 || streamIndex >= _mkv.Tracks.Count) return IntPtr.Zero;
        if (!_managedPar.TryGetValue(streamIndex, out var par)) _managedPar[streamIndex] = par = FfCodecPar.Create(_mkv.Tracks[streamIndex]);
        return par;
    }

    // 指定ストリームの ms 位置へシーク（その時刻以前のキーフレームへ）。
    public bool SeekMs(int streamIndex, long ms)
    {
        if (_disposed) return false;
        try { return _mkv.Seek((long)System.Math.Round(ms * 1e6 / _mkv.TimestampScaleNs), streamIndex); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { LastReadThrew = true; return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var par in _managedPar.Values) FfCodecPar.Free(par);
        _managedPar.Clear();
        _mkv.Dispose();
        if (!_leaveOpen) { try { _stream.Dispose(); } catch { } }
    }
}
