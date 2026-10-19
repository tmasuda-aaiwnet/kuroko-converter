namespace Kuroko.Media.Dvd;

// 復号済みサブピクチャ（字幕/メニューボタン）1枚。BGRA・表示領域・表示/消去時刻（SPUのPTS相対）。
internal sealed class DvdSubpictureFrame
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Bgra { get; init; } = Array.Empty<byte>();
    public TimeSpan StartDelay { get; init; } // SPU PTS からの表示開始遅延
    public TimeSpan StopDelay { get; init; }  // SPU PTS からの消去遅延（無ければ TimeSpan.MaxValue）
    public bool Forced { get; init; }         // 強制字幕（PGS の forced_on_flag / AV_SUBTITLE_FLAG_FORCED）
}

// メニュー用に復号した SPU（2bitインデックス画像＋既定の色/コントラスト）。
// ボタンごとに色組（通常/選択/動作）を差し替えて合成するため、パレット適用前のインデックスを保持する。
internal sealed class DvdMenuSpu
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Index { get; init; } = Array.Empty<byte>(); // 0..3 / 画素
    public int[] DefaultColors { get; init; } = { 0, 1, 2, 3 };
    public int[] DefaultContrast { get; init; } = { 0, 0xF, 0xF, 0xF };
    public uint[] Palette { get; init; } = new uint[16];
}

// DVD サブピクチャ（SPU）デコーダ（クリーンルーム）。
// private_stream_1 のサブピクチャ(0x20-0x3F)を PTS 単位に組み立て、RLE 画素＋表示制御シーケンス
// (DCSQT) を解釈して 4 色インデックス画像 → パレット/コントラスト適用 → BGRA を生成する。
internal sealed class DvdSpuDecoder
{
    private readonly List<byte> _buf = new();
    private int _spuSize = -1;

    // 新しい SPU は PTS 付き PES から始まる。完成した時点で1枚を返す（途中は null）。
    // 注意：再mux後（H.264化）の VOB は、1つの字幕ユニットを複数PESに分割し継続PESにもPTSを付けることが
    // ある。ユニット途中（size既知かつ未完）でPTSが来てもクリアしない（クリアすると複数PESにまたがる字幕が
    // 組み立てられず表示されない）。境界判定はサイズ基準。先頭/完成済みでのPTSのみ新ユニット開始としてクリア。
    public DvdSubpictureFrame? Push(byte[] payload, bool hasPts, uint[] palette)
    {
        bool midUnit = _spuSize >= 0 && _buf.Count < _spuSize;
        if (hasPts && !midUnit)
        {
            _buf.Clear();
            _spuSize = -1;
        }
        _buf.AddRange(payload);
        if (_spuSize < 0 && _buf.Count >= 2)
            _spuSize = (_buf[0] << 8) | _buf[1];
        // 安全策：誤整合（シーク直後にユニット途中から拾う等）で size に達しないまま肥大化したらリセットして
        // 次ユニットから再同期する。最大SPUサイズは 0xFFFF なのでそれを超えたら異常。
        if (_buf.Count > 0x10000)
        {
            _buf.Clear();
            _spuSize = -1;
            return null;
        }
        if (_spuSize >= 4 && _buf.Count >= _spuSize)
        {
            DvdSubpictureFrame? frame = null;
            try { frame = Decode(_buf.ToArray(), _spuSize, palette); }
            catch { frame = null; }
            _buf.Clear();
            _spuSize = -1;
            return frame;
        }
        return null;
    }

    // Push の複数状態版。1つの SPU が時間変化する表示（フェード／行範囲ごとの出し分け）を持つ場合に、
    // その全状態を時刻順で返す。再生側はこれをそのまま表示待ち行列へ積む。
    public IReadOnlyList<DvdSubpictureFrame>? PushStates(byte[] payload, bool hasPts, uint[] palette)
    {
        bool midUnit = _spuSize >= 0 && _buf.Count < _spuSize;
        if (hasPts && !midUnit)
        {
            _buf.Clear();
            _spuSize = -1;
        }
        _buf.AddRange(payload);
        if (_spuSize < 0 && _buf.Count >= 2)
            _spuSize = (_buf[0] << 8) | _buf[1];
        if (_buf.Count > 0x10000)
        {
            _buf.Clear();
            _spuSize = -1;
            return null;
        }
        if (_spuSize >= 4 && _buf.Count >= _spuSize)
        {
            List<DvdSubpictureFrame>? frames = null;
            try { frames = DecodeStates(_buf.ToArray(), _spuSize, palette); }
            catch { frames = null; }
            _buf.Clear();
            _spuSize = -1;
            return frames;
        }
        return null;
    }

