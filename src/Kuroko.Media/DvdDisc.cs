using System.IO;

namespace Kuroko.Media.Dvd;

// DVD-Video の ISO を開き、タイトル単位の再生モデルを提供するファサード。
// 実際のデコード/描画は DvdPlaybackEngine が担う。本クラスは「どのセクタを再生順に流すか」
// （＝PS バイトストリーム）と、章・音声・字幕・映像の構造情報を提供する。
internal sealed class DvdDisc : IDisposable
{
    // 現在のアングル（1 始まり）。アングルブロックを持つ PGC で「どのセルを再生するか」を決める。
    // DVD-Video では SPRM[3] が現在のアングルなので、再生エンジンが SPRM[3] と揃えて設定する。
    // ここを変えたあとに OpenTitle し直すと、セル選択・章の時刻・尺の全てが新しいアングルで組み直される。
    // **既定の 0 は「絞り込まない」（＝全セル。従来どおりの動作）**。変換器や診断ツールのように
    // セルを 1 個ずつ直接指定して取り出す用途は、アングルで間引かれると区間が消えるため既定のまま使う。
    public int Angle { get; set; } = 0;

    private readonly Iso9660Reader _iso;
    private readonly string _isoPath;
    private readonly byte[] _vmgiBytes; // VIDEO_TS.IFO（VMGI）生バイト：VMGM メニュー解析に使う

    private DvdDisc(Iso9660Reader iso, string isoPath, DvdVmgIfo vmg, byte[] vmgiBytes,
        bool videoIsH264, bool interlaced, bool tff, double gopSeconds, bool cellIdr, bool menuH264)
    {
        _iso = iso;
        _isoPath = isoPath;
        Vmg = vmg;
        _vmgiBytes = vmgiBytes;
        VideoIsH264 = videoIsH264;
        VideoH264Interlaced = interlaced;
        VideoH264TopFieldFirst = tff;
        VideoH264GopSeconds = gopSeconds;
        VideoH264CellIdr = cellIdr;
        VideoMenuH264 = menuH264;
    }

    public DvdVmgIfo Vmg { get; }
    public IReadOnlyList<DvdTitleEntry> Titles => Vmg.Titles;

    // Vuecove 拡張：映像コーデックが H.264（標準DVDではあり得ない）であることのマーカー。
    // 変換アプリが ISO に /VIDEO_TS/VUECOVE.264 を置く。実機（標準プレーヤー）は未知ファイルを無視する。
    public bool VideoIsH264 { get; }
    // H.264 がインターレース符号化か（再生側でボブ・デインターレースする）。
    public bool VideoH264Interlaced { get; }
    // インターレース時のフィールド順（true=トップフィールドファースト）。
    public bool VideoH264TopFieldFirst { get; }
    // H.264 の最大GOP長（秒）。再生側のシーク助走幅に使う（直前IDRを窓に含める）。既定10s。
    public double VideoH264GopSeconds { get; }
    // 各セル先頭が IDR か（章/セル入場を助走なしで先頭から復号できる）。
    public bool VideoH264CellIdr { get; }
    // メニューのモーションセルが H.264 化されているか（静止セルは MPEG-2 のまま混在）。再生側はメニューセルの
    // コーデックをセル毎に判定して復号する。
    public bool VideoMenuH264 { get; }
    // 元映像のフィールド数(RFF)テーブル（あれば）。再生側でテレシネのフレーム表示時間/フィールド枚数を厳密化。
    public DvdRffTable? RffTable { get; private set; }
    // H.264化メニューのボタン表（あれば）。各メニューセルの代表PCI（再エンコで失われたボタン情報）を保持。
    public DvdMenuTable? MenuTable { get; private set; }

    // マーカーファイル名（Vuecove 専用拡張の目印）。
    private const string H264MarkerPath = "/VIDEO_TS/VUECOVE.264";
    private const string RffTablePath = "/VIDEO_TS/VUECOVE.RFF";
    private const string MenuTablePath = "/VIDEO_TS/VUECOVE.MNU";

    private static DvdMenuTable? ReadMenuTable(Iso9660Reader iso)
    {
        try { return iso.FindEntry(MenuTablePath) is IsoFileEntry e ? DvdMenuTable.Parse(iso.ReadAllBytes(e)) : null; }
        catch { return null; }
    }

    private static DvdRffTable? ReadRffTable(Iso9660Reader iso)
    {
        try
        {
            return iso.FindEntry(RffTablePath) is IsoFileEntry e ? DvdRffTable.Parse(iso.ReadAllBytes(e)) : null;
        }
        catch { return null; }
    }

    private static (bool h264, bool interlaced, bool tff, double gopSeconds, bool cellIdr, bool menuH264) ReadH264Marker(Iso9660Reader iso)
    {
        if (iso.FindEntry(H264MarkerPath) is not IsoFileEntry mk)
            return (false, false, true, 10.0, false, false);
        try
        {
            var txt = System.Text.Encoding.ASCII.GetString(iso.ReadAllBytes(mk));
            bool interlaced = txt.Contains("interlaced=1");
            bool tff = !txt.Contains("tff=0"); // 既定 TFF
            bool cellIdr = txt.Contains("cellidr=1");
            bool menuH264 = txt.Contains("menu=1"); // メニューのモーションセルが H.264 化されている
            double gop = 10.0; // 既定（libx264 既定GOP相当）。マーカーに gop=秒 があれば使う。
            var mm = System.Text.RegularExpressions.Regex.Match(txt, @"gop=([0-9]+(\.[0-9]+)?)");
            if (mm.Success && double.TryParse(mm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var g) && g > 0)
                gop = g;
            return (true, interlaced, tff, gop, cellIdr, menuH264);
        }
        catch { return (true, false, true, 10.0, false, false); }
    }

