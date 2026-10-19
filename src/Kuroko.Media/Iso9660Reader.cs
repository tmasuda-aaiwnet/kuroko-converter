using System.IO;
using System.Text;

namespace Kuroko.Media.Dvd;

// ISO9660（ECMA-119）の最小リーダ。DVD-Video の ISO から VIDEO_TS/*.IFO,*.VOB を解決するために使う。
//
// 方針（[docs/dvd-iso-engine-spec.md]）：
// - LGPL を保つためのクリーンルーム実装。GPL の libdvdread/libdvdnav 等のコードは参照・流用しない。
//   公開フォーマット（ECMA-119 / ISO9660）の仕様に基づき自前で実装する。
// - 当面は ISO9660 経路のみ（多くの DVD は UDF/ISO9660 ブリッジで ISO9660 からも VIDEO_TS を辿れる）。
//   純 UDF のみのディスクは将来 UDF リーダを追加して対応する。
// - 2048 バイト/論理ブロックの「データのみ」イメージ（一般的な DVD の .iso）を前提とする。
//   生（2352B/セクタ）イメージや多重エクステント/インターリーブは非対応（DVD の VOB は連続単一エクステント）。
internal sealed class Iso9660Reader : IDisposable
{
    private const int DefaultBlockSize = 2048;

    private readonly Stream _image;
    private readonly bool _leaveOpen;
    private readonly string? _imagePath; // 判明していれば OpenRead で別ハンドルを開く（スレッド競合回避）
    private readonly object _readLock = new();

    public int BlockSize { get; }
    public IsoDirectoryEntry Root { get; }

    public Iso9660Reader(string isoPath)
        : this(MediaFile.OpenRead(isoPath), leaveOpen: false, isoPath)
    {
    }

    public Iso9660Reader(Stream image, bool leaveOpen = false, string? imagePath = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        if (!_image.CanRead || !_image.CanSeek)
            throw new ArgumentException("ISO イメージは読み取り・シーク可能なストリームが必要です。", nameof(image));
        _leaveOpen = leaveOpen;
        _imagePath = imagePath;

        // 先頭ボリューム記述子は論理ブロック16から。プライマリ(型1)を探す。
        // ブロックサイズは PVD 内の論理ブロックサイズ（通常2048）を採用する。
        BlockSize = DefaultBlockSize;
        Root = ParsePrimaryVolume();
    }

    private IsoDirectoryEntry ParsePrimaryVolume()
    {
        // 論理ブロック16以降を走査して型1（プライマリ）を探す。型255（終端）か上限で打ち切る。
        for (long lba = 16; lba < 16 + 64; lba++)
        {
            var block = ReadRaw(lba * DefaultBlockSize, DefaultBlockSize);
            byte type = block[0];
            // 標準識別子 "CD001"。
            if (!(block[1] == 'C' && block[2] == 'D' && block[3] == '0' && block[4] == '0' && block[5] == '1'))
            {
                // セクタ16で識別子が無ければ ISO9660 ではない。
                if (lba == 16)
                    throw new InvalidDataException("ISO9660 の標準識別子 'CD001' が見つかりません（対応外のイメージ）。");
                continue;
            }

            if (type == 255) // ボリューム記述子集合終端
                break;
            if (type != 1) // プライマリ以外（補助/ブート等）はスキップ
                continue;

            // PVD：論理ブロックサイズは offset 128（both-endian、LE側を採用）。
            int blockSize = ReadU16Le(block, 128);
            // 通常2048。異常値なら既定にフォールバック。
            int effective = blockSize is 2048 or 1024 or 512 ? blockSize : DefaultBlockSize;
            // ルートディレクトリ記述子は offset 156（34バイト固定）。
            var root = ParseDirectoryRecord(block, 156, out _);
            if (root is null || !root.IsDirectory)
                throw new InvalidDataException("ルートディレクトリ記述子の解析に失敗しました。");

            return new IsoDirectoryEntry("", root.ExtentLba, root.Length, effective);
        }

        throw new InvalidDataException("プライマリボリューム記述子が見つかりません。");
    }

    // 指定ディレクトリ直下のエントリ一覧（"." ".." を除く）。
    public IReadOnlyList<IsoEntry> ListChildren(IsoDirectoryEntry dir)
    {
        var data = ReadRaw(dir.ExtentLba * BlockSize, checked((int)dir.Length));
        var list = new List<IsoEntry>();
        int pos = 0;
        while (pos < data.Length)
        {
            int recLen = data[pos];
            if (recLen == 0)
            {
                // 記述子はブロック境界をまたがない。残りはパディング → 次ブロックへ。
                long next = ((pos / BlockSize) + 1L) * BlockSize;
                if (next <= pos) break;
                pos = (int)next;
                continue;
            }
            if (pos + recLen > data.Length) break;

            var entry = ParseDirectoryRecord(data, pos, out byte fileIdLen);
            // 先頭2件は "."（id長1, 0x00）と ".."（0x01）。それらは除外。
            if (entry is not null && !(fileIdLen == 1 && (entry.RawFirstIdByte == 0x00 || entry.RawFirstIdByte == 0x01)))
                list.Add(entry);

            pos += recLen;
        }
        return list;
    }

