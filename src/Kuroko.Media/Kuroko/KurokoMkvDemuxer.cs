using System.IO;
using System.Runtime.InteropServices;

namespace Kuroko.Media.Krk;

// 再生側 demux の抽象（v1＝単一 av.mkv の KurokoMkvDemuxer、v2＝セグメント連結の KurokoPlaylistDemuxer、
// メニュー＝単一 video.mkv）。エンジンはこの I/F 越しにパケットを読む。
internal interface IKurokoDemuxer : IDisposable
{
    bool ReadPacket(out KurokoMkvDemuxer.MkvPacket packet);
    bool SeekMs(int streamIndex, long ms);
    // 直近の下位ストリーム読み取りが例外（接続断＝SMB放置等）で失敗したか。AVIO は例外を EOF に化かすため、
    // 上位（KurokoPlaybackEngine）は EOF 時にこれを見て「本当の終端」か「断」かを識別し、断なら再オープンする。
    bool LastReadThrew => false;
    // 直近の読みが「**末尾に達していないのに 0 バイト**」だったか。AVIO は下位の失敗を EOF に化かすので、
    // 例外を投げない種類の異常（ファイル共有のセッションが正常終了に見える等）は EOF と区別がつかない。
    // 読み位置がまだ末尾でなければ本物の終端ではないと判定する。上位は LastReadThrew と同じく再オープンする。
    bool LastReadUnexpectedEof => false;
    // このセグメント（＝プレイリスト連結時の SegmentIndex）が「疎セル＝静止画パーツ」か。上位（KurokoPlaybackEngine）は
    // true のパーツの映像パケットを投入した直後にデコーダを即 Drain し、保持中の1枚を排出する（§8.2）。単一 mkv 等は false。
    bool IsStillSegment(int segmentIndex) => false;
    // このセグメントが fields dual（top/bottom の2映像トラック）パーツか（§8.1）。単一 mkv 等は false。
    bool IsDualSegment(int segmentIndex) => false;
    // このセグメントの**タイトル通し時刻での終端**（＝次セグメントの開始）。最後／単一 mkv は無限大。
    // 上位はこれで境界の音声を時刻に合わせる（宣言尺を超える PCM を捨て、足りなければ無音で詰める）。
    double SegmentEndMs(int segmentIndex) => double.MaxValue;
    // 指定ストリームの AVCodecParameters（音声デコーダをコンテナモードで作るため。詳細は実装側の注釈）。
    // 取得できない実装は IntPtr.Zero を返してよい（従来どおり extradata 無しで作られる）。
    IntPtr TryGetCodecPar(int streamIndex) => IntPtr.Zero;
}

// .krk 内 av.mkv を再生するための Matroska デマルチプレクサ。
// **既定は自前の MatroskaReader（Kuroko SDK）**：ffmpeg を使わない（再生器に LGPL を同梱しない方針）。パケットの並び・時刻
//（CodecDelay を引く・レーシングの割り振り）・キー・中身・シーク後の着地は ffmpeg と同じになるよう合わせてある。
// 環境変数 VUECOVE_MANAGED_DEMUX=0 で従来の ffmpeg（下の説明）。
// 従来：同梱 LGPL ffmpeg（avformat）に
// custom AVIO で任意の Stream（Iso9660Reader.OpenRead の BoundedStream 等）を食わせ、
// av_read_frame でパケット列（映像h264/音声ac3/字幕vobsub）を取り出す。
//
// 設計上の単純化（堅牢性のため構造体オフセット依存を最小化）：
//  ・トラック→役割の対応は title.json（MkvTrack）が持つので、ここでは stream_index をそのまま返す
//    （AVStream/AVCodecParameters を覗かない）。
//  ・Matroska の AVStream.time_base は既定 1/1000（ms）。Kuroko の av.mkv は既定 TimecodeScale で
//    オーサリングするので、pts/dts は ms として扱う。
//  ・依存オフセットは AVPacket の各フィールド（版間で安定）と AVFormatContext->pb(=32) のみ。
internal sealed class KurokoMkvDemuxer : IDisposable, IKurokoDemuxer
{
    // ── ネイティブ関数ロード（avutil/avcodec/avformat） ────────────────────
    private static readonly object SyncRoot = new();
    private static bool _loaded;
    private static string? _loadError;

