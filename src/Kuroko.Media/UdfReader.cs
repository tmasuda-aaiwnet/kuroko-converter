using System.IO;
using System.Text;

namespace Kuroko.Media.Dvd;

// UDF（ECMA-167 / OSTA UDF 2.50）の読み取り専用リーダ。Blu-ray（BDMV）の ISO から
// BDMV/*.mpls, CLIPINF/*.clpi, STREAM/*.m2ts を解決するために使う。
//
// 方針（[docs/dvd-iso-engine-spec.md]）：
// - LGPL を保つためのクリーンルーム実装。GPL/LGPL の libudfread・libbluray 等のコードは参照・流用しない。
//   公開仕様（ECMA-167 第3版 / OSTA UDF 2.50）に基づき自前で実装する。
// - **DVD-Video の ISO は UDF1.02＋ISO9660 ブリッジ**で [Iso9660Reader](Iso9660Reader.cs) から辿れるが、
//   **Blu-ray は純 UDF（ISO9660 を持たない）**ため、こちらが要る。実測：BD の ISO は
//   ボリューム認識シーケンスが BEA01/NSR03/TEA01 のみで、ISO9660 の PVD が無い。
// - 2048 バイト/セクタの「データのみ」イメージ前提（DVD/BD の .iso）。
//
// 対応範囲（BD の ISO を読むのに要る分だけ）：
//  ・AVDP(256 / N-256 / N) → 主ボリューム記述子列 → PD（パーティション）/ LVD（論理ボリューム）
//  ・パーティションマップ 種別1（物理）と 種別2 の **メタデータパーティション**（UDF2.50 の BD が使う）
//  ・FSD（ファイルセット）→ ルートICB → FE/EFE（ファイルエントリ）→ FID（ディレクトリ項目）
//  ・割当記述子 short_ad / long_ad と **埋め込みデータ**（ICB flags の下位3bit）
//  ・多エクステントのファイル（論理オフセット→物理位置の写像つきストリーム）
// 非対応：書き込み、Virtual/Sparable パーティション（書換型メディア用。ROM には無い）、
//         名前の 16bit 圧縮以外の特殊な符号化、ICB の階層戦略（実運用は戦略4＝単一FE）。
internal sealed class UdfReader : IDisposable
{
    private const int Sector = 2048;
    private const long SmallFileBytes = 1 << 20;   // これ以下のファイルは開き直さずにメモリへ読む（OpenRead）

    // 記述子タグ識別子（ECMA-167 3/7.2.1、4/7.2.1）。
    private const int TagPrimaryVolumeDescriptor = 1;
    private const int TagAnchorVolumeDescriptorPointer = 2;
    private const int TagPartitionDescriptor = 5;
    private const int TagLogicalVolumeDescriptor = 6;
    private const int TagTerminatingDescriptor = 8;
    private const int TagFileSetDescriptor = 256;
    private const int TagFileIdentifierDescriptor = 257;
    private const int TagFileEntry = 261;
    private const int TagExtendedFileEntry = 266;

    private readonly Stream _image;
    private readonly bool _leaveOpen;
    private readonly string? _imagePath;
    private readonly object _readLock = new();

    // パーティションマップ（LVD の並び順＝long_ad の partition reference number が指す添字）。
    private readonly List<PartitionMap> _maps = new();
    private uint _logicalBlockSize = Sector;

    public UdfEntry Root { get; }
    public string? ImagePath => _imagePath;

    public UdfReader(string isoPath)
        : this(MediaFile.OpenRead(isoPath), leaveOpen: false, isoPath)
    {
    }

    public UdfReader(Stream image, bool leaveOpen = false, string? imagePath = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        if (!_image.CanRead || !_image.CanSeek)
            throw new ArgumentException("ISO イメージは読み取り・シーク可能なストリームが必要です。", nameof(image));
        _leaveOpen = leaveOpen;
        _imagePath = imagePath;
        try
        {
            Root = Mount();
        }
        catch
        {
            if (!_leaveOpen) _image.Dispose();
            throw;
        }
    }

