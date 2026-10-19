using System.IO;

namespace Kuroko.Media.Dvd;

// 暗号化されたままの入力を見分ける（変換器は変換を、再生器 Vuecove は再生を断る。どちらも暗号の解除をしない・できない）。
// 結果は「何が・どう保護されているか」（EncryptedInput）で返し、利用者へ出す文は使う側が Message で組み立てる。
//
// 見るのは「**今まさに暗号化されているか**」だけ。一度解除されたものの痕跡（DVD の管理情報の CSS の印、BD の AACS の
// フォルダ、TS の番組情報のスクランブル方式の記述）は、自作の正当なディスクまで拒むおそれがあるので判定に使わない。
//  ・DVD（CSS）：タイトルの VOB のパックの PES ヘッダの PES_scrambling_control（暗号化されたパックは 0 以外）。
//  ・BD（AACS）：m2ts の 6144 バイトの単位（Aligned Unit）の各ソースパケットの copy_permission_indicator（暗号化
//    された単位は 0 以外）と、TS の同期バイト（暗号化された単位は 2 つ目以降のパケットが TS として読めない）。
//  ・TS（放送のスクランブル）：映像・音声のパケットの transport_scrambling_control（スクランブル中は 0 以外）。
// どれも、ファイルの先頭と途中から一部を読むだけで判定する（全体は読まない）。
// 見つかった保護。Medium＝「DVD」など、Protection＝「暗号化（CSS）」「スクランブル」など、Detail＝どのファイル・どれだけか。
internal sealed record EncryptedInput(string Medium, string Protection, string Detail)
{
    // tool＝「この変換器」「Vuecove」、action＝「変換」「再生」。
    public string Message(string tool, string action)
        => $"この {Medium} は{Protection}されたままです（{Detail}）。{tool}{(char.IsAscii(tool[^1]) ? " " : "")}は"
           + (Protection == "スクランブル" ? "スクランブルの解除" : "暗号の解除") + $"をしないので、{action}できません。";
}

internal static class EncryptedInputCheck
{
    // 暗号化されていれば、その内容。されていなければ null。
    public static EncryptedInput? CheckDvdIso(string isoPath)
    {
        int checkedFiles = 0;
        foreach (var (name, stream) in OpenFiles(isoPath, "VIDEO_TS",
                     n => n.StartsWith("VTS_", StringComparison.OrdinalIgnoreCase)
                          && n.EndsWith(".VOB", StringComparison.OrdinalIgnoreCase)
                          && !n.EndsWith("_0.VOB", StringComparison.OrdinalIgnoreCase), largestFirst: true))
        {
            using (stream)
                if (VobScrambled(stream))
                    return new EncryptedInput("DVD", "暗号化（CSS）", name);
            if (++checkedFiles >= 4) break; // 大きいものから 4 本見れば足りる（CSS はタイトルの VOB 全体にかかる）
        }
        return null;
    }

    // VOB 1 本（の先頭と途中）に暗号化されたパックがあるか。
    public static bool VobScrambled(Stream stream)
    {
        foreach (long at in SamplePositions(stream.Length, 2048))
        {
            stream.Position = at;
            var buf = ReadUpTo(stream, 2048 * 256);
            for (int s = 0; s + 2048 <= buf.Length; s += 2048)
                if (DvdPackScrambled(buf, s)) return true;
        }
        return false;
    }