    public void Reset()
    {
        _buf.Clear();
        _spuSize = -1;
    }

    // 完成した menu SPU（substream 0x20 等）を 2bit インデックス画像へ復号する（メニューのボタン図柄）。
    public static DvdMenuSpu? DecodeMenuSpu(byte[] spu, uint[] palette)
    {
        if (spu.Length < 4)
            return null;
        int size = (spu[0] << 8) | spu[1];
        if (size < 4 || size > spu.Length)
            size = spu.Length;
        var p = ParseSpu(spu, size);
        if (p is null)
            return null;
        return new DvdMenuSpu
        {
            X = p.X0,
            Y = p.Y0,
            Width = p.Width,
            Height = p.Height,
            Index = p.Index,
            DefaultColors = p.Colors,
            DefaultContrast = p.Contrast,
            Palette = palette,
        };
    }

    // 完成した字幕 SPU 1つを BGRA フレーム（表示領域・表示/消去遅延つき）へ復号する。変換器が字幕を
    // subs/img/<source>/ の PNG へ書き出す（§11）ために使う。再生側の Push と同じ Decode を通す。
    public static DvdSubpictureFrame? DecodeSubpicture(byte[] spu, uint[] palette)
    {
        if (spu.Length < 4) return null;
        int size = (spu[0] << 8) | spu[1];
        if (size < 4 || size > spu.Length) size = spu.Length;
        try { return Decode(spu, size, palette); } catch { return null; }
    }

    private static DvdSubpictureFrame? Decode(byte[] spu, int size, uint[] palette)
    {
        var list = DecodeStates(spu, size, palette);
        return list is { Count: > 0 } ? list[0] : null;
    }

    // SPU を「表示状態の列」へ復号する。DCSQ ごとに1枚を作り、各フレームは自分の表示区間
    // （StartDelay＝その DCSQ の時刻／StopDelay＝次の DCSQ の時刻。最後は STP_DSP）を持つ。
    //
    // これが必要な理由：ディスクは1つの SPU の中で表示内容を時間変化させる。フェードイン/アウトのほか、
    // **1枚のビットマップに複数の字幕（縦書きの説明＋横書きのセリフ等）を入れておき、CHG_COLCON で行範囲ごとに
    // 濃度を切り替えて出し分ける**構成がある。1枚に畳むと「両方同時に見える／全部透明で消える」になる。

    // 状態の「時刻と領域」だけを取り出す（描画しない）。krk 再生で cue 一覧を作るときに使う：
    // 全状態を先に描くとメモリが破綻するため、一覧作成は時刻だけで行い、描画は切替時に DecodeStateAt で行う。
    public static List<(TimeSpan Start, TimeSpan Stop, int X, int Y)> DecodeStateTimings(byte[] spu, int size)
    {
        var result = new List<(TimeSpan, TimeSpan, int, int)>();
        var p = ParseSpu(spu, size);
        if (p is null)
            return result;
        var states = p.States;
        if (states.Count == 0)
        {
            result.Add((p.StartDelay, p.StopDelay, p.X0, p.Y0));
            return result;
        }
        for (int si = 0; si < states.Count; si++)
        {
            var start = states[si].When;
            var stop = si + 1 < states.Count ? states[si + 1].When : p.StopDelay;
            if (stop <= start && si + 1 < states.Count) continue;
            result.Add((start, stop, p.X0, p.Y0));
        }
        return result;
    }

    // 指定インデックスの状態だけを描画する（DecodeStateTimings と同じ並び）。
    public static DvdSubpictureFrame? DecodeStateAt(byte[] spu, int size, uint[] palette, int stateIndex)
    {
        var list = DecodeStates(spu, size, palette, stateIndex);
        return list.Count > 0 ? list[0] : null;
    }

