using System.Linq;

namespace Kuroko.Media.Dvd;

// H.264化メニュー（Vuecove拡張）の「メニューボタン表」。メニューセルをH.264化すると NAV(PCI) が
// ストリームから失われるため、各メニューセルの代表PCI（ボタンの座標/コマンド/色組）を退避し、再生側が
// ボタン/ハイライト/ナビを復元する。サブピクチャ（ボタン図柄）は ffmpeg が保持するのでVOB側に残る。
// VIDEO_TS/VUECOVE.MNU に格納。キーは (domain, 新VOB内のセル先頭セクタ)。
//
// 形式（リトルエンディアン）:
//   magic "VCMNU1"(6) , u8 version(=1) , i32 entryCount
//   per entry: u8 domain(0=VMGM, 1.. =VTS番号) , i32 firstSector , i32 pciLen , byte[pciLen]
//     pci は private_stream_2 のペイロード（先頭バイト 0x00 = PCI）。再生側は DvdNavPci.Parse に渡す。
internal sealed class DvdMenuTable
{
    private readonly Dictionary<long, byte[]> _byCell = new();

    private static long Key(int domain, long firstSector) => ((long)(domain & 0xFF) << 40) | (firstSector & 0xFFFFFFFFFFL);

    public void Add(int domain, long firstSector, byte[] pci) => _byCell[Key(domain, firstSector)] = pci;
    public byte[]? Get(int domain, long firstSector) => _byCell.TryGetValue(Key(domain, firstSector), out var p) ? p : null;
    public int Count => _byCell.Count;

    private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("VCMNU1");

    public byte[] ToBytes()
    {
        using var ms = new System.IO.MemoryStream();
        using var w = new System.IO.BinaryWriter(ms);
        w.Write(Magic);
        w.Write((byte)1); // version
        w.Write(_byCell.Count);
        foreach (var kv in _byCell)
        {
            int domain = (int)((kv.Key >> 40) & 0xFF);
            long fs = kv.Key & 0xFFFFFFFFFFL;
            w.Write((byte)domain);
            w.Write((int)fs);
            w.Write(kv.Value.Length);
            w.Write(kv.Value);
        }
        return ms.ToArray();
    }

    public static DvdMenuTable? Parse(byte[] data)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(data);
            using var r = new System.IO.BinaryReader(ms);
            if (!r.ReadBytes(6).SequenceEqual(Magic)) return null;
            r.ReadByte(); // version
            int n = r.ReadInt32();
            var t = new DvdMenuTable();
            for (int i = 0; i < n; i++)
            {
                int domain = r.ReadByte();
                int fs = r.ReadInt32();
                int len = r.ReadInt32();
                t.Add(domain, fs, r.ReadBytes(len));
            }
            return t;
        }
        catch { return null; }
    }
}
