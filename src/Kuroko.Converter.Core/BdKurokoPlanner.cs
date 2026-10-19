using System.Text;
using Kuroko.Media.Dvd;

namespace Kuroko.Converter;

// BD-ISO → Kuroko（controller `none`）の**変換計画**を立てる。符号化の前に、
//   ・どのクリップを何本のセグメントとして格納するか（同じクリップは 1 本＝共有）
//   ・タイトルとアングルの構成（仕様 §10.1）
//   ・章・論理音声/字幕（STN_table 由来）
// を確定させる。ここで決まった内容がそのまま seg.json / title.json / disc.json になる。
//
// タイトルの集合は **ISO 再生と同じもの**を使う（`BdmvTitleSet.Build`）。同じ関数を呼ぶので、
// 再生器と変換器で「どれをタイトルと呼ぶか」が食い違わない。
internal static class BdKurokoPlanner
{
    // アングル間で「同じ」とみなす尺の許容差（≒1フレーム@29.97）。
    private const double DurationToleranceMs = 34.0;

    public static BdPlan Build(UdfReader udf, TimeSpan? minDuration = null)
    {
        var plan = new BdPlan();
        var titles = BdmvTitleSet.Build(udf, minDuration);
        var segByClip = new Dictionary<string, BdPlanSegment>(StringComparer.OrdinalIgnoreCase);

        int titleId = 1;
        foreach (var group in titles.GroupBy(t => t.Number).OrderBy(g => g.Key))
        {
            var variants = group.OrderBy(t => t.AngleIndex).ToList();
            var head = variants[0];
            var items = head.Playlist.Items;

            // 各アングルのプレイリスト（セグメント参照）を作る。セグメントはクリップ単位で共有する。
            var angles = new List<BdPlanAngle>(variants.Count);
            foreach (var v in variants)
            {
                double at = 0;
                var pl = new List<BdPlanPlaylistItem>(v.Playlist.Items.Count);
                for (int i = 0; i < v.Playlist.Items.Count; i++)
                {
                    var it = v.Playlist.Items[i];
                    string clip = it.ClipForAngle(v.AngleIndex);
                    // セグメントの同一性は **クリップ＋IN/OUT** で決まる。Kuroko のプレイリスト項目は
                    // 「そのセグメントを丸ごと再生する」だけで IN/OUT を持たないので、同じクリップでも
                    // 参照範囲が違えば別セグメントとして符号化する必要がある（範囲が同じなら共有できる）。
                    string key = $"{clip}:{it.InTime}:{it.OutTime}";
                    if (!segByClip.TryGetValue(key, out var seg))
                    {
                        var ent = udf.FindEntry($"BDMV/STREAM/{clip}.m2ts");
                        // セグメントのストリームは**クリップ自身の宣言**から取る。STN_table は PlayItem に属し、
                        // 同じクリップでも参照するプレイリスト次第で内容が変わるので、物理の権威にはならない。
                        // clpi の ProgramInfo は映像形式・フレームレート・言語まで持つので第一候補。
                        // 無い盤のために PMT（stream_type と PID だけ）へ落ちる。
                        var es = BdmvClipProgram.Read(udf, clip);
                        if (es.Count == 0 && ent is not null)
                        {
                            using var s = udf.OpenRead(ent);
                            es = BdClipStreams.Probe(s).Select(x => new BdClipStream(x.Pid, x.StreamType, null)).ToList();
                        }
                        seg = new BdPlanSegment($"S{segByClip.Count + 1:0000}", clip, ent?.Length ?? 0,
                            it.DurationTicks * 2 / 90.0, es, it.InTime, it.OutTime);
                        segByClip[key] = seg;
                    }
                    pl.Add(new BdPlanPlaylistItem(seg.SegId, at));
                    at += it.DurationTicks * 2 / 90.0;
                }
                angles.Add(new BdPlanAngle(v.AngleIndex + 1, pl, at));
            }

            // アングル整合（仕様 §10.1）。**STN_table は PlayItem に属し、アングル間で共有される**ので、
            // 「アングルごとに音声構成が違う」ことは原理的に起こらない。展開を止めるのは、Kuroko の
            // タイトル構造そのものが表現できない場合だけ：
            //  ・PlayItem 数が違う（プレイリストの形が違う）
            //  ・尺が揃っていない（Kuroko はタイトルで 1 つの timing しか持たない）
            string? mismatch = variants.Count > 1 ? CheckAngles(variants, angles) : null;
            if (mismatch is not null)
                plan.Warnings.Add($"タイトル{head.Number}: アングルを展開しません（{mismatch}）");

            // 宣言と実体の食い違い（STN_table にある PID がそのクリップに無い）は**展開を止めない**。
            // Kuroko は論理トラック → セグメントの source を 2 段で解決するので、無いセグメントでは
            // 単に「その区間はそのトラックが出ない」で正しく表現できる。記録だけしておく。
            foreach (var note in MissingStreamNotes(variants, segByClip))
                plan.Warnings.Add($"タイトル{head.Number}: {note}");

            double totalMs = angles[0].TotalMs;
            var chapters = ChapterMs(head);
            var audio = items.Count > 0 ? items[0].AudioStreams : new List<BdStreamEntry>();
            var pg = items.Count > 0 ? items[0].PgStreams : new List<BdStreamEntry>();

            if (mismatch is null)
            {
                plan.Titles.Add(new BdPlanTitle
                {
                    Id = titleId++,
                    Name = $"タイトル{head.Number}",
                    AngleType = variants.Count > 1 ? "bd" : null,
                    Angles = angles,
                    TotalMs = totalMs,
                    ChapterMs = chapters,
                    Audio = audio,
                    Pg = pg,
                });
            }
            else
            {
                // フォールバック：アングルごとに 1 タイトル（案1 相当）。
                for (int a = 0; a < variants.Count; a++)
                    plan.Titles.Add(new BdPlanTitle
                    {
                        Id = titleId++,
                        Name = $"タイトル{head.Number}アングル{a + 1}",
                        AngleType = null,
                        Angles = new List<BdPlanAngle> { angles[a] with { Number = 1 } },
                        TotalMs = angles[a].TotalMs,
                        ChapterMs = ChapterMs(variants[a]),
                        Audio = variants[a].Playlist.Items.Count > 0 ? variants[a].Playlist.Items[0].AudioStreams : audio,
                        Pg = variants[a].Playlist.Items.Count > 0 ? variants[a].Playlist.Items[0].PgStreams : pg,
                    });
            }
        }

        plan.Segments.AddRange(segByClip.Values.OrderBy(s => s.SegId, StringComparer.Ordinal));
        return plan;
    }

