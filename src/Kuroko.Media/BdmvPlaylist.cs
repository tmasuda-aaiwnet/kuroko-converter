using System.Text;

namespace Kuroko.Media.Dvd;

// Blu-ray（BDMV）のプレイリスト（`PLAYLIST/*.mpls`）とクリップ情報（`CLIPINF/*.clpi`）の読み取り。
//
// 方針：LGPL を保つためのクリーンルーム実装。libbluray 等のコードは参照・流用せず、
// 公開仕様（Blu-ray Disc Read-Only Format part 3 / BDMV ディレクトリ構造）に基づいて実装する。
//
// **通常のメディアプレーヤー動作**（メニュー無し）に必要な範囲だけを読む：
//  ・PlayItem  … 再生するクリップ（`STREAM/<clipId>.m2ts`）と IN/OUT 時刻＝**タイトルの実体**
//  ・PlayListMark … エントリマーク＝**チャプター**
//  ・EP map（clpi）… 時刻→バイト位置の索引＝**シーク**用
// HDMV ナビゲーション（MovieObject.bdmv）と BD-J（BDJO/JAR）は読まない（メニュー非対応のため）。
//
// 時刻の単位は **45kHz**（BD の PTS 基準。TS の 90kHz の 1/2）。
internal static class BdmvPlaylist
{
    public const int TimeScale = 45000;

    // PLAYLIST ディレクトリの *.mpls を全部読む（ファイル名昇順）。壊れているものは黙って飛ばす。
    public static List<BdPlaylist> ReadAll(UdfReader udf)
    {
        var list = new List<BdPlaylist>();
        var dir = udf.FindEntry("BDMV/PLAYLIST");
        if (dir is null || !dir.IsDirectory) return list;
        foreach (var e in udf.ListChildren(dir).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (e.IsDirectory || !e.Name.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var p = Parse(e.Name, udf.ReadAllBytes(e));
                if (p is not null) list.Add(p);
            }
            catch { }
        }
        return list;
    }

    public static BdPlaylist? Parse(string fileName, byte[] b)
    {
        if (b.Length < 40 || Encoding.ASCII.GetString(b, 0, 4) != "MPLS") return null;
        string version = Encoding.ASCII.GetString(b, 4, 4);
        uint playListStart = Be32(b, 8);
        uint markStart = Be32(b, 12);

        var items = new List<BdPlayItem>();
        if (playListStart + 10 <= b.Length)
        {
            // PlayList：長さ(4)＋予約(2)＋PlayItem数(2)＋SubPath数(2)
            int p = (int)playListStart + 6;
            int itemCount = Be16(b, p); p += 4;
            for (int i = 0; i < itemCount && p + 2 <= b.Length; i++)
            {
                int len = Be16(b, p);
                if (len <= 0 || p + 2 + len > b.Length) break;
                items.Add(ParsePlayItem(b, p + 2, len));
                p += 2 + len; // **長さで送る**（STN_table を解析しなくても次項目へ進める）
            }
        }

        var marks = new List<BdMark>();
        if (markStart + 6 <= b.Length)
        {
            int p = (int)markStart + 4;
            int markCount = Be16(b, p); p += 2;
            for (int i = 0; i < markCount && p + 14 <= b.Length; i++)
            {
                marks.Add(new BdMark(b[p + 1], Be16(b, p + 2), Be32(b, p + 4)));
                p += 14;
            }
        }

        return new BdPlaylist(fileName, version, items, marks);
    }

    private static BdPlayItem ParsePlayItem(byte[] b, int o, int len)
    {
        // clip_Information_file_name(5)＋clip_codec_identifier(4)＋フラグ(2)＋ref_to_STC_id(1)
        // ＋IN_time(4)＋OUT_time(4)＋UO_mask(8)＋random_access(1)＋still_mode(1)＋still_time(2)
        // ＋（マルチアングルなら）角度数(1)＋フラグ(1)＋(角度数-1)×{clip名(5)＋codec(4)＋STC(1)}
        string clipId = Encoding.ASCII.GetString(b, o, 5);
        string codec = Encoding.ASCII.GetString(b, o + 5, 4);
        int flags = Be16(b, o + 9);
        bool multiAngle = ((flags >> 4) & 1) != 0;
        int connection = flags & 0x0F;
        uint inTime = Be32(b, o + 12);
        uint outTime = Be32(b, o + 16);

        // 角度（マルチアングル）。**言語別の版がここに入っている**ディスクがある：先頭クリップだけ見ると
        // 別物のタイトルが同じに見えてしまう（実測：本編前半が 00882/00884/00885/00886 の4版）。
        var angles = new List<string> { clipId };
        if (multiAngle && o + 33 < b.Length)
        {
            int angleCount = b[o + 32];
            for (int a = 1; a < angleCount; a++)
            {
                int ao = o + 34 + (a - 1) * 10;
                if (ao + 5 > b.Length || ao + 5 > o + len) break;
                angles.Add(Encoding.ASCII.GetString(b, ao, 5));
            }
        }
        // STN_table：この PlayItem で選べるストリームの一覧。**論理番号↔PID を決めるのはここ**。
        // 位置は固定部の直後（角度ブロックがあればその後ろ）。
        int stn = o + 32;
        if (multiAngle && o + 33 < b.Length) stn = o + 34 + (Math.Max(1, (int)b[o + 32]) - 1) * 10;
        var (au, pg) = ParseStnTable(b, stn, o + len);

        return new BdPlayItem(clipId, codec, inTime, outTime, connection, multiAngle, angles, au, pg);
    }

