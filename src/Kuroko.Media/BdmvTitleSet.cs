using System.Text;

namespace Kuroko.Media.Dvd;

// Blu-ray（BDMV）の**タイトル一覧**を作る。「通常のメディアプレーヤー」動作なので、
// HDMV/BD-J のナビゲーションは実行せず、**プレイリストをタイトルとして並べる**。
//
// なぜ index.bdmv を使わないか（実測に基づく）：
//  ・index.bdmv のタイトルは 91 本あったが、うち 86 本が BD-J で、**79 本が同じ BDJO を指す**スタブだった。
//  ・残る HDMV 5 本も、参照先 Movie Object が 100〜2300 命令の分岐プログラムで、どのプレイリストを
//    再生するかは**レジスタ次第**（静的には決まらない）。3 本は NOP 1 個だけ。
//  ・そもそも本編のプレイリストは、どの Movie Object からも静的には現れなかった（BD-J が選ぶため）。
//  → index.bdmv からは実用的なタイトル一覧が作れない。プレイリスト側を正とする。
//
// 集約と展開：
//  ・**同一構成のプレイリストは1本に集約**する（中身の完全な複製が複数入っている盤があるため）。
//    ただし同一判定には **STN テーブル（選べる音声・字幕）も含める**。実測：クリップも IN/OUT も同じで
//    **STN だけが違う**言語別の版を持つ盤があり（ある市販ディスク：00800=国際版 / 00801=日本語版）、
//    STN を見ないと**日本語版が丸ごと消える**（日本語音声・字幕が選べなくなる）。
//    角度が違えば既に別タイトルになる（ある市販ディスク はこちらで分かれていた）。
//  ・**マルチアングルは角度ごとに別タイトルへ展開**する。角度に言語別の版が入っている盤があり
//    （実測：本編前半が 00882=英語 / 00884=日本語 / 00885・00886=他言語）、展開しないと選べない。
internal static class BdmvTitleSet
{
    // 既定でこれ未満のプレイリストは隠す（メニュー用の数秒の映像が大量に入っているため）。
    public static readonly TimeSpan DefaultMinDuration = TimeSpan.FromMinutes(1);

    // そのディスクイメージが Blu-ray（BDMV）か。DVD-Video の ISO・非UDF・読めないものは false。
    // 拡張子は見ない（再生器はファイルの中身でエンジンを決めるので、.iso 以外の名前でも来る）。
    // DVD-Video と同じ .iso のことが多いので、再生器も変換器もこれで経路を分ける。
    public static bool IsBlurayIso(string isoPath)
    {
        try
        {
            using (var probe = MediaFile.OpenRead(isoPath))
                if (!UdfReader.LooksLikeUdf(probe)) return false;
            using var udf = new UdfReader(isoPath);
            return udf.FindEntry("BDMV/index.bdmv") is not null;
        }
        catch { return false; }
    }

    public static List<BdTitle> Build(UdfReader udf, TimeSpan? minDuration = null)
    {
        var min = minDuration ?? DefaultMinDuration;
        var playlists = BdmvPlaylist.ReadAll(udf);

        // 同一構成（クリップ列・角度構成・IN/OUT・接続条件）の重複を落とす。ファイル名の昇順で先着を残す。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<BdPlaylist>();
        foreach (var pl in playlists.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase))
            if (seen.Add(Signature(pl))) unique.Add(pl);