    internal static List<DvdSubpictureFrame> DecodeStates(byte[] spu, int size, uint[] palette, int onlyIndex = -1)
    {
        var result = new List<DvdSubpictureFrame>();
        var p = ParseSpu(spu, size);
        if (p is null)
            return result;

        var states = p.States;
        if (states.Count == 0)
        {
            // 状態が取れない（STA_DSP が無い等）ときは従来どおり最終状態で1枚だけ作る。
            states = new List<SpuState>
            {
                new() { When = p.StartDelay, Colors = p.Colors, Contrast = p.Contrast },
            };
        }

        int emitted = 0;
        for (int si = 0; si < states.Count; si++)
        {
            var st = states[si];
            var start = st.When;
            var stop = si + 1 < states.Count ? states[si + 1].When : p.StopDelay;
            if (stop <= start && si + 1 < states.Count)
                continue; // 同時刻の重複 DCSQ は後勝ち

            // onlyIndex 指定時は、その1枚だけを描く（DecodeStateTimings と採用条件が同じなので番号は一致する）。
            emitted++;
            if (onlyIndex >= 0 && emitted - 1 != onlyIndex)
            {
                if (emitted - 1 > onlyIndex) break;
                continue;
            }

            var bgra = new byte[p.Width * p.Height * 4];
            for (int y = 0; y < p.Height; y++)
            {
                int absLine = p.Y0 + y;
                for (int x = 0; x < p.Width; x++)
                {
                    int i = y * p.Width + x;
                    int col = p.Index[i];
                    var colors = st.Colors;
                    var contrast = st.Contrast;

                    // CHG_COLCON：この絶対ライン・絶対列に効く変更点があれば、色/濃度を差し替える。
                    var ov = FindColCon(st.ColCon, absLine, p.X0 + x);
                    if (ov is not null)
                    {
                        colors = ov.Value.Colors;
                        contrast = ov.Value.Contrast;
                    }

                    uint yuv = palette[colors[col] & 0x0F];
                    (byte r, byte g, byte b) = YuvToRgb(yuv);
                    byte a = (byte)(contrast[col] * 17);
                    int idx = i * 4;
                    bgra[idx + 0] = b;
                    bgra[idx + 1] = g;
                    bgra[idx + 2] = r;
                    bgra[idx + 3] = a;
                }
            }

            result.Add(new DvdSubpictureFrame
            {
                X = p.X0,
                Y = p.Y0,
                Width = p.Width,
                Height = p.Height,
                Bgra = bgra,
                StartDelay = start,
                StopDelay = stop,
            });
        }

        return result;
    }

    // 指定の絶対ライン/絶対列に適用される CHG_COLCON の変更点を返す（無ければ null）。
    // 同一行グループ内では「開始列がその列以下の変更点のうち最後のもの」が有効。
    private static (int[] Colors, int[] Contrast)? FindColCon(List<ColConChange> colcon, int line, int col)
    {
        if (colcon.Count == 0)
            return null;
        for (int i = 0; i < colcon.Count; i++)
        {
            var c = colcon[i];
            if (line < c.LineStart || line > c.LineEnd)
                continue;
            (int[], int[])? hit = null;
            for (int k = 0; k < c.Points.Count; k++)
            {
                if (c.Points[k].Col <= col)
                    hit = (c.Points[k].Colors, c.Points[k].Contrast);
                else
                    break;
            }
            if (hit is not null)
                return hit;
        }
        return null;
    }

    // SPU の DCSQ を解釈し、表示領域・色/コントラスト・RLE を 2bit インデックス画像へ復号した結果。
    private sealed class SpuParsed
    {
        public int X0, Y0, Width, Height;
        public int[] Colors = { 0, 1, 2, 3 };
        public int[] Contrast = { 0, 0xF, 0xF, 0xF };
        public byte[] Index = Array.Empty<byte>();
        public TimeSpan StartDelay;
        public TimeSpan StopDelay;
        // DCSQ ごとの表示状態（時刻順）。フェードや行範囲ごとの表示切替を再現するために使う。
        public List<SpuState> States = new();
    }

    // 1つの DCSQ 時点での表示状態。DCSQ は前の状態を引き継いで差分更新される。
    private sealed class SpuState
    {
        public TimeSpan When;
        public int[] Colors = { 0, 1, 2, 3 };
        public int[] Contrast = { 0, 0xF, 0xF, 0xF };
        public List<ColConChange> ColCon = new();   // CHG_COLCON（行範囲ごとの色/濃度差し替え）
    }

    // CHG_COLCON の 1 行グループ。LineStart..LineEnd（絶対ライン）に対し、列 Col 以降へ Colors/Contrast を適用する
    // 変更点（PX_CTLI）を並べる。列は絶対座標。
    internal sealed class ColConChange
    {
        public int LineStart;
        public int LineEnd;
        public List<(int Col, int[] Colors, int[] Contrast)> Points = new();
    }

