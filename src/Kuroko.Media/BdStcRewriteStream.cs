using System.IO;

namespace Kuroko.Media.Dvd;

// Blu-ray のクリップを連結したストリームの **STC（PCR/PTS/DTS）をプレイリストの時刻軸へ揃える**。
//
// なぜ要るか：BDAV のクリップは**それぞれ独自の STC**を持つ。単純にバイト連結すると、境界で時刻が
// 逆行する（実測：00882 は PCR 10.7秒〜130秒、続く 00800 は 12秒〜8062秒で範囲が重なる）。
// 内蔵TSエンジンの位置・尺・シークはすべて PCR 基準なので、これでは
//  ・尺が 2本目の分しか出ない（last−first が 2本目の長さになる）
//  ・15秒へのシークが 2本目の 15秒に当たる（PCR の二分探索で先に見つかる）
//  ・境界で時刻が逆行して映像/音声が崩れる
// という壊れ方をする。そこで **クリップごとに一定のオフセットを足して**、1本の連続した録画に見せる。
//
// オフセットはクリップ単位の定数なので、**任意のバイト位置から読み始めても結果が変わらない**
// （＝シークしても破綻しない）。これが「再多重化」ではなくこの方式を採る理由。
internal sealed class BdStcRewriteStream : Stream
{
    private const int Unit = 192;              // BDAV：TP_extra_header(4)＋TSパケット(188)
    private const long PtsWrap = 1L << 33;     // PTS/DTS は 33bit（90kHz）
    private const long PcrWrap = (1L << 33) * 300; // PCR は 33bit×300＋9bit拡張（27MHz）

    private readonly Stream _inner;            // 連結済みストリーム（クリップの単純な並び）
    private readonly long[] _clipStarts;       // 各クリップの開始バイト位置
    private readonly long[] _offset90k;        // 各クリップに足す 90kHz オフセット
    private long _position;
    private byte[] _work = new byte[Unit * 64];

    public BdStcRewriteStream(Stream inner, long[] clipStarts, long[] offset90k)
    {
        _inner = inner; _clipStarts = clipStarts; _offset90k = offset90k;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, Length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= Length || count <= 0) return 0;

        // 192B 単位で読み直して書き換え、要求された範囲だけ返す（要求位置が単位境界でなくてもよい）。
        long unitStart = _position / Unit * Unit;
        int skip = (int)(_position - unitStart);
        int need = skip + count;
        int units = Math.Max(1, (need + Unit - 1) / Unit);
        int want = units * Unit;
        if (_work.Length < want) _work = new byte[want];

        _inner.Position = unitStart;
        int got = 0;
        while (got < want)
        {
            int n = _inner.Read(_work, got, want - got);
            if (n <= 0) break;
            got += n;
        }
        if (got <= skip) return 0;

        int wholeUnits = got / Unit;
        for (int u = 0; u < wholeUnits; u++)
            PatchUnit(_work, u * Unit, unitStart + (long)u * Unit);

