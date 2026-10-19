using System.IO;
using System.Text;

namespace Kuroko.Media.Dvd;

// DVD-Video の IFO（VMG=VIDEO_TS.IFO / VTS=VTS_xx_0.IFO）の最小パーサ。
//
// 方針（[docs/dvd-iso-engine-spec.md]）：LGPL を保つためのクリーンルーム実装。
// GPL の libdvdread/libdvdnav の「コード」は参照・流用せず、公開された DVD-Video の
// フォーマット（IFO のテーブル構造）に基づいて自前で解析する。
// IFO 内の多バイト値は基本ビッグエンディアン、各テーブルの「オフセット」は IFO 先頭からの
// セクタ番号（2048B）またはバイトオフセットで格納される（テーブルごとに規定）。

internal static class DvdIfoBin
{
    public static int U8(byte[] b, int o) => b[o];
    public static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    public static long U32(byte[] b, int o) => ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
}

// DVD の再生時間（BCD hh:mm:ss:ff ＋ フレームレート）。
internal readonly struct DvdTime
{
    public readonly int Hours;
    public readonly int Minutes;
    public readonly int Seconds;
    public readonly int Frames;
    public readonly double FrameRate; // 29.97 or 25（不明時は0）

    public DvdTime(byte[] b, int o)
    {
        static int Bcd(int v) => ((v >> 4) & 0x0F) * 10 + (v & 0x0F);
        Hours = Bcd(b[o]);
        Minutes = Bcd(b[o + 1]);
        Seconds = Bcd(b[o + 2]);
        int f = b[o + 3];
        int rateCode = (f >> 6) & 0x03;
        FrameRate = rateCode switch { 0x03 => 30000.0 / 1001.0, 0x01 => 25.0, _ => 0.0 };
        Frames = Bcd(f & 0x3F);
    }

    public TimeSpan ToTimeSpan()
    {
        double secs = Hours * 3600.0 + Minutes * 60.0 + Seconds;
        if (FrameRate > 0)
            secs += Frames / FrameRate;
        return TimeSpan.FromSeconds(secs);
    }
}

internal enum DvdAudioCodingMode
{
    Ac3 = 0,
    Mpeg1 = 2,
    Mpeg2Ext = 3,
    Lpcm = 4,
    Dts = 6,
    Unknown = 0xFF,
}

internal sealed class DvdAudioAttr
{
    public DvdAudioCodingMode CodingMode { get; init; }
    public int Channels { get; init; }       // 1..6
    public int SampleRateHz { get; init; }    // 48000 / 96000
    public int BitsPerSample { get; init; }   // LPCM の量子化ビット数（16/20/24）
    public string? LanguageCode { get; init; } // ISO639 2文字（無ければ null）
    public bool HasLanguage { get; init; }
}

internal sealed class DvdSubpictureAttr
{
    public string? LanguageCode { get; init; }
    public bool HasLanguage { get; init; }
}

internal sealed class DvdVideoAttr
{
    public bool IsPal { get; init; }            // true=PAL(25fps), false=NTSC(29.97)
    public bool IsMpeg2 { get; init; }
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public string AspectRatio { get; init; } = "4:3";
}

// PGC 内の1セル。VOB アドレス空間（連結 VTS_xx_1.VOB..9.VOB）におけるセクタ範囲を持つ。
internal sealed class DvdCell
{
    public int Category { get; init; }
    public int StillTime { get; init; }          // セル静止時間（秒）。0=なし、0xFF=無限（ユーザ入力待ち）
    public int CommandNr { get; init; }          // セル終端で実行する CELL コマンド番号（1-based、0=なし）。CELL_PB offset 0x03
    public DvdTime PlaybackTime { get; init; }
    public long FirstSector { get; init; }     // 最初の VOBU 開始セクタ
    public long LastSector { get; init; }      // 最後の VOBU 終了セクタ（このセルの末尾）
    public int VobId { get; init; }
    public int CellId { get; init; }