    // 章（PlayListMark）→ タイトル通し時刻 ms。**Kuroko では隙間を挟まない**ので、先行 PlayItem の尺の
    // 累積＋（マーク時刻−IN）で素直に求まる（ISO 再生側で必要だった Gap 補正が要らない）。
    private static List<double> ChapterMs(BdTitle t)
    {
        var result = new List<double>();
        var items = t.Playlist.Items;
        foreach (var m in t.Playlist.Marks)
        {
            if (m.MarkType != 1 || m.PlayItemId >= items.Count) continue;
            double at = 0;
            for (int i = 0; i < m.PlayItemId; i++) at += items[i].DurationTicks * 2 / 90.0;
            at += Math.Max(0, (double)m.TimeStamp - items[m.PlayItemId].InTime) * 2 / 90.0;
            result.Add(at);
        }
        result.Sort();
        return result;
    }

    // アングルを展開できない理由（できれば null）。Kuroko の 1 タイトル＝1 timing に載らない形だけを弾く。
    private static string? CheckAngles(List<BdTitle> variants, List<BdPlanAngle> angles)
    {
        int n = variants[0].Playlist.Items.Count;
        for (int a = 1; a < variants.Count; a++)
        {
            if (variants[a].Playlist.Items.Count != n) return "PlayItem 数が違う";
            if (Math.Abs(angles[a].TotalMs - angles[0].TotalMs) > DurationToleranceMs)
                return $"尺が違う（{angles[0].TotalMs:F0}ms / {angles[a].TotalMs:F0}ms）";
        }
        return null;
    }

    // STN_table が宣言した PID のうち、そのクリップに実在しないもの。クリップ単位でまとめる
    // （同じクリップを何百回も参照するプレイリストがあるため、PlayItem ごとに出すと膨れる）。
    private static IEnumerable<string> MissingStreamNotes(List<BdTitle> variants,
        Dictionary<string, BdPlanSegment> segByClip)
    {
        var missingByClip = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variants)
            foreach (var it in v.Playlist.Items)
            {
                string clip = it.ClipForAngle(v.AngleIndex);
                if (!segByClip.TryGetValue($"{clip}:{it.InTime}:{it.OutTime}", out var seg)
                    || seg.Streams.Count == 0) continue;                // 判定できない
                var have = seg.Streams.Select(e => e.Pid).ToHashSet();
                foreach (var pid in it.AudioStreams.Select(x => x.Pid).Concat(it.PgStreams.Select(x => x.Pid)))
                {
                    if (have.Contains(pid)) continue;
                    if (!missingByClip.TryGetValue(clip, out var set))
                        missingByClip[clip] = set = new SortedSet<int>();
                    set.Add(pid);
                }
            }