        // 尺の長い順＝本編が先。番号は 1 から通しで振る。
        var titles = new List<BdTitle>();
        int number = 0;
        foreach (var pl in unique.Where(p => p.Duration >= min).OrderByDescending(p => p.DurationTicks))
        {
            number++;
            int angleCount = pl.Items.Count == 0 ? 1 : pl.Items.Max(i => i.Angles.Count);
            if (angleCount <= 1)
            {
                titles.Add(new BdTitle(number, 0, 1, pl));
            }
            else
            {
                for (int a = 0; a < angleCount; a++) titles.Add(new BdTitle(number, a, angleCount, pl));
            }
        }
        return titles;
    }

    // 一覧から「本編らしい」1本を選ぶ。**診断用**（`--udf-list` の表示と `--bd-dump` の対象決め）。
    // 再生器の既定タイトルはこれではなく**一覧の先頭（タイトル1）**：内蔵ISOの通常メディアプレーヤー動作
    // （`DvdPlaybackEngine` の defaultTitle = Titles[0]）と揃えるため。
    //
    // 単純な最長は当てにならない：実測の盤では最長が「同じ1分のクリップを302回並べたループ用」だった
    // （BD-J の背景映像と思われる）。**同じクリップの反復で尺を稼いでいるものを外して**から最長を採る。
    public static BdTitle? PickMain(List<BdTitle> titles)
    {
        static bool IsRepetitive(BdTitle t)
        {
            int items = t.Playlist.Items.Count;
            int distinct = t.ClipIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return items >= 10 && distinct <= 2;
        }
        return titles.Where(t => !IsRepetitive(t)).OrderByDescending(t => t.Duration).FirstOrDefault()
            ?? titles.FirstOrDefault();
    }

    private static string Signature(BdPlaylist pl)
    {
        var sb = new StringBuilder();
        foreach (var it in pl.Items)
        {
            sb.Append(string.Join('|', it.Angles)).Append(':')
              .Append(it.InTime).Append('-').Append(it.OutTime).Append('/').Append(it.ConnectionCondition).Append(';');
            // STN テーブル（選べる音声・字幕）まで見る。ここを省くと、同じクリップを指す
            // 「国際版」と「日本語版」が同一とみなされ、後者が一覧から消える。
            sb.Append('A').Append(string.Join(',', it.AudioStreams.Select(a => $"{a.Pid:X}{a.Language}")))
              .Append('P').Append(string.Join(',', it.PgStreams.Select(a => $"{a.Pid:X}{a.Language}")))
              .Append(';');
        }
        return sb.ToString();
    }
}

// タイトル1件。角度が無ければ AngleCount=1・AngleIndex=0。
internal sealed class BdTitle
{
    public BdTitle(int number, int angleIndex, int angleCount, BdPlaylist playlist)
    {
        Number = number; AngleIndex = angleIndex; AngleCount = angleCount; Playlist = playlist;
    }

    public int Number { get; }        // 1 始まりのタイトル番号（角度違いは同じ番号）
    public int AngleIndex { get; }    // 0 始まり
    public int AngleCount { get; }
    public BdPlaylist Playlist { get; }

    public bool HasAngles => AngleCount > 1;
    public TimeSpan Duration => Playlist.Duration;
    public IReadOnlyList<TimeSpan> Chapters => Playlist.Chapters;

    // この角度で実際に読むクリップ列（PlayItem 順）。
    public IReadOnlyList<string> ClipIds => Playlist.Items.Select(i => i.ClipForAngle(AngleIndex)).ToList();

    // 表示名。角度が無ければ「タイトル3」、あれば「タイトル3アングル1」（角度は1始まりで見せる）。
    public string DisplayName => HasAngles ? $"タイトル{Number}アングル{AngleIndex + 1}" : $"タイトル{Number}";

    // このタイトルで選べる音声・字幕（STN テーブルの出現順＝論理番号）。先頭 PlayItem を代表とする
    // （規格上、プレイリスト内の PlayItem は同じ构成を持つ）。
    public IReadOnlyList<BdStreamEntry> AudioStreams
        => Playlist.Items.Count > 0 ? Playlist.Items[0].AudioStreams : Array.Empty<BdStreamEntry>();
    public IReadOnlyList<BdStreamEntry> PgStreams
        => Playlist.Items.Count > 0 ? Playlist.Items[0].PgStreams : Array.Empty<BdStreamEntry>();

    // 一覧で見分けるための言語注記。同じ尺の「国際版」と「日本語版」が並ぶことがあるので、
    // 音声と字幕の言語を重複無しで並べる（例：`[eng,jpn]`）。無ければ空文字列。
    public string LanguagesDisplay
    {
        get
        {
            var langs = AudioStreams.Concat(PgStreams)
                .Select(s => s.Language)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return langs.Count == 0 ? "" : $"[{string.Join(",", langs)}]";
        }
    }

    // クリップ構成の表示。**連続する同じクリップは回数でまとめる**（`00720×451` のように）。
    // 重複を単純に除くと「18秒のクリップを 451 回繰り返す」ようなプレイリストが 1 本に見えてしまい、
    // 実際に読む長さ（451 倍）と食い違って原因調査を誤らせる。
    public string ClipsDisplay
    {
        get
        {
            var parts = new List<string>();
            string? cur = null;
            int run = 0;
            foreach (var c in ClipIds)
            {
                if (c == cur) { run++; continue; }
                if (cur is not null) parts.Add(run > 1 ? $"{cur}×{run}" : cur);
                cur = c; run = 1;
            }
            if (cur is not null) parts.Add(run > 1 ? $"{cur}×{run}" : cur);
            return string.Join("+", parts);
        }
    }

    public override string ToString()
        => $"{DisplayName} {Duration:hh\\:mm\\:ss} {Playlist.FileName} clips={ClipsDisplay}";
}