    public TimeSpan Duration => PlaybackTime.ToTimeSpan();
}

// Program Chain（タイトルの実再生単位）。
internal sealed class DvdPgc
{
    public int Number { get; init; }           // 1-based（VTS_PGCIT 内）
    public DvdTime PlaybackTime { get; init; }
    public uint UopControl { get; init; }      // 禁止ユーザー操作 PGC_UOP_CTL（0x08, 4バイト。bitセット=禁止）
    // PGC ヘッダのナビゲーションリンク（0=なし）。セル/Link/POST が無いPGCの完了後に next を辿る等に使う。
    public int NextPgcn { get; init; }         // 0x9C
    public int PrevPgcn { get; init; }         // 0x9E
    public int GoUpPgcn { get; init; }         // 0xA0
    public IReadOnlyList<DvdCell> Cells { get; init; } = Array.Empty<DvdCell>();
    public IReadOnlyList<int> ProgramEntryCells { get; init; } = Array.Empty<int>(); // program→開始セル(1-based)
    public uint[] Palette { get; init; } = new uint[16]; // サブピクチャ用 YUV パレット
    public IReadOnlyList<byte[]> PreCommands { get; init; } = Array.Empty<byte[]>();   // PGC 先頭命令（DVD VM）
    public IReadOnlyList<byte[]> PostCommands { get; init; } = Array.Empty<byte[]>();  // PGC 末尾命令
    public IReadOnlyList<byte[]> CellCommands { get; init; } = Array.Empty<byte[]>();  // セル命令
    // PGC 音声ストリーム制御表（0x0C, 8×2）。論理音声番号→有効性＋実ストリーム番号。
    public IReadOnlyList<DvdAudioStreamControl> AudioControl { get; init; } = Array.Empty<DvdAudioStreamControl>();
    // PGC 字幕ストリーム制御表（0x1C, 32×4）。論理字幕番号→有効性＋表示モード別(4:3/wide/LB/PS)の実ストリーム番号。
    public IReadOnlyList<DvdSubpStreamControl> SubpictureControl { get; init; } = Array.Empty<DvdSubpStreamControl>();

    // ── アングルブロック（C_CAT） ───────────────────────────────────────────────
    // 同じ区間を別アングルで撮ったセルが**連続して並ぶ**のがアングルブロック。C_CAT の上位バイトで
    //   block_mode (bit7-6)… 0=ブロック外 / 1=ブロック先頭 / 2=ブロック中 / 3=ブロック末尾
    //   block_type (bit5-4)… 1=アングルブロック
    // ブロック内の並び順がそのまま**アングル番号（1 始まり）**。再生時は現在のアングルのセルだけを再生し、
    // 残りは飛ばす。飛ばさないと同じ区間がアングルの数だけ繰り返し再生され、しかも別アングルは別 VOB なので
    // 音声まで変わって聞こえる（実測：ある市販ディスク タイトル1は先頭に 2 アングルのブロックが 3 つあり、先頭 38.2 秒が
    // 二重に再生されていた。ディスクが申告する PGC 尺 01:17:39.63 に対し、全セル合計は 01:18:18 だった）。
    private int[]? _angleOrdinals;    // ブロック内の順番（1 始まり）。0＝ブロック外
    private int[]? _angleBlockSize;   // そのセルが属するブロックのセル数。0＝ブロック外
    private int _angleCount;