    public static DvdDisc Open(string isoPath)
    {
        var iso = new Iso9660Reader(isoPath);
        try
        {
            if (iso.FindEntry("/VIDEO_TS/VIDEO_TS.IFO") is not IsoFileEntry vmgEntry)
                throw new InvalidDataException("VIDEO_TS/VIDEO_TS.IFO が見つかりません（DVD-Video ではない ISO）。");
            var vmgiBytes = iso.ReadAllBytes(vmgEntry);
            var vmg = DvdVmgIfo.Parse(vmgiBytes);
            var (h264, interlaced, tff, gopSec, cellIdr, menuH264) = ReadH264Marker(iso);
            var disc = new DvdDisc(iso, isoPath, vmg, vmgiBytes, h264, interlaced, tff, gopSec, cellIdr, menuH264);
            if (h264) disc.RffTable = ReadRffTable(iso);
            if (menuH264) disc.MenuTable = ReadMenuTable(iso);
            return disc;
        }
        catch
        {
            iso.Dispose();
            throw;
        }
    }

    private List<DvdMenuPgc>? _vmgmPgcsCache;

    // VMGM（VIDEO_TS.IFO）のメニュー PGC 一覧（VMGM_PGCI_UT, VMGI 0xC8）。DVD仮想マシンのディスパッチャ
    // PGC 等を実行するために命令列を提供する（表示用のセル再生は別途）。
    public IReadOnlyList<DvdMenuPgc> GetVmgmPgcs()
    {
        if (_vmgmPgcsCache is not null)
            return _vmgmPgcsCache;
        long sector = _vmgiBytes.Length >= 0xCC ? DvdIfoBin.U32(_vmgiBytes, 0xC8) : 0;
        _vmgmPgcsCache = sector > 0 ? DvdMenuIfo.ParsePgciUt(_vmgiBytes, sector) : new List<DvdMenuPgc>();
        return _vmgmPgcsCache;
    }

    public DvdPgc? GetVmgmPgc(int pgcNumber)
        => GetVmgmPgcs().FirstOrDefault(x => x.Pgc.Number == pgcNumber)?.Pgc;

    // 指定タイトル（ディスク通し番号 1-based）の再生モデルを構築。
    public DvdTitlePlayback OpenTitle(int titleNumber)
    {
        var title = Titles.FirstOrDefault(t => t.TitleNumber == titleNumber)
                    ?? throw new ArgumentOutOfRangeException(nameof(titleNumber), $"タイトル {titleNumber} は存在しません。");

        var vts = GetTitleVtsIfo(title.VtsNumber)
                  ?? throw new InvalidDataException($"VTS_{title.VtsNumber:00}_0.IFO が見つかりません。");

        // タイトルの章（PTT）から主 PGC を決定。
        var ptts = vts.GetTitleChapters(title.VtsTitleNumber);
        int pgcn = ptts.Count > 0 ? ptts[0].Pgcn : 1;
        var pgc = vts.GetPgc(pgcn)
                  ?? vts.Pgcs.FirstOrDefault()
                  ?? throw new InvalidDataException("再生対象の PGC が見つかりません。");

        return BuildTitleDomainPlayback(title.VtsNumber, vts, pgc, ptts, Angle)
               ?? throw new InvalidDataException("タイトル VOB が見つかりません。");
    }

    // タイトルドメイン用の VTS IFO キャッシュ（メニュー用 _menuVtsCache とは別に持つ）。
    private DvdVtsIfo? _titleVtsIfoCache;
    private int _titleVtsIfoCacheNo = -1;

    private DvdVtsIfo? GetTitleVtsIfo(int vtsNumber)
    {
        if (_titleVtsIfoCacheNo == vtsNumber && _titleVtsIfoCache is not null)
            return _titleVtsIfoCache;
        if (_iso.FindEntry($"/VIDEO_TS/VTS_{vtsNumber:00}_0.IFO") is not IsoFileEntry e)
            return null;
        _titleVtsIfoCache = DvdVtsIfo.Parse(_iso.ReadAllBytes(e));
        _titleVtsIfoCacheNo = vtsNumber;
        return _titleVtsIfoCache;
    }

    // 指定 VTS のタイトルドメイン PGC 一覧（VTS_PGCIT）。タイトル番号を持たない中間 PGC も含む。
    // LinkPGCN のドメイン内解決（タイトルドメイン）と、変換器の titleDomains 出力に使う。
    public IReadOnlyList<DvdPgc> GetTitleDomainPgcs(int vtsNumber)
        => GetTitleVtsIfo(vtsNumber)?.Pgcs ?? (IReadOnlyList<DvdPgc>)Array.Empty<DvdPgc>();

