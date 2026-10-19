namespace Kuroko.Media.Dvd;

// DVD メニューの1ボタン（NAV PCI の BTN_IT より）。
internal sealed class DvdNavButton
{
    public int Number { get; init; }      // 1-based
    public int XStart { get; init; }
    public int YStart { get; init; }
    public int XEnd { get; init; }
    public int YEnd { get; init; }
    public int ColorGroup { get; init; }  // BTN_COLIT のどの色組を使うか（0..2）
    public int AutoAction { get; init; }   // 0以外なら選択即実行
    public int Up { get; init; }
    public int Down { get; init; }
    public int Left { get; init; }
    public int Right { get; init; }
    public byte[] Command { get; init; } = new byte[8]; // DVD仮想マシン命令（8バイト）

    public int Width => XEnd - XStart + 1;
    public int Height => YEnd - YStart + 1;
}

// ボタンの色組（選択色/動作色：パレット索引＋コントラスト、各4色）。
internal sealed class DvdButtonColor
{
    public int[] SelectColor { get; init; } = new int[4];
    public int[] SelectContrast { get; init; } = new int[4];
    public int[] ActionColor { get; init; } = new int[4];
    public int[] ActionContrast { get; init; } = new int[4];
}

// NAV PCI（プレゼンテーション制御情報）のハイライト情報。メニューのボタン群とハイライト色。
internal sealed class DvdNavPci
{
    public int HighlightStatus { get; init; } // HLI_SS: 0=ハイライト無し
    public uint VobuUopControl { get; init; }  // PCI_GI の vobu_uop_ctl（このVOBU区間の禁止操作。本編再生中の UOP）
    public int ButtonCount { get; init; }
    public int ForcedSelect { get; init; }   // FOSL_BTNN: 強制選択ボタン番号（0=なし）
    public int ForcedActivate { get; init; } // FOAC_BTNN
    public IReadOnlyList<DvdNavButton> Buttons { get; init; } = Array.Empty<DvdNavButton>();
    public IReadOnlyList<DvdButtonColor> ColorGroups { get; init; } = Array.Empty<DvdButtonColor>();

    public bool HasButtons => ButtonCount > 0 && Buttons.Count > 0;

    // NavPacket（private_stream_2）のペイロードを解析。先頭バイトが 0x00 のとき PCI。
    // 構造はビッグエンディアン。PCI 本体は substream id の次バイトから始まる。
    public static DvdNavPci? Parse(byte[] payload)
    {
        if (payload.Length < 2 || payload[0] != 0x00)
            return null; // PCI ではない（DSI=0x01 等）

        // PCI 本体先頭（substream id を除く）。以降のオフセットは PCI 先頭基準。
        const int b = 1;
        int U16(int o) => (payload[b + o] << 8) | payload[b + o + 1];
        int U8(int o) => payload[b + o];
        long U32(int o) => ((long)payload[b + o] << 24) | ((long)payload[b + o + 1] << 16) | ((long)payload[b + o + 2] << 8) | payload[b + o + 3];

        // PCI_GI の vobu_uop_ctl（offset 8, 4バイト）＝このVOBU区間で禁止される操作（本編再生中のUOP）。
        uint vobuUop = b + 12 <= payload.Length ? (uint)U32(8) : 0u;

        // HLI / HL_GI（PCI_GI 60 + NSML_AGLI 36 = 0x60 から）。
        const int hlgi = 0x60;
        if (b + hlgi + 24 > payload.Length)
            return null;
        int hliSs = U16(hlgi + 0x00) & 0x3;     // HLI_SS（下位2bit）
        // HL_GI: 0x10=BTN_OFN, 0x11=BTN_NS（総ボタン数）, 0x12=NSL_BTN_NS（数値選択可能数）,
        // 0x14=FOSL_BTNN, 0x15=FOAC_BTNN。BTN_NS は 0x11（0x12 ではない）。
        int btnNs = U8(hlgi + 0x11) & 0x3F;     // BTN_NS
        int fosl = U8(hlgi + 0x14);             // FOSL_BTNN
        int foac = U8(hlgi + 0x15);             // FOAC_BTNN

        // BTN_COLIT（0x76 から 6×u32：3組×(選択, 動作)）。
        const int colit = 0x76;
        var groups = new List<DvdButtonColor>(3);
        for (int g = 0; g < 3; g++)
        {
            int o = colit + g * 8;
            if (b + o + 8 > payload.Length)
                break;
            long sel = U32(o);
            long act = U32(o + 4);
            // ボタン色フィールド（32bit）：上位16bit=色（パレット番号 4ニブル）、下位16bit=コントラスト（α 4ニブル）。
            groups.Add(new DvdButtonColor
            {
                SelectColor = Nibbles((int)(sel >> 16)),
                SelectContrast = Nibbles((int)sel),
                ActionColor = Nibbles((int)(act >> 16)),
                ActionContrast = Nibbles((int)act),
            });
        }

        // BTN_IT（0x8E から 18バイト×ボタン）。
        const int btnit = 0x8E;
        int count = Math.Clamp(btnNs, 0, 36);
        var buttons = new List<DvdNavButton>(count);
        for (int i = 0; i < count; i++)
        {
            int o = btnit + i * 18;
            if (b + o + 18 > payload.Length)
                break;
            int x0 = ((U8(o + 0) & 0x3F) << 4) | (U8(o + 1) >> 4);
            int x1 = ((U8(o + 1) & 0x03) << 8) | U8(o + 2);
            int y0 = ((U8(o + 3) & 0x3F) << 4) | (U8(o + 4) >> 4);
            int y1 = ((U8(o + 4) & 0x03) << 8) | U8(o + 5);
            // BTN_COLN（byte0 bit7-6）は 1-based で BTN_COLIT の色組を選ぶ（btn_coln-1 が添字）。0=色無し→0扱い。
            int colnRaw = U8(o + 0) >> 6;
            int coln = colnRaw > 0 ? colnRaw - 1 : 0;
            int auto = (U8(o + 3) >> 6) & 0x03;
            var cmd = new byte[8];
            Array.Copy(payload, b + o + 10, cmd, 0, 8);
            buttons.Add(new DvdNavButton
            {
                Number = i + 1,
                XStart = x0, YStart = y0, XEnd = x1, YEnd = y1,
                ColorGroup = coln,
                AutoAction = auto,
                Up = U8(o + 6) & 0x3F,
                Down = U8(o + 7) & 0x3F,
                Left = U8(o + 8) & 0x3F,
                Right = U8(o + 9) & 0x3F,
                Command = cmd,
            });
        }

        return new DvdNavPci
        {
            HighlightStatus = hliSs,
            VobuUopControl = vobuUop,
            ButtonCount = btnNs,
            ForcedSelect = fosl,
            ForcedActivate = foac,
            Buttons = buttons,
            ColorGroups = groups,
        };
    }

    // u16 を 4 ニブル（画素タイプ 0..3 ＝ 背景/パターン/強調1/強調2）に分解。
    // DvdSpuDecoder の colors[]/contrast[] と同じ並び（index0=最下位ニブル=画素タイプ0）に合わせる。
    private static int[] Nibbles(int v) => new[] { v & 0xF, (v >> 4) & 0xF, (v >> 8) & 0xF, (v >> 12) & 0xF };
}