    private void EnsureAngleMap()
    {
        if (_angleOrdinals is not null) return;
        int n = Cells.Count;
        var ord = new int[n];
        var size = new int[n];
        int max = 1;
        for (int i = 0; i < n; i++)
        {
            int cat = Cells[i].Category;
            int blockMode = (cat >> 6) & 3;
            int blockType = (cat >> 4) & 3;
            if (blockType != 1 || blockMode == 0) continue;   // アングルブロックでない
            if (blockMode != 1) continue;                      // ブロック先頭からだけ辿る（途中から拾わない）
            // 先頭から末尾（block_mode=3）までを 1 ブロックとして数える。末尾が無い壊れた並びは、
            // アングルブロックでないセルに当たった時点で打ち切る（安全側＝そこまでを 1 ブロックとする）。
            int count = 0;
            for (int j = i; j < n; j++)
            {
                int c2 = Cells[j].Category;
                if (((c2 >> 4) & 3) != 1) break;
                int bm2 = (c2 >> 6) & 3;
                if (j > i && bm2 == 1) break;                  // 次のブロックが始まった
                count++;
                if (bm2 == 3) break;                           // ブロック末尾
            }
            for (int j = 0; j < count; j++) { ord[i + j] = j + 1; size[i + j] = count; }
            if (count > max) max = count;
            i += count - 1;
        }
        _angleBlockSize = size;
        _angleCount = max;
        _angleOrdinals = ord;   // 最後に入れる（他フィールドが揃ってから見えるように）
    }

    // この PGC のアングル数（1＝アングル無し）。
    public int AngleCount { get { EnsureAngleMap(); return _angleCount; } }

    // このセルをアングル angle で再生するか。ブロック外のセルは常に再生する。
    // ブロックのセル数がアングル番号に満たないディスクでは、そのブロックの最後のセルを使う（保険）。
    // **angle が 0 以下なら絞り込まない**（＝全セル）。変換器のようにセルを 1 個ずつ直接指定して
    // 取り出す用途は、アングルで間引かれると区間が消えてしまうため、既定はこちら（DvdDisc.Angle=0）。
    public bool IsCellForAngle(int cellIndex, int angle)
    {
        if (angle <= 0) return true;
        EnsureAngleMap();
        var ord = _angleOrdinals!;
        if (cellIndex < 0 || cellIndex >= ord.Length) return false;
        if (ord[cellIndex] == 0) return true;
        int want = Math.Clamp(angle, 1, _angleCount);
        int blockSize = _angleBlockSize![cellIndex];
        if (want > blockSize) want = blockSize;
        return ord[cellIndex] == want;
    }

    // 診断用：そのセルのアングルブロック内の順番（1 始まり）とブロックのセル数。ブロック外は (0,0)。
    public (int Ordinal, int BlockSize) AngleOrdinalOfCell(int cellIndex)
    {
        EnsureAngleMap();
        var ord = _angleOrdinals!;
        if (cellIndex < 0 || cellIndex >= ord.Length) return (0, 0);
        return (ord[cellIndex], _angleBlockSize![cellIndex]);
    }

    public TimeSpan Duration => PlaybackTime.ToTimeSpan();
}

// PGC_AST_CTL の1エントリ（2バイト）。byte0 bit7=有効、byte0 下位=実音声ストリーム番号。
// 実サブストリームID = コーデック基底(AC3=0x80/DTS=0x88/LPCM=0xA0/MPEG=0xC0) + Number。
internal readonly record struct DvdAudioStreamControl(bool Available, int Number);

// PGC_SPST_CTL の1エントリ（4バイト）。byte0 bit7=有効、各バイト下位=表示モード別の実字幕ストリーム番号。
// 実サブストリームID = 0x20 + 番号。
internal readonly record struct DvdSubpStreamControl(bool Available, int N43, int NWide, int NLetterbox, int NPanScan);

// VMG（VIDEO_TS.IFO）のタイトル1件。
internal sealed class DvdTitleEntry
{
    public int TitleNumber { get; init; }      // ディスク全体の通し番号（1-based）
    public int VtsNumber { get; init; }        // 所属 VTS 番号（1-based）
    public int VtsTitleNumber { get; init; }   // VTS 内タイトル番号（1-based）
    public int ChapterCount { get; init; }
    public int AngleCount { get; init; }
}