    // (vts, タイトルドメイン PGC 番号) に対応するディスク通しタイトル番号。対応が無ければ 0（中間 PGC）。
    public int TitleNumberForVtsPgc(int vtsNumber, int pgcNumber)
    {
        var vts = GetTitleVtsIfo(vtsNumber);
        if (vts is null)
            return 0;
        foreach (var t in Titles)
        {
            if (t.VtsNumber != vtsNumber)
                continue;
            var ptts = vts.GetTitleChapters(t.VtsTitleNumber);
            int pgcn = ptts.Count > 0 ? ptts[0].Pgcn : 1;
            if (pgcn == pgcNumber)
                return t.TitleNumber;
        }
        return 0;
    }

    // 指定 VTS のタイトルドメイン PGC（番号指定）を再生モデルとして開く。タイトル番号を持たない中間 PGC も
    // 開ける（LinkPGCN の同一ドメイン解決用）。見つからない／VOB 無しは null。章表は空になる。
    public DvdTitlePlayback? OpenTitleDomainPgc(int vtsNumber, int pgcNumber)
    {
        var vts = GetTitleVtsIfo(vtsNumber);
        var pgc = vts?.GetPgc(pgcNumber);
        if (vts is null || pgc is null)
            return null;
        return BuildTitleDomainPlayback(vtsNumber, vts, pgc, Array.Empty<(int, int)>(), Angle);
    }

    // タイトルドメイン PGC の再生モデル共通構築（OpenTitle / OpenTitleDomainPgc）。VOB 無しは null。
    private DvdTitlePlayback? BuildTitleDomainPlayback(int vtsNumber, DvdVtsIfo vts, DvdPgc pgc, IReadOnlyList<(int Pgcn, int Pgn)> ptts, int angle)
    {
        // タイトル VOB（VTS_xx_1.VOB..9.VOB）を順に集める。
        var titleVobs = new List<IsoFileEntry>();
        for (int n = 1; n <= 9; n++)
        {
            if (_iso.FindEntry($"/VIDEO_TS/VTS_{vtsNumber:00}_{n}.VOB") is IsoFileEntry vob)
                titleVobs.Add(vob);
            else
                break;
        }
        if (titleVobs.Count == 0)
            return null;

        var space = new DvdVobAddressSpace(titleVobs);

        // 各セルの開始時刻（セル再生時間の累積）。
        // **アングルブロックは現在のアングルのセルだけを数える**。数えないと、別アングルのセルぶんだけ
        // 以降の章・シーク位置が後ろへずれる（実測：ある市販ディスク タイトル1で最大 38.2 秒）。
        // ブロック内のセルは**どのアングルでも同じ区間**なので、ブロックの全セルに同じ開始時刻を与える
        //（規格上ブロック内の各アングルの尺は等しい。実測でも 2 枚の市販 DVDとも一致）。こうしておくと、
        // 章や LinkCN が別アングルのセルを指していても正しい時刻に着地し、krk 変換器が全アングルのセルを
        // 1 本の時間軸へ並べられる。
        var cells = pgc.Cells;
        var cellStartTimes = new TimeSpan[cells.Count];
        TimeSpan acc = TimeSpan.Zero;
        for (int i = 0; i < cells.Count; )
        {
            var (ordinal, blockSize) = pgc.AngleOrdinalOfCell(i);
            if (angle <= 0 || ordinal == 0 || blockSize <= 0)
            {
                // アングル絞り込み無し、またはブロック外のセル。従来どおり単純に積む。
                cellStartTimes[i] = acc;
                acc += cells[i].Duration;
                i++;
                continue;
            }
            // アングルブロック：全アングルのセルが同じ開始時刻を共有し、進むのは現在のアングルの尺だけ。
            TimeSpan blockStart = acc, played = TimeSpan.Zero;
            int n = Math.Min(blockSize, cells.Count - i);
            for (int j = 0; j < n; j++)
            {
                cellStartTimes[i + j] = blockStart;
                if (pgc.IsCellForAngle(i + j, angle)) played = cells[i + j].Duration;
            }
            acc += played;
            i += n;
        }

        // 章 → 開始セル（プログラム→開始セルの写像を経由）。
        var chapters = new List<DvdChapter>();
        for (int k = 0; k < ptts.Count; k++)
        {
            int pgn = ptts[k].Pgn;
            int entryCell = pgn >= 1 && pgn <= pgc.ProgramEntryCells.Count ? pgc.ProgramEntryCells[pgn - 1] : 1;
            int cellIndex = entryCell - 1;
            if (cellIndex < 0 || cellIndex >= cells.Count)
                cellIndex = 0;
            chapters.Add(new DvdChapter
            {
                Number = k + 1,
                StartCellIndex = cellIndex,
                StartTime = cellStartTimes[cellIndex],
            });
        }
        if (chapters.Count == 0 && cells.Count > 0)
            chapters.Add(new DvdChapter { Number = 1, StartCellIndex = 0, StartTime = TimeSpan.Zero });

        return new DvdTitlePlayback(_isoPath, _iso, space, pgc, cellStartTimes, chapters,
            vts.VideoAttr, vts.AudioStreams, vts.SubpictureStreams, angle: angle);
    }

    private List<DvdMenuPgc>? _menuPgcsCache;
    private DvdVtsIfo? _menuVtsCache;
    private int _menuPgcsVts = -1;

