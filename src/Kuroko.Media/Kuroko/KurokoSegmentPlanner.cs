using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kuroko.Media.Dvd;

namespace Kuroko.Media.Krk;

// M4a（docs/kuroko-format-spec.md §13.5）：複数タイトルのセル列を「最大共有run（セグメント）」へ因数分解し、
// 共有部を1回だけ格納できるようにする計画を作る。判定は物理同一キー (VTS,VobId,CellId,FirstSector,LastSector)。
// 因数分解の要点：あるキー隣接 (A,B) の間で切る条件＝「A の後続が全タイトルで一意でない」または「B の先行が
// 全タイトルで一意でない」。これで、分岐/合流のある箇所だけに境界が入り、共有run が最大化される（同一キー列の
// run は同一セグメントへ dedup）。境界条件は全タイトル共通のグローバル集合で決まるため、同じ run はどのタイトルでも
// 同じ範囲に切れる＝共有部が一致する。
//
// アングル（§10.1 の angleType:"dvd"）：アングルブロックのセルは「同じ区間の別アングル」なので、**アングルごとに
// 別のセル列**になる。因数分解は (タイトル, アングル) の各セル列を並べて行い、タイトルはアングルぶんのプレイリストを持つ。
// さらに**アングルブロックのセルは必ず単独のセグメントにする**：krk のプレイリストは
// 「タイトルセル [titleCellStart..+cellCount)」という**連続範囲**でセグメントを指すが、あるアングルの再生セル列は
// PGC セル index では飛び飛び（例：0,2,4,6…）なので、まとめると連続範囲で表せないため。
// 共有部（アングルブロックの外）は従来どおり最大 run のまま 1 セグメントに畳まれ、全アングルから参照される。

internal sealed class KurokoPlannedSegment
{
    public string Id = "";                       // 連番 "S0001"
    public int SourceTitle;                       // 代表タイトル（符号化に使う。どのタイトルの実体も同一）
    // このセグメントのセルが属するアングル番号（1始まり）。0＝アングルブロックの外（全アングル共通）。
    // 符号化時に「次に再生されるセル」を求めるのに要る（アングルブロックのセルの物理的な隣は別アングル）。
    public int SourceAngle;
    public List<int> SourceCells = new();         // 代表タイトルでのセル index 列（PGC セル index。連続）
    public List<string> Keys = new();             // 物理同一キー列（dedup 判定）
    public List<int> UsedByTitles = new();        // このセグメントを参照するタイトル番号
}

internal sealed class KurokoPlannedRef
{
    public string SegmentId = "";
    public int TitleCellStart;                    // タイトルのセル index（0基準）でのこのセグメント開始
    public int CellCount;
}

internal sealed class KurokoSegmentPlan
{
    public List<KurokoPlannedSegment> Segments = new();
    // (タイトル番号, アングル番号1..) -> 参照列。アングルを持たないタイトルはアングル 1 だけ。
    public Dictionary<(int Title, int Angle), List<KurokoPlannedRef>> TitleAnglePlaylists = new();
    // タイトル番号 -> アングル数（1＝アングル無し）。
    public Dictionary<int, int> TitleAngleCount = new();

    public int AngleCountOf(int title) => TitleAngleCount.TryGetValue(title, out var n) ? Math.Max(1, n) : 1;

    public List<KurokoPlannedRef>? PlaylistOf(int title, int angle)
        => TitleAnglePlaylists.TryGetValue((title, angle), out var r) ? r : null;
}

