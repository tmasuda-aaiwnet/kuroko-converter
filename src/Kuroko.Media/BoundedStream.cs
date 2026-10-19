using System.IO;

namespace Kuroko.Media.Dvd;

// 基底ストリームの [start, start+length) だけを見せるシーク可能なサブストリーム。
// ISO 内の連続単一エクステント（DVD の VOB 等）を1ファイルとして扱うために使う。
// sharedLock を渡した場合は基底ストリームを共有する前提で読み取りを直列化する
// （専用ハンドルを開ける場合は sharedLock=null で競合しない）。
internal sealed class BoundedStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private readonly object? _sharedLock;
    private long _position;

    public BoundedStream(Stream baseStream, long start, long length, bool leaveOpen, object? sharedLock)
    {
        _base = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        if (!_base.CanRead || !_base.CanSeek)
            throw new ArgumentException("基底ストリームは読み取り・シーク可能が必要です。", nameof(baseStream));
        _start = start;
        _length = length;
        _leaveOpen = leaveOpen;
        _sharedLock = sharedLock;
        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
                throw new IOException("負のシーク位置は無効です。");
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
            return 0;
        long remaining = _length - _position;
        if (count > remaining)
            count = (int)remaining;
        if (count <= 0)
            return 0;

        int read;
        if (_sharedLock is not null)
        {
            lock (_sharedLock)
            {
                _base.Position = _start + _position;
                read = _base.Read(buffer, offset, count);
            }
        }
        else
        {
            _base.Position = _start + _position;
            read = _base.Read(buffer, offset, count);
        }

        if (read > 0)
            _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
            throw new IOException("ストリーム先頭より前へはシークできません。");
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
            _base.Dispose();
        base.Dispose(disposing);
    }
}