// VMG（VIDEO_TS.IFO）。タイトル一覧を提供。
internal sealed class DvdVmgIfo
{
    public int VtsCount { get; private set; }
    public IReadOnlyList<DvdTitleEntry> Titles { get; private set; } = Array.Empty<DvdTitleEntry>();

    // First Play PGC（ディスク投入時に最初に実行する PGC）の PRE 命令（DVD VM）。
    // 多くは「JumpTT でタイトル再生」または「JumpSS でトップメニュー表示」へ解決する。
    public IReadOnlyList<byte[]> FirstPlayCommands { get; private set; } = Array.Empty<byte[]>();

    public static DvdVmgIfo Parse(byte[] ifo)
    {
        if (ifo.Length < 0xCC || Encoding.ASCII.GetString(ifo, 0, 12) != "DVDVIDEO-VMG")
            throw new InvalidDataException("VIDEO_TS.IFO（VMG）の識別子が不正です。");

        var vmg = new DvdVmgIfo
        {
            VtsCount = DvdIfoBin.U16(ifo, 0x3E),
        };

        // First Play PGC（VMGI 先頭からのバイトオフセット、offset 0x84）。
        long fpPgcByte = DvdIfoBin.U32(ifo, 0x84);
        if (fpPgcByte > 0 && fpPgcByte + 0xEC <= ifo.Length)
        {
            var fp = DvdVtsIfo.ParsePgc(ifo, checked((int)fpPgcByte), 0);
            if (fp is not null)
                vmg.FirstPlayCommands = fp.PreCommands;
        }

        // TT_SRPT（タイトル検索ポインタ表）の位置：VMGI 先頭からのセクタ番号（offset 0xC4）。
        long ttSrptSector = DvdIfoBin.U32(ifo, 0xC4);
        if (ttSrptSector == 0)
            return vmg;
        int baseOff = checked((int)(ttSrptSector * 2048));
        if (baseOff + 8 > ifo.Length)
            return vmg;

        int nrTitles = DvdIfoBin.U16(ifo, baseOff);
        var titles = new List<DvdTitleEntry>(nrTitles);
        for (int i = 0; i < nrTitles; i++)
        {
            int e = baseOff + 8 + i * 12;
            if (e + 12 > ifo.Length)
                break;
            titles.Add(new DvdTitleEntry
            {
                TitleNumber = i + 1,
                AngleCount = DvdIfoBin.U8(ifo, e + 1),
                ChapterCount = DvdIfoBin.U16(ifo, e + 2),
                VtsNumber = DvdIfoBin.U8(ifo, e + 6),
                VtsTitleNumber = DvdIfoBin.U8(ifo, e + 7),
            });
        }
        vmg.Titles = titles;
        return vmg;
    }
}

// VTS（VTS_xx_0.IFO）。ストリーム属性・PGC・PTT(章) を提供。
internal sealed class DvdVtsIfo
{
    public DvdVideoAttr VideoAttr { get; private set; } = new();
    public IReadOnlyList<DvdAudioAttr> AudioStreams { get; private set; } = Array.Empty<DvdAudioAttr>();
    public IReadOnlyList<DvdSubpictureAttr> SubpictureStreams { get; private set; } = Array.Empty<DvdSubpictureAttr>();
    public long VtsmPgciUtSector { get; private set; } // VTSM_PGCI_UT（メニューPGCユニット表）
    public IReadOnlyList<DvdPgc> Pgcs { get; private set; } = Array.Empty<DvdPgc>();

    // VTS 内タイトル番号(1-based) → そのタイトルの PTT 列（各章の (PGCN, PGN)）。
    private List<List<(int Pgcn, int Pgn)>> _pttByTitle = new();

    public IReadOnlyList<(int Pgcn, int Pgn)> GetTitleChapters(int vtsTitleNumber)
    {
        int idx = vtsTitleNumber - 1;
        if (idx < 0 || idx >= _pttByTitle.Count)
            return Array.Empty<(int, int)>();
        return _pttByTitle[idx];
    }

