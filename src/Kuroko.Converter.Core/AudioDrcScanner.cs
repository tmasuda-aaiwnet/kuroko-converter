using System.IO;
using System.Text;

namespace Kuroko.Converter;

// AC-3 / DTS の実ビットストリームを直接パースし、DRC（ダイナミックレンジ制御）メタデータが
// 実際に記録されているかを判定する診断。出力の音量差ではなく、記録されたビットそのものを見る。
//  ・AC-3：ATSC A/52 の BSI 内 compr（heavy DRC, RFモード用）の有無、各オーディオブロックの
//          dynrng（light DRC）の有無と非ユニティ値の割合を集計する。
//  ・DTS ：Coherent Acoustics コアヘッダの DYNF（embedded dynamic range flag）の有無を集計する。
internal static class AudioDrcScanner
{
    // MSB ファーストのビットリーダ。
    private sealed class BitReader
    {
        private readonly byte[] _b; private int _bit;
        public BitReader(byte[] b, int startByte = 0) { _b = b; _bit = startByte * 8; }
        public int Pos => _bit;
        public void Skip(int n) => _bit += n;
        public uint Read(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                int by = _bit >> 3, bo = 7 - (_bit & 7);
                int bitv = by < _b.Length ? (_b[by] >> bo) & 1 : 0;
                v = (v << 1) | (uint)bitv;
                _bit++;
            }
            return v;
        }
        public bool Eof => (_bit >> 3) >= _b.Length;
    }

    public static string Scan(string esPath, bool isDts)
    {
        var data = File.ReadAllBytes(esPath);
        return isDts ? ScanDts(data) : ScanAc3(data);
    }

    // ── AC-3（ATSC A/52）──────────────────────────────────────────
    // 有効な AC-3 は fscod/frmsizecod から frame_size(語) が決まる。frame ごとに BSI を解いて compr の有無を、
    // さらに各オーディオブロック先頭の dynrng を読んで非ユニティ(≠0x00 でない)値の数を数える。
    private static string ScanAc3(byte[] data)
    {
        int frames = 0, comprFrames = 0;
        int blocks = 0, dynrngBlocks = 0, dynrngNonUnity = 0;
        int pos = 0;
        while (pos + 6 < data.Length)
        {
            if (!(data[pos] == 0x0B && data[pos + 1] == 0x77)) { pos++; continue; }
            // syncinfo: crc1(16) fscod(2) frmsizecod(6)
            int fscod = (data[pos + 4] >> 6) & 0x3;
            int frmsizecod = data[pos + 4] & 0x3F;
            if (fscod == 3 || (frmsizecod >> 1) >= 19) { pos++; continue; }
            int frameWords = Ac3FrameWords(fscod, frmsizecod);
            int frameBytes = frameWords * 2;
            if (frameBytes < 6 || pos + frameBytes > data.Length) { pos++; continue; }

            var r = new BitReader(data, pos + 5); // BSI 開始（syncword2+crc1(2)+fscod/frmsizecod(1)=5バイト後）
            int bsid = (int)r.Read(5);
            if (bsid > 10) { pos += 2; continue; } // EAC-3 等はここでは対象外
            int bsmod = (int)r.Read(3);
            int acmod = (int)r.Read(3);
            if ((acmod & 0x1) != 0 && acmod != 0x1) r.Read(2); // cmixlev（3フロント）
            if ((acmod & 0x4) != 0) r.Read(2);                 // surmixlev（サラウンドあり）
            if (acmod == 0x2) r.Read(2);                        // dsurmod（ステレオ）
            int lfeon = (int)r.Read(1);
            r.Read(5);                                          // dialnorm
            int compre = (int)r.Read(1); if (compre == 1) { r.Read(8); }
            int langcode = (int)r.Read(1); if (langcode == 1) r.Read(8);
            int audprodie = (int)r.Read(1); if (audprodie == 1) { r.Read(5); r.Read(2); }
            if (acmod == 0) // 1+1 デュアルモノは2系統
            {
                r.Read(5);                                       // dialnorm2
                int compr2e = (int)r.Read(1); if (compr2e == 1) r.Read(8);
                int langcod2e = (int)r.Read(1); if (langcod2e == 1) r.Read(8);
                int audprodi2e = (int)r.Read(1); if (audprodi2e == 1) { r.Read(5); r.Read(2); }
            }
            r.Read(1);                                          // copyrightb
            r.Read(1);                                          // origbs
            if (bsid == 6) { /* alt bit stream syntax は稀。ここでは通常構文のみ */ }
            int timecod1e = (int)r.Read(1); if (timecod1e == 1) r.Read(14);
            int timecod2e = (int)r.Read(1); if (timecod2e == 1) r.Read(14);
            int addbsie = (int)r.Read(1); if (addbsie == 1) { int addbsil = (int)r.Read(6); for (int i = 0; i <= addbsil; i++) r.Read(8); }

            // ここから audblk0。先頭：blksw[nfchan](各1), dithflag[nfchan](各1), dynrnge(1)[+dynrng(8)]
            int nfchans = AcmodChannels(acmod);
            frames++;
            if (compre == 1) comprFrames++;
            // block0 の dynrng を読む（DRC はブロック毎だが、記録有無の判定は block0 で十分）
            for (int ch = 0; ch < nfchans; ch++) r.Read(1); // blksw
            for (int ch = 0; ch < nfchans; ch++) r.Read(1); // dithflag
            int dynrnge = (int)r.Read(1);
            blocks++;
            if (dynrnge == 1)
            {
                int dynrng = (int)r.Read(8);
                dynrngBlocks++;
                if (dynrng != 0x00) dynrngNonUnity++; // 0x00 = 0dB(ユニティ)。それ以外＝圧縮/増幅指示
            }
            pos += frameBytes;
        }
        // ffmpeg 既定(drc_scale=1)が実際に適用するのは line-DRC（各ブロックの dynrng）。compr(RF/heavy)は
        // ほぼ常に記録されるが既定では適用されない。よって「変換で音量が変わるか」の実効判定は dynrng 非ユニティで行う。
        var sb = new StringBuilder();
        sb.Append($"AC-3 frames={frames}");
        sb.Append($" line-DRC(dynrng非ユニティ)={(dynrngNonUnity > 0 ? $"あり({dynrngNonUnity}/{blocks})" : "無し")}");
        sb.Append($" RF-DRC(compr)={(comprFrames > 0 ? "記録あり" : "無し")}");
        sb.Append($" => 変換で減衰={(dynrngNonUnity > 0 ? "する(line-DRC適用)" : "しない")}");
        return sb.ToString();
    }

    private static int AcmodChannels(int acmod) => acmod switch
    { 0 => 2, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 4, 6 => 4, 7 => 5, _ => 2 };

    // frmsizecod→フレーム語数（fscod 別）。A/52 Table 5.18 の nominal bitrate から算出。
    private static int Ac3FrameWords(int fscod, int frmsizecod)
    {
        int[] rate = { 32,40,48,56,64,80,96,112,128,160,192,224,256,320,384,448,512,576,640 };
        int fr = frmsizecod >> 1; if (fr >= rate.Length) return 0;
        int kbps = rate[fr];
        return fscod switch
        {
            0 => 2 * kbps,                                            // 48kHz: words = 2*kbps
            1 => (int)System.Math.Floor(1920.0 * kbps / 44.1) / 2 * 2 + ((frmsizecod & 1) == 1 ? 2 : 0), // 44.1k（近似・奇数は+1語）
            2 => 3 * kbps,                                            // 32kHz
            _ => 0,
        };
    }

    // ── DTS（Coherent Acoustics コア）──────────────────────────────
    // sync=7FFE8001。ヘッダ：FTYPE(1) SHORT(5) CPF(1) NBLKS(7) FSIZE(14) AMODE(6) SFREQ(4) RATE(5)
    //   FixedBit(1) DYNF(1) TIMEF(1) AUXF(1) ...。DYNF=embedded dynamic range flag（DRC 情報の有無）。
    private static string ScanDts(byte[] data)
    {
        int frames = 0, dynf = 0;
        int pos = 0;
        while (pos + 16 < data.Length)
        {
            if (!(data[pos] == 0x7F && data[pos + 1] == 0xFE && data[pos + 2] == 0x80 && data[pos + 3] == 0x01)) { pos++; continue; }
            var r = new BitReader(data, pos + 4);
            r.Read(1);                 // FTYPE
            r.Read(5);                 // SHORT (deficit sample count)
            r.Read(1);                 // CPF
            r.Read(7);                 // NBLKS
            int fsize = (int)r.Read(14); // FSIZE（フレームバイト-1）
            r.Read(6);                 // AMODE
            r.Read(4);                 // SFREQ
            r.Read(5);                 // RATE
            r.Read(1);                 // FixedBit
            int dynfBit = (int)r.Read(1); // DYNF
            frames++;
            if (dynfBit == 1) dynf++;
            int frameBytes = fsize + 1;
            pos += frameBytes >= 32 ? frameBytes : 4;
        }
        return $"DTS frames={frames} DYNF(DRC有効フラグ)={(dynf > 0 ? $"あり({dynf}/{frames})" : "なし")}"
             + $" => 実効DRC={(dynf > 0 ? "記録あり" : "無し")}";
    }
}