    // この ISO が UDF として読めるか（読めなければ false。DVD 用の ISO9660 経路へ落とす判定に使う）。
    public static bool LooksLikeUdf(Stream image)
    {
        try
        {
            // ボリューム認識シーケンス（ECMA-167 2/9）：セクタ16から NSR02/NSR03 を探す。
            var buf = new byte[Sector];
            for (int s = 16; s < 32; s++)
            {
                image.Seek((long)s * Sector, SeekOrigin.Begin);
                if (image.Read(buf, 0, Sector) != Sector) return false;
                var id = Encoding.ASCII.GetString(buf, 1, 5);
                if (id == "NSR02" || id == "NSR03") return true;
                if (id == "TEA01") return false;
            }
        }
        catch { }
        return false;
    }

    // ── マウント（アンカー → 記述子列 → ファイルセット → ルート） ─────────────────

    private UdfEntry Mount()
    {
        long imageSectors = _image.Length / Sector;
        // アンカーは 256 / N-256 / N のいずれか（ECMA-167 3/8.4.2.1）。ROM は 256 に必ずある。
        long[] anchors = { 256, imageSectors - 256, imageSectors - 1 };
        byte[]? avdp = null;
        foreach (var a in anchors)
        {
            if (a < 0) continue;
            var b = ReadSector(a);
            if (b is not null && TagId(b) == TagAnchorVolumeDescriptorPointer) { avdp = b; break; }
        }
        if (avdp is null) throw new InvalidDataException("UDF: アンカー記述子(AVDP)が見つかりません。");

        // AVDP: 主ボリューム記述子列の extent（長さ4B＋位置4B）。
        uint mvdsLength = Le32(avdp, 16);
        uint mvdsLocation = Le32(avdp, 20);
        ReadVolumeDescriptorSequence(mvdsLocation, mvdsLength);

        if (_maps.Count == 0) throw new InvalidDataException("UDF: パーティションマップがありません。");
        if (_fsdLocation is null) throw new InvalidDataException("UDF: ファイルセット記述子の位置が不明です。");

        // FSD（ECMA-167 4/14.1）→ ルートディレクトリの ICB（byte 400 から long_ad）。
        var fsd = ReadLogicalBlock(_fsdLocation.Value.Partition, _fsdLocation.Value.Block)
                  ?? throw new InvalidDataException("UDF: ファイルセット記述子を読めません。");
        if (TagId(fsd) != TagFileSetDescriptor)
            throw new InvalidDataException($"UDF: ファイルセット記述子のタグが不正です（{TagId(fsd)}）。");
        var rootIcb = ReadLongAd(fsd, 400);
        return new UdfEntry("", isDirectory: true, length: 0, icb: rootIcb);
    }

    private (ushort Partition, uint Block)? _fsdLocation;