    public DvdPgc? GetPgc(int pgcn)
    {
        foreach (var p in Pgcs)
            if (p.Number == pgcn)
                return p;
        return null;
    }

    public static DvdVtsIfo Parse(byte[] ifo)
    {
        if (ifo.Length < 0x100 || Encoding.ASCII.GetString(ifo, 0, 12) != "DVDVIDEO-VTS")
            throw new InvalidDataException("VTS_xx_0.IFO（VTS）の識別子が不正です。");

        var vts = new DvdVtsIfo();

        // 各テーブルの位置（VTSI 先頭からのセクタ番号）。VTSI_MAT のポインタ群は 0xC0 以降：
        // 0xC0=VTSM_VOBS, 0xC4=VTSTT_VOBS, 0xC8=VTS_PTT_SRPT, 0xCC=VTS_PGCIT, 0xD0=VTSM_PGCI_UT,
        // 0xE0=VTS_C_ADT, 0xE4=VTS_VOBU_ADMAP。
        long pttSrptSector = DvdIfoBin.U32(ifo, 0xC8);  // VTS_PTT_SRPT
        long pgcitSector = DvdIfoBin.U32(ifo, 0xCC);    // VTS_PGCIT
        vts.VtsmPgciUtSector = DvdIfoBin.U32(ifo, 0xD0); // VTSM_PGCI_UT（メニュー）

        vts.VideoAttr = ParseVideoAttr(ifo, 0x200);
        vts.AudioStreams = ParseAudioStreams(ifo);
        vts.SubpictureStreams = ParseSubpictureStreams(ifo);

        if (pgcitSector > 0)
            vts.Pgcs = ParsePgcit(ifo, checked((int)(pgcitSector * 2048)));
        if (pttSrptSector > 0)
            vts._pttByTitle = ParsePttSrpt(ifo, checked((int)(pttSrptSector * 2048)));

        return vts;
    }

    // メニュー映像属性（VMGM_V_ATR / VTSM_V_ATR は IFO 先頭から 0x100）。VMGM メニュー再生用。
    public static DvdVideoAttr ParseMenuVideoAttr(byte[] ifoBytes) => ParseVideoAttr(ifoBytes, 0x100);

    private static DvdVideoAttr ParseVideoAttr(byte[] b, int o)
    {
        if (o + 2 > b.Length)
            return new DvdVideoAttr();
        int v0 = b[o];
        int v1 = b[o + 1];
        bool pal = ((v0 >> 4) & 0x03) == 0x01;       // ビット5-4: 0=NTSC,1=PAL
        bool mpeg2 = ((v0 >> 6) & 0x03) == 0x01;     // ビット7-6: コーディングモード
        int aspect = (v0 >> 2) & 0x03;               // ビット3-2: 0=4:3, 3=16:9
        // 解像度（ビット5-3 of v1）。NTSC/PAL で高さが異なる。
        int resCode = (v1 >> 3) & 0x07;
        (int w, int h) = (pal, resCode) switch
        {
            (false, 0) => (720, 480),
            (false, 1) => (704, 480),
            (false, 2) => (352, 480),
            (false, 3) => (352, 240),
            (true, 0) => (720, 576),
            (true, 1) => (704, 576),
            (true, 2) => (352, 576),
            (true, 3) => (352, 288),
            _ => (720, pal ? 576 : 480),
        };
        return new DvdVideoAttr
        {
            IsPal = pal,
            IsMpeg2 = mpeg2,
            DisplayWidth = w,
            DisplayHeight = h,
            AspectRatio = aspect == 3 ? "16:9" : "4:3",
        };
    }

