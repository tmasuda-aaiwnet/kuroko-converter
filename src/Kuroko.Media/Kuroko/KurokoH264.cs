namespace Kuroko.Media.Krk;

// Matroska の H.264/H.265 は AVCC/HVCC（length 前置 NAL）で格納される。一方 再生器の映像デコーダは
// Annex-B（開始コード 00 00 00 01）前提。Kuroko の変換器は x264/x265 を repeat-headers=1 で符号化し、
// 各IDR直前に SPS/PPS(/VPS) を**在留**させるので、length 前置を開始コードへ置換するだけで（extradata 不要で）
// デコーダに食わせられる。lengthSize は既定 4。長さで完全にタイルできなければ既に Annex-B とみなし素通し。
internal static class KurokoH264
{
    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    public static byte[] AvccToAnnexB(byte[] data, int lengthSize = 4)
    {
        if (data.Length == 0) return data;

        // 「先頭が 00 00 01 か」のヒューリスティックは使わない（AVCC の length 前置 00 00 01xx を
        //  誤検出するため）。length で全 NAL を過不足なくタイルできる lengthSize を採用する。matroska は
        //  常に AVCC（既定 length=4）。既定で駄目なら 2,1 を試し、どれもタイルできなければ素通し
        //  （＝既に Annex-B 等とみなす）。
        if (!TilesAsAvcc(data, lengthSize))
        {
            if (TilesAsAvcc(data, 4)) lengthSize = 4;
            else if (TilesAsAvcc(data, 2)) lengthSize = 2;
            else if (TilesAsAvcc(data, 1)) lengthSize = 1;
            else return data;
        }

        // 置換：各 NAL の length 前置を 00 00 00 01 開始コードに。
        using var ms = new System.IO.MemoryStream(data.Length + 16);
        int pos = 0;
        while (pos + lengthSize <= data.Length)
        {
            long n = 0;
            for (int i = 0; i < lengthSize; i++) n = (n << 8) | data[pos + i];
            pos += lengthSize;
            ms.Write(StartCode, 0, StartCode.Length);
            ms.Write(data, pos, (int)n);
            pos += (int)n;
        }
        return ms.ToArray();
    }

    // data が lengthSize バイトの big-endian 長さ前置 NAL 列として過不足なくタイルできるか。
    private static bool TilesAsAvcc(byte[] data, int lengthSize)
    {
        if (lengthSize is not (1 or 2 or 4)) return false;
        int pos = 0;
        int count = 0;
        while (pos + lengthSize <= data.Length)
        {
            long n = 0;
            for (int i = 0; i < lengthSize; i++) n = (n << 8) | data[pos + i];
            pos += lengthSize;
            if (n <= 0 || pos + n > data.Length) return false;
            pos += (int)n;
            count++;
        }
        return pos == data.Length && count > 0;
    }
}