    private void ReadVolumeDescriptorSequence(uint location, uint lengthBytes)
    {
        // 種別2 マップは PD より先に現れ得るので、まず全記述子を集めてから解決する。
        var partitionDescriptors = new Dictionary<ushort, uint>(); // パーティション番号 → 開始物理ブロック
        byte[]? lvd = null;
        uint sectors = Math.Max(1u, lengthBytes / Sector);
        for (uint i = 0; i < sectors; i++)
        {
            var b = ReadSector(location + i);
            if (b is null) break;
            int tag = TagId(b);
            if (tag == TagTerminatingDescriptor || tag == 0) break;
            if (tag == TagPartitionDescriptor)
            {
                ushort num = Le16(b, 22);
                uint start = Le32(b, 188);
                partitionDescriptors[num] = start;
            }
            else if (tag == TagLogicalVolumeDescriptor)
            {
                lvd = b; // 最後に現れたものを採用（通常1つ）
            }
            else if (tag == TagPrimaryVolumeDescriptor)
            {
                // 使わないが、記述子列の妥当性確認のため読み飛ばす。
            }
        }
        if (lvd is null) throw new InvalidDataException("UDF: 論理ボリューム記述子(LVD)がありません。");

        _logicalBlockSize = Le32(lvd, 212);
        if (_logicalBlockSize == 0 || _logicalBlockSize > 1 << 20) _logicalBlockSize = Sector;
        // LVD byte 248: ファイルセット記述子の位置（long_ad）
        var fsdAd = ReadLongAd(lvd, 248);
        _fsdLocation = (fsdAd.Partition, fsdAd.Block);

        // パーティションマップ（LVD byte 440 から。個数 byte 268、総バイト長 byte 264）。
        uint mapCount = Le32(lvd, 268);
        int off = 440;
        var pending = new List<(int MapIndex, uint MetadataFileLbn, ushort PartitionNumber)>();
        for (uint i = 0; i < mapCount && off + 2 <= lvd.Length; i++)
        {
            byte type = lvd[off];
            byte len = lvd[off + 1];
            if (len == 0) break;
            if (type == 1)
            {
                // 種別1（ECMA-167 3/10.7.2）：ボリューム列番号(2)＋パーティション番号(2)
                ushort partNum = Le16(lvd, off + 4);
                _maps.Add(PartitionMap.Physical(partNum));
            }
            else if (type == 2)
            {
                // 種別2（3/10.7.3）：識別子 32B の中に "*UDF Metadata Partition" 等が入る。
                string ident = ReadEntityId(lvd, off + 4);
                ushort partNum = Le16(lvd, off + 38);
                if (ident.Contains("Metadata Partition", StringComparison.Ordinal))
                {
                    // UDF2.50 2.2.10：メタデータファイルの位置（種別2マップ内 byte 40 から）
                    uint metaFileLbn = Le32(lvd, off + 40);
                    _maps.Add(PartitionMap.MetadataPlaceholder(partNum));
                    pending.Add((_maps.Count - 1, metaFileLbn, partNum));
                }
                else
                {
                    // Virtual/Sparable は ROM には無い。物理として扱って先へ進む（読めなければ後段で失敗する）。
                    _maps.Add(PartitionMap.Physical(partNum));
                }
            }
            off += len;
        }

        // 物理パーティションの開始ブロックを埋める。
        for (int i = 0; i < _maps.Count; i++)
        {
            if (partitionDescriptors.TryGetValue(_maps[i].PartitionNumber, out var start))
                _maps[i].PartitionStart = start;
        }

        // メタデータパーティションの実体（メタデータファイルのエクステント）を解決する。
        // メタデータファイル自身の FE は**物理パーティション上**にあるので、先に物理側を確定しておく必要がある。
        foreach (var (mapIndex, metaLbn, partNum) in pending)
        {
            uint physStart = partitionDescriptors.TryGetValue(partNum, out var s) ? s : 0;
            var fe = ReadSector(physStart + metaLbn);
            if (fe is null || (TagId(fe) != TagFileEntry && TagId(fe) != TagExtendedFileEntry))
                throw new InvalidDataException("UDF: メタデータファイルのファイルエントリを読めません。");
            var extents = ParseAllocation(fe, defaultPartition: (ushort)mapIndex, out _, physicalPartitionHint: partNum);
            // 物理ブロック列へ写す（メタデータファイルのエクステントは物理パーティション基準）。
            var phys = new List<(long Start, long Blocks)>();
            foreach (var e in extents)
                phys.Add((physStart + e.Block, (e.Length + _logicalBlockSize - 1) / _logicalBlockSize));
            _maps[mapIndex].MetadataExtents = phys;
        }
    }

    // ── アドレス解決 ─────────────────────────────────────────────

    // 論理ブロック（パーティション参照番号＋ブロック番号）→ 物理セクタ番号。
    private long ToPhysicalBlock(ushort partitionRef, uint block)
    {
        if (partitionRef >= _maps.Count) throw new InvalidDataException($"UDF: パーティション参照 {partitionRef} が範囲外です。");
        var map = _maps[partitionRef];
        if (map.MetadataExtents is null) return map.PartitionStart + block;

        // メタデータパーティション：ブロック番号は**メタデータファイル内**の位置。
        long remain = block;
        foreach (var (start, blocks) in map.MetadataExtents)
        {
            if (remain < blocks) return start + remain;
            remain -= blocks;
        }
        throw new InvalidDataException("UDF: メタデータパーティションの範囲外を参照しました。");
    }

    private byte[]? ReadLogicalBlock(ushort partitionRef, uint block)
        => ReadSector(ToPhysicalBlock(partitionRef, block));

    private byte[]? ReadSector(long lba)
    {
        if (lba < 0) return null;
        lock (_readLock)
        {
            long pos = lba * Sector;
            if (pos + Sector > _image.Length) return null;
            _image.Seek(pos, SeekOrigin.Begin);
            var buf = new byte[Sector];
            int read = 0;
            while (read < Sector)
            {
                int n = _image.Read(buf, read, Sector - read);
                if (n <= 0) break;
                read += n;
            }
            return read == Sector ? buf : null;
        }
    }

    // ── ディレクトリとファイル ───────────────────────────────────────