    private static List<DvdAudioAttr> ParseAudioStreams(byte[] b)
    {
        var list = new List<DvdAudioAttr>();
        if (0x202 + 2 > b.Length)
            return list;
        int count = DvdIfoBin.U16(b, 0x202);
        if (count > 8)
            count = 8;
        for (int i = 0; i < count; i++)
        {
            int o = 0x204 + i * 8;
            if (o + 8 > b.Length)
                break;
            int a0 = b[o];
            int a1 = b[o + 1];
            var coding = ((a0 >> 5) & 0x07) switch
            {
                0 => DvdAudioCodingMode.Ac3,
                2 => DvdAudioCodingMode.Mpeg1,
                3 => DvdAudioCodingMode.Mpeg2Ext,
                4 => DvdAudioCodingMode.Lpcm,
                6 => DvdAudioCodingMode.Dts,
                _ => DvdAudioCodingMode.Unknown,
            };
            int langType = (a0 >> 2) & 0x03;
            int sampleRate = ((a1 >> 4) & 0x03) == 0 ? 48000 : 96000;
            int channels = (a1 & 0x07) + 1;
            // LPCM の量子化（a1 ビット7-6: 0=16,1=20,2=24bit）。
            int bits = ((a1 >> 6) & 0x03) switch { 1 => 20, 2 => 24, _ => 16 };
            string? lang = null;
            bool hasLang = langType == 1;
            if (hasLang && o + 4 <= b.Length)
            {
                lang = DecodeLang(b, o + 2);
                if (string.IsNullOrWhiteSpace(lang))
                    hasLang = false;
            }
            list.Add(new DvdAudioAttr
            {
                CodingMode = coding,
                Channels = channels,
                SampleRateHz = sampleRate,
                BitsPerSample = bits,
                LanguageCode = lang,
                HasLanguage = hasLang,
            });
        }
        return list;
    }

    private static List<DvdSubpictureAttr> ParseSubpictureStreams(byte[] b)
    {
        var list = new List<DvdSubpictureAttr>();
        if (0x254 + 2 > b.Length)
            return list;
        int count = DvdIfoBin.U16(b, 0x254);
        if (count > 32)
            count = 32;
        for (int i = 0; i < count; i++)
        {
            int o = 0x256 + i * 6;
            if (o + 6 > b.Length)
                break;
            int langType = (b[o] >> 0) & 0x03;
            string? lang = null;
            bool hasLang = langType == 1;
            if (hasLang)
            {
                lang = DecodeLang(b, o + 2);
                if (string.IsNullOrWhiteSpace(lang))
                    hasLang = false;
            }
            list.Add(new DvdSubpictureAttr { LanguageCode = lang, HasLanguage = hasLang });
        }
        return list;
    }

    private static string? DecodeLang(byte[] b, int o)
    {
        if (o + 2 > b.Length)
            return null;
        char c0 = (char)b[o];
        char c1 = (char)b[o + 1];
        if (char.IsLetter(c0) && char.IsLetter(c1))
            return new string(new[] { c0, c1 }).ToLowerInvariant();
        return null;
    }

    private static List<DvdPgc> ParsePgcit(byte[] b, int baseOff)
    {
        var list = new List<DvdPgc>();
        if (baseOff + 8 > b.Length)
            return list;
        int nr = DvdIfoBin.U16(b, baseOff);
        for (int i = 0; i < nr; i++)
        {
            int srp = baseOff + 8 + i * 8;
            if (srp + 8 > b.Length)
                break;
            long pgcOff = DvdIfoBin.U32(b, srp + 4); // VTS_PGCIT 先頭からのバイトオフセット
            int pgcAbs = baseOff + checked((int)pgcOff);
            var pgc = ParsePgc(b, pgcAbs, i + 1);
            if (pgc is not null)
                list.Add(pgc);
        }
        return list;
    }