    private static SpuParsed? ParseSpu(byte[] spu, int size)
    {
        if (size < 4)
            return null;
        int dcsqtStart = (spu[2] << 8) | spu[3];
        if (dcsqtStart + 2 > size)
            return null;

        int[] colors = { 0, 1, 2, 3 };
        int[] contrast = { 0, 0xF, 0xF, 0xF };
        int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        int topOffset = 4, bottomOffset = 4;
        TimeSpan startDelay = TimeSpan.Zero;
        TimeSpan stopDelay = TimeSpan.MaxValue;
        bool haveArea = false;
        // DCSQ ごとの表示状態（フェードや、行範囲ごとの表示/非表示の切り替えを時系列で保持する）。
        var states = new List<SpuState>();
        var colcon = new List<ColConChange>();
        bool started = false;

        int p = dcsqtStart;
        int guard = 0;
        while (p + 4 <= size && guard++ < 64)
        {
            int delay = (spu[p] << 8) | spu[p + 1];          // 1/90000*1024 単位
            int nextDcsq = (spu[p + 2] << 8) | spu[p + 3];
            var when = TimeSpan.FromSeconds(delay * 1024.0 / 90000.0);
            int q = p + 4;

            bool end = false;
            while (q < size && !end)
            {
                int cmd = spu[q++];
                switch (cmd)
                {
                    case 0x00: // FSTA_DSP（強制表示開始）
                    case 0x01: // STA_DSP（表示開始）
                        if (!started) { startDelay = when; started = true; }
                        break;
                    case 0x02: // STP_DSP（表示停止）
                        stopDelay = when;
                        break;
                    case 0x03: // SET_COLOR（4色のパレット索引）
                        if (q + 2 <= size)
                        {
                            colors[3] = spu[q] >> 4;
                            colors[2] = spu[q] & 0x0F;
                            colors[1] = spu[q + 1] >> 4;
                            colors[0] = spu[q + 1] & 0x0F;
                            q += 2;
                        }
                        break;
                    case 0x04: // SET_CONTR（4色のコントラスト=透過）
                        if (q + 2 <= size)
                        {
                            contrast[3] = spu[q] >> 4;
                            contrast[2] = spu[q] & 0x0F;
                            contrast[1] = spu[q + 1] >> 4;
                            contrast[0] = spu[q + 1] & 0x0F;
                            q += 2;
                        }
                        break;
                    case 0x05: // SET_DAREA（表示領域）
                        if (q + 6 <= size)
                        {
                            x0 = (spu[q] << 4) | (spu[q + 1] >> 4);
                            x1 = ((spu[q + 1] & 0x0F) << 8) | spu[q + 2];
                            y0 = (spu[q + 3] << 4) | (spu[q + 4] >> 4);
                            y1 = ((spu[q + 4] & 0x0F) << 8) | spu[q + 5];
                            haveArea = true;
                            q += 6;
                        }
                        break;
                    case 0x06: // SET_DSPXA（上/下フィールドの画素データ位置）
                        if (q + 4 <= size)
                        {
                            topOffset = (spu[q] << 8) | spu[q + 1];
                            bottomOffset = (spu[q + 2] << 8) | spu[q + 3];
                            q += 4;
                        }
                        break;
                    case 0x07: // CHG_COLCON（行範囲ごとの色/濃度の差し替え）
                        // 構造（実ディスクで確認）：2バイト=このブロック全体のサイズ（自身を含む）。
                        // 続けて LN_CTLI（4バイト）＝[12bit 開始ライン][4bit 変更点数 n][12bit 終了ライン]。
                        // さらに PX_CTLI（6バイト）× n ＝[12bit 開始列][4色のパレット索引][4色のコントラスト]。
                        // 開始ライン 0xFFF は終端。ライン/列は**絶対座標**（表示領域 SET_DAREA と同じ基準）。
                        if (q + 2 <= size)
                        {
                            int len = (spu[q] << 8) | spu[q + 1];
                            int blockEnd = Math.Min(size, q + Math.Max(2, len));
                            int r = q + 2;
                            colcon = new List<ColConChange>();
                            while (r + 4 <= blockEnd)
                            {
                                int lnStart = ((spu[r] << 8) | spu[r + 1]) & 0x0FFF;
                                int w1 = (spu[r + 2] << 8) | spu[r + 3];
                                int n = w1 >> 12;
                                int lnEnd = w1 & 0x0FFF;
                                r += 4;
                                if (lnStart == 0x0FFF)
                                    break; // 終端マーカー
                                var chg = new ColConChange { LineStart = lnStart, LineEnd = lnEnd };
                                for (int k = 0; k < n && r + 6 <= blockEnd; k++)
                                {
                                    int col = ((spu[r] << 8) | spu[r + 1]) & 0x0FFF;
                                    var cc = new int[4];
                                    cc[3] = spu[r + 2] >> 4; cc[2] = spu[r + 2] & 0x0F;
                                    cc[1] = spu[r + 3] >> 4; cc[0] = spu[r + 3] & 0x0F;
                                    var ct = new int[4];
                                    ct[3] = spu[r + 4] >> 4; ct[2] = spu[r + 4] & 0x0F;
                                    ct[1] = spu[r + 5] >> 4; ct[0] = spu[r + 5] & 0x0F;
                                    chg.Points.Add((col, cc, ct));
                                    r += 6;
                                }
                                colcon.Add(chg);
                            }
                            q = blockEnd;
                        }
                        else end = true;
                        break;
                    case 0xFF: // コマンド列終端
                        end = true;
                        break;
                    default:
                        end = true;
                        break;
                }
            }

            // この DCSQ 時点の状態を積む（表示開始後のみ。停止時点は積まない＝StopDelay で終わる）。
            if (started && when < stopDelay)
            {
                states.Add(new SpuState
                {
                    When = when,
                    Colors = (int[])colors.Clone(),
                    Contrast = (int[])contrast.Clone(),
                    ColCon = colcon,
                });
            }

            if (nextDcsq == p || nextDcsq < dcsqtStart || nextDcsq + 4 > size)
                break;
            p = nextDcsq;
        }

        if (!haveArea || x1 < x0 || y1 < y0)
            return null;

        int width = x1 - x0 + 1;
        int height = y1 - y0 + 1;
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048)
            return null;