    // 指定 VTS のメニューPGC一覧（VTSM）。キャッシュする。
    public IReadOnlyList<DvdMenuPgc> GetMenuPgcs(int vtsNumber)
    {
        if (_menuPgcsVts == vtsNumber && _menuPgcsCache is not null)
            return _menuPgcsCache;
        _menuPgcsCache = new List<DvdMenuPgc>();
        _menuPgcsVts = vtsNumber;
        string ifoPath = $"/VIDEO_TS/VTS_{vtsNumber:00}_0.IFO";
        if (_iso.FindEntry(ifoPath) is not IsoFileEntry ifoEntry)
            return _menuPgcsCache;
        var ifoBytes = _iso.ReadAllBytes(ifoEntry);
        _menuVtsCache = DvdVtsIfo.Parse(ifoBytes);
        if (_menuVtsCache.VtsmPgciUtSector > 0)
            _menuPgcsCache = DvdMenuIfo.ParsePgciUt(ifoBytes, _menuVtsCache.VtsmPgciUtSector);
        return _menuPgcsCache;
    }

    // 指定 VTS のメニューPGC（番号指定）の再生モデルを構築（セルが無ければ null）。
    public DvdTitlePlayback? OpenMenuPgc(int vtsNumber, int pgcNumber)
    {
        GetMenuPgcs(vtsNumber);
        var m = _menuPgcsCache?.FirstOrDefault(x => x.Pgc.Number == pgcNumber);
        return m is null ? null : BuildMenuPlayback(vtsNumber, m.Pgc);
    }


    private DvdTitlePlayback? BuildMenuPlayback(int vtsNumber, DvdPgc pgc)
    {
        if (pgc.Cells.Count == 0 || _menuVtsCache is null)
            return null;
        string vobPath = $"/VIDEO_TS/VTS_{vtsNumber:00}_0.VOB";
        if (_iso.FindEntry(vobPath) is not IsoFileEntry vob || vob.Length < 2048)
            return null;

        var space = new DvdVobAddressSpace(new[] { vob });
        var cellStartTimes = new TimeSpan[pgc.Cells.Count];
        TimeSpan acc = TimeSpan.Zero;
        for (int i = 0; i < pgc.Cells.Count; i++)
        {
            cellStartTimes[i] = acc;
            acc += pgc.Cells[i].Duration;
        }
        return new DvdTitlePlayback(_isoPath, _iso, space, pgc, cellStartTimes, Array.Empty<DvdChapter>(),
            _menuVtsCache.VideoAttr, _menuVtsCache.AudioStreams, _menuVtsCache.SubpictureStreams);
    }

    // VMGM メニュー（VIDEO_TS.VOB）の指定 PGC の再生モデルを構築する（セルが無ければ null）。
    // タイトル/トップメニューの正式表示に使う。映像属性は VMGI_MAT の VMGM メニュー属性(0x100)。
    public DvdTitlePlayback? OpenVmgmMenuPgc(int pgcNumber)
    {
        var pgc = GetVmgmPgc(pgcNumber);
        if (pgc is null || pgc.Cells.Count == 0)
            return null;
        if (_iso.FindEntry("/VIDEO_TS/VIDEO_TS.VOB") is not IsoFileEntry vob || vob.Length < 2048)
            return null;

        var videoAttr = DvdVtsIfo.ParseMenuVideoAttr(_vmgiBytes);
        var space = new DvdVobAddressSpace(new[] { vob });
        var cellStartTimes = new TimeSpan[pgc.Cells.Count];
        TimeSpan acc = TimeSpan.Zero;
        for (int i = 0; i < pgc.Cells.Count; i++)
        {
            cellStartTimes[i] = acc;
            acc += pgc.Cells[i].Duration;
        }
        return new DvdTitlePlayback(_isoPath, _iso, space, pgc, cellStartTimes, Array.Empty<DvdChapter>(),
            videoAttr, Array.Empty<DvdAudioAttr>(), Array.Empty<DvdSubpictureAttr>());
    }

    public void Dispose() => _iso.Dispose();
}

internal sealed class DvdChapter
{
    public int Number { get; init; }
    public int StartCellIndex { get; init; }
    public TimeSpan StartTime { get; init; }
}

// 1タイトルの再生モデル。セル連結の PS ストリームと構造情報を提供する。
internal sealed class DvdTitlePlayback
{
    private readonly string _isoPath;
    private readonly Iso9660Reader _iso;
    private readonly DvdVobAddressSpace _space;
    private readonly DvdPgc _pgc;
    private readonly TimeSpan[] _cellStartTimes;
    private readonly int _angle;

    internal DvdTitlePlayback(
        string isoPath,
        Iso9660Reader iso,
        DvdVobAddressSpace space,
        DvdPgc pgc,
        TimeSpan[] cellStartTimes,
        IReadOnlyList<DvdChapter> chapters,
        DvdVideoAttr videoAttr,
        IReadOnlyList<DvdAudioAttr> audioStreams,
        IReadOnlyList<DvdSubpictureAttr> subpictureStreams,
        int angle = 0)
    {
        _angle = angle;
        _isoPath = isoPath;
        _iso = iso;
        _space = space;
        _pgc = pgc;
        _cellStartTimes = cellStartTimes;
        Chapters = chapters;
        VideoAttr = videoAttr;
        AudioStreams = audioStreams;
        SubpictureStreams = subpictureStreams;
    }

