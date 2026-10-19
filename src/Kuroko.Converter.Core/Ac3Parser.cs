using System;
using System.Collections.Generic;

namespace Kuroko.Converter;

// AC-3（ATSC A/52）ビットストリームを完全走査し、ラウドネス・メタデータを取り出す。
//
// 目的は復号ではなく、**各オーディオブロック（256サンプル）の dynrng / compr** と BSI の **dialnorm** を
// 正確な位置とともに得ること（Kuroko 仕様 §9.1 の DRC エンベロープ）。
// audblk は可変長で、ブロック N の開始位置は N-1 の全長に依存する。全長を知るには仮数のビット数が要り、
// そのためにビット割当（bap）＝指数→PSD→帯域積分→励起→マスキング→bap を解く必要がある。
// よってここでは「仮数のビット数を求めるところまで」実装し、仮数そのものは読み飛ばす（値は使わない）。
//
// 実装は ATSC A/52:2018 の記述に基づく（テーブルは Ac3Tables.cs＝規格の表から機械抽出）。
internal static class Ac3Parser
{
    private const int ExpReuse = 0, ExpD15 = 1, ExpD25 = 2, ExpD45 = 3;

    // MSB ファーストのビットリーダ。Pos はストリーム先頭からのビット位置。
    private sealed class BitReader
    {
        private readonly byte[] _b;
        private int _bit;
        public BitReader(byte[] b, int startByte) { _b = b; _bit = startByte * 8; }
        public int Pos { get => _bit; set => _bit = value; }
        public void Skip(int n) => _bit += n;
        public int Read(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int by = _bit >> 3;
                int bitv = by < _b.Length ? (_b[by] >> (7 - (_bit & 7))) & 1 : 0;
                v = (v << 1) | bitv;
                _bit++;
            }
            return v;
        }
    }

    // 1 フレーム分の抽出結果。
    public sealed class Frame
    {
        public int DialnormRaw;             // BSI dialnorm（1..31）。0 は規格上禁止
        public int? ComprRaw;               // BSI compr（8bit）。無ければ null
        public int?[] Dynrng = new int?[6]; // 各ブロックの dynrng（8bit）。そのブロックで未送出なら null（＝直前を継続）
        public int SampleRate;
        public int FrameBytes;
        public int EndBitPos;               // 6ブロック解析後のビット位置（フレーム先頭ビットからの相対）
        public int FrameBits;               // frame_size*16
    }

    // dialnorm(1..31) → dB。1=-1dB … 31=-31dB。0 は禁止値なので -31 と同じ扱いにする。
    public static double DialnormToDb(int raw) => -(raw == 0 ? 31 : raw);

    // dynrng(8bit) → 線形ゲイン（A/52 §7.7.1 / Table 7.29）。
    //   ビット構成は X0X1X2.Y3Y4Y5Y6Y7。X は **符号付き3bit（-4..3）** で (X+1)*6.02dB を表し、
    //   Y は 5bit の線形項。gain = (1 + Y/32) * 2^X。
    //   raw=0x00 → 1.0（ユニティ=0dB）／raw=0x7F → 15.75（+23.9dB, 最大増幅）／raw=0x80 → 0.0625（-24dB, 最大減衰）。
    public static double DynrngToLinear(int raw)
    {
        int x = (raw >> 5) & 0x7;
        if (x >= 4) x -= 8;                 // 3bit 符号拡張（4..7 → -4..-1）
        int y = raw & 0x1F;
        return (1.0 + y / 32.0) * Math.Pow(2.0, x);
    }

    public static double DynrngToDb(int raw) => 20.0 * Math.Log10(DynrngToLinear(raw));

    // compr(8bit) → 線形ゲイン（A/52 §7.7.2 / Table 7.30）。dynrng とビット構成が違う：
    // 上位 **4bit** が符号付き指数（-8..7）、下位 4bit が線形項。gain = (1 + Y/16) * 2^X。
    public static double ComprToLinear(int raw)
    {
        int x = (raw >> 4) & 0xF;
        if (x >= 8) x -= 16;                // 4bit 符号拡張（8..15 → -8..-1）
        int y = raw & 0xF;
        return (1.0 + y / 16.0) * Math.Pow(2.0, x);
    }

    public static double ComprToDb(int raw) => 20.0 * Math.Log10(ComprToLinear(raw));

    private static int AcmodChannels(int acmod) => acmod switch
    { 0 => 2, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 4, 6 => 4, 7 => 5, _ => 2 };

    private static readonly int[] SampleRates = { 48000, 44100, 32000 };

    // pos から 1 フレームを解析する。成功なら Frame、同期不能/非対応なら null。
    public static Frame? ParseFrame(byte[] data, int pos)
    {
        if (pos + 6 >= data.Length) return null;
        if (!(data[pos] == 0x0B && data[pos + 1] == 0x77)) return null;
        int fscod = (data[pos + 4] >> 6) & 0x3;
        int frmsizecod = data[pos + 4] & 0x3F;
        if (fscod == 3 || frmsizecod >= 38) return null;
        int frameWords = Ac3Tables.FrameWords[frmsizecod, fscod];
        int frameBytes = frameWords * 2;
        if (frameBytes < 6 || pos + frameBytes > data.Length) return null;

        var f = new Frame { SampleRate = SampleRates[fscod], FrameBytes = frameBytes, FrameBits = frameWords * 16 };
        var r = new BitReader(data, pos + 5); // syncword(2)+crc1(2)+fscod/frmsizecod(1) の後＝BSI
        int frameStartBit = pos * 8;

        // ── BSI（§5.3）──
        int bsid = r.Read(5);
        if (bsid > 10) return null;   // E-AC-3 等は対象外
        r.Read(3);                    // bsmod
        int acmod = r.Read(3);
        if ((acmod & 0x1) != 0 && acmod != 0x1) r.Read(2); // cmixlev
        if ((acmod & 0x4) != 0) r.Read(2);                 // surmixlev
        if (acmod == 0x2) r.Read(2);                       // dsurmod
        int lfeon = r.Read(1);
        f.DialnormRaw = r.Read(5);
        if (r.Read(1) == 1) f.ComprRaw = r.Read(8);        // compre / compr
        if (r.Read(1) == 1) r.Read(8);                     // langcode / langcod
        if (r.Read(1) == 1) { r.Read(5); r.Read(2); }      // audprodie / mixlevel, roomtyp
        if (acmod == 0)
        {
            r.Read(5);                                     // dialnorm2
            if (r.Read(1) == 1) r.Read(8);                 // compr2e / compr2
            if (r.Read(1) == 1) r.Read(8);                 // langcod2e / langcod2
            if (r.Read(1) == 1) { r.Read(5); r.Read(2); }  // audprodi2e
        }
        r.Read(1);                                         // copyrightb
        r.Read(1);                                         // origbs
        if (r.Read(1) == 1) r.Read(14);                    // timecod1e / timecod1
        if (r.Read(1) == 1) r.Read(14);                    // timecod2e / timecod2
        if (r.Read(1) == 1) { int n = r.Read(6); for (int i = 0; i <= n; i++) r.Read(8); } // addbsie

        int nfchans = AcmodChannels(acmod);
        var st = new BlockState(nfchans, lfeon == 1, acmod, fscod);

        // ── audblk × 6（§5.4.2）──
        for (int blk = 0; blk < 6; blk++)
        {
            if (!ParseAudblk(r, st, blk, f)) return null;
            if (r.Pos - frameStartBit > f.FrameBits) return null; // オーバーラン＝解析破綻
        }
        f.EndBitPos = r.Pos - frameStartBit;
        return f;
    }

    // フレームを通じて保持される状態（指数の再利用・カップリング構成など）。
    private sealed class BlockState
    {
        public readonly int Nfchans, Acmod, Fscod;
        public readonly bool Lfeon;
        public bool Cplinu, Phsflginu;
        public int Ncplsubnd, Ncplbnd, Cplbegf, Cplendf, Cplstrtmant, Cplendmant;
        public readonly bool[] Chincpl;
        public readonly int[] Chbwcod, Endmant, Chexpstr;
        public int Cplexpstr, Lfeexpstr;
        // ビット割当パラメータ（baie で更新され、送出されないブロックでは前値を継続）。
        public int Sdcycod, Fdcycod, Sgaincod, Dbpbcod, Floorcod;
        // snroffst 系（snroffste で更新）。
        public int Csnroffst, Cplfsnroffst, Cplfgaincod, Lfefsnroffst, Lfefgaincod;
        public readonly int[] Fsnroffst, Fgaincod;
        // delta bit allocation（deltbaie）。
        public int Cpldeltbae = 2, Cpldeltnseg;
        public readonly int[] Deltbae, Deltnseg;
        public readonly int[][] Deltoffst, Deltlen, Deltba;
        public readonly int[] Cpldeltoffst = new int[8], Cpldeltlen = new int[8], Cpldeltba = new int[8];
        // 指数（再利用のため保持）。
        public readonly int[][] Exps;   // [ch][bin]
        public readonly int[] Cplexps = new int[256];
        public readonly int[] Lfeexps = new int[8];

        public BlockState(int nfchans, bool lfeon, int acmod, int fscod)
        {
            Nfchans = nfchans; Lfeon = lfeon; Acmod = acmod; Fscod = fscod;
            Chincpl = new bool[nfchans];
            Chbwcod = new int[nfchans]; Endmant = new int[nfchans]; Chexpstr = new int[nfchans];
            Fsnroffst = new int[nfchans]; Fgaincod = new int[nfchans];
            Deltbae = new int[nfchans]; Deltnseg = new int[nfchans];
            Deltoffst = new int[nfchans][]; Deltlen = new int[nfchans][]; Deltba = new int[nfchans][];
            for (int i = 0; i < nfchans; i++)
            { Deltbae[i] = 2; Deltoffst[i] = new int[8]; Deltlen[i] = new int[8]; Deltba[i] = new int[8]; }
            Exps = new int[nfchans][];
            for (int i = 0; i < nfchans; i++) Exps[i] = new int[256];
        }
    }

    private static bool ParseAudblk(BitReader r, BlockState st, int blk, Frame f)
    {
        int nf = st.Nfchans;
        for (int ch = 0; ch < nf; ch++) r.Read(1);          // blksw
        for (int ch = 0; ch < nf; ch++) r.Read(1);          // dithflag
        if (r.Read(1) == 1) f.Dynrng[blk] = r.Read(8);      // dynrnge / dynrng
        if (st.Acmod == 0) { if (r.Read(1) == 1) r.Read(8); } // dynrng2e / dynrng2

        // カップリング構成
        if (r.Read(1) == 1) // cplstre
        {
            st.Cplinu = r.Read(1) == 1;
            if (st.Cplinu)
            {
                for (int ch = 0; ch < nf; ch++) st.Chincpl[ch] = r.Read(1) == 1;
                if (st.Acmod == 0x2) st.Phsflginu = r.Read(1) == 1;
                st.Cplbegf = r.Read(4);
                st.Cplendf = r.Read(4);
                st.Ncplsubnd = 3 + st.Cplendf - st.Cplbegf;
                if (st.Ncplsubnd < 1) return false;
                st.Ncplbnd = st.Ncplsubnd;
                for (int bnd = 1; bnd < st.Ncplsubnd; bnd++)
                    if (r.Read(1) == 1) st.Ncplbnd--;       // cplbndstrc
                st.Cplstrtmant = st.Cplbegf * 12 + 37;
                st.Cplendmant = (st.Cplendf + 3) * 12 + 37;
            }
        }
        if (st.Cplinu)
        {
            bool[] cplcoe = new bool[nf];
            for (int ch = 0; ch < nf; ch++)
            {
                if (!st.Chincpl[ch]) continue;
                cplcoe[ch] = r.Read(1) == 1;
                if (cplcoe[ch])
                {
                    r.Read(2);                              // mstrcplco
                    for (int bnd = 0; bnd < st.Ncplbnd; bnd++) { r.Read(4); r.Read(4); } // cplcoexp/cplcomant
                }
            }
            if (st.Acmod == 0x2 && st.Phsflginu && (cplcoe[0] || cplcoe[1]))
                for (int bnd = 0; bnd < st.Ncplbnd; bnd++) r.Read(1); // phsflg
        }
        if (st.Acmod == 0x2)
        {
            if (r.Read(1) == 1) // rematstr
            {
                int nrematbnd = (st.Cplinu && st.Cplbegf == 0) ? 2 : (st.Cplinu && st.Cplbegf <= 2) ? 3 : 4;
                if (!st.Cplinu) nrematbnd = 4;
                for (int rbnd = 0; rbnd < nrematbnd; rbnd++) r.Read(1); // rematflg
            }
        }

        // 指数戦略
        if (st.Cplinu) st.Cplexpstr = r.Read(2);
        for (int ch = 0; ch < nf; ch++) st.Chexpstr[ch] = r.Read(2);
        if (st.Lfeon) st.Lfeexpstr = r.Read(1);
        for (int ch = 0; ch < nf; ch++)
            if (st.Chexpstr[ch] != ExpReuse && !st.Chincpl[ch]) st.Chbwcod[ch] = r.Read(6);
        for (int ch = 0; ch < nf; ch++)
            st.Endmant[ch] = st.Chincpl[ch] ? st.Cplstrtmant : (st.Chbwcod[ch] + 12) * 3 + 37;

        // 指数（カップリング）
        if (st.Cplinu && st.Cplexpstr != ExpReuse)
        {
            int ncplmant = st.Cplendmant - st.Cplstrtmant;
            int grpsize = 3 << (st.Cplexpstr - 1);
            int ncplgrps = ncplmant / grpsize;
            int absexp = r.Read(4);
            if (!ReadExponents(r, st.Cplexps, st.Cplstrtmant, ncplgrps, st.Cplexpstr, absexp << 1, true)) return false;
        }
        // 指数（各 fbw ch）
        for (int ch = 0; ch < nf; ch++)
        {
            if (st.Chexpstr[ch] == ExpReuse) continue;
            int nchgrps = st.Chexpstr[ch] switch
            {
                ExpD15 => (st.Endmant[ch] - 1) / 3,
                ExpD25 => (st.Endmant[ch] - 1 + 3) / 6,
                _ => (st.Endmant[ch] - 1 + 9) / 12,
            };
            int absexp = r.Read(4);
            if (!ReadExponents(r, st.Exps[ch], 0, nchgrps, st.Chexpstr[ch], absexp, false)) return false;
            r.Read(2); // gainrng
        }
        // 指数（LFE）
        if (st.Lfeon && st.Lfeexpstr != ExpReuse)
        {
            int absexp = r.Read(4);
            if (!ReadExponents(r, st.Lfeexps, 0, 2, ExpD15, absexp, false)) return false;
        }

        // ビット割当パラメータ
        if (r.Read(1) == 1) // baie
        {
            st.Sdcycod = r.Read(2); st.Fdcycod = r.Read(2); st.Sgaincod = r.Read(2);
            st.Dbpbcod = r.Read(2); st.Floorcod = r.Read(3);
        }
        if (r.Read(1) == 1) // snroffste
        {
            st.Csnroffst = r.Read(6);
            if (st.Cplinu) { st.Cplfsnroffst = r.Read(4); st.Cplfgaincod = r.Read(3); }
            for (int ch = 0; ch < nf; ch++) { st.Fsnroffst[ch] = r.Read(4); st.Fgaincod[ch] = r.Read(3); }
            if (st.Lfeon) { st.Lfefsnroffst = r.Read(4); st.Lfefgaincod = r.Read(3); }
        }
        if (st.Cplinu && r.Read(1) == 1) { r.Read(3); r.Read(3); } // cplleake / cplfleak, cplsleak
        if (r.Read(1) == 1) // deltbaie
        {
            if (st.Cplinu) st.Cpldeltbae = r.Read(2);
            for (int ch = 0; ch < nf; ch++) st.Deltbae[ch] = r.Read(2);
            if (st.Cplinu && st.Cpldeltbae == 1)
            {
                st.Cpldeltnseg = r.Read(3);
                for (int seg = 0; seg <= st.Cpldeltnseg && seg < 8; seg++)
                { st.Cpldeltoffst[seg] = r.Read(5); st.Cpldeltlen[seg] = r.Read(4); st.Cpldeltba[seg] = r.Read(3); }
            }
            for (int ch = 0; ch < nf; ch++)
            {
                if (st.Deltbae[ch] != 1) continue;
                st.Deltnseg[ch] = r.Read(3);
                for (int seg = 0; seg <= st.Deltnseg[ch] && seg < 8; seg++)
                { st.Deltoffst[ch][seg] = r.Read(5); st.Deltlen[ch][seg] = r.Read(4); st.Deltba[ch][seg] = r.Read(3); }
            }
        }
        if (r.Read(1) == 1) { int skipl = r.Read(9); r.Skip(skipl * 8); } // skiple / skipfld

        // ── 仮数：bap を解いて必要ビット数だけ読み飛ばす ──
        // グループ化（bap 1/2/4）のキャッシュはブロック内・全チャネル通しで共有される。
        var g = new MantGroups();
        bool cplMantDone = false;
        for (int ch = 0; ch < nf; ch++)
        {
            var bap = ComputeBap(st, ch, isCoupling: false, isLfe: false);
            if (bap is null) return false;
            SkipMantissas(r, bap, 0, st.Endmant[ch], g);
            if (st.Cplinu && st.Chincpl[ch] && !cplMantDone)
            {
                var cbap = ComputeBap(st, ch, isCoupling: true, isLfe: false);
                if (cbap is null) return false;
                SkipMantissas(r, cbap, st.Cplstrtmant, st.Cplendmant, g);
                cplMantDone = true;
            }
        }
        if (st.Lfeon)
        {
            var lbap = ComputeBap(st, 0, isCoupling: false, isLfe: true);
            if (lbap is null) return false;
            SkipMantissas(r, lbap, 0, 7, g);
        }
        return true;
    }

    // 指数の復号（D15/D25/D45 のグループ展開）。§7.1.3
    private static bool ReadExponents(BitReader r, int[] dest, int start, int ngrps, int expstr, int absexp, bool coupling)
    {
        int grpsize = expstr switch { ExpD15 => 1, ExpD25 => 2, _ => 4 };
        int prev = absexp;
        int i = start;
        if (!coupling) dest[i++] = absexp; // fbw/lfe は絶対指数が bin0
        for (int grp = 0; grp < ngrps; grp++)
        {
            int gcode = r.Read(7);
            if (gcode > 124) return false;
            int m0 = gcode / 25, m1 = (gcode % 25) / 5, m2 = gcode % 5;
            foreach (int m in stackalloc int[] { m0, m1, m2 })
            {
                prev += m - 2;
                if (prev < 0 || prev > 24) return false;
                for (int k = 0; k < grpsize; k++)
                {
                    if (i >= dest.Length) return false;
                    dest[i++] = prev;
                }
            }
        }
        return true;
    }

    // グループ化仮数のキャッシュ残数（bap1: 3個/5bit, bap2: 3個/7bit, bap4: 2個/7bit）。
    private sealed class MantGroups { public int B1, B2, B4; }

    private static readonly int[] BapBits = { 0, 0, 0, 3, 0, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16 };

    private static void SkipMantissas(BitReader r, int[] bap, int start, int end, MantGroups g)
    {
        for (int bin = start; bin < end; bin++)
        {
            switch (bap[bin])
            {
                case 0: break;
                case 1: if (g.B1 == 0) { r.Read(5); g.B1 = 2; } else g.B1--; break;
                case 2: if (g.B2 == 0) { r.Read(7); g.B2 = 2; } else g.B2--; break;
                case 4: if (g.B4 == 0) { r.Read(7); g.B4 = 1; } else g.B4--; break;
                default: r.Read(BapBits[bap[bin]]); break;
            }
        }
    }

    // ビット割当（§7.2.2）：指数→psd→帯域積分→励起→マスキング→delta→bap。
    private static int[]? ComputeBap(BlockState st, int ch, bool isCoupling, bool isLfe)
    {
        int start, end, fgaincod, fsnroffst, deltbae, nseg;
        int[] exps, deltoffst, deltlen, deltba;
        bool fastleakInit = isCoupling;
        if (isCoupling)
        {
            start = st.Cplstrtmant; end = st.Cplendmant; exps = st.Cplexps;
            fgaincod = st.Cplfgaincod; fsnroffst = st.Cplfsnroffst;
            deltbae = st.Cpldeltbae; nseg = st.Cpldeltnseg;
            deltoffst = st.Cpldeltoffst; deltlen = st.Cpldeltlen; deltba = st.Cpldeltba;
        }
        else if (isLfe)
        {
            start = 0; end = 7; exps = st.Lfeexps;
            fgaincod = st.Lfefgaincod; fsnroffst = st.Lfefsnroffst;
            deltbae = 2; nseg = 0; deltoffst = st.Cpldeltoffst; deltlen = st.Cpldeltlen; deltba = st.Cpldeltba;
        }
        else
        {
            start = 0; end = st.Endmant[ch]; exps = st.Exps[ch];
            fgaincod = st.Fgaincod[ch]; fsnroffst = st.Fsnroffst[ch];
            deltbae = st.Deltbae[ch]; nseg = st.Deltnseg[ch];
            deltoffst = st.Deltoffst[ch]; deltlen = st.Deltlen[ch]; deltba = st.Deltba[ch];
        }
        if (end <= start || end > 253) return null;

        int sdecay = Ac3Tables.SlowDec[st.Sdcycod];
        int fdecay = Ac3Tables.FastDec[st.Fdcycod];
        int sgain = Ac3Tables.SlowGain[st.Sgaincod];
        int dbknee = Ac3Tables.DbPerBit[st.Dbpbcod];
        int floor = Ac3Tables.Floor[st.Floorcod];
        int fgain = Ac3Tables.FastGain[fgaincod];
        int snroffset = ((st.Csnroffst - 15) << 4 | fsnroffst) << 2;

        var psd = new int[256];
        for (int bin = start; bin < end; bin++) psd[bin] = 3072 - (exps[bin] << 7);

        // 帯域積分（logadd）
        var bndpsd = new int[50];
        int j = start, k = Ac3Tables.MaskTab[start];
        do
        {
            int lastbin = Math.Min(Ac3Tables.BndTab[k] + Ac3Tables.BndSz[k], end);
            bndpsd[k] = psd[j];
            j++;
            for (int i2 = j; i2 < lastbin; i2++) { bndpsd[k] = LogAdd(bndpsd[k], psd[j]); j++; }
            k++;
        } while (end > Math.Min(Ac3Tables.BndTab[k - 1] + Ac3Tables.BndSz[k - 1], end) && k < 50 && j < end);

        // 励起関数
        var excite = new int[50];
        int bndstrt = Ac3Tables.MaskTab[start], bndend = Ac3Tables.MaskTab[end - 1] + 1;
        int fastleak = 0, slowleak = 0, begin;
        if (bndstrt == 0)
        {
            int lowcomp = 0;
            lowcomp = CalcLowcomp(lowcomp, bndpsd[0], bndpsd[1], 0);
            excite[0] = bndpsd[0] - fgain - lowcomp;
            lowcomp = CalcLowcomp(lowcomp, bndpsd[1], bndpsd[2], 1);
            excite[1] = bndpsd[1] - fgain - lowcomp;
            begin = 7;
            for (int bin = 2; bin < 7; bin++)
            {
                if (bndend != 7 || bin != 6) lowcomp = CalcLowcomp(lowcomp, bndpsd[bin], bndpsd[bin + 1], bin);
                fastleak = bndpsd[bin] - fgain;
                slowleak = bndpsd[bin] - sgain;
                excite[bin] = fastleak - lowcomp;
                if (bndend != 7 || bin != 6)
                    if (bndpsd[bin] <= bndpsd[bin + 1]) { begin = bin + 1; break; }
            }
            for (int bin = begin; bin < Math.Min(bndend, 22); bin++)
            {
                if (bndend != 7 || bin != 6) lowcomp = CalcLowcomp(lowcomp, bndpsd[bin], bndpsd[bin + 1], bin);
                fastleak -= fdecay; fastleak = Math.Max(fastleak, bndpsd[bin] - fgain);
                slowleak -= sdecay; slowleak = Math.Max(slowleak, bndpsd[bin] - sgain);
                excite[bin] = Math.Max(fastleak - lowcomp, slowleak);
            }
            begin = 22;
        }
        else
        {
            begin = bndstrt;
        }
        for (int bin = begin; bin < bndend; bin++)
        {
            fastleak -= fdecay; fastleak = Math.Max(fastleak, bndpsd[bin] - fgain);
            slowleak -= sdecay; slowleak = Math.Max(slowleak, bndpsd[bin] - sgain);
            excite[bin] = Math.Max(fastleak, slowleak);
        }

        // マスキング
        var mask = new int[50];
        for (int bin = bndstrt; bin < bndend; bin++)
        {
            if (bndpsd[bin] < dbknee) excite[bin] += (dbknee - bndpsd[bin]) >> 2;
            mask[bin] = Math.Max(excite[bin], Ac3Tables.Hth[st.Fscod, bin]);
        }

        // delta bit allocation
        if (deltbae == 0 || deltbae == 1)
        {
            int band = 0;
            for (int seg = 0; seg <= nseg && seg < 8; seg++)
            {
                band += deltoffst[seg];
                int delta = deltba[seg] >= 4 ? (deltba[seg] - 3) << 7 : (deltba[seg] - 4) << 7;
                for (int kk = 0; kk < deltlen[seg]; kk++) { if (band >= 50) break; mask[band] += delta; band++; }
            }
        }

        // bap
        var bap = new int[256];
        int i3 = start; int j3 = Ac3Tables.MaskTab[start];
        do
        {
            int lastbin = Math.Min(Ac3Tables.BndTab[j3] + Ac3Tables.BndSz[j3], end);
            mask[j3] -= snroffset;
            mask[j3] -= floor;
            if (mask[j3] < 0) mask[j3] = 0;
            mask[j3] &= 0x1fe0;
            mask[j3] += floor;
            for (int kk = i3; kk < lastbin; kk++)
            {
                int address = (psd[i3] - mask[j3]) >> 5;
                address = Math.Min(63, Math.Max(0, address));
                bap[i3] = Ac3Tables.BapTab[address];
                i3++;
            }
            j3++;
        } while (end > i3 && j3 < 50);
        return bap;
    }

    // 1本の AC-3 ES 全体から、Kuroko 仕様 §9.1 のラウドネス・メタデータを組み立てる。
    //   dialnorm … 先頭フレームの値（一定ゲイン）
    //   drc      … 各オーディオブロック（256サンプル）の dynrng を変化点RLEにしたエンベロープ（ac3-line）
    //   drcRf    … 各フレームの compr を同様にしたエンベロープ（ac3-rf）
    // 時刻はこの ES の先頭を 0 とする。全区間ユニティ（0dB）のエンベロープは null（＝保持不要）を返す。
    public sealed class Loudness
    {
        public double? DialnormDb;
        public List<double[]>? LinePoints;
        public List<double[]>? RfPoints;
    }

    // limitMs>0 なら、その時刻までのフレームだけを対象にする（試験用の尺制限と範囲を合わせるため。
    // 格納された音声より長いエンベロープを持たせない）。
    public static Loudness Extract(byte[] es, double limitMs = 0)
    {
        var res = new Loudness();
        var line = new List<double[]>();
        var rf = new List<double[]>();
        double? lastLine = null, lastRf = null;
        int pos = 0;
        long sampleIndex = 0;   // ES 先頭からの累積サンプル数
        double curLineDb = 0;   // dynrng は未送出ブロックで直前を継続する
        bool anyLine = false, anyRf = false;
        while (pos + 6 < es.Length)
        {
            if (!(es[pos] == 0x0B && es[pos + 1] == 0x77)) { pos++; continue; }
            var fr = ParseFrame(es, pos);
            if (fr is null) { pos += 2; continue; }
            if (limitMs > 0 && sampleIndex * 1000.0 / fr.SampleRate >= limitMs) break;
            res.DialnormDb ??= DialnormToDb(fr.DialnormRaw);

            if (fr.ComprRaw is int cr)
            {
                double db = ComprToDb(cr);
                double tMs = sampleIndex * 1000.0 / fr.SampleRate;
                if (lastRf is null || Math.Abs(db - lastRf.Value) > 1e-9) { rf.Add(new[] { Round(tMs), Round(db) }); lastRf = db; }
                if (db != 0) anyRf = true;
            }
            for (int b = 0; b < 6; b++)
            {
                if (fr.Dynrng[b] is int dv) curLineDb = DynrngToDb(dv);
                double tMs = (sampleIndex + b * 256) * 1000.0 / fr.SampleRate;
                if (lastLine is null || Math.Abs(curLineDb - lastLine.Value) > 1e-9)
                { line.Add(new[] { Round(tMs), Round(curLineDb) }); lastLine = curLineDb; }
                if (curLineDb != 0) anyLine = true;
            }
            sampleIndex += 1536;
            pos += fr.FrameBytes;
        }
        // 全区間ユニティ＝保持しない（仕様：エンベロープを持たないトラックは drc ごと省略）。
        if (anyLine && line.Count > 0) res.LinePoints = line;
        if (anyRf && rf.Count > 0) res.RfPoints = rf;
        return res;
    }

    private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    private static int LogAdd(int a, int b)
    {
        int c = a - b;
        int address = Math.Min(Math.Abs(c) >> 1, 255);
        return c >= 0 ? a + Ac3Tables.LaTab[address] : b + Ac3Tables.LaTab[address];
    }

    private static int CalcLowcomp(int a, int b0, int b1, int bin)
    {
        if (bin < 7)
        {
            if (b0 + 256 == b1) a = 384;
            else if (b0 > b1) a = Math.Max(0, a - 64);
        }
        else if (bin < 20)
        {
            if (b0 + 256 == b1) a = 320;
            else if (b0 > b1) a = Math.Max(0, a - 64);
        }
        else a = Math.Max(0, a - 128);
        return a;
    }
}