    internal static DvdPgc? ParsePgc(byte[] b, int o, int number)
    {
        if (o + 0xEC > b.Length)
            return null;
        int nrPrograms = DvdIfoBin.U8(b, o + 0x02);
        int nrCells = DvdIfoBin.U8(b, o + 0x03);
        var playbackTime = new DvdTime(b, o + 0x04);
        uint uopControl = (uint)DvdIfoBin.U32(b, o + 0x08); // PGC_UOP_CTL（禁止ユーザー操作）

        // パレット（offset 0xA4, 16×4バイト）。
        var palette = new uint[16];
        for (int i = 0; i < 16; i++)
            palette[i] = (uint)DvdIfoBin.U32(b, o + 0xA4 + i * 4);

        int cmdTblOff = DvdIfoBin.U16(b, o + 0xE4);
        int pgmapOff = DvdIfoBin.U16(b, o + 0xE6);
        int cellPbOff = DvdIfoBin.U16(b, o + 0xE8);
        int cellPosOff = DvdIfoBin.U16(b, o + 0xEA);

        // PGC コマンド表（PRE/POST/CELL 命令、各8バイト）。
        var preCmds = new List<byte[]>();
        var postCmds = new List<byte[]>();
        var cellCmds = new List<byte[]>();
        if (cmdTblOff > 0 && o + cmdTblOff + 8 <= b.Length)
        {
            int ct = o + cmdTblOff;
            int nrPre = DvdIfoBin.U16(b, ct);
            int nrPost = DvdIfoBin.U16(b, ct + 2);
            int nrCell = DvdIfoBin.U16(b, ct + 4);
            int p = ct + 8;
            byte[]? ReadCmd()
            {
                if (p + 8 > b.Length) return null;
                var c = new byte[8];
                Array.Copy(b, p, c, 0, 8);
                p += 8;
                return c;
            }
            for (int i = 0; i < nrPre; i++) { var c = ReadCmd(); if (c is null) break; preCmds.Add(c); }
            for (int i = 0; i < nrPost; i++) { var c = ReadCmd(); if (c is null) break; postCmds.Add(c); }
            for (int i = 0; i < nrCell; i++) { var c = ReadCmd(); if (c is null) break; cellCmds.Add(c); }
        }

        // プログラム→開始セル（PGC_PGMAP：1バイト×program数）。
        var entryCells = new List<int>(nrPrograms);
        if (pgmapOff > 0)
        {
            for (int i = 0; i < nrPrograms; i++)
            {
                int p = o + pgmapOff + i;
                if (p < b.Length)
                    entryCells.Add(DvdIfoBin.U8(b, p));
            }
        }

        // セル再生情報（CELL_PLAYBACK：24バイト×cell数）＋セル位置（CELL_POSITION：4バイト×cell数）。
        var cells = new List<DvdCell>(nrCells);
        for (int i = 0; i < nrCells; i++)
        {
            int cp = o + cellPbOff + i * 0x18;
            if (cellPbOff == 0 || cp + 0x18 > b.Length)
                break;
            int category = DvdIfoBin.U8(b, cp);
            int stillTime = DvdIfoBin.U8(b, cp + 0x02); // セル静止時間（秒。0xFF=無限）
            int cellCmdNr = DvdIfoBin.U8(b, cp + 0x03); // セルコマンド番号（1-based、0=なし）
            var cellTime = new DvdTime(b, cp + 0x04);
            long firstSector = DvdIfoBin.U32(b, cp + 0x08);
            long lastSector = DvdIfoBin.U32(b, cp + 0x14);

            int vobId = 0, cellId = 0;
            if (cellPosOff > 0)
            {
                int pp = o + cellPosOff + i * 4;
                if (pp + 4 <= b.Length)
                {
                    vobId = DvdIfoBin.U16(b, pp);
                    cellId = DvdIfoBin.U8(b, pp + 3);
                }
            }

            cells.Add(new DvdCell
            {
                Category = category,
                StillTime = stillTime,
                CommandNr = cellCmdNr,
                PlaybackTime = cellTime,
                FirstSector = firstSector,
                LastSector = lastSector,
                VobId = vobId,
                CellId = cellId,
            });
        }

        // PGC ヘッダのナビゲーションリンク（next/prev/goup PGCN）。
        int nextPgcn = o + 0x9E <= b.Length ? DvdIfoBin.U16(b, o + 0x9C) : 0;
        int prevPgcn = o + 0xA0 <= b.Length ? DvdIfoBin.U16(b, o + 0x9E) : 0;
        int goupPgcn = o + 0xA2 <= b.Length ? DvdIfoBin.U16(b, o + 0xA0) : 0;

        // PGC 音声ストリーム制御表（0x0C, 8×2）。byte0 bit7=有効, byte0 下位=実音声ストリーム番号。
        var audioCtl = new List<DvdAudioStreamControl>(8);
        for (int i = 0; i < 8; i++)
        {
            int p = o + 0x0C + i * 2;
            if (p + 2 > b.Length) break;
            int b0 = DvdIfoBin.U8(b, p);
            audioCtl.Add(new DvdAudioStreamControl((b0 & 0x80) != 0, b0 & 0x07));
        }
        // PGC 字幕ストリーム制御表（0x1C, 32×4）。byte0 bit7=有効, 各バイト下位5bit=表示モード別の実字幕番号。
        var subpCtl = new List<DvdSubpStreamControl>(32);
        for (int i = 0; i < 32; i++)
        {
            int p = o + 0x1C + i * 4;
            if (p + 4 > b.Length) break;
            int b0 = DvdIfoBin.U8(b, p), b1 = DvdIfoBin.U8(b, p + 1), b2 = DvdIfoBin.U8(b, p + 2), b3 = DvdIfoBin.U8(b, p + 3);
            subpCtl.Add(new DvdSubpStreamControl((b0 & 0x80) != 0, b0 & 0x1F, b1 & 0x1F, b2 & 0x1F, b3 & 0x1F));
        }

        return new DvdPgc
        {
            Number = number,
            PlaybackTime = playbackTime,
            UopControl = uopControl,
            NextPgcn = nextPgcn,
            PrevPgcn = prevPgcn,
            GoUpPgcn = goupPgcn,
            Cells = cells,
            ProgramEntryCells = entryCells,
            Palette = palette,
            PreCommands = preCmds,
            PostCommands = postCmds,
            CellCommands = cellCmds,
            AudioControl = audioCtl,
            SubpictureControl = subpCtl,
        };
    }