    public TimeSpan Duration => _pgc.Duration;
    public DvdPgc Pgc => _pgc; // メニューの POST 命令解決等に使う
    public IReadOnlyList<DvdChapter> Chapters { get; }
    public DvdVideoAttr VideoAttr { get; }
    public IReadOnlyList<DvdAudioAttr> AudioStreams { get; }
    public IReadOnlyList<DvdSubpictureAttr> SubpictureStreams { get; }
    public uint[] Palette => _pgc.Palette; // サブピクチャ用 YUV パレット（16色）
    public int CellCount => _pgc.Cells.Count;
    // アングル数（1＝アングル無し）と、この再生で選んでいるアングル（1 始まり）。
    public int AngleCount => _pgc.AngleCount;
    public int Angle => _angle;
    // このセルを今のアングルで再生するか。アングルブロック外は常に true。
    public bool IsCellPlayed(int cellIndex) => _pgc.IsCellForAngle(cellIndex, _angle);

    // 指定セル以降を再生順に連結した PS バイトストリームを開く。
    public Stream OpenStream(int startCellIndex = 0)
    {
        var ranges = BuildRanges(startCellIndex);
        return new DvdSectorStream(_isoPath, _space, ranges);
    }

    // 指定セル範囲 [start..endInclusive] を連結した PS ストリームを開く（メニューの per-cell still で、
    // still>0 のセルまでで再生を止めるために使う）。
    public Stream OpenStream(int startCellIndex, int endCellInclusive)
    {
        var ranges = BuildRanges(startCellIndex, endCellInclusive);
        return new DvdSectorStream(_isoPath, _space, ranges);
    }

    // 指定セルの先頭付近にある最初の映像PES PTS(90kHz)を実読みする。無ければ -1。
    // MPEG-2 章入場の「精密着地」で、セル先頭フレームの実PTSを破棄しきい値の目標にするために使う
    //（IFO積算時刻はフレーム丸めでズレるため、実PTSでセル先頭フレームちょうどに着地させる）。
    public long GetCellFirstVideoPts(int cellIndex)
    {
        if (cellIndex < 0 || cellIndex >= _pgc.Cells.Count) return -1;
        try
        {
            using var stream = (DvdSectorStream)OpenStream(cellIndex);
            long cellSectors = stream.Length / 2048;
            if (cellSectors <= 0) return -1;
            return ReadVideoPts(stream, 0, cellSectors);
        }
        catch { return -1; }
    }

    // 指定時刻付近から再生する PS ストリームを開く（セル内はバイト比例の概算シーク。
    // パック境界に丸める。精密化は NAV/DSI 対応で後続）。segmentStart は概算の開始メディア時刻。
    public Stream OpenStreamAt(TimeSpan position, out TimeSpan segmentStart)
    {
        int cell = CellIndexForTime(position, out var cellStart);
        var stream = (DvdSectorStream)OpenStream(cell);

        var cellObj = _pgc.Cells[cell];
        TimeSpan cellDur = cellObj.Duration;
        long cellSectors = Math.Max(0, cellObj.LastSector - cellObj.FirstSector + 1);
        double frac = cellDur.TotalSeconds > 0
            ? Math.Clamp((position - cellStart).TotalSeconds / cellDur.TotalSeconds, 0, 1)
            : 0;
        long sectorOffset = (long)(frac * cellSectors);
        stream.Seek(sectorOffset * 2048, SeekOrigin.Begin);

        segmentStart = cellStart + TimeSpan.FromSeconds(frac * cellDur.TotalSeconds);
        return stream;
    }

    // 指定セル内を「映像PESのPTSで二分探索」して targetPts90k 以下で最大のセクタを開く。openedPts90k に
    // 実際に開いたセクタの映像PTSを返す。H.264 のシークで使う（呼び出し側が RFFテーブルから targetPts を
    // 算出する。バイト比例では VBR で大きくずれ、かつ変換後のPTS↔フィールド時間のズレも吸収できないため）。
    // PTSが全く読めない時はセル先頭を開いて openedPts90k=-1 を返す。
    public Stream OpenStreamAtPts(int cell, long targetPts90k, out long openedPts90k)
    {
        var stream = (DvdSectorStream)OpenStream(cell);
        var cellObj = _pgc.Cells[cell];
        long cellSectors = Math.Max(1, cellObj.LastSector - cellObj.FirstSector + 1);

        long cellStartPts = ReadVideoPts(stream, 0, cellSectors);
        if (cellStartPts < 0) { openedPts90k = -1; stream.Seek(0, SeekOrigin.Begin); return stream; }

        long lo = 0, hi = cellSectors - 1, best = 0, bestPts = cellStartPts;
        for (int iter = 0; iter < 18 && lo <= hi; iter++)
        {
            long mid = (lo + hi) / 2;
            long pts = ReadVideoPts(stream, mid, cellSectors);
            if (pts < 0) { hi = mid - 1; continue; }            // 近傍に映像PTS無し → 手前を探す
            if (pts <= targetPts90k) { best = mid; bestPts = pts; lo = mid + 1; }
            else hi = mid - 1;
        }
        stream.Seek(best * 2048, SeekOrigin.Begin);
        openedPts90k = bestPts;
        return stream;
    }