        foreach (var kv in missingByClip.OrderBy(k => k.Key, StringComparer.Ordinal))
            yield return $"クリップ {kv.Key} に " + string.Join("/", kv.Value.Select(p => $"0x{p:X4}"))
                       + " が無い（この区間は該当トラックが出ない）";
    }

    // 計画を人が読める形にする（診断・変換ログ用）。
    public static string Describe(BdPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"セグメント {plan.Segments.Count} 本（クリップ＋IN/OUT 単位・同じ範囲は共有）");
        double totalBytes = 0, totalMs = 0;
        foreach (var s in plan.Segments)
        {
            totalBytes += s.Bytes; totalMs += s.DurationMs;
            sb.AppendLine($"  {s.SegId} = {s.ClipId}  IN {TimeSpan.FromSeconds(s.In90k / 45000.0):hh\\:mm\\:ss\\.fff}"
                + $" 〜 {TimeSpan.FromSeconds(s.Out90k / 45000.0):hh\\:mm\\:ss\\.fff}"
                + $"  {TimeSpan.FromMilliseconds(s.DurationMs):hh\\:mm\\:ss}"
                + $"  クリップ {s.Bytes:N0} bytes  実在: 音声{s.Audio.Count()} 字幕{s.Pg.Count()}"
                + (s.Video is { } v ? $"  映像 {v.Size.W}x{v.Size.H}{(v.Interlaced ? "i" : "p")}"
                    + $" {v.FrameRate ?? "?"} {v.Aspect} type=0x{v.CodingType:X2}" : "  映像なし"));
        }
        sb.AppendLine($"  合計 {totalBytes:N0} bytes / 符号化が必要な実尺 {TimeSpan.FromMilliseconds(totalMs):hh\\:mm\\:ss}");

        sb.AppendLine();
        sb.AppendLine($"タイトル {plan.Titles.Count} 本");
        double playTotal = 0;
        foreach (var t in plan.Titles)
        {
            playTotal += t.TotalMs;
            sb.AppendLine($"  T{t.Id:00} {t.Name}  {TimeSpan.FromMilliseconds(t.TotalMs):hh\\:mm\\:ss}"
                + $"  章{t.ChapterMs.Count}  音声{t.Audio.Count} 字幕{t.Pg.Count}"
                + (t.AngleType is null ? "" : $"  angleType={t.AngleType} アングル{t.Angles.Count}"));
            foreach (var a in t.Angles)
                sb.AppendLine($"      アングル{a.Number}: {string.Join(" + ", Compact(a.Playlist))}");
        }
        sb.AppendLine($"  プレイリスト尺の合計 {TimeSpan.FromMilliseconds(playTotal):hh\\:mm\\:ss}"
            + $"（実尺の {(totalMs > 0 ? playTotal / totalMs : 0):F1} 倍＝共有と繰り返しでこれだけ節約できる）");

        if (plan.Warnings.Count > 0)
        {
            sb.AppendLine();
            foreach (var w in plan.Warnings) sb.AppendLine("警告: " + w);
        }
        return sb.ToString();
    }

    // 連続する同じセグメントを回数でまとめる（451 回繰り返すプレイリストがあるため）。
    private static IEnumerable<string> Compact(List<BdPlanPlaylistItem> pl)
    {
        string? cur = null; int run = 0;
        foreach (var p in pl)
        {
            if (p.SegId == cur) { run++; continue; }
            if (cur is not null) yield return run > 1 ? $"{cur}×{run}" : cur;
            cur = p.SegId; run = 1;
        }
        if (cur is not null) yield return run > 1 ? $"{cur}×{run}" : cur;
    }
}

internal sealed class BdPlan
{
    public List<BdPlanSegment> Segments { get; } = new();
    public List<BdPlanTitle> Titles { get; } = new();
    public List<string> Warnings { get; } = new();
}

// 格納するセグメント 1 本＝**クリップの、ある IN/OUT 区間**（複数タイトル／複数回参照から共有される）。
// In90k/Out90k はクリップ内の 45kHz 刻み（BDMV の時刻単位）。Bytes は元クリップ全体のサイズ（目安）。
internal sealed record BdPlanSegment(string SegId, string ClipId, long Bytes, double DurationMs,
    List<BdClipStream> Streams, uint In90k, uint Out90k)
{
    public BdClipStream? Video => Streams.FirstOrDefault(e => e.IsVideo);
    public IEnumerable<BdClipStream> Audio => Streams.Where(e => e.IsAudio);
    public IEnumerable<BdClipStream> Pg => Streams.Where(e => e.IsPg);
}

internal sealed record BdPlanPlaylistItem(string SegId, double StartMs);

internal sealed record BdPlanAngle(int Number, List<BdPlanPlaylistItem> Playlist, double TotalMs);

internal sealed class BdPlanTitle
{
    public int Id;
    public string Name = "";
    public string? AngleType;                       // "bd" もしくは null（アングル無し）
    public List<BdPlanAngle> Angles = new();
    public double TotalMs;
    public List<double> ChapterMs = new();
    public List<BdStreamEntry> Audio = new();
    public List<BdStreamEntry> Pg = new();
}
