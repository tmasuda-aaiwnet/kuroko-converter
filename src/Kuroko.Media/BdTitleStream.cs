using System.IO;

namespace Kuroko.Media.Dvd;

// Blu-ray のタイトル（プレイリスト×角度）を **1本の連続した m2ts ストリーム**に見せる。
//
// 内蔵TSエンジンは 192B(BDAV) のストライドを自前で判定できるので、**クリップを順に連結して渡すだけ**で
// 映像・音声（DTS/TrueHD/E-AC-3/LPCM）・PGS 字幕がそのまま再生できる（0.47.0/0.48.0 の対応がそのまま効く）。
//
// 現状の割り切り：PlayItem の IN/OUT でのトリムは行わず**クリップ全体**を連結する。手元の盤では
// IN/OUT がクリップ全体と一致していた（本編前半 2:00＋本編 2:14:10＝プレイリスト尺 2:16:10）。
// 途中を切り出す盤に当たったら、clpi の EP マップで時刻→バイト位置を引いて範囲を絞る必要がある。
internal static class BdTitleStream
{
    // タイトルの実体を開くファクトリを作る。エンジンは再オープン時にも同じ口を使う。
    public static Func<Stream> CreateFactory(string isoPath, BdTitle title)
        => () => Open(isoPath, title);

    // 構成表（連結・チャプター・シーク索引で共有）。解決できなければ null。
    public static BdTitleLayout? BuildLayout(string isoPath, BdTitle title)
    {
        try
        {
            using var udf = new UdfReader(isoPath);
            return BdTitleLayout.Build(udf, title);
        }
        catch { return null; }
    }

    // チャプター（PlayListMark）の位置を **連結後の再生タイムライン上**で求める。
    // `BdPlaylist.Chapters`（PlayItem の尺の累積＝理論値）は境界ごとの隙間ぶんずれるので使わない。
    public static IReadOnlyList<TimeSpan> ComputeChapterTimes(string isoPath, BdTitle title)
    {
        var layout = BuildLayout(isoPath, title);
        if (layout is null) return title.Playlist.Chapters;
        var ch = layout.ChapterTimes(title.Playlist);
        return ch.Count > 0 ? ch : title.Playlist.Chapters;
    }

    // クリップ先頭の PCR（90kHz）。STC を揃えるオフセット計算に使う。見つからなければ -1。
    private static long FirstPcr90k(Stream s)
    {
        const int Unit = 192, Scan = 4 << 20;
        var buf = new byte[Math.Min(Scan, (int)Math.Min(s.Length, int.MaxValue))];
        s.Position = 0;
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) break;
            got += n;
        }
        for (int o = 0; o + Unit <= got; o += Unit)
        {
            int ts = o + 4;
            if (buf[ts] != 0x47) continue;
            int afc = (buf[ts + 3] >> 4) & 0x03;
            if (afc != 2 && afc != 3) continue;
            int afLen = buf[ts + 4];
            if (afLen <= 0 || (buf[ts + 5] & 0x10) == 0 || ts + 11 >= got) continue;
            long b0 = ((long)buf[ts + 6] << 25) | ((long)buf[ts + 7] << 17) | ((long)buf[ts + 8] << 9)
                    | ((long)buf[ts + 9] << 1) | ((long)(buf[ts + 10] >> 7) & 0x01);
            return b0;
        }
        return -1;
    }

    // 指定範囲を走査して PES の PTS の最小/最大（90kHz）を返す。見つからなければ (-1,-1)。
    // **PTS は PCR より先行して入る**ので、クリップを繋ぐ基準は PCR ではなくこちらを使う。
    // PCR で繋ぐと、前クリップ末尾の「未来の PTS」と次クリップ先頭の PTS が重なり、提示側が
    // 過去時刻のフレームを受け取って詰まる（実測：境界の1秒前から瞬停し、やがて提示が止まった）。
    public static (long Min, long Max) ScanPtsRange(Stream s, long start, int bytes)
    {
        const int Unit = 192;
        long from = Math.Max(0, start) / Unit * Unit;
        if (from >= s.Length) return (-1, -1);
        int want = (int)Math.Min(bytes, s.Length - from);
        var buf = new byte[want];
        s.Position = from;
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) break;
            got += n;
        }
        long min = long.MaxValue, max = -1;
        for (int o = 0; o + Unit <= got; o += Unit)
        {
            int ts = o + 4;
            if (buf[ts] != 0x47) continue;
            if ((buf[ts + 1] & 0x40) == 0) continue;          // PES 先頭のみ PTS を持つ
            int afc = (buf[ts + 3] >> 4) & 0x03;
            int payload = afc == 3 ? 5 + buf[ts + 4] : 4;
            if (afc == 0 || afc == 2) continue;
            int p = ts + payload;
            if (p + 14 > o + Unit) continue;
            if (buf[p] != 0 || buf[p + 1] != 0 || buf[p + 2] != 1) continue;
            int sid = buf[p + 3];
            if (sid is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF) continue;
            if (((buf[p + 7] >> 6) & 0x03) == 0) continue;
            int q = p + 9;
            long pts = ((long)(buf[q] & 0x0E) << 29) | ((long)buf[q + 1] << 22)
                     | ((long)(buf[q + 2] & 0xFE) << 14) | ((long)buf[q + 3] << 7)
                     | ((long)(buf[q + 4] & 0xFE) >> 1);
            if (pts < min) min = pts;
            if (pts > max) max = pts;
        }
        return max < 0 ? (-1, -1) : (min, max);
    }

    private static Stream Open(string isoPath, BdTitle title)
    {
        // UdfReader はストリームごとに別ハンドルを開く（OpenRead）。連結ストリームが生きている間は
        // リーダも生かしておく必要があるので、連結側に持たせて一緒に破棄する。
        var udf = new UdfReader(isoPath);
        var owned = new List<IDisposable> { udf };
        try
        {
            var layout = BdTitleLayout.Build(udf, title)
                ?? throw new InvalidOperationException($"BD: {title.DisplayName} の構成を解決できません。");

            // **同じクリップが何度も現れるプレイリストがある**（実測：00720.mpls は 18秒のクリップを 451 個の
            // PlayItem で繰り返す＝2:14:48）。素直に PlayItem ごとに開くとハンドルが 451 本になるので、
            // クリップごとに 1 本だけ開いて共有する。区間は読み取りのたびに位置を設定するので、
            // 同じ実体を複数の位置から使っても問題ない。
            var opened = new Dictionary<string, Stream>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<Stream>(layout.Parts.Count);
            var starts = new long[layout.Parts.Count];
            var offsets = new long[layout.Parts.Count];
            for (int i = 0; i < layout.Parts.Count; i++)
            {
                var p = layout.Parts[i];
                if (!opened.TryGetValue(p.ClipId, out var baseStream))
                {
                    var ent = udf.FindEntry($"BDMV/STREAM/{p.ClipId}.m2ts")
                        ?? throw new FileNotFoundException($"BD: {p.ClipId}.m2ts が見つかりません。");
                    baseStream = udf.OpenRead(ent);
                    opened[p.ClipId] = baseStream;
                    owned.Add(baseStream);
                }
                // クリップ全体を使う区間は実体をそのまま、切り出す区間は範囲ビューを渡す。
                parts.Add(p.ClipByteFrom == 0 && p.Bytes == baseStream.Length
                    ? baseStream
                    : new RangeStream(baseStream, p.ClipByteFrom, p.Bytes));
                starts[i] = p.StreamByteFrom;
                offsets[i] = p.Offset90k;
            }
            return new BdStcRewriteStream(new ConcatStream(parts, owned), starts, offsets);
        }
        catch
        {
            for (int i = owned.Count - 1; i >= 0; i--) { try { owned[i].Dispose(); } catch { } }
            throw;
        }
    }
}