    // シーク窓内の「クリーンなIフレーム入口」（SPS付きの映像AU先頭セクタ）を表示PTS付きで列挙し、
    // 表示PTSが targetPts90k 以下で最も近い入口を開く。openCell から開き、scanFromPts90k(=target−助走)付近から
    // target を少し越えるまで前方走査する。これで「データ順では target を含むGOPのIを踏み越え、前方最初のIが
    // 次GOP（表示順で target より後）のIになる」入場ずれ（シーク直後しばらく映像が出ない）を根治する。
    //   背景：PTS（表示順）は復号順のセクタ並びでは GOP境界ごとに前後する（B並べ替え）。従来の OpenStreamAtPts は
    //   「PTS≤target で最大セクタ＋前方最初のSPS」で入口を決めるため、踏み越えると次GOPのIへ飛んでしまう。
    //   ここでは入口候補（SPS付きAU先頭）を表示PTSで評価し、target以前で最寄りのIを選ぶ。
    // SPS入口が全く読めない時は OpenStreamAtPts と同じ「PTS≤target で最大セクタ」へフォールバック。
    public Stream OpenStreamAtCleanEntry(int openCell, long scanFromPts90k, long targetPts90k, out long openedPts90k)
    {
        var stream = (DvdSectorStream)OpenStream(openCell);
        long totalSectors = stream.Length / 2048;
        if (totalSectors <= 0) { openedPts90k = -1; stream.Seek(0, SeekOrigin.Begin); return stream; }

        // 走査開始セクタ：scanFromPts 以下で最大のセクタ（並べ替えの揺れは前方走査で吸収するので近似で良い）。
        long startSector = 0;
        {
            long lo = 0, hi = totalSectors - 1;
            for (int iter = 0; iter < 24 && lo <= hi; iter++)
            {
                long mid = (lo + hi) / 2;
                long pts = ReadVideoPts(stream, mid, totalSectors);
                if (pts < 0) { hi = mid - 1; continue; }
                if (pts <= scanFromPts90k) { startSector = mid; lo = mid + 1; } else hi = mid - 1;
            }
        }

        // 前方走査：SPS付きAU先頭(=クリーンI入口)を表示PTS付きで集め、PTS≤target で最も近い入口を採る。
        long bestSector = -1, bestPts = long.MinValue; // PTS≤target で最大（=最寄り）
        long firstSector = -1, firstPts = -1;          // 全部 target より後だった時の保険
        var buf = new byte[2048];
        for (long s = startSector; s < totalSectors; s++)
        {
            if (!ReadSectorRaw(stream, s, buf)) break;
            long pts = CleanEntryPts(buf, buf.Length);
            if (pts < 0) continue;
            if (firstSector < 0) { firstSector = s; firstPts = pts; }
            if (pts <= targetPts90k && pts > bestPts) { bestSector = s; bestPts = pts; }
            if (pts > targetPts90k + 90000) break; // target を約1秒越えた → 以降の入口は不要
        }

        long chosen;
        if (bestSector >= 0) { chosen = bestSector; openedPts90k = bestPts; }
        else if (firstSector >= 0) { chosen = firstSector; openedPts90k = firstPts; }
        else
        {
            // クリーン入口が読めない → 従来挙動（PTS≤target で最大セクタ）へフォールバック。
            long lo = 0, hi = totalSectors - 1, best = 0, bp = -1;
            for (int iter = 0; iter < 24 && lo <= hi; iter++)
            {
                long mid = (lo + hi) / 2;
                long pts = ReadVideoPts(stream, mid, totalSectors);
                if (pts < 0) { hi = mid - 1; continue; }
                if (pts <= targetPts90k) { best = mid; bp = pts; lo = mid + 1; } else hi = mid - 1;
            }
            chosen = best; openedPts90k = bp;
        }
        stream.Seek(chosen * 2048, SeekOrigin.Begin);
        return stream;
    }

    // セクタ sectorIndex を 2048 バイト丸ごと buf へ読む（成否を返す）。位置は呼び出し後に変わってよい。
    private static bool ReadSectorRaw(DvdSectorStream stream, long sectorIndex, byte[] buf)
    {
        stream.Seek(sectorIndex * 2048, SeekOrigin.Begin);
        int got = 0, n;
        while (got < buf.Length && (n = stream.Read(buf, got, buf.Length - got)) > 0) got += n;
        return got == buf.Length;
    }