        var index = new byte[width * height];
        DecodeFieldIndex(spu, size, topOffset, width, height, 0, index);    // 偶数ライン
        DecodeFieldIndex(spu, size, bottomOffset, width, height, 1, index); // 奇数ライン

        return new SpuParsed
        {
            X0 = x0,
            Y0 = y0,
            Width = width,
            Height = height,
            Colors = colors,
            Contrast = contrast,
            Index = index,
            StartDelay = startDelay,
            StopDelay = stopDelay,
            States = states,
        };
    }

    // 片フィールド（偶数 or 奇数ライン）の RLE を復号して 2bit インデックスを書く。
    private static void DecodeFieldIndex(byte[] spu, int size, int byteOffset, int width, int height, int fieldParity,
        byte[] index)
    {
        var br = new NibbleReader(spu, byteOffset, size);
        for (int line = fieldParity; line < height; line += 2)
        {
            int x = 0;
            while (x < width)
            {
                int v = br.Read4();
                if (v < 0x4)
                {
                    v = (v << 4) | br.Read4();
                    if (v < 0x10)
                    {
                        v = (v << 4) | br.Read4();
                        if (v < 0x40)
                            v = (v << 4) | br.Read4();
                    }
                }
                int run = v >> 2;
                int col = v & 0x3;
                if (run == 0)
                    run = width - x; // 行末まで

                int end = Math.Min(width, x + run);
                for (; x < end; x++)
                    index[line * width + x] = (byte)col;
            }
            br.AlignToByte();
        }
    }

    private static (byte r, byte g, byte b) YuvToRgb(uint yuv)
    {
        // DVD の CLUT は 0x00 Y Cr Cb の順（Cr が先、Cb が後）。Cb/Cr を取り違えると赤青が入れ替わる。
        int y = (int)((yuv >> 16) & 0xFF);
        int cr = (int)((yuv >> 8) & 0xFF);
        int cb = (int)(yuv & 0xFF);
        double yy = y - 16, u = cb - 128, vv = cr - 128;
        int r = (int)(1.164 * yy + 1.596 * vv);
        int g = (int)(1.164 * yy - 0.392 * u - 0.813 * vv);
        int b = (int)(1.164 * yy + 2.017 * u);
        return ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
    }

    // 4ビット（ニブル）単位の読み出し。RLE 用。
    private struct NibbleReader
    {
        private readonly byte[] _d;
        private readonly int _end;
        private int _byte;
        private bool _high;

        public NibbleReader(byte[] d, int start, int end)
        {
            _d = d;
            _byte = start;
            _end = end;
            _high = true;
        }

        public int Read4()
        {
            if (_byte >= _end)
                return 0;
            int b = _d[_byte];
            int v = _high ? (b >> 4) : (b & 0x0F);
            if (_high) _high = false;
            else { _high = true; _byte++; }
            return v;
        }

        public void AlignToByte()
        {
            if (!_high) { _high = true; _byte++; }
        }
    }
}
