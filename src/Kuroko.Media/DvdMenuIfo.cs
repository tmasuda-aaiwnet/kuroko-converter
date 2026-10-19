namespace Kuroko.Media.Dvd;

// メニューの種別（PGCI_SRP のカテゴリ下位4bit）。
internal enum DvdMenuType
{
    Title = 2,       // タイトルメニュー（VMGM のトップ）
    Root = 3,        // ルートメニュー
    Subpicture = 4,
    Audio = 5,
    Angle = 6,
    Chapter = 7,     // PTT（チャプター）メニュー
}

internal sealed class DvdMenuPgc
{
    public DvdMenuType Type { get; init; }
    public DvdPgc Pgc { get; init; } = null!;
}

// VMGM_PGCI_UT / VTSM_PGCI_UT（メニュー PGC ユニットテーブル）を解析する。
// 構造は VTS_PGCIT と似るが、先頭に言語ユニット(LU)の階層を持つ。先頭 LU のメニュー PGC 群を返す。
internal static class DvdMenuIfo
{
    private static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static long U32(byte[] b, int o) => ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];

    public static List<DvdMenuPgc> ParsePgciUt(byte[] ifo, long pgciUtSector)
    {
        var result = new List<DvdMenuPgc>();
        if (pgciUtSector <= 0)
            return result;
        int baseOff = checked((int)(pgciUtSector * 2048));
        if (baseOff + 8 > ifo.Length)
            return result;

        int nrLu = U16(ifo, baseOff);
        if (nrLu < 1)
            return result;

        // 先頭言語ユニット（LU_SRP は 8バイト：langCode(2) reserved(1) exists(1) LU開始(u32)）。
        long luStart = U32(ifo, baseOff + 8 + 4);
        int pgcitOff = baseOff + checked((int)luStart);
        if (pgcitOff + 8 > ifo.Length)
            return result;

        int nrPgc = U16(ifo, pgcitOff);
        for (int j = 0; j < nrPgc; j++)
        {
            int srp = pgcitOff + 8 + j * 8;
            if (srp + 8 > ifo.Length)
                break;
            int category = ifo[srp];
            int menuType = category & 0x0F;
            long pgcOff = U32(ifo, srp + 4);
            var pgc = DvdVtsIfo.ParsePgc(ifo, pgcitOff + checked((int)pgcOff), j + 1);
            if (pgc is not null)
                result.Add(new DvdMenuPgc { Type = (DvdMenuType)menuType, Pgc = pgc });
        }
        return result;
    }
}