// 別ストリームの一部分だけを見せる読み取り専用ビュー（PlayItem の IN/OUT で切り出す用）。
// 実体は複数のビューで共有されるので、**読み取りのたびに実体の位置を設定**し、破棄もしない。
internal sealed class RangeStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public RangeStream(Stream baseStream, long start, long length)
    {
        _base = baseStream; _start = start;
        _length = Math.Max(0, Math.Min(length, baseStream.Length - start));
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length || count <= 0) return 0;
        int take = (int)Math.Min(count, _length - _position);
        _base.Position = _start + _position;
        int n = _base.Read(buffer, offset, take);
        if (n > 0) _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) => base.Dispose(disposing);   // 実体は所有しない
}

// 複数ストリームを順に連結して1本に見せる（読み取り・シーク可能）。
internal sealed class ConcatStream : Stream
{
    private readonly List<Stream> _parts;
    private readonly long[] _starts;   // 各パートの論理開始位置
    private readonly List<IDisposable> _owned;   // 破棄するのはこちら（パートは実体を共有する）
    private readonly long _length;
    private long _position;

    public ConcatStream(List<Stream> parts, List<IDisposable> owned)
    {
        _parts = parts; _owned = owned;
        _starts = new long[parts.Count];
        long acc = 0;
        for (int i = 0; i < parts.Count; i++) { _starts[i] = acc; acc += parts[i].Length; }
        _length = acc;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length || count <= 0) return 0;
        int i = IndexOf(_position);
        // 位置が変わっていなければ Position 代入も省く（下位が毎回 Seek するのを避ける）。
        if (i < 0) return 0;
        long inPart = _position - _starts[i];
        int take = (int)Math.Min(count, _parts[i].Length - inPart);
        if (take <= 0) return 0;
        _parts[i].Position = inPart;
        int n = _parts[i].Read(buffer, offset, take);
        if (n > 0) _position += n;
        // パート境界ちょうどで 0 が返ったら次のパートの先頭から読む。
        if (n == 0 && i + 1 < _parts.Count) { _position = _starts[i + 1]; return Read(buffer, offset, count); }
        return n;
    }

    private int IndexOf(long pos)
    {
        for (int i = _parts.Count - 1; i >= 0; i--)
            if (pos >= _starts[i]) return i;
        return _parts.Count > 0 ? 0 : -1;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            // パートは実体を共有する（繰り返しプレイリスト／範囲ビュー）ので、所有物だけを逆順に破棄する。
            for (int i = _owned.Count - 1; i >= 0; i--) { try { _owned[i].Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