    private static List<List<(int Pgcn, int Pgn)>> ParsePttSrpt(byte[] b, int baseOff)
    {
        var result = new List<List<(int, int)>>();
        if (baseOff + 8 > b.Length)
            return result;
        int nrTitles = DvdIfoBin.U16(b, baseOff);
        // 各タイトルの PTT 列へのオフセット表（u32×タイトル数、baseOff からのバイト）。
        var offsets = new long[nrTitles + 1];
        for (int i = 0; i < nrTitles; i++)
        {
            int p = baseOff + 8 + i * 4;
            if (p + 4 > b.Length) { offsets[i] = -1; continue; }
            offsets[i] = DvdIfoBin.U32(b, p);
        }
        // 終端：テーブル末尾アドレス（offset 4, u32）+1。
        long endAddr = DvdIfoBin.U32(b, baseOff + 4) + 1;
        offsets[nrTitles] = endAddr;

        for (int i = 0; i < nrTitles; i++)
        {
            var ptts = new List<(int, int)>();
            if (offsets[i] < 0)
            {
                result.Add(ptts);
                continue;
            }
            long start = offsets[i];
            long end = offsets[i + 1] > start ? offsets[i + 1] : start;
            // PTT エントリは4バイト（PGCN u16, PGN u16）。
            for (long p = start; p + 4 <= end; p += 4)
            {
                int abs = baseOff + (int)p;
                if (abs + 4 > b.Length)
                    break;
                int pgcn = DvdIfoBin.U16(b, abs);
                int pgn = DvdIfoBin.U16(b, abs + 2);
                ptts.Add((pgcn, pgn));
            }
            result.Add(ptts);
        }
        return result;
    }
}