    // "/VIDEO_TS" や "/VIDEO_TS/VTS_01_0.IFO" を大文字小文字無視で解決。
    public IsoEntry? FindEntry(string path)
    {
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        IsoEntry current = Root;
        foreach (var part in parts)
        {
            if (current is not IsoDirectoryEntry dir)
                return null;
            IsoEntry? next = null;
            foreach (var child in ListChildren(dir))
            {
                if (string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }
            if (next is null)
                return null;
            current = next;
        }
        return current;
    }

    // 小さなファイル（IFO/BUP 等）を一括読み出し。
    public byte[] ReadAllBytes(IsoFileEntry file)
    {
        return ReadRaw(file.ExtentLba * BlockSize, checked((int)file.Length));
    }

    // 大きなファイル（VOB 等）のシーク可能サブストリーム。
    // パスが判明していれば専用ハンドルを開き、スレッド競合を避ける。
    public Stream OpenRead(IsoFileEntry file)
    {
        long start = file.ExtentLba * BlockSize;
        // 小さなファイル（IFO 等）は別ハンドルを開かず、開いているイメージから読んでメモリに置く（UdfReader.OpenRead と同じ理由：
        // ファイル源（SMB）では開き直しのたびに往復が要る）。
        if (file.Length <= 1 << 20)
        {
            using var shared = new BoundedStream(_image, start, file.Length, leaveOpen: true, sharedLock: _readLock);
            var bytes = new byte[file.Length];
            int got = 0;
            while (got < bytes.Length)
            {
                int n = shared.Read(bytes, got, bytes.Length - got);
                if (n <= 0) break;
                got += n;
            }
            return new MemoryStream(bytes, 0, got, writable: false);
        }
        if (_imagePath is not null)
        {
            var fs = MediaFile.OpenRead(_imagePath);
            return new BoundedStream(fs, start, file.Length, leaveOpen: false, sharedLock: null);
        }
        return new BoundedStream(_image, start, file.Length, leaveOpen: true, sharedLock: _readLock);
    }

    // 記述子1件を解析。fileIdLen は呼び出し側で "."/".." 判定に使う。
    private IsoEntry? ParseDirectoryRecord(byte[] b, int offset, out byte fileIdLen)
    {
        fileIdLen = 0;
        int recLen = b[offset];
        if (recLen < 33)
            return null;

        long extentLba = ReadU32Le(b, offset + 2);
        long dataLen = ReadU32Le(b, offset + 10);
        byte flags = b[offset + 25];
        bool isDir = (flags & 0x02) != 0;
        fileIdLen = b[offset + 32];
        int idStart = offset + 33;

        byte rawFirst = fileIdLen >= 1 ? b[idStart] : (byte)0xFF;

        string name;
        if (fileIdLen == 1 && (rawFirst == 0x00 || rawFirst == 0x01))
        {
            name = rawFirst == 0x00 ? "." : "..";
        }
        else
        {
            // ISO9660 のファイル識別子は ASCII 大文字。ファイルは "NAME.EXT;1"（バージョン付）。
            name = Encoding.ASCII.GetString(b, idStart, fileIdLen);
            int semi = name.IndexOf(';');
            if (semi >= 0)
                name = name.Substring(0, semi);
            // 拡張子の末尾の "." を除去（"NAME." → "NAME"）。
            if (name.EndsWith('.'))
                name = name.Substring(0, name.Length - 1);
        }

        return isDir
            ? new IsoDirectoryEntry(name, extentLba, dataLen, BlockSize) { RawFirstIdByte = rawFirst }
            : new IsoFileEntry(name, extentLba, dataLen) { RawFirstIdByte = rawFirst };
    }

    // 画像内の物理オフセットを判明していれば返す（DVD の VOB アドレス空間ストリームが
    // 専用ハンドルを開くために使う）。Stream 構築の場合は null。
    public string? ImagePath => _imagePath;

    private byte[] ReadRaw(long offset, int count)
    {
        var buf = new byte[count];
        lock (_readLock)
        {
            _image.Position = offset;
            int read = 0;
            while (read < count)
            {
                int n = _image.Read(buf, read, count - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read < count)
                throw new EndOfStreamException($"ISO の読み取りが不足しました（offset={offset}, want={count}, got={read}）。");
        }
        return buf;
    }

    private static uint ReadU32Le(byte[] b, int o)
        => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static int ReadU16Le(byte[] b, int o)
        => b[o] | (b[o + 1] << 8);

    public void Dispose()
    {
        if (!_leaveOpen)
            _image.Dispose();
    }
}

// ISO 内エントリ（ファイル/ディレクトリ共通）。
internal abstract class IsoEntry
{
    protected IsoEntry(string name, long extentLba, long length)
    {
        Name = name;
        ExtentLba = extentLba;
        Length = length;
    }

    public string Name { get; }
    public long ExtentLba { get; }
    public long Length { get; }
    public abstract bool IsDirectory { get; }

    // 解析時の識別子先頭バイト（"."/".." 判定の補助）。
    internal byte RawFirstIdByte { get; set; } = 0xFF;
}

internal sealed class IsoDirectoryEntry : IsoEntry
{
    public IsoDirectoryEntry(string name, long extentLba, long length, int blockSize)
        : base(name, extentLba, length)
    {
        BlockSize = blockSize;
    }

    public int BlockSize { get; }
    public override bool IsDirectory => true;
}

internal sealed class IsoFileEntry : IsoEntry
{
    public IsoFileEntry(string name, long extentLba, long length)
        : base(name, extentLba, length)
    {
    }

    public override bool IsDirectory => false;
}