    public IReadOnlyList<UdfEntry> ListChildren(UdfEntry dir)
    {
        if (!dir.IsDirectory) return Array.Empty<UdfEntry>();
        var fe = ReadFileEntry(dir.Icb) ?? throw new InvalidDataException("UDF: ディレクトリのファイルエントリを読めません。");
        // short_ad はパーティション番号を持たないので、**その FE が居るパーティション**を既定にする
        //（0 固定にすると、メタデータパーティション上のディレクトリが物理側の別セクタを指してしまう）。
        var data = ReadFileEntryData(fe, dir.Icb.Partition);
        var list = new List<UdfEntry>();
        int off = 0;
        while (off + 38 <= data.Length)
        {
            if (Le16(data, off) != TagFileIdentifierDescriptor) break;
            byte characteristics = data[off + 18];
            byte fileIdLen = data[off + 19];
            var icb = ReadLongAd(data, off + 20);
            ushort implUseLen = Le16(data, off + 36);
            int nameOff = off + 38 + implUseLen;
            int total = 38 + implUseLen + fileIdLen;
            total = (total + 3) & ~3; // 4バイト境界へ切り上げ（ECMA-167 4/14.4.9）
            if (nameOff + fileIdLen > data.Length) break;

            bool isParent = (characteristics & 0x08) != 0;
            bool isDeleted = (characteristics & 0x04) != 0;
            bool isDir = (characteristics & 0x02) != 0;
            if (!isParent && !isDeleted && fileIdLen > 0)
            {
                string name = DecodeDString(data, nameOff, fileIdLen);
                long length = 0;
                if (!isDir)
                {
                    var childFe = ReadFileEntry(icb);
                    if (childFe is not null) length = ReadInformationLength(childFe);
                }
                list.Add(new UdfEntry(name, isDir, length, icb));
            }
            off += total;
        }
        return list;
    }

    // "BDMV/PLAYLIST/00000.mpls" のようなパスで引く（大文字小文字は区別しない）。
    public UdfEntry? FindEntry(string path)
    {
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var cur = Root;
        foreach (var part in parts)
        {
            if (!cur.IsDirectory) return null;
            UdfEntry? next = null;
            foreach (var c in ListChildren(cur))
                if (string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next is null) return null;
            cur = next;
        }
        return ReferenceEquals(cur, Root) ? null : cur;
    }

    public byte[] ReadAllBytes(UdfEntry file)
    {
        using var s = OpenRead(file);
        var buf = new byte[s.Length];
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == buf.Length ? buf : buf[..read];
    }

    // ファイル本体を読むストリーム。**多エクステントに対応**するため、論理オフセット→物理位置の
    // 写像を持つ専用ストリームを返す（BD の m2ts は連続1エクステントとは限らない）。
    public Stream OpenRead(UdfEntry file)
    {
        if (file.IsDirectory) throw new InvalidOperationException("ディレクトリは開けません。");
        var fe = ReadFileEntry(file.Icb) ?? throw new InvalidDataException("UDF: ファイルエントリを読めません。");
        long length = ReadInformationLength(fe);
        if (TryGetEmbedded(fe, out var embedded))
            return new MemoryStream(embedded, writable: false);

        var extents = ParseAllocation(fe, file.Icb.Partition, out _);
        var segs = new List<(long PhysicalOffset, long Length)>();
        foreach (var e in extents)
        {
            if (e.Length == 0) continue;
            segs.Add((ToPhysicalBlock(e.Partition, e.Block) * Sector, e.Length));
        }
        // 小さなファイル（mpls・clpi・index 等）は別ハンドルを開かず、開いているイメージから読んでメモリに置く。
        // ファイル源（SMB）では開き直しのたびに往復が要り、BD のタイトル一覧（プレイリスト・クリップ情報を何十も読む）で
        // 開き直しが 100 回を超えていた。ローカルでもハンドルの開閉が減るだけで中身は同じ。
        if (length <= SmallFileBytes)
        {
            using var shared = new UdfFileStream(_image, segs, length, ownsBase: false, _readLock);
            var bytes = new byte[length];
            int got = 0;
            while (got < bytes.Length)
            {
                int n = shared.Read(bytes, got, bytes.Length - got);
                if (n <= 0) break;
                got += n;
            }
            return new MemoryStream(bytes, 0, got, writable: false);
        }
        // 別ハンドルを開けるなら開く（再生スレッドと診断が同時に読んでも競合しない）。
        Stream baseStream = _imagePath is not null
            ? MediaFile.OpenRead(_imagePath)
            : _image;
        bool ownsBase = _imagePath is not null;
        return new UdfFileStream(baseStream, segs, length, ownsBase, ownsBase ? null : _readLock);
    }