    // セル相対セクタ sectorIndex から最大 maxScan セクタ先までを走査し、最初に見つかる映像PESのPTS(90kHz)。
    // 無ければ -1。呼び出し前後でストリーム位置は不変。
    private static long ReadVideoPts(DvdSectorStream stream, long sectorIndex, long cellSectors, int maxScan = 64)
    {
        if (sectorIndex < 0 || sectorIndex >= cellSectors) return -1;
        long save = stream.Position;
        try
        {
            var buf = new byte[2048];
            for (int s = 0; s < maxScan && sectorIndex + s < cellSectors; s++)
            {
                stream.Seek((sectorIndex + s) * 2048, SeekOrigin.Begin);
                int got = 0;
                while (got < buf.Length)
                {
                    int n = stream.Read(buf, got, buf.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got <= 0) break;
                long pts = PackVideoPts(buf, got);
                if (pts >= 0) return pts;
            }
            return -1;
        }
        finally { stream.Seek(save, SeekOrigin.Begin); }
    }

    // 1パック(先頭=pack_header 00 00 01 BA)を「pack_header→任意のsystem_header→単一PES」と正しく辿り、
    // そのPESが映像(0xE0..EF)かつPTSを持てば PTS(90kHz)を返す。映像でない/PTS無し/不正は -1。
    // 素朴な start code 走査だと NAV(PCI/DSI)や音声のペイロード内の偽 00 00 01 Ex を誤検出するため長さで辿る。
    private static long PackVideoPts(byte[] b, int len)
    {
        if (len < 14 || b[0] != 0 || b[1] != 0 || b[2] != 1 || b[3] != 0xBA) return -1;
        if ((b[4] & 0xC0) != 0x40) return -1;                 // MPEG-2 pack のみ
        int off = 14 + (b[13] & 0x07);                        // pack_header(14) + pack_stuffing_length
        if (off + 6 <= len && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1 && b[off + 3] == 0xBB)
            off += 6 + ((b[off + 4] << 8) | b[off + 5]);      // 任意の system_header をスキップ
        if (off + 9 > len || b[off] != 0 || b[off + 1] != 0 || b[off + 2] != 1) return -1;
        int sid = b[off + 3];
        if (sid < 0xE0 || sid > 0xEF) return -1;              // 映像PESでない
        if (((b[off + 7] >> 6) & 0x02) == 0) return -1;       // PTS無し
        int o = off + 9;
        if (o + 5 > len) return -1;
        return ((long)(b[o] & 0x0E) << 29) | ((long)b[o + 1] << 22) | ((long)(b[o + 2] & 0xFE) << 14)
             | ((long)b[o + 3] << 7) | ((long)(b[o + 4] & 0xFE) >> 1);
    }

    // パックが「映像PES＋PTSあり」かつ「その映像PESペイロード内に H.264 SPS(NAL type 7)」を満たすなら、
    // その映像PESの PTS(90kHz) を返す。違えば -1。＝クリーンなIフレーム復号開始点（H.264変換は repeat-headers で
    // 各IDR直前にSPS/PPSを置くので、SPSを持つAU＝そのフレームがI）。PTS無し/映像でない/SPS無しは入口でない。
    //   SPSはペイロードの数百〜数千バイト目に来る（DVD多重化では PTS付き映像PESの先頭に「前フレーム末尾スライス」が
    //   入り、その後ろにIDRのAU=AUD/SPS/PPS/IDRが続くため、SPS位置は前フレーム尺で変動する）。先頭512Bだけ見ると
    //   大半のIを取り逃すので、映像PESペイロード全体を走査する。H.264はエミュレーション防止で slice 内に 00 00 01 が
    //   現れない＝偽SPS誤検出は起きない。走査範囲は当該映像PESの長さで区切る（後続の音声PES等へはみ出さない）。
    private static long CleanEntryPts(byte[] b, int len)
    {
        if (len < 14 || b[0] != 0 || b[1] != 0 || b[2] != 1 || b[3] != 0xBA) return -1;
        if ((b[4] & 0xC0) != 0x40) return -1;                 // MPEG-2 pack のみ
        int off = 14 + (b[13] & 0x07);                        // pack_header(14) + pack_stuffing_length
        if (off + 6 <= len && b[off] == 0 && b[off + 1] == 0 && b[off + 2] == 1 && b[off + 3] == 0xBB)
            off += 6 + ((b[off + 4] << 8) | b[off + 5]);      // 任意の system_header をスキップ
        if (off + 9 > len || b[off] != 0 || b[off + 1] != 0 || b[off + 2] != 1) return -1;
        int sid = b[off + 3];
        if (sid < 0xE0 || sid > 0xEF) return -1;              // 映像PESでない
        if (((b[off + 7] >> 6) & 0x02) == 0) return -1;       // PTS無し
        int o = off + 9;
        if (o + 5 > len) return -1;
        long pts = ((long)(b[o] & 0x0E) << 29) | ((long)b[o + 1] << 22) | ((long)(b[o + 2] & 0xFE) << 14)
                 | ((long)b[o + 3] << 7) | ((long)(b[o + 4] & 0xFE) >> 1);
        int pesLen = (b[off + 4] << 8) | b[off + 5];
        int payload = off + 9 + b[off + 8];                   // PES_header_data_length の先＝ペイロード先頭
        int end = pesLen > 0 ? Math.Min(len, off + 6 + pesLen) : len; // 当該映像PES末尾（0=無制限ならセクタ末）
        for (int i = payload; i >= 0 && i + 4 < end; i++)
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 && (b[i + 3] & 0x1F) == 7) return pts;
        return -1;
    }

    // 時刻 → 開始すべきセル番号（その開始時刻も返す）。粗いセル単位シーク（精密化は NAV で後対応）。
    public int CellIndexForTime(TimeSpan position, out TimeSpan cellStart)
    {
        int idx = 0;
        for (int i = 0; i < _cellStartTimes.Length; i++)
        {
            // 再生しないセル（別アングル）は開始セルに選ばない。選ぶと BuildRanges がそのセルを飛ばして
            // 次のブロックから読み始めてしまい、区間が丸ごと抜ける。
            if (!_pgc.IsCellForAngle(i, _angle))
                continue;
            if (_cellStartTimes[i] <= position)
                idx = i;
            else
                break;
        }
        cellStart = _cellStartTimes.Length > 0 ? _cellStartTimes[idx] : TimeSpan.Zero;
        return idx;
    }