    private static D_av_malloc _avMalloc = null!;
    private static D_av_freep _avFreep = null!;
    private static D_av_packet_alloc _packetAlloc = null!;
    private static D_av_packet_unref _packetUnref = null!;
    private static D_av_packet_free _packetFree = null!;
    private static D_avformat_alloc_context _allocCtx = null!;
    private static D_avformat_open_input _openInput = null!;
    private static D_avformat_close_input _closeInput = null!;
    private static D_avformat_find_stream_info _findStreamInfo = null!;
    private static D_av_read_frame _readFrame = null!;
    private static D_av_seek_frame _seekFrame = null!;
    private static D_avio_alloc_context _avioAlloc = null!;
    private static D_avio_context_free _avioFree = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr D_av_malloc(UIntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void D_av_freep(IntPtr ptrToPtr);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr D_av_packet_alloc();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void D_av_packet_unref(IntPtr pkt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void D_av_packet_free(ref IntPtr pkt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr D_avformat_alloc_context();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_avformat_open_input(ref IntPtr ps, IntPtr url, IntPtr fmt, IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void D_avformat_close_input(ref IntPtr ps);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_avformat_find_stream_info(IntPtr ic, IntPtr options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_av_read_frame(IntPtr ic, IntPtr pkt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int D_av_seek_frame(IntPtr ic, int streamIndex, long timestamp, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr D_avio_alloc_context(IntPtr buffer, int bufSize, int writeFlag, IntPtr opaque, ReadPacketCb read, IntPtr write, SeekCb seek);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void D_avio_context_free(ref IntPtr s);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadPacketCb(IntPtr opaque, IntPtr buf, int bufSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long SeekCb(IntPtr opaque, long offset, int whence);

    // 自前の MatroskaReader を使うか（既定）
    public static readonly bool UseManaged = Environment.GetEnvironmentVariable("VUECOVE_MANAGED_DEMUX") != "0";

    public static bool TryLoad(string? ffmpegDir, out string error)
    {
        if (UseManaged) { error = ""; return true; }
        lock (SyncRoot)
        {
            if (_loaded) { error = ""; return true; }
            if (_loadError is not null) { error = _loadError; return false; }
            try
            {
                // 共有ライブラリの名前と置き場所はプラットフォームごとに違う（FfmpegLibraryLocator）。
                IntPtr avutil = Load(ffmpegDir, "avutil");
                IntPtr avcodec = Load(ffmpegDir, "avcodec");
                IntPtr avformat = Load(ffmpegDir, "avformat");

                _avMalloc = Get<D_av_malloc>(avutil, "av_malloc");
                _avFreep = Get<D_av_freep>(avutil, "av_freep");
                _packetAlloc = Get<D_av_packet_alloc>(avcodec, "av_packet_alloc");
                _packetUnref = Get<D_av_packet_unref>(avcodec, "av_packet_unref");
                _packetFree = Get<D_av_packet_free>(avcodec, "av_packet_free");
                _allocCtx = Get<D_avformat_alloc_context>(avformat, "avformat_alloc_context");
                _openInput = Get<D_avformat_open_input>(avformat, "avformat_open_input");
                _closeInput = Get<D_avformat_close_input>(avformat, "avformat_close_input");
                _findStreamInfo = Get<D_avformat_find_stream_info>(avformat, "avformat_find_stream_info");
                _readFrame = Get<D_av_read_frame>(avformat, "av_read_frame");
                _seekFrame = Get<D_av_seek_frame>(avformat, "av_seek_frame");
                _avioAlloc = Get<D_avio_alloc_context>(avformat, "avio_alloc_context");
                _avioFree = Get<D_avio_context_free>(avformat, "avio_context_free");

                _loaded = true;
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                _loadError = ex.GetType().Name + ": " + ex.Message;
                error = _loadError;
                return false;
            }
        }
    }

    private static IntPtr Load(string? extraDirectory, string library)
    {
        var path = FfmpegLibraryLocator.Resolve(library, extraDirectory)
            ?? throw new FileNotFoundException(
                $"ffmpeg の共有ライブラリが見つかりません: {library}"
                + $"（{FfmpegLibraryLocator.SearchDescription}）");
        return NativeLibrary.Load(path);
    }

    private static T Get<T>(IntPtr lib, string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    // ── インスタンス ───────────────────────────────────────────────────
    private const int IoBufferSize = 1 << 16;
    private const int AvErrorEof = -541478725;     // AVERROR_EOF = -MKTAG('E','O','F',' ')
    private const int AvSeekFlagBackward = 1;
    private const int AvSeekSize = 0x10000;
    private const int AvPktFlagKey = 1;
    private const long AvNoPts = long.MinValue;
    private const int OffPktPts = 8, OffPktDts = 16, OffPktData = 24, OffPktSize = 32, OffPktStreamIndex = 36, OffPktFlags = 40;
    private const int OffFmtPb = 32;               // AVFormatContext->pb（x64）
    private const int OffAvioBuffer = 8;           // AVIOContext->buffer（x64）
    // TryGetCodecPar 用（FFmpeg 8.1.1・x64。NativeAvformatDemuxer と同じ値）。
    private const int OffFmtNbStreams = 44;        // AVFormatContext.nb_streams (int)
    private const int OffFmtStreams = 48;          // AVFormatContext.streams (AVStream**)
    private const int OffStreamCodecpar = 16;      // AVStream.codecpar (AVCodecParameters*)
    private const int OffStreamTimeBase = 32;      // AVStream.time_base (AVRational: num@32, den@36)

    // ストリーム別の time_base（AVStream 由来）。**Matroska の TimestampScale は既定 1ms だが 1μs 等にもできる**
    // ので、ms 固定で解釈すると 1000 倍ずれる。開いた実体から読んで換算する（1ms でも 1μs でも正しく動く）。
    private readonly Dictionary<int, (int Num, int Den)> _timeBase = new();
    private (int Num, int Den) _seekTimeBase = (1, 1000);   // シーク時刻の換算に使う（映像＝stream 0 想定）

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly byte[] _scratch = new byte[IoBufferSize];
    private readonly ReadPacketCb _readCb;          // GC で回収されないよう保持
    private readonly SeekCb _seekCb;
    private IntPtr _fmtCtx;
    private IntPtr _avioCtx;
    private IntPtr _packet;
    private bool _disposed;

    // 自前の読み出し（UseManaged のとき）
    private readonly Kuroko.Matroska.MatroskaReader? _mkv;
    private readonly Dictionary<int, IntPtr> _managedPar = new();

    public KurokoMkvDemuxer(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _readCb = ReadPacketImpl;
        _seekCb = SeekImpl;
        if (UseManaged)
        {
            _mkv = new Kuroko.Matroska.MatroskaReader(stream, leaveOpen: true);
            return;
        }
        if (!_loaded) throw new InvalidOperationException("KurokoMkvDemuxer.TryLoad() が先に必要です。");
        IntPtr buffer = _avMalloc((UIntPtr)IoBufferSize);
        if (buffer == IntPtr.Zero) throw new OutOfMemoryException("av_malloc 失敗");
        _avioCtx = _avioAlloc(buffer, IoBufferSize, 0, IntPtr.Zero, _readCb, IntPtr.Zero, _seekCb);
        if (_avioCtx == IntPtr.Zero) throw new InvalidOperationException("avio_alloc_context 失敗");

        _fmtCtx = _allocCtx();
        if (_fmtCtx == IntPtr.Zero) throw new InvalidOperationException("avformat_alloc_context 失敗");
        Marshal.WriteIntPtr(_fmtCtx, OffFmtPb, _avioCtx); // fmtCtx->pb = avioCtx（CUSTOM_IO は open 時に自動セット）

        int rc = _openInput(ref _fmtCtx, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (rc < 0) throw new InvalidOperationException($"avformat_open_input 失敗 rc={rc}");
        rc = _findStreamInfo(_fmtCtx, IntPtr.Zero);
        if (rc < 0) throw new InvalidOperationException($"avformat_find_stream_info 失敗 rc={rc}");
        CacheTimeBases();

        _packet = _packetAlloc();
        if (_packet == IntPtr.Zero) throw new InvalidOperationException("av_packet_alloc 失敗");
    }

    // 直近の下位読み取りが例外（接続断）で落ちたか。ReadPacket 成功時に false へ戻す。
    public bool LastReadThrew { get; private set; }

    // 直近の下位読み取りが「末尾未到達なのに 0 バイト」だったか（＝本物の終端ではない）。同上。
    public bool LastReadUnexpectedEof { get; private set; }

    private int ReadPacketImpl(IntPtr opaque, IntPtr buf, int bufSize)
    {
        try
        {
            int n = _stream.Read(_scratch, 0, Math.Min(bufSize, _scratch.Length));
            if (n <= 0)
            {
                // **本物の終端かを読み位置で裏取りする**。末尾に達していないのに 0 バイトなら、下位で何かが
                // 起きている（例外を投げない切断・共有の一時的な失敗など）。EOF として扱うと再生途中で
                // 終端＝次タイトルへ進んでしまうので、上位へ「終端ではない」と伝える。
                try { if (_stream.CanSeek && _stream.Position < _stream.Length) LastReadUnexpectedEof = true; }
                catch { }
                return AvErrorEof;
            }
            Marshal.Copy(_scratch, 0, buf, n);
            return n;
        }
        catch { LastReadThrew = true; return AvErrorEof; }
    }

    private long SeekImpl(IntPtr opaque, long offset, int whence)
    {
        try
        {
            if ((whence & AvSeekSize) != 0) return _stream.Length;
            var origin = (whence & 0xFFFF) switch { 0 => SeekOrigin.Begin, 1 => SeekOrigin.Current, 2 => SeekOrigin.End, _ => SeekOrigin.Begin };
            return _stream.Seek(offset, origin);
        }
        catch { return -1; }
    }

    // SegmentIndex＝プレイリスト連結時の現セグメント（＝メニューではセル）番号。上位が境界を検出して
    // Opus のギャップレス処理（デコーダリセット＋pre-skip 破棄）を行うのに使う。単一 mkv では常に 0。
    // PtsMs/DtsMs は **ms（小数あり）**。TimestampScale=1μs の krk でサブms を保つため double にする
    // （1ms の旧 krk では従来と同じ整数値になる）。無ければ -1。
    // HasPts/HasDts＝**AV_NOPTS_VALUE ではなかった**か。`PtsMs < 0` で時刻の有無を判定してはいけない：
    // Opus の pre-skip（312サンプル＝6.5ms）でストリーム先頭のパケットは **-6.5ms という実在の時刻**を持つ。
    // 負を「時刻無し」と見なすと、プレイリスト連結でこのパケットだけオフセットを失う（§18）。
    public readonly record struct MkvPacket(int StreamIndex, double PtsMs, double DtsMs, bool KeyFrame, byte[] Data,
        int SegmentIndex = 0, bool HasPts = true, bool HasDts = true);

    // 次のパケットを返す。EOF で false。pts/dts は ms（無ければ -1）。
    public bool ReadPacket(out MkvPacket packet)
    {
        packet = default;
        if (_disposed) return false;
        if (_mkv is not null) return ReadPacketManaged(out packet);
        LastReadThrew = false;        // この av_read_frame 中に下位読み取りが例外落ちしたかを測る
        LastReadUnexpectedEof = false; // 同上（末尾未到達の 0 バイト）
        int rc = _readFrame(_fmtCtx, _packet);
        if (rc < 0) return false;
        try
        {
            long pts = Marshal.ReadInt64(_packet, OffPktPts);
            long dts = Marshal.ReadInt64(_packet, OffPktDts);
            IntPtr data = Marshal.ReadIntPtr(_packet, OffPktData);
            int size = Marshal.ReadInt32(_packet, OffPktSize);
            int idx = Marshal.ReadInt32(_packet, OffPktStreamIndex);
            int flags = Marshal.ReadInt32(_packet, OffPktFlags);
            var bytes = new byte[Math.Max(0, size)];
            if (size > 0 && data != IntPtr.Zero) Marshal.Copy(data, bytes, 0, size);
            packet = new MkvPacket(idx,
                pts == AvNoPts ? -1 : ToMs(pts, idx), dts == AvNoPts ? -1 : ToMs(dts, idx),
                (flags & AvPktFlagKey) != 0, bytes, 0,
                HasPts: pts != AvNoPts, HasDts: dts != AvNoPts);
            return true;
        }
        finally { _packetUnref(_packet); }
    }

    private bool ReadPacketManaged(out MkvPacket packet)
    {
        packet = default;
        LastReadThrew = false;
        LastReadUnexpectedEof = false;
        Kuroko.Matroska.MatroskaFrame f;
        try
        {
            if (!_mkv!.ReadFrame(out f))
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

    // AVStream.time_base を全ストリーム分控える。取れなければ 1/1000（従来の既定）のまま。
    private void CacheTimeBases()
    {
        try
        {
            int n = Marshal.ReadInt32(_fmtCtx, OffFmtNbStreams);
            var streams = Marshal.ReadIntPtr(_fmtCtx, OffFmtStreams);
            if (streams == IntPtr.Zero) return;
            for (int i = 0; i < n; i++)
            {
                var st = Marshal.ReadIntPtr(streams, i * IntPtr.Size);
                if (st == IntPtr.Zero) continue;
                int num = Marshal.ReadInt32(st, OffStreamTimeBase);
                int den = Marshal.ReadInt32(st, OffStreamTimeBase + 4);
                if (num <= 0 || den <= 0) continue;
                _timeBase[i] = (num, den);
                if (i == 0) _seekTimeBase = (num, den);
            }
        }
        catch { /* 取れなければ 1/1000 想定のまま（旧 krk と同じ振る舞い） */ }
    }

    // ストリーム単位の時刻 → ms（小数あり）。TimestampScale=1μs のときのサブms を落とさない。
    private double ToMs(long v, int streamIndex)
    {
        var tb = _timeBase.TryGetValue(streamIndex, out var t) ? t : (Num: 1, Den: 1000);
        return v * 1000.0 * tb.Num / tb.Den;
    }

    // 指定ストリームの AVCodecParameters を返す（無ければ IntPtr.Zero）。
    // **音声デコーダを「コンテナモード」で作るために要る**：Opus はチャンネル数とチャンネルマッピングが
    // CodecPrivate(OpusHead) にあり、これをデコーダへ転写しないと ffmpeg 既定（ステレオ・mapping family 0）
    // に固定され、5.1ch のマルチストリーム Opus を正しく復号できない（実測：当方の復号だけ約21dB 大きく
    // 振り切れ、ffmpeg CLI の結果と全く合わなかった）。
    // 返すのは avformat が所有するポインタなので、**この Demuxer が生きている間だけ有効**。
    public IntPtr TryGetCodecPar(int streamIndex)
    {
        if (_mkv is not null)
        {
            if (_disposed || streamIndex < 0 || streamIndex >= _mkv.Tracks.Count) return IntPtr.Zero;
            if (!_managedPar.TryGetValue(streamIndex, out var par)) _managedPar[streamIndex] = par = FfCodecPar.Create(_mkv.Tracks[streamIndex]);
            return par;
        }
        if (_disposed || _fmtCtx == IntPtr.Zero || streamIndex < 0) return IntPtr.Zero;
        try
        {
            int n = Marshal.ReadInt32(_fmtCtx, OffFmtNbStreams);
            if (streamIndex >= n) return IntPtr.Zero;
            var streams = Marshal.ReadIntPtr(_fmtCtx, OffFmtStreams);
            if (streams == IntPtr.Zero) return IntPtr.Zero;
            var st = Marshal.ReadIntPtr(streams, streamIndex * IntPtr.Size);
            return st == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(st, OffStreamCodecpar);
        }
        catch { return IntPtr.Zero; }
    }

    // 指定ストリームの ms 位置へシーク（その時刻以前のキーフレームへ）。
    public bool SeekMs(int streamIndex, long ms)
    {
        if (_disposed) return false;
        if (_mkv is not null)
        {
            try { return _mkv.Seek((long)System.Math.Round(ms * 1e6 / _mkv.TimestampScaleNs), streamIndex); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { LastReadThrew = true; return false; }
        }
        // av_seek_frame の時刻は**そのストリームの time_base 単位**。1ms 固定で渡すと 1μs スケールの krk で
        // 1000 分の 1 の位置へ飛ぶ（＝常に先頭付近）。実体の time_base で換算する。
        var tb = _timeBase.TryGetValue(streamIndex, out var t) ? t : _seekTimeBase;
        long ts = (long)System.Math.Round(ms * (double)tb.Den / (tb.Num * 1000.0));
        return _seekFrame(_fmtCtx, streamIndex, ts, AvSeekFlagBackward) >= 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mkv is not null)
        {
            foreach (var par in _managedPar.Values) FfCodecPar.Free(par);
            _managedPar.Clear();
            _mkv.Dispose();
            if (!_leaveOpen) { try { _stream.Dispose(); } catch { } }
            return;
        }
        if (_packet != IntPtr.Zero) { var p = _packet; _packetFree(ref p); _packet = IntPtr.Zero; }
        if (_fmtCtx != IntPtr.Zero) { var c = _fmtCtx; _closeInput(ref c); _fmtCtx = IntPtr.Zero; }
        if (_avioCtx != IntPtr.Zero)
        {
            // avio が再確保している可能性があるので現在の buffer を読んで解放してから context を解放。
            IntPtr bufPtrField = _avioCtx + OffAvioBuffer;
            _avFreep(bufPtrField);
            var a = _avioCtx; _avioFree(ref a); _avioCtx = IntPtr.Zero;
        }
        if (!_leaveOpen) { try { _stream.Dispose(); } catch { } }
    }
}