    // ── ファイルエントリの解釈 ───────────────────────────────────────

    private byte[]? ReadFileEntry(LongAd icb)
    {
        var b = ReadLogicalBlock(icb.Partition, icb.Block);
        if (b is null) return null;
        int tag = TagId(b);
        return tag == TagFileEntry || tag == TagExtendedFileEntry ? b : null;
    }

    // FE と EFE で割当記述子までのオフセットが違う（EFE は 216→176+40）。
    private static (int AllocOffset, int LengthOfAllocFieldOffset, int ExtAttrOffset) FeLayout(byte[] fe)
        => TagId(fe) == TagExtendedFileEntry ? (216, 212, 208) : (176, 172, 168);

    private static long ReadInformationLength(byte[] fe) => (long)Le64(fe, 56);

    // ICB tag（byte 16 から）の flags 下位3bit：0=short_ad, 1=long_ad, 2=ext_ad, 3=埋め込み。
    private static int AllocationType(byte[] fe) => Le16(fe, 16 + 18) & 0x07;

    private static bool TryGetEmbedded(byte[] fe, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (AllocationType(fe) != 3) return false;
        var (allocOff, lenOff, extAttrOff) = FeLayout(fe);
        uint extAttrLen = Le32(fe, extAttrOff);
        uint allocLen = Le32(fe, lenOff);
        int start = allocOff + (int)extAttrLen;
        if (start < 0 || start + allocLen > fe.Length) return false;
        data = fe[start..(start + (int)allocLen)];
        return true;
    }

    // ディレクトリ/短いファイルの本体を丸ごと読む（FID 列の解析に使う）。
    private byte[] ReadFileEntryData(byte[] fe, ushort partition)
    {
        if (TryGetEmbedded(fe, out var embedded)) return embedded;
        long length = ReadInformationLength(fe);
        var extents = ParseAllocation(fe, partition, out _);
        var ms = new MemoryStream();
        foreach (var e in extents)
        {
            long remain = e.Length;
            uint blk = e.Block;
            while (remain > 0)
            {
                var b = ReadSector(ToPhysicalBlock(e.Partition, blk));
                if (b is null) break;
                int take = (int)Math.Min(Sector, remain);
                ms.Write(b, 0, take);
                remain -= take;
                blk++;
            }
        }
        var all = ms.ToArray();
        return length > 0 && length < all.Length ? all[..(int)length] : all;
    }

    // 割当記述子列 →（パーティション参照, 開始ブロック, バイト長）の並び。
    // physicalPartitionHint：メタデータファイル自身を読むときだけ使う（その FE は物理側にあり、
    // short_ad のパーティションは「そのマップが指す物理パーティション」になる）。
    private List<(ushort Partition, uint Block, long Length)> ParseAllocation(
        byte[] fe, ushort defaultPartition, out ushort usedPartition, ushort? physicalPartitionHint = null)
    {
        var result = new List<(ushort, uint, long)>();
        usedPartition = defaultPartition;
        int type = AllocationType(fe);
        var (allocOff, lenOff, extAttrOff) = FeLayout(fe);
        uint extAttrLen = Le32(fe, extAttrOff);
        uint allocLen = Le32(fe, lenOff);
        int p = allocOff + (int)extAttrLen;
        int end = p + (int)allocLen;
        if (p < 0 || end > fe.Length) return result;

        while (p + 8 <= end)
        {
            uint rawLen = Le32(fe, p);
            long len = rawLen & 0x3FFFFFFF;
            int extType = (int)(rawLen >> 30);
            if (type == 0) // short_ad（長さ4＋ブロック4）
            {
                uint blk = Le32(fe, p + 4);
                if (extType == 0 && len > 0) result.Add((defaultPartition, blk, len));
                p += 8;
            }
            else if (type == 1) // long_ad（長さ4＋ブロック4＋パーティション2＋実装用6）
            {
                if (p + 16 > end) break;
                uint blk = Le32(fe, p + 4);
                ushort part = Le16(fe, p + 8);
                if (extType == 0 && len > 0) result.Add((part, blk, len));
                p += 16;
            }
            else break; // ext_ad は BD では使われない
            if (len == 0) break;
        }
        return result;
    }

