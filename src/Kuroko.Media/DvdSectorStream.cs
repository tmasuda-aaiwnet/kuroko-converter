using System.IO;

namespace Kuroko.Media.Dvd;

// タイトルの VOB アドレス空間（連結 VTS_xx_1.VOB..9.VOB）。
// セルの再生セクタ番号はこの空間で表される。各 VOB は ISO 上で別々の連続エクステントに
// 置かれているため、空間セクタ→ISO物理バイトオフセットへ写像する。
internal sealed class DvdVobAddressSpace
{
    private readonly (long SpaceStart, long SectorCount, long IsoLba)[] _vobs;
    public long TotalSectors { get; }

    public DvdVobAddressSpace(IReadOnlyList<IsoFileEntry> orderedTitleVobs)
    {
        var list = new List<(long, long, long)>(orderedTitleVobs.Count);
        long acc = 0;
        foreach (var v in orderedTitleVobs)
        {
            long sectors = v.Length / 2048;
            list.Add((acc, sectors, v.ExtentLba));
            acc += sectors;
        }
        _vobs = list.ToArray();
        TotalSectors = acc;
    }

    // 空間セクタを ISO 物理バイトオフセットへ。contiguousSectors は同一 VOB 内で
    // そこから連続して読めるセクタ数（VOB 境界で頭打ち）。
    public bool TryMap(long spaceSector, out long isoByteOffset, out long contiguousSectors)
    {
        foreach (var v in _vobs)
        {
            if (spaceSector >= v.SpaceStart && spaceSector < v.SpaceStart + v.SectorCount)
            {
                long local = spaceSector - v.SpaceStart;
                isoByteOffset = (v.IsoLba + local) * 2048;
                contiguousSectors = v.SectorCount - local;
                return true;
            }
        }
        isoByteOffset = 0;
        contiguousSectors = 0;
        return false;
    }
}

// セル（VOB アドレス空間のセクタ範囲）の列を、再生順に連結した1本の連続ストリームとして見せる。
// DVD の VOB は MPEG-2 Program Stream なので、このストリームをそのまま PS demux に渡せる。
// NAV パック（private_stream_2）はストリーム中に残るが PS demux 側で無視されるため、
// 基本再生では問題ない（精密シーク/メニューでは別途 NAV を解釈する）。
internal sealed class DvdSectorStream : Stream
{
    private readonly Stream _iso;
    private readonly DvdVobAddressSpace _space;
    private readonly (long First, long Last)[] _ranges; // 包含セクタ範囲（再生順）
    private readonly long[] _cumByteStart;              // 各レンジ先頭の累積バイト位置
    private readonly long _totalBytes;
    private long _position;

    public DvdSectorStream(string isoPath, DvdVobAddressSpace space, IReadOnlyList<(long First, long Last)> ranges)
        : this(MediaFile.OpenRead(isoPath), space, ranges)
    {
    }

    public DvdSectorStream(Stream isoStream, DvdVobAddressSpace space, IReadOnlyList<(long First, long Last)> ranges)
    {
        _iso = isoStream ?? throw new ArgumentNullException(nameof(isoStream));
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _ranges = ranges.ToArray();
        _cumByteStart = new long[_ranges.Length + 1];
        long acc = 0;
        for (int i = 0; i < _ranges.Length; i++)
        {
            _cumByteStart[i] = acc;
            long sectors = _ranges[i].Last - _ranges[i].First + 1;
            if (sectors < 0)
                sectors = 0;
            acc += sectors * 2048;
        }
        _cumByteStart[_ranges.Length] = acc;
        _totalBytes = acc;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _totalBytes;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? 0 : value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _totalBytes || count <= 0)
            return 0;

        if (!Locate(_position, out long isoByteOffset, out long maxContiguous))
            return 0;

        int toRead = (int)Math.Min(count, Math.Min(maxContiguous, _totalBytes - _position));
        if (toRead <= 0)
            return 0;

        _iso.Position = isoByteOffset;
        int read = _iso.Read(buffer, offset, toRead);
        if (read > 0)
            _position += read;
        return read;
    }

    // バイト位置を、ISO 物理バイトオフセットと「そこから連続して読める最大バイト数」へ。
    private bool Locate(long pos, out long isoByteOffset, out long maxContiguous)
    {
        isoByteOffset = 0;
        maxContiguous = 0;

        // pos を含むレンジを二分/線形で特定（レンジ数は通常少数）。
        int r = -1;
        for (int i = 0; i < _ranges.Length; i++)
        {
            if (pos >= _cumByteStart[i] && pos < _cumByteStart[i + 1])
            {
                r = i;
                break;
            }
        }
        if (r < 0)
            return false;

        long offsetInRange = pos - _cumByteStart[r];
        long spaceSector = _ranges[r].First + offsetInRange / 2048;
        long byteInSector = offsetInRange % 2048;

        if (!_space.TryMap(spaceSector, out long secByteOffset, out long contigSectors))
            return false;

        isoByteOffset = secByteOffset + byteInSector;
        long bytesLeftInRange = (_cumByteStart[r + 1] - _cumByteStart[r]) - offsetInRange;
        long bytesLeftInVob = contigSectors * 2048 - byteInSector;
        maxContiguous = Math.Min(bytesLeftInRange, bytesLeftInVob);
        return maxContiguous > 0;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _totalBytes + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        _position = target < 0 ? 0 : target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _iso.Dispose();
        base.Dispose(disposing);
    }
}