    public TimeSpan CellStartTime(int cellIndex)
        => cellIndex >= 0 && cellIndex < _cellStartTimes.Length ? _cellStartTimes[cellIndex] : TimeSpan.Zero;

    private List<(long First, long Last)> BuildRanges(int startCellIndex)
        => BuildRanges(startCellIndex, _pgc.Cells.Count - 1);

    // 診断用：1セルの実再生セクタ範囲（インターリーブセルは VOBU フィルタ後のサブレンジ）。
    public IReadOnlyList<(long First, long Last)> CellSectorRanges(int cellIndex)
        => BuildRanges(cellIndex, cellIndex);

    private List<(long First, long Last)> BuildRanges(int startCellIndex, int endCellInclusive)
    {
        var ranges = new List<(long, long)>();
        int last = Math.Min(endCellInclusive, _pgc.Cells.Count - 1);
        for (int i = Math.Max(0, startCellIndex); i <= last; i++)
        {
            var c = _pgc.Cells[i];
            if (c.LastSector < c.FirstSector)
                continue;
            // アングルブロックは現在のアングルのセルだけ再生する（残りは同じ区間の別アングル）。
            if (!_pgc.IsCellForAngle(i, _angle))
                continue;
            // インターリーブブロック（C_CAT bit2＝interleaved_allocation_flag）は、同一セクタ領域に複数の分岐/アングルの
            // VOBU が交互配置されている（シームレス分岐）。[First..Last] をそのまま連結すると別枝の VOBU まで読み込んで
            // 映像が混ざるため、各 VOBU の NAV(DSI) の VOB_ID/Cell_ID を見て「このセルの枝の VOBU」だけを拾う。
            if ((c.Category & 0x04) != 0)
                ranges.AddRange(InterleavedCellRanges(c));
            else
                ranges.Add((c.FirstSector, c.LastSector));
        }
        return ranges;
    }

    // インターリーブセルの再生対象サブレンジ（VOBU 単位）。各 VOBU 先頭の NAV パック DSI から VOB_ID/Cell_ID/VOBU_EA を
    // 読み、セルの (VobId,CellId) に一致する VOBU だけを集める。NAV が読めない/一致0件なら安全側で全域へフォールバック。
    private List<(long First, long Last)> InterleavedCellRanges(DvdCell c)
    {
        var result = new List<(long, long)>();
        try
        {
            using var fs = MediaFile.OpenRead(_isoPath);
            var buf = new byte[2048];
            long sector = c.FirstSector;
            int guard = 0;
            while (sector <= c.LastSector && guard++ < 2_000_000)
            {
                if (!_space.TryMap(sector, out long isoOff, out _))
                    break;
                fs.Position = isoOff;
                if (!ReadFull(fs, buf, 2048))
                    break;
                if (!TryParseNavDsi(buf, out int vobId, out int cellId, out long vobuEA))
                    break; // NAV パックでない（想定外）→ 安全側で打ち切り、フォールバックへ
                long vobuLast = Math.Min(c.LastSector, sector + vobuEA);
                if (vobuLast < sector)
                    break;
                if (vobId == c.VobId && cellId == c.CellId)
                {
                    int li = result.Count - 1;
                    if (li >= 0 && result[li].Item2 + 1 == sector) // 直前 VOBU と連続なら結合
                        result[li] = (result[li].Item1, vobuLast);
                    else
                        result.Add((sector, vobuLast));
                }
                sector = vobuLast + 1;
            }
        }
        catch { result.Clear(); }

        if (result.Count == 0) // 解析失敗/一致0件は従来どおり全域（壊さない）
            result.Add((c.FirstSector, c.LastSector));
        return result;
    }

    private static bool ReadFull(Stream s, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    // NAV パック（VOBU 先頭 2048B）の DSI から VOBU_VOB_IDN / VOBU_C_IDN / VOBU_EA を取り出す。
    // NAV パック配置は固定：pack header(0x000, 00 00 01 BA)、DSI PES(0x400, 00 00 01 BF)、DSI substream 0x01(0x406)、
    // DSI_GI(0x407 起点)。DSI_GI 内 VOBU_EA@+0x08 / VOBU_VOB_IDN@+0x18 / VOBU_C_IDN@+0x1B（すべてビッグエンディアン）。
    private static bool TryParseNavDsi(byte[] nav, out int vobId, out int cellId, out long vobuEA)
    {
        vobId = 0; cellId = 0; vobuEA = 0;
        if (nav.Length < 0x423)
            return false;
        if (!(nav[0x000] == 0x00 && nav[0x001] == 0x00 && nav[0x002] == 0x01 && nav[0x003] == 0xBA))
            return false; // pack header でない
        if (!(nav[0x400] == 0x00 && nav[0x401] == 0x00 && nav[0x402] == 0x01 && nav[0x403] == 0xBF))
            return false; // DSI PES(private_stream_2) でない
        if (nav[0x406] != 0x01)
            return false; // DSI substream id でない
        vobuEA = DvdIfoBin.U32(nav, 0x40F); // DSI_GI+0x08
        vobId = DvdIfoBin.U16(nav, 0x41F);  // DSI_GI+0x18
        cellId = nav[0x422];                 // DSI_GI+0x1B
        return true;
    }
}