    // STN_table() を解析して、主音声と PG(字幕) の**出現順＝論理番号**で並べた一覧を返す。
    //
    //   STN_table(): length(16) reserved(16) 各種本数(8×7) reserved(5B) ＋ エントリ列
    //   stream_entry():      length(8) ＋ payload  … payload[0]=種別、種別1（本クリップ内）なら payload[1..2]=PID
    //   stream_attributes(): length(8) ＋ payload  … payload[0]=符号化種別、音声は payload[2..4]／PGは payload[1..3] が言語
    //
    // **長さで送れる**ので、個々の属性を全部解釈しなくても次のエントリへ進める（mpls 本体と同じ流儀）。
    private static (List<BdStreamEntry> Audio, List<BdStreamEntry> Pg) ParseStnTable(byte[] b, int o, int limit)
    {
        var audio = new List<BdStreamEntry>();
        var pg = new List<BdStreamEntry>();
        try
        {
            if (o < 0 || o + 16 > limit || limit > b.Length) return (audio, pg);
            int len = Be16(b, o);
            if (len <= 0 || o + 2 + len > limit) return (audio, pg);
            int p = o + 4;                       // length(2) ＋ reserved(2)
            int nVideo = b[p], nAudio = b[p + 1], nPg = b[p + 2];
            p += 7 + 5;                          // 本数 7 種 ＋ reserved 5B

            // 主映像はここでは使わないが、位置を進めるために読み飛ばす。
            for (int i = 0; i < nVideo && p < limit; i++) { p = SkipPair(b, p, limit); }
            for (int i = 0; i < nAudio && p < limit; i++)
            {
                if (ReadPair(b, ref p, limit) is { } e) audio.Add(e with { Number = audio.Count + 1 });
            }
            for (int i = 0; i < nPg && p < limit; i++)
            {
                if (ReadPair(b, ref p, limit) is { } e) pg.Add(e with { Number = pg.Count + 1 });
            }
        }
        catch { }
        return (audio, pg);
    }

    private static int SkipPair(byte[] b, int p, int limit)
    {
        if (p >= limit) return limit;
        p += 1 + b[p];                            // stream_entry
        if (p >= limit) return limit;
        p += 1 + b[p];                            // stream_attributes
        return p;
    }

    private static BdStreamEntry? ReadPair(byte[] b, ref int p, int limit)
    {
        if (p >= limit) return null;
        int entLen = b[p];
        int ent = p + 1;
        p = ent + entLen;
        if (p >= limit || entLen < 3) { p = Math.Min(limit, p + (p < limit ? 1 + b[p] : 0)); return null; }
        int attLen = b[p];
        int att = p + 1;
        p = att + attLen;
        // 種別 1＝本クリップ内のストリーム。副パス（種別 2〜4）はここでは扱わない。
        if (b[ent] != 1 || attLen < 1) return null;
        int pid = Be16(b, ent + 1);
        int coding = b[att];
        string? lang = null;
        // 音声（0x80〜0xA2）は format/rate の後に言語 3 文字、PG(0x90)/textST(0x92) は直後に言語 3 文字。
        int langOff = coding is 0x90 or 0x91 or 0x92 ? 1 : 2;
        if (attLen >= langOff + 3)
        {
            var s = Encoding.ASCII.GetString(b, att + langOff, 3).Trim('\0', ' ');
            if (s.Length == 3) lang = s;
        }
        return new BdStreamEntry(0, pid, coding, lang);
    }

