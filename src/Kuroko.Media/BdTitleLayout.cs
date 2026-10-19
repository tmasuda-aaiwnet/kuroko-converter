namespace Kuroko.Media.Dvd;

// Blu-ray タイトルの**構成表**。PlayItem をひとつの「区間」として並べ、連結ストリーム上のバイト位置と、
// 再生タイムラインへ揃えるための時刻オフセットを一箇所で決める。
//
// なぜ一箇所にまとめるか：連結（`BdTitleStream.Open`）・チャプター位置・シーク索引の 3 者が**同じ式**で
// 求まっていないと、章が数十ms〜数十秒ずれる／シークが別の場所へ飛ぶ、という食い違いが出る。
// 実際に一度、連結側だけが Gap を挟んでいてチャプターがずれた。
internal sealed class BdTitleLayout
{
    // クリップ全体を使うか、IN/OUT で切り出すかの判定しきい値（≒1フレーム@29.97）。
    // これ以内なら「クリップ全体」とみなし、従来どおりトリムしない。
    private const long TrimTolerance90k = 3003;
    private const long Gap90k = 3003;   // 区間の境界で時刻が重ならないための最小の隙間

    private BdTitleLayout(List<BdTitlePart> parts, long origin90k, long totalBytes, long timelineEnd90k)
    {
        Parts = parts; Origin90k = origin90k; TotalBytes = totalBytes; TimelineEnd90k = timelineEnd90k;
    }

    public IReadOnlyList<BdTitlePart> Parts { get; }
    public long Origin90k { get; }        // 位置表示の 0 に対応する絶対時刻（先頭区間の開始）
    public long TotalBytes { get; }
    public long TimelineEnd90k { get; }

    public static BdTitleLayout? Build(UdfReader udf, BdTitle title)
    {
        var items = title.Playlist.Items;
        if (items.Count == 0) return null;

        var stcCache = new Dictionary<string, (long S, long E)>(StringComparer.OrdinalIgnoreCase);
        var epCache = new Dictionary<string, BdEpMap?>(StringComparer.OrdinalIgnoreCase);
        var lenCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var parts = new List<BdTitlePart>(items.Count);
        long acc = 0, timelineEnd = long.MinValue, origin = long.MinValue;

        for (int i = 0; i < items.Count; i++)
        {
            string clip = items[i].ClipForAngle(title.AngleIndex);
            var ent = udf.FindEntry($"BDMV/STREAM/{clip}.m2ts");
            if (ent is null) return null;
            if (!lenCache.TryGetValue(clip, out long clipLen)) lenCache[clip] = clipLen = ent.Length;

            // クリップの STC 範囲。clpi が権威（PES の窓走査は音声/PGS の前後で基準がぶれる）。
            if (!stcCache.TryGetValue(clip, out var stc))
                stcCache[clip] = stc = BdmvPlaylist.ReadClipStcRange(udf, clip);
            long clipStart90k = stc.S >= 0 ? stc.S * 2 : -1;
            long clipEnd90k = stc.E >= 0 ? stc.E * 2 : -1;
            if (clipStart90k < 0)
            {
                // clpi が読めない盤：クリップ全体の PTS 範囲で代用する（防御的な経路）。
                using var probe = udf.OpenRead(ent);
                var head = BdTitleStream.ScanPtsRange(probe, 0, 8 << 20);
                var tail = BdTitleStream.ScanPtsRange(probe, Math.Max(0, clipLen - (8 << 20)), 8 << 20);
                clipStart90k = head.Min;
                clipEnd90k = tail.Max;
                if (clipStart90k < 0) return null;
            }

            if (!epCache.TryGetValue(clip, out var ep)) epCache[clip] = ep = BdmvEpMap.Read(udf, clip);

            long in90k = (long)items[i].InTime * 2;
            long out90k = (long)items[i].OutTime * 2;

            // **IN/OUT が実質クリップ全体なら従来どおり全体を使う**（手元の盤はすべてこちら）。
            // 切り出しが要るのは、EP マップがあって、かつ範囲がクリップより内側に入っているときだけ。
            bool trim = ep is { Count: > 0 }
                        && (in90k > clipStart90k + TrimTolerance90k
                            || (clipEnd90k > 0 && out90k > 0 && out90k < clipEnd90k - TrimTolerance90k));

            long byteFrom = 0, bytes = clipLen, partIn90k = clipStart90k, partOut90k = clipEnd90k;
            if (trim && ep is not null)
            {
                // 開始は IN 以下の直近 EP（I フレーム先頭）へ切り下げる。IN より手前の数フレームが
                // 余分に出るが、途中から復号を始めるより確実。
                int from = ep.IndexAtOrBefore(in90k);
                byteFrom = ep.ByteAt(from);
                partIn90k = ep.Pts90k[from];
                // 終端は OUT 以上の直近 EP まで。無ければクリップ末尾まで。
                int to = ep.IndexAtOrAfter(out90k);
                long byteTo = to >= 0 ? ep.ByteAt(to) : clipLen;
                if (byteTo <= byteFrom) byteTo = clipLen;
                bytes = byteTo - byteFrom;
                partOut90k = to >= 0 ? ep.Pts90k[to] : clipEnd90k;
            }

            long offset = (i == 0 || timelineEnd == long.MinValue) ? 0 : timelineEnd + Gap90k - partIn90k;
            if (origin == long.MinValue) origin = partIn90k + offset;
            if (partOut90k > 0) timelineEnd = partOut90k + offset;

            parts.Add(new BdTitlePart(clip, byteFrom, bytes, acc, partIn90k, offset, ep));
            acc += bytes;
        }
        return new BdTitleLayout(parts, origin, acc, timelineEnd);
    }