    // ── 下位ヘルパ ──────────────────────────────────────────────

    private static int TagId(byte[] b) => Le16(b, 0);
    private static ushort Le16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint Le32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | ((uint)b[o + 3] << 24));
    private static ulong Le64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
        return v;
    }

    private static LongAd ReadLongAd(byte[] b, int o)
        => new(Le16(b, o + 8), Le32(b, o + 4), Le32(b, o) & 0x3FFFFFFF);

    private static string ReadEntityId(byte[] b, int o)
    {
        // EntityID（1/7.4）：flags(1)＋identifier(23)＋identifier suffix(8)
        int len = 0;
        while (len < 23 && b[o + 1 + len] != 0) len++;
        return Encoding.ASCII.GetString(b, o + 1, len);
    }

    // OSTA 圧縮 Unicode（UDF 2.1.1）：先頭バイトが 8＝8bit、16＝16bit(BE)。
    private static string DecodeDString(byte[] b, int o, int len)
    {
        if (len <= 1) return string.Empty;
        byte compression = b[o];
        if (compression == 8) return Encoding.ASCII.GetString(b, o + 1, len - 1).TrimEnd('\0');
        if (compression == 16)
        {
            var sb = new StringBuilder();
            for (int i = o + 1; i + 1 < o + len; i += 2) sb.Append((char)((b[i] << 8) | b[i + 1]));
            return sb.ToString().TrimEnd('\0');
        }
        return Encoding.ASCII.GetString(b, o + 1, len - 1).TrimEnd('\0');
    }

    // 診断：ファイルのエクステント解決の様子（宣言サイズと、実際に辿れた合計が合うか）。
    public string DescribeExtents(UdfEntry file)
    {
        var fe = ReadFileEntry(file.Icb);
        if (fe is null) return "  FE を読めない";
        long info = ReadInformationLength(fe);
        var ex = ParseAllocation(fe, file.Icb.Partition, out _);
        long sum = ex.Sum(e => e.Length);
        var sb = new StringBuilder();
        sb.AppendLine($"  {file.Name}: 宣言サイズ={info:N0} エクステント数={ex.Count} 合計={sum:N0} 差={info - sum:N0}");
        for (int i = 0; i < Math.Min(4, ex.Count); i++)
            sb.AppendLine($"    [{i}] part={ex[i].Partition} blk={ex[i].Block} len={ex[i].Length:N0}");
        if (ex.Count > 4) sb.AppendLine($"    …（末尾）len={ex[^1].Length:N0}");
        return sb.ToString();
    }

    // 診断：解決したパーティション構成・ルートの様子を文字列で返す（--udf-list 用）。
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"  logicalBlockSize={_logicalBlockSize} maps={_maps.Count}");
        for (int i = 0; i < _maps.Count; i++)
        {
            var m = _maps[i];
            sb.Append($"  map[{i}] partNum={m.PartitionNumber} start={m.PartitionStart}");
            if (m.MetadataExtents is null) sb.AppendLine(" (物理)");
            else sb.AppendLine($" (メタデータ: エクステント{m.MetadataExtents.Count}件 先頭={(m.MetadataExtents.Count > 0 ? m.MetadataExtents[0].Start : -1)})");
        }
        sb.AppendLine($"  fsd=part{_fsdLocation?.Partition} blk={_fsdLocation?.Block}");
        sb.AppendLine($"  rootIcb=part{Root.Icb.Partition} blk={Root.Icb.Block} len={Root.Icb.Length}");
        var fe = ReadLogicalBlock(Root.Icb.Partition, Root.Icb.Block);
        if (fe is null) sb.AppendLine("  rootFE: 読めない");
        else
        {
            sb.AppendLine($"  rootFE: tag={TagId(fe)} allocType={AllocationType(fe)} infoLen={ReadInformationLength(fe)}");
            var (allocOff, lenOff, extAttrOff) = FeLayout(fe);
            sb.AppendLine($"  rootFE: extAttrLen={Le32(fe, extAttrOff)} allocLen={Le32(fe, lenOff)} allocOff={allocOff}");
            var ex = ParseAllocation(fe, Root.Icb.Partition, out _);
            foreach (var e in ex) sb.AppendLine($"    extent part={e.Partition} blk={e.Block} len={e.Length} → phys={SafePhys(e.Partition, e.Block)}");
        }
        return sb.ToString();
    }

    private string SafePhys(ushort part, uint blk)
    {
        try { return ToPhysicalBlock(part, blk).ToString(); } catch (Exception ex) { return "エラー:" + ex.Message; }
    }

    public void Dispose()
    {
        if (!_leaveOpen) _image.Dispose();
    }

    // パーティションマップ1件。MetadataExtents が非 null ならメタデータパーティション。
    private sealed class PartitionMap
    {
        public ushort PartitionNumber;
        public uint PartitionStart;
        public List<(long Start, long Blocks)>? MetadataExtents;

        public static PartitionMap Physical(ushort num) => new() { PartitionNumber = num };
        public static PartitionMap MetadataPlaceholder(ushort num) => new() { PartitionNumber = num, MetadataExtents = null };
    }
}