    public static EncryptedInput? CheckBdIso(string isoPath)
    {
        int checkedFiles = 0;
        foreach (var (name, stream) in OpenFiles(isoPath, "BDMV/STREAM",
                     n => n.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase), largestFirst: true))
        {
            using (stream)
                if (M2tsEncrypted(stream))
                    return new EncryptedInput("Blu-ray", "暗号化（AACS）", name);
            if (++checkedFiles >= 4) break; // 大きいものから 4 本見れば足りる
        }
        return null;
    }

    // m2ts 1 本（の先頭と途中）に暗号化された単位があるか。
    public static bool M2tsEncrypted(Stream stream)
    {
        foreach (long at in SamplePositions(stream.Length, 6144))
        {
            stream.Position = at;
            var buf = ReadUpTo(stream, 6144 * 64);
            for (int u = 0; u + 6144 <= buf.Length; u += 6144)
                if (BdUnitEncrypted(buf, u)) return true;
        }
        return false;
    }

    public static EncryptedInput? CheckTs(string tsPath)
    {
        using var fs = new FileStream(tsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        int packetSize = DetectTsPacketSize(fs);
        if (packetSize == 0) return null; // TS として読めない（別の段階でエラーになる）
        long scrambled = 0, payload = 0;
        foreach (long at in SamplePositions(fs.Length, packetSize))
        {
            fs.Position = at;
            var buf = ReadUpTo(fs, packetSize * 8192);
            int off = packetSize == 192 ? 4 : 0;
            for (int p = 0; p + packetSize <= buf.Length; p += packetSize)
            {
                int i = p + off;
                if (buf[i] != 0x47) continue;
                int pid = ((buf[i + 1] & 0x1F) << 8) | buf[i + 2];
                if (pid < 0x20 || pid == 0x1FFF) continue;          // PSI・ヌルは見ない
                if ((buf[i + 3] & 0x10) == 0) continue;             // ペイロードなし
                payload++;
                if ((buf[i + 3] & 0xC0) != 0) scrambled++;          // transport_scrambling_control
            }
        }
        // 一部のパケットだけが印つきでも、映像・音声が読めないので断る（1% を越えたら）。
        if (payload > 0 && scrambled * 100 > payload)
            return new EncryptedInput("TS", "スクランブル", $"{scrambled:N0}/{payload:N0} パケット");
        return null;
    }

    // ── DVD ─────────────────────────────────────────────────────────────
    // 1 セクタ＝1 パック。パックヘッダ（00 00 01 BA）の後の最初の PES のヘッダを見る。
    private static bool DvdPackScrambled(byte[] b, int s)
    {
        if (b[s] != 0 || b[s + 1] != 0 || b[s + 2] != 1 || b[s + 3] != 0xBA) return false;
        if ((b[s + 4] & 0xC0) != 0x40) return false;                // MPEG-2 のパックヘッダだけ
        int p = s + 14 + (b[s + 13] & 0x07);                         // pack_stuffing_length
        // システムヘッダ（00 00 01 BB）があれば飛ばす。
        if (p + 6 <= s + 2048 && b[p] == 0 && b[p + 1] == 0 && b[p + 2] == 1 && b[p + 3] == 0xBB)
            p += 6 + ((b[p + 4] << 8) | b[p + 5]);
        if (p + 7 > s + 2048 || b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1) return false;
        int sid = b[p + 3];
        bool av = sid is >= 0xE0 and <= 0xEF or >= 0xC0 and <= 0xDF or 0xBD;
        if (!av || (b[p + 6] & 0xC0) != 0x80) return false;          // MPEG-2 の PES ヘッダ（'10'）
        return (b[p + 6] & 0x30) != 0;                               // PES_scrambling_control
    }

    // ── BD ─────────────────────────────────────────────────────────────
    // Aligned Unit（192 バイト×32）。暗号化された単位は、先頭 16 バイトを除いて暗号化されている＝2 つ目以降の
    // ソースパケットの同期バイトが 0x47 にならない。copy_permission_indicator（TP_extra_header の上 2 ビット）も見る。
    private static bool BdUnitEncrypted(byte[] b, int u)
    {
        if (b[u + 4] != 0x47) return false;                          // 先頭のパケットも読めない＝m2ts ではない（判定しない）
        if ((b[u] & 0xC0) != 0) return true;                         // copy_permission_indicator
        int bad = 0;
        for (int k = 1; k < 32; k++) if (b[u + k * 192 + 4] != 0x47) bad++;
        return bad > 4;
    }

    // ── TS ─────────────────────────────────────────────────────────────
    private static int DetectTsPacketSize(Stream s)
    {
        s.Position = 0;
        var buf = ReadUpTo(s, 192 * 20);
        foreach (int size in new[] { 188, 192 })
        {
            int off = size == 192 ? 4 : 0;
            bool ok = buf.Length >= size * 10;
            for (int k = 0; ok && k < 10; k++) ok = buf[k * size + off] == 0x47;
            if (ok) return size;
        }
        return 0;
    }

    // ── 共通 ────────────────────────────────────────────────────────────
    // 先頭・1/3・2/3 の位置（unit の倍数にそろえる）。
    private static IEnumerable<long> SamplePositions(long length, int unit)
    {
        foreach (double f in new[] { 0.0, 1.0 / 3, 2.0 / 3 })
        {
            long at = (long)(length * f) / unit * unit;
            if (at < length) yield return at;
        }
    }

    private static byte[] ReadUpTo(Stream s, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        return read == count ? buf : buf[..read];
    }

    // ISO の中のフォルダから、条件に合うファイルを開く（UDF を優先し、読めなければ ISO 9660）。
    private static IEnumerable<(string Name, Stream Stream)> OpenFiles(string isoPath, string dir, Func<string, bool> match,
        bool largestFirst = false)
    {
        UdfReader? udf = null;
        try
        {
            using (var probe = MediaFile.OpenRead(isoPath))
                if (UdfReader.LooksLikeUdf(probe)) udf = new UdfReader(isoPath);
        }
        catch { udf = null; }

        if (udf is not null)
        {
            using (udf)
            {
                if (udf.FindEntry(dir) is not { IsDirectory: true } d) yield break;
                var files = udf.ListChildren(d).Where(f => !f.IsDirectory && match(f.Name)).ToList();
                if (largestFirst) files = files.OrderByDescending(f => f.Length).ToList();
                foreach (var f in files) yield return (f.Name, udf.OpenRead(f));
            }
            yield break;
        }

        using var iso = new Iso9660Reader(isoPath);
        if (iso.FindEntry(dir) is not IsoDirectoryEntry idir) yield break;
        var isoFiles = iso.ListChildren(idir).OfType<IsoFileEntry>().Where(f => match(f.Name)).ToList();
        if (largestFirst) isoFiles = isoFiles.OrderByDescending(f => f.Length).ToList();
        foreach (var f in isoFiles) yield return (f.Name, iso.OpenRead(f));
    }
}
