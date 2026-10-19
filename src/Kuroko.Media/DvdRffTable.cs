using System.IO;

namespace Kuroko.Media.Dvd;

// H.264化DVD（Vuecove拡張）の「フィールド数テーブル」。元MPEG-2のソフトテレシネ(RFF)情報を保持し、
// 再生側で各フレームの表示時間（=フィールド数×フィールド時間）と bob のフィールド枚数を厳密に再現する。
// H.264化で RFF フラグが失われ、かつ VOB の PTS が一部欠落するため、テレシネ部のフレーム単位の正確な
// タイミングは VOB だけからは復元できない。そこで変換時に本テーブルを作り VIDEO_TS/VUECOVE.RFF に格納する。
//
// 形式（リトルエンディアン）:
//   magic "VCRFF1" (6) , u8 version(=1) , u8 titleCount
//   per title:
//     u8 titleNumber , i32 frameCount , i32 cellCount
//     i32[cellCount+1] cellBaseFrame   （各セル先頭の表示順フレーム番号。末尾=frameCount）
//     i32 runCount , per run: i32 runLength , u8 fieldCount   （フィールド数RLE。fieldCount=2/3…）
//     i32 anchorCount , per anchor: i32 frameIndex , i64 pts90k  （疎アンカー：seekの起点特定用）
internal sealed class DvdRffTable
{
    internal sealed class TitleRff
    {
        public int TitleNumber;
        public int FrameCount;
        public int[] CellBaseFrame = System.Array.Empty<int>(); // [cellCount+1]、末尾=FrameCount
        public byte[] FieldCounts = System.Array.Empty<byte>(); // [FrameCount]、各フレームのフィールド数(2/3…)
        public int[] AnchorFrame = System.Array.Empty<int>();   // 疎アンカー：表示順フレーム番号（昇順）
        public long[] AnchorPts90k = System.Array.Empty<long>(); // 同上：そのフレームの生PTS(90kHz)

        // セル先頭PTS(90kHz)の近似：そのセルのフレーム範囲内で最小フレーム番号のアンカーのPTS。
        // 範囲にアンカーが無ければ long.MinValue。隣接セル間でこの値が大きく後退＝PTS不連続(リセット)。
        public long CellStartPts90k(int cell)
        {
            if (cell < 0 || cell + 1 >= CellBaseFrame.Length) return long.MinValue;
            int lo = CellBaseFrame[cell], hi = CellBaseFrame[cell + 1];
            long bestP = long.MinValue; int bestF = int.MaxValue;
            for (int i = 0; i < AnchorFrame.Length; i++)
            {
                int fi = AnchorFrame[i];
                if (fi < lo || fi >= hi) continue;
                if (fi < bestF) { bestF = fi; bestP = AnchorPts90k[i]; }
            }
            return bestP;
        }

        // frameIndex のセルを特定（CellBaseFrame の二分探索）。
        public int CellOfFrame(int frameIndex)
        {
            int lo = 0, hi = CellBaseFrame.Length - 2; // 末尾(=FrameCount)は番兵なので除く
            if (hi < 0) return 0;
            int ans = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (CellBaseFrame[mid] <= frameIndex) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }
    }

    private readonly Dictionary<int, TitleRff> _byTitle = new();
    public TitleRff? Get(int titleNumber) => _byTitle.TryGetValue(titleNumber, out var t) ? t : null;
    public void Add(TitleRff t) => _byTitle[t.TitleNumber] = t;
    public int Count => _byTitle.Count;

    private static readonly byte[] MagicBytes = System.Text.Encoding.ASCII.GetBytes("VCRFF1");

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(MagicBytes);
        w.Write((byte)1); // version
        w.Write((byte)_byTitle.Count);
        foreach (var t in _byTitle.Values.OrderBy(x => x.TitleNumber))
        {
            w.Write((byte)t.TitleNumber);
            w.Write(t.FrameCount);
            int cellCount = System.Math.Max(0, t.CellBaseFrame.Length - 1);
            w.Write(cellCount);
            for (int i = 0; i <= cellCount; i++)
                w.Write(i < t.CellBaseFrame.Length ? t.CellBaseFrame[i] : t.FrameCount);
            // フィールド数RLE
            var runs = new List<(int Len, byte Fc)>();
            for (int i = 0; i < t.FieldCounts.Length;)
            {
                byte fc = t.FieldCounts[i]; int j = i + 1;
                while (j < t.FieldCounts.Length && t.FieldCounts[j] == fc) j++;
                runs.Add((j - i, fc)); i = j;
            }
            w.Write(runs.Count);
            foreach (var (len, fc) in runs) { w.Write(len); w.Write(fc); }
            // アンカー
            int an = System.Math.Min(t.AnchorFrame.Length, t.AnchorPts90k.Length);
            w.Write(an);
            for (int i = 0; i < an; i++) { w.Write(t.AnchorFrame[i]); w.Write(t.AnchorPts90k[i]); }
        }
        return ms.ToArray();
    }

    public static DvdRffTable? Parse(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            var magic = r.ReadBytes(6);
            if (magic.Length < 6 || System.Text.Encoding.ASCII.GetString(magic) != "VCRFF1") return null;
            r.ReadByte(); // version
            int titleCount = r.ReadByte();
            var table = new DvdRffTable();
            for (int ti = 0; ti < titleCount; ti++)
            {
                var t = new TitleRff { TitleNumber = r.ReadByte(), FrameCount = r.ReadInt32() };
                int cellCount = r.ReadInt32();
                t.CellBaseFrame = new int[cellCount + 1];
                for (int i = 0; i <= cellCount; i++) t.CellBaseFrame[i] = r.ReadInt32();
                int runCount = r.ReadInt32();
                var fc = new byte[t.FrameCount];
                int p = 0;
                for (int i = 0; i < runCount; i++)
                {
                    int len = r.ReadInt32(); byte v = r.ReadByte();
                    for (int k = 0; k < len && p < fc.Length; k++) fc[p++] = v;
                }
                t.FieldCounts = fc;
                int an = r.ReadInt32();
                t.AnchorFrame = new int[an];
                t.AnchorPts90k = new long[an];
                for (int i = 0; i < an; i++) { t.AnchorFrame[i] = r.ReadInt32(); t.AnchorPts90k[i] = r.ReadInt64(); }
                table.Add(t);
            }
            return table;
        }
        catch { return null; }
    }
}