    // CLIPINF/<clipId>.clpi の SequenceInfo から、そのクリップの **STC 範囲**（45kHz）を読む。
    // これがクリップ連結の正しい基準：PES を窓走査して推定すると、音声/PGS が映像と大きく前後する
    // 多重化で基準がずれる（実測：窓走査だと境界で PTS が約8秒重なった）。
    // 戻り値は (開始, 終了)。読めなければ (-1,-1)。
    public static (long Start45k, long End45k) ReadClipStcRange(UdfReader udf, string clipId)
    {
        try
        {
            var e = udf.FindEntry($"BDMV/CLIPINF/{clipId}.clpi");
            if (e is null) return (-1, -1);
            var b = udf.ReadAllBytes(e);
            if (b.Length < 40 || Encoding.ASCII.GetString(b, 0, 4) != "HDMV") return (-1, -1);
            int seqStart = (int)Be32(b, 8);
            if (seqStart + 6 > b.Length) return (-1, -1);
            int p = seqStart + 4;
            p++;                                  // 予約(1)
            int atcCount = b[p++];
            long min = long.MaxValue, max = -1;
            for (int a = 0; a < atcCount && p + 6 <= b.Length; a++)
            {
                p += 4;                           // SPN_ATC_start
                int stcCount = b[p++];
                p++;                              // offset_STC_id
                for (int t = 0; t < stcCount && p + 14 <= b.Length; t++)
                {
                    long ps = Be32(b, p + 6);     // presentation_start_time
                    long pe = Be32(b, p + 10);    // presentation_end_time
                    if (ps < min) min = ps;
                    if (pe > max) max = pe;
                    p += 14;                      // PCR_PID(2)+SPN_STC_start(4)+start(4)+end(4)
                }
            }
            return max < 0 ? (-1, -1) : (min, max);
        }
        catch { return (-1, -1); }
    }

    internal static ushort Be16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
    internal static uint Be32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
}

// プレイリスト1本＝**タイトル1つ**（通常のメディアプレーヤー動作での扱い）。
internal sealed class BdPlaylist
{
    public BdPlaylist(string fileName, string version, List<BdPlayItem> items, List<BdMark> marks)
    {
        FileName = fileName; Version = version; Items = items; Marks = marks;
    }

    public string FileName { get; }          // "00000.mpls"
    public string Version { get; }           // "0200" 等
    public List<BdPlayItem> Items { get; }
    public List<BdMark> Marks { get; }

    // 全 PlayItem の (OUT-IN) 合計＝タイトルの尺（45kHz）。
    public long DurationTicks => Items.Sum(i => (long)i.DurationTicks);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)DurationTicks / BdmvPlaylist.TimeScale);

    // エントリマーク（mark_type=1）を「タイトル先頭からの時刻」へ直したチャプター位置。
    // マークは所属 PlayItem のクリップ時刻で入っているので、前の項目の尺を足して通し時刻にする。
    public IReadOnlyList<TimeSpan> Chapters
    {
        get
        {
            var result = new List<TimeSpan>();
            foreach (var m in Marks)
            {
                if (m.MarkType != 1) continue;
                if (m.PlayItemId >= Items.Count) continue;
                long baseTicks = 0;
                for (int i = 0; i < m.PlayItemId; i++) baseTicks += Items[i].DurationTicks;
                long rel = baseTicks + Math.Max(0, (long)m.TimeStamp - Items[m.PlayItemId].InTime);
                result.Add(TimeSpan.FromSeconds((double)rel / BdmvPlaylist.TimeScale));
            }
            result.Sort();
            return result;
        }
    }

    // 参照しているクリップ（重複除去・出現順）。
    public IReadOnlyList<string> ClipIds => Items.Select(i => i.ClipId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public override string ToString() => $"{FileName} {Duration:hh\\:mm\\:ss} items={Items.Count} chapters={Chapters.Count}";
}

// PlayItem：再生するクリップと区間。ConnectionCondition は 1=非連続, 5/6=シームレス接続。
// Angles[0] が既定の角度（＝ClipId と同じ）。マルチアングルでなければ要素1つ。
// STN_table の 1 エントリ。Number＝**出現順の論理番号**（1始まり）、Pid＝本クリップ内の PID。
// Kuroko では Pid をそのまま `source`（論理↔物理の安定キー）に使う。
internal sealed record BdStreamEntry(int Number, int Pid, int CodingType, string? Language);

internal sealed record BdPlayItem(string ClipId, string Codec, uint InTime, uint OutTime,
    int ConnectionCondition, bool MultiAngle, List<string> Angles,
    List<BdStreamEntry> AudioStreams, List<BdStreamEntry> PgStreams)
{
    public long DurationTicks => Math.Max(0, (long)OutTime - InTime);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)DurationTicks / BdmvPlaylist.TimeScale);

    // 角度番号に対応するクリップ。角度がその項目に無ければ既定（角度0）へ落とす
    //（規格上プレイリスト内の角度数は揃うが、揃っていない盤でも破綻させない）。
    public string ClipForAngle(int angle) => angle >= 0 && angle < Angles.Count ? Angles[angle] : Angles[0];
}

// PlayListMark：mark_type=1 がエントリマーク（チャプター）。
internal sealed record BdMark(byte MarkType, ushort PlayItemId, uint TimeStamp);