internal static class KurokoSegmentPlanner
{
    public static KurokoSegmentPlan Plan(DvdDisc disc)
    {
        // 1. 各タイトルの PGC（セル物理同一キーとアングル判定に使う）。VTS を含めて別 VTS が混ざらないようにする。
        var titleKeys = new Dictionary<int, List<string>>();   // PGC セル index -> 物理同一キー
        var titlePgc = new Dictionary<int, DvdPgc?>();
        var angleCount = new Dictionary<int, int>();
        foreach (var t in disc.Titles)
        {
            var keys = new List<string>();
            DvdPgc? pgc = null;
            try
            {
                var tp = disc.OpenTitle(t.TitleNumber);
                pgc = tp.Pgc;
                foreach (var c in pgc.Cells)
                    keys.Add($"{t.VtsNumber}:{c.VobId}:{c.CellId}:{c.FirstSector}:{c.LastSector}");
            }
            catch { }
            titleKeys[t.TitleNumber] = keys;
            titlePgc[t.TitleNumber] = pgc;
            angleCount[t.TitleNumber] = Math.Max(1, pgc?.AngleCount ?? 1);
        }

        // 2. (タイトル, アングル) ごとの再生セル列（PGC セル index）。アングルブロックはそのアングルのセルだけを採る。
        var seqs = new Dictionary<(int Title, int Angle), List<int>>();
        foreach (var t in disc.Titles)
        {
            var pgc = titlePgc[t.TitleNumber];
            int n = titleKeys[t.TitleNumber].Count;
            for (int a = 1; a <= angleCount[t.TitleNumber]; a++)
            {
                var cells = new List<int>(n);
                for (int i = 0; i < n; i++)
                    if (pgc is null || pgc.IsCellForAngle(i, a)) cells.Add(i);
                seqs[(t.TitleNumber, a)] = cells;
            }
        }

        // 3. 各キーの先行/後続集合（^=START, $=END）を**全(タイトル,アングル)横断**で集める。
        var preds = new Dictionary<string, HashSet<string>>();
        var succs = new Dictionary<string, HashSet<string>>();
        static void Add(Dictionary<string, HashSet<string>> m, string k, string v)
        {
            if (!m.TryGetValue(k, out var s)) m[k] = s = new HashSet<string>();
            s.Add(v);
        }
        foreach (var kv in seqs)
        {
            var keys = titleKeys[kv.Key.Title];
            var cells = kv.Value;
            for (int i = 0; i < cells.Count; i++)
            {
                Add(preds, keys[cells[i]], i == 0 ? "^" : keys[cells[i - 1]]);
                Add(succs, keys[cells[i]], i == cells.Count - 1 ? "$" : keys[cells[i + 1]]);
            }
        }

        // 4-6. (タイトル,アングル) 毎に境界で run に切り、同一キー列の run を同一セグメントへ dedup、プレイリストを組む。
        var plan = new KurokoSegmentPlan();
        var byKeySeq = new Dictionary<string, KurokoPlannedSegment>();
        int nextId = 1;
        foreach (var t in disc.Titles)
        {
            plan.TitleAngleCount[t.TitleNumber] = angleCount[t.TitleNumber];
            var keys = titleKeys[t.TitleNumber];
            var pgc = titlePgc[t.TitleNumber];
            // アングルブロックのセルか（＝単独セグメントにする）。
            bool InAngleBlock(int cell) => pgc is not null && pgc.AngleOrdinalOfCell(cell).Ordinal != 0;

            for (int a = 1; a <= angleCount[t.TitleNumber]; a++)
            {
                var cells = seqs[(t.TitleNumber, a)];
                var refs = new List<KurokoPlannedRef>();
                int i = 0;
                while (i < cells.Count)
                {
                    int j = i + 1;
                    // アングルブロックのセルは単独 run。それ以外は境界が来るまで伸ばす
                    //（境界＝ preds(次)>=2 か succs(前)>=2、または PGC セル index が不連続、または次がアングルブロック）。
                    if (!InAngleBlock(cells[i]))
                        while (j < cells.Count
                               && !InAngleBlock(cells[j])
                               && cells[j] == cells[j - 1] + 1
                               && preds[keys[cells[j]]].Count < 2
                               && succs[keys[cells[j - 1]]].Count < 2)
                            j++;

                    var runCells = cells.GetRange(i, j - i);
                    var runKeys = runCells.Select(c => keys[c]).ToList();
                    string seqKey = string.Join("|", runKeys);
                    if (!byKeySeq.TryGetValue(seqKey, out var seg))
                    {
                        seg = new KurokoPlannedSegment
                        {
                            Id = $"S{nextId++:0000}",
                            SourceTitle = t.TitleNumber,
                            SourceAngle = pgc?.AngleOrdinalOfCell(runCells[0]).Ordinal ?? 0,
                            SourceCells = runCells,
                            Keys = runKeys,
                        };
                        byKeySeq[seqKey] = seg;
                        plan.Segments.Add(seg);
                    }
                    if (!seg.UsedByTitles.Contains(t.TitleNumber)) seg.UsedByTitles.Add(t.TitleNumber);
                    // プレイリストはタイトルセル（＝PGC セル index）の連続範囲で指す。run は上記の切り方により必ず連続。
                    refs.Add(new KurokoPlannedRef
                    {
                        SegmentId = seg.Id,
                        TitleCellStart = runCells[0],
                        CellCount = runCells.Count,
                    });
                    i = j;
                }
                plan.TitleAnglePlaylists[(t.TitleNumber, a)] = refs;
            }
        }
        return plan;
    }

    // 診断ダンプ（--segment-plan）。共有でどれだけセルが減るかと、各タイトル/アングルのプレイリストを出す。
    public static string Dump(DvdDisc disc)
    {
        var plan = Plan(disc);
        var sb = new StringBuilder();
        long uniqueCells = plan.Segments.Sum(s => (long)s.Keys.Count);
        long totalCells = plan.TitleAnglePlaylists.Sum(p => (long)p.Value.Sum(r => r.CellCount));
        sb.AppendLine($"=== segment plan: titles={disc.Titles.Count} segments={plan.Segments.Count} ===");
        sb.AppendLine($"cells: total(across titles/angles)={totalCells} unique(segments)={uniqueCells} shared-saved={totalCells - uniqueCells}");
        foreach (var s in plan.Segments)
            sb.AppendLine($"{s.Id}: cells={s.Keys.Count} srcTitle=T{s.SourceTitle}[{string.Join(",", s.SourceCells)}] usedBy=[{string.Join(",", s.UsedByTitles.Select(x => "T" + x))}]");
        sb.AppendLine("--- playlists ---");
        foreach (var kv in plan.TitleAnglePlaylists.OrderBy(k => k.Key.Title).ThenBy(k => k.Key.Angle))
        {
            int angles = plan.AngleCountOf(kv.Key.Title);
            string head = angles > 1 ? $"T{kv.Key.Title}A{kv.Key.Angle}" : $"T{kv.Key.Title}";
            sb.AppendLine($"{head}: {string.Join(" -> ", kv.Value.Select(r => $"{r.SegmentId}(cells {r.TitleCellStart}..{r.TitleCellStart + r.CellCount - 1})"))}");
        }
        return sb.ToString();
    }
}