        int give = Math.Min(count, got - skip);
        Buffer.BlockCopy(_work, skip, buffer, offset, give);
        _position += give;
        return give;
    }

    // 1 単位（TP_extra_header＋TSパケット）の時刻を書き換える。
    private void PatchUnit(byte[] b, int o, long streamPos)
    {
        long add = OffsetFor(streamPos);
        if (add == 0) return;
        int ts = o + 4;                      // TP_extra_header の次が TS パケット
        if (b[ts] != 0x47) return;           // 同期していない＝触らない
        int afc = (b[ts + 3] >> 4) & 0x03;
        int payload = 4;

        if (afc == 2 || afc == 3)
        {
            int afLen = b[ts + 4];
            if (afLen > 0 && (b[ts + 5] & 0x10) != 0 && ts + 11 < b.Length)
                PatchPcr(b, ts + 6, add * 300);   // PCR は 27MHz 基準なので 300 倍
            payload = 5 + afLen;
        }
        if (afc == 0 || afc == 2) return;         // ペイロード無し
        if ((b[ts + 1] & 0x40) == 0) return;      // PES 先頭でなければ PTS/DTS は無い

        int p = ts + payload;
        if (p + 14 > o + Unit) return;
        if (b[p] != 0x00 || b[p + 1] != 0x00 || b[p + 2] != 0x01) return;  // PES 開始コードでない
        int streamId = b[p + 3];
        // PTS/DTS を持たない種別（パディング・PSM/PSD 等）は対象外。
        if (streamId is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF) return;
        int flags = (b[p + 7] >> 6) & 0x03;
        if (flags == 0) return;
        PatchTimestamp(b, p + 9, add);                       // PTS
        if (flags == 3) PatchTimestamp(b, p + 14, add);      // DTS
    }

    // このバイト位置が属するクリップのオフセット（90kHz）。
    private long OffsetFor(long streamPos)
    {
        for (int i = _clipStarts.Length - 1; i >= 0; i--)
            if (streamPos >= _clipStarts[i]) return _offset90k[i];
        return 0;
    }

    // PES の 5 バイト時刻フィールド（'0010'/'0011' + 33bit をマーカ挟みで格納）。
    //   byte0: 種別(4) + v[32..30] + marker    byte1: v[29..22]    byte2: v[21..15] + marker
    //   byte3: v[14..7]                        byte4: v[6..0] + marker
    // **読み側と書き側でビット位置を必ず対にすること**。ここを 1 ビットずらすと PCR は正しいのに
    // PTS だけ過去へ飛ぶ（実測：byte2 を >>14 で書いて 720,896 ずれ＝約 8 秒。STC は正常なのに
    // 提示待ちフレームが滞留し、境界の少し先で再生が止まった）。
    private static void PatchTimestamp(byte[] b, int o, long add90k)
    {
        long v = ((long)(b[o] & 0x0E) << 29)
               | ((long)b[o + 1] << 22)
               | ((long)(b[o + 2] & 0xFE) << 14)
               | ((long)b[o + 3] << 7)
               | ((long)(b[o + 4] & 0xFE) >> 1);
        long nv = ((v + add90k) % PtsWrap + PtsWrap) % PtsWrap;
        int prefix = b[o] & 0xF0;   // '0010'(PTSのみ) / '0011'(PTS) / '0001'(DTS) を保つ
        b[o] = (byte)(prefix | (byte)(((nv >> 30) & 0x07) << 1) | 0x01);
        b[o + 1] = (byte)((nv >> 22) & 0xFF);
        b[o + 2] = (byte)((((nv >> 15) & 0x7F) << 1) | 0x01);
        b[o + 3] = (byte)((nv >> 7) & 0xFF);
        b[o + 4] = (byte)(((nv & 0x7F) << 1) | 0x01);
    }

    // アダプテーションフィールドの PCR（33bit base ＋ 6bit 予約 ＋ 9bit extension）。
    private static void PatchPcr(byte[] b, int o, long add27m)
    {
        long baseVal = ((long)b[o] << 25) | ((long)b[o + 1] << 17) | ((long)b[o + 2] << 9)
                     | ((long)b[o + 3] << 1) | ((long)(b[o + 4] >> 7) & 0x01);
        long ext = ((long)(b[o + 4] & 0x01) << 8) | b[o + 5];
        long v = baseVal * 300 + ext;
        long nv = ((v + add27m) % PcrWrap + PcrWrap) % PcrWrap;
        long nb = nv / 300, ne = nv % 300;
        b[o] = (byte)((nb >> 25) & 0xFF);
        b[o + 1] = (byte)((nb >> 17) & 0xFF);
        b[o + 2] = (byte)((nb >> 9) & 0xFF);
        b[o + 3] = (byte)((nb >> 1) & 0xFF);
        b[o + 4] = (byte)(((nb & 0x01) << 7) | 0x7E | ((ne >> 8) & 0x01));
        b[o + 5] = (byte)(ne & 0xFF);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