// long_ad（ECMA-167 4/14.14.2）。パーティション参照番号＋論理ブロック番号。
internal readonly record struct LongAd(ushort Partition, uint Block, uint Length);

internal sealed class UdfEntry
{
    public UdfEntry(string name, bool isDirectory, long length, LongAd icb)
    {
        Name = name; IsDirectory = isDirectory; Length = length; Icb = icb;
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public long Length { get; }
    public LongAd Icb { get; }
    public override string ToString() => IsDirectory ? Name + "/" : $"{Name} ({Length:N0} bytes)";
}

// 多エクステントのファイルを1本の連続ストリームに見せる（論理オフセット→物理オフセットの写像）。
internal sealed class UdfFileStream : Stream
{
    private readonly Stream _base;
    private readonly List<(long PhysicalOffset, long Length, long LogicalStart)> _segs = new();
    private int _segHint;   // 直近に使ったエクステント。連続読みは同じところに留まるので毎回の線形探索を避ける
    private readonly long _length;
    private readonly bool _ownsBase;
    private readonly object? _sharedLock;
    private long _position;

    public UdfFileStream(Stream baseStream, List<(long PhysicalOffset, long Length)> segments,
        long length, bool ownsBase, object? sharedLock)
    {
        _base = baseStream; _ownsBase = ownsBase; _sharedLock = sharedLock;
        long logical = 0;
        foreach (var (off, len) in segments)
        {
            if (len <= 0) continue;
            _segs.Add((off, len, logical));
            logical += len;
        }
        _length = length > 0 && length <= logical ? length : logical;
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
        count = (int)Math.Min(count, _length - _position);

        // 現在位置を含むエクステントを探し、その中に収まる分だけ読む（跨ぐ分は次回の Read で）。
        // **毎回の線形探索は避ける**：BD の大きな m2ts はエクステントが多数に分かれることがあり、
        // 1回の Read ごとに全部を走査すると再生が引っかかる。直近の位置から探し、外れたら二分探索する。
        int idx = FindSegment(_position);
        if (idx >= 0)
        {
            var (phys, len, logStart) = _segs[idx];
            _segHint = idx;
            long inSeg = _position - logStart;
            int take = (int)Math.Min(count, len - inSeg);
            int n;
            if (_sharedLock is not null)
            {
                lock (_sharedLock)
                {
                    _base.Seek(phys + inSeg, SeekOrigin.Begin);
                    n = _base.Read(buffer, offset, take);
                }
            }
            else
            {
                _base.Seek(phys + inSeg, SeekOrigin.Begin);
                n = _base.Read(buffer, offset, take);
            }
            if (n > 0) _position += n;
            return n;
        }
        return 0;
    }

    private int FindSegment(long pos)
    {
        if (_segs.Count == 0) return -1;
        // まず直近とその次（連続読みはここで当たる）。
        for (int k = _segHint; k < Math.Min(_segs.Count, _segHint + 2); k++)
            if (pos >= _segs[k].LogicalStart && pos < _segs[k].LogicalStart + _segs[k].Length) return k;
        int lo = 0, hi = _segs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            var s = _segs[mid];
            if (pos < s.LogicalStart) hi = mid - 1;
            else if (pos >= s.LogicalStart + s.Length) lo = mid + 1;
            else return mid;
        }
        return -1;
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
        Position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsBase) _base.Dispose();
        base.Dispose(disposing);
    }
}