    // 位置表示（コンテンツ起点）→ 連結ストリーム上のバイト位置。EP マップが無ければ null。
    // 戻り値は **I フレームの先頭パケット**を指すので、呼び出し側は GOP の後方走査が要らない。
    public long? ByteForContentTime(TimeSpan t)
    {
        long streamPts = (long)Math.Round(t.TotalSeconds * 90000.0) + Origin90k;
        // 終端より後ろ（シークバー右端へのドラッグ等）は索引の管轄外。従来の PCR 探索へ委ねる
        //（あちらは「コンテンツ末より後ろ」を検出してフロアを手前へクランプする処理を持っている）。
        if (TimelineEnd90k > long.MinValue && streamPts > TimelineEnd90k) return null;
        // 区間を選ぶ：開始時刻（オフセット込み）が streamPts 以下で最後のもの。
        int idx = 0;
        for (int i = 0; i < Parts.Count; i++)
        {
            if (Parts[i].In90k + Parts[i].Offset90k > streamPts) break;
            idx = i;
        }
        var p = Parts[idx];
        if (p.EpMap is not { Count: > 0 }) return null;
        long inClip = streamPts - p.Offset90k;
        long b = p.EpMap.ByteAt(p.EpMap.IndexAtOrBefore(inClip));
        // 区間の範囲へ収める（トリムしているときは区間の先頭より前へ行かせない）。
        if (b < p.ClipByteFrom) b = p.ClipByteFrom;
        long max = p.ClipByteFrom + p.Bytes - BdEpMap.SourcePacketBytes;
        if (b > max) b = Math.Max(p.ClipByteFrom, max);
        return p.StreamByteFrom + (b - p.ClipByteFrom);
    }

    // チャプター（PlayListMark）の位置を再生タイムライン上で求める。
    public IReadOnlyList<TimeSpan> ChapterTimes(BdPlaylist pl)
    {
        var result = new List<TimeSpan>();
        foreach (var m in pl.Marks)
        {
            if (m.MarkType != 1 || m.PlayItemId >= Parts.Count) continue;
            long rel = (long)m.TimeStamp * 2 + Parts[m.PlayItemId].Offset90k - Origin90k;
            result.Add(TimeSpan.FromSeconds(Math.Max(0, rel) / 90000.0));
        }
        result.Sort();
        return result;
    }
}

// タイトルを構成する 1 区間（PlayItem 1 個ぶん）。
//   ClipByteFrom/Bytes … クリップ内で実際に使うバイト範囲（トリムしないときは 0〜全長）
//   StreamByteFrom     … 連結ストリーム上の開始バイト
//   In90k              … この区間の先頭の絶対時刻（クリップ自身の STC）
//   Offset90k          … 連結タイムラインへ揃えるために足す量
internal sealed record BdTitlePart(
    string ClipId, long ClipByteFrom, long Bytes, long StreamByteFrom, long In90k, long Offset90k, BdEpMap? EpMap);
