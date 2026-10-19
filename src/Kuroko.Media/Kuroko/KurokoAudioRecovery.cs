using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Kuroko.Media.Dvd; // KurokoContainerReader / KrkEntry

namespace Kuroko.Media.Krk;

// 壊れた .krk（音声定義が欠落＝音が出ない）を、コンテナ内の av.mkv を ffprobe して音声定義を再構成して修復する。
// ffmpeg 再エンコードは不要。
//
// 対応する壊れ方：
//  (A) seg.json の audio.tracks が空（TS→krk の PTSラップ由来）。av.mkv を probe してトラックを再構成。
//  (B) seg.json にはトラックがあるが title.json の論理音声が空（DVD→krk で MPEG audio の物理ID食い違い由来）。
//      seg のトラックから title.json 論理音声を再構成。
//
// 重要：JSON はモデルにデシリアライズせず **JsonNode で「audio 配列だけ」差し替える非破壊編集**を行う。
//   title.json は controller やバージョンでモデルが異なり（playlist/video/cells 等）、型に落とすと未知フィールドが
//   欠落して壊れる（seg のプレイリストが消え「av.mkv が見つからない」等）。node 編集なら他フィールドは完全保持。
//
// 復旧できる：音声の本数・コーデック・チャンネル数・言語・トラック名（av.mkv から）。
// 復旧できない：dualMono・DVDの元 AST_CTL/論理番号。→ dualMono=false・論理番号=並び順、で再構成する。
internal static class KurokoAudioRecovery
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語を素の文字で出す
    };

    private sealed record TrackInfo(int Source, int Track, string Codec, int Channels, string? Language, string? Name);

    public static string Recover(string krkPath, string? ffprobePath, CancellationToken ct)
    {
        var log = new StringBuilder();
        log.AppendLine($"音声定義の復旧: {Path.GetFileName(krkPath)}");
        if (string.IsNullOrWhiteSpace(ffprobePath))
            throw new InvalidOperationException("ffprobe のパスが未設定です（av.mkv の音声を解析できません）。");

        var newBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);   // entryName(正規化) → 新JSON
        var segTracks = new Dictionary<string, List<TrackInfo>>(StringComparer.OrdinalIgnoreCase); // segId → トラック

        using (var reader = KurokoContainerReader.Open(krkPath))
        {
            if (!reader.IsZip)
                throw new NotSupportedException("この .krk は ZIP 形式ではありません（旧 ISO 形式の復旧は未対応）。");

            // ── seg.json：トラックを収集（空なら av.mkv から再構成） ──
            var segEntries = reader.Entries
                .Where(e => Norm(e.Name).EndsWith("/seg.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => Norm(e.Name), StringComparer.OrdinalIgnoreCase)
                .ToList();
            log.AppendLine($"セグメント数: {segEntries.Count}");

            foreach (var segEntry in segEntries)
            {
                ct.ThrowIfCancellationRequested();
                var node = JsonNode.Parse(reader.ReadAllBytes(segEntry))?.AsObject();
                if (node is null) { log.AppendLine($"  {Norm(segEntry.Name)}: 読めず（スキップ）"); continue; }
                string segId = LastSegOf(DirOf(segEntry.Name));

                var existing = ReadSegTracks(node);
                var spans = node["audio"]?.AsArray();
                bool broken = existing.Count == 0; // どのスパンにもトラックが無い
                if (!broken)
                {
                    segTracks[segId] = existing; // seg は健全（title.json だけ空のパターン）。書き換えない
                    continue;
                }

                var avEntry = reader.FindEntry(DirOf(segEntry.Name) + "/av.mkv");
                if (avEntry is null) { log.AppendLine($"  {Norm(segEntry.Name)}: av.mkv 無し（スキップ）"); continue; }
                var (audios, durMs) = ProbeAudioStreams(krkPath, avEntry.DataOffset, avEntry.Length, ffprobePath!, ct);
                if (audios.Count == 0) { log.AppendLine($"  {Norm(segEntry.Name)}: av.mkv に音声なし（スキップ）"); continue; }

                var tracks = new List<TrackInfo>();
                for (int i = 0; i < audios.Count; i++)
                {
                    var a = audios[i];
                    tracks.Add(new TrackInfo(i, a.Index, NormCodec(a.Codec), a.Channels > 0 ? a.Channels : 2,
                        NormLang(a.Lang), string.IsNullOrWhiteSpace(a.Title) ? null : a.Title));
                }

                // 既存スパンがあれば境界を保って tracks を埋める。無ければ単一スパン [0, 全長] を追加する（他フィールドは保持）。
                if (spans is { Count: > 0 })
                    foreach (var sp in spans) if (sp is JsonObject so) so["tracks"] = BuildTrackArray(tracks);
                else
                {
                    double totalMs = (double?)node["timing"]?["totalMs"] is double tm && tm > 0 ? tm : durMs;
                    node["audio"] = new JsonArray(new JsonObject { ["startMs"] = 0, ["endMs"] = totalMs, ["tracks"] = BuildTrackArray(tracks) });
                }

                newBytes[Norm(segEntry.Name)] = ToBytes(node);
                segTracks[segId] = tracks;
                log.AppendLine($"  {Norm(segEntry.Name)}: 音声 {tracks.Count} トラック再構成（" +
                    string.Join(", ", tracks.Select(t => $"{t.Codec}/{t.Channels}ch{(t.Language != null ? "/" + t.Language : "")}")) + "）");
            }

            // ── title.json：論理音声が空なら seg のトラックから再構成（audio 配列だけ差し替え） ──
            var manifest = reader.FindEntry(KurokoLayout.ManifestFile) is { } mE
                ? JsonNode.Parse(reader.ReadAllBytes(mE)) : null;
            bool dvdVm = string.Equals((string?)manifest?["controller"], KurokoLayout.ControllerDvdVideoVm, StringComparison.OrdinalIgnoreCase);
            // レイアウト version 5 は論理音声の number が 1 始まり（4 以前は DVD の論理番号＝0 始まり）。krk の版に合わせて書く。
            int layoutVersion = manifest?["version"] is JsonValue vv && vv.TryGetValue<int>(out int vi) ? vi : 0;
            bool v5 = layoutVersion >= KurokoLayout.CleanLayoutVersion;

            var discE = reader.FindEntry(KurokoLayout.DiscJson);
            var disc = discE is not null ? JsonNode.Parse(reader.ReadAllBytes(discE)) : null;
            foreach (var te in disc?["titles"]?.AsArray() ?? new JsonArray())
            {
                if (te is null) continue;
                int id = (int?)te["id"] ?? 0;
                string video = (string?)te["video"] ?? "";
                var titleE = reader.FindEntry(video.TrimStart('/') + "/title.json")
                             ?? reader.FindEntry(KurokoLayout.TitleJson(id));
                if (titleE is null) continue;

                var tnode = JsonNode.Parse(reader.ReadAllBytes(titleE))?.AsObject();
                if (tnode is null) continue;
                if (tnode["audio"]?.AsArray() is { Count: > 0 }) continue; // 既に論理音声あり

                // タイトルが参照するセグメント（"playlist"[].seg。無ければ全セグメント＝単一タイトル想定）。
                var segIds = tnode["playlist"]?.AsArray()?
                    .Select(p => (string?)p?["seg"]).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                var trs = GatherTitleTracks(segIds, segTracks);
                if (trs.Count == 0) continue;

                tnode["audio"] = dvdVm ? BuildDvdVmAudio(trs, v5) : BuildNoneAudio(trs);
                newBytes[Norm(titleE.Name)] = ToBytes(tnode);
                log.AppendLine($"  title T{id:00}: 論理音声 {trs.Count} を再構成（{(dvdVm ? "dvd-vm" : "none")}）");
            }
        }

        if (newBytes.Count == 0)
        {
            log.AppendLine("修復対象なし（音声定義は壊れていないか、av.mkv に音声がありません）。");
            return log.ToString();
        }

        // ── 再パック（seg.json/title.json だけ差し替え、av.mkv は生コピー。KurokoRepacker と共用） ──
        string bak = KurokoRepacker.ReplaceEntries(krkPath, newBytes, progress: null, ct, ".recovering.tmp");
        log.AppendLine($"修復完了: {newBytes.Count} 件の JSON を書き戻しました（元ファイルは {Path.GetFileName(bak)} に退避）。");
        return log.ToString();
    }

    // seg.json の audio[].tracks[] から source 重複を除いたトラックを読む。
    private static List<TrackInfo> ReadSegTracks(JsonObject segNode)
    {
        var list = new List<TrackInfo>();
        var seen = new HashSet<int>();
        foreach (var span in segNode["audio"]?.AsArray() ?? new JsonArray())
            foreach (var t in span?["tracks"]?.AsArray() ?? new JsonArray())
            {
                if (t is null) continue;
                int source = (int?)t["source"] ?? -1;
                if (source < 0 || !seen.Add(source)) continue;
                list.Add(new TrackInfo(source, (int?)t["track"] ?? 0, (string?)t["codec"] ?? "opus",
                    (int?)t["channels"] ?? 2, (string?)t["language"], (string?)t["name"]));
            }
        return list.OrderBy(t => t.Source).ToList();
    }

    // タイトルが参照するセグメント（無ければ全セグメント）から source 重複を除いて集める。
    private static List<TrackInfo> GatherTitleTracks(List<string>? segIds, Dictionary<string, List<TrackInfo>> segTracks)
    {
        var src = segIds is { Count: > 0 }
            ? segIds.Where(segTracks.ContainsKey).Select(id => segTracks[id])
            : segTracks.Values;
        var byPhys = new SortedDictionary<int, TrackInfo>();
        foreach (var trs in src)
            foreach (var tr in trs)
                if (!byPhys.ContainsKey(tr.Source)) byPhys[tr.Source] = tr;
        return byPhys.Values.ToList();
    }

    private static JsonArray BuildTrackArray(List<TrackInfo> trs)
    {
        var arr = new JsonArray();
        foreach (var t in trs)
        {
            var o = new JsonObject { ["track"] = t.Track, ["source"] = t.Source, ["codec"] = t.Codec, ["channels"] = t.Channels, ["dualMono"] = false };
            if (t.Name != null) o["name"] = t.Name;
            if (t.Language != null) o["language"] = t.Language;
            arr.Add((JsonNode)o);   // JsonNode の版を選ぶ（Add<T> は AOT で警告が出る）
        }
        return arr;
    }

    // dvd-video-vm の title.json 論理音声（KurokoVmAudioLogical＝number/source/language/astCtl）。
    // 再生側は la.Source で seg.audio[source==].track を引いて mkvTrack を得る（codec/channels/mkvTrack は seg が権威で
    // title には書かない）。number＝VM/SPRM[1] が参照する論理番号。astCtl は元IFO値を復元できないので恒等（=number）。
    // v5：number は 1 始まり（DVD の論理番号＋1）。astCtl は DVD の論理番号（0 始まり）のまま。
    private static JsonArray BuildDvdVmAudio(List<TrackInfo> trs, bool v5)
    {
        var arr = new JsonArray();
        int number = 0;
        foreach (var t in trs)
        {
            var o = new JsonObject { ["number"] = v5 ? number + 1 : number, ["source"] = t.Source };
            if (t.Language != null) o["language"] = v5 ? KurokoLanguage.Normalize(t.Language) : t.Language;
            o["astCtl"] = number;
            arr.Add((JsonNode)o);   // JsonNode の版を選ぶ（Add<T> は AOT で警告が出る）
            number++;
        }
        return arr;
    }

    // none：title.audio[number].source → seg.audio[source==].track。
    private static JsonArray BuildNoneAudio(List<TrackInfo> trs)
    {
        var arr = new JsonArray();
        foreach (var t in trs)
        {
            var o = new JsonObject { ["number"] = t.Source + 1, ["source"] = t.Source };
            if (t.Language != null) o["language"] = t.Language;
            arr.Add((JsonNode)o);   // JsonNode の版を選ぶ（Add<T> は AOT で警告が出る）
        }
        return arr;
    }

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(JsonOpts));

    // av.mkv（krk 内オフセット）を ffprobe し、音声ストリーム（index/codec/channels/language/title）と尺(ms)を返す。
    private static (List<(int Index, string Codec, int Channels, string? Lang, string? Title)> Audios, double DurMs)
        ProbeAudioStreams(string krkPath, long start, long length, string ffprobePath, CancellationToken ct)
    {
        string url = $"subfile,,start,{start},end,{start + length},,:{krkPath}";
        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=index,codec_type,codec_name,channels:stream_tags=language,title:format=duration");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);

        var audios = new List<(int, string, int, string?, string?)>();
        double durMs = 0;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe を起動できませんでした。");
        using (p)
        {
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120000)) { try { p.Kill(true); } catch { } throw new TimeoutException("ffprobe がタイムアウトしました。"); }
            ct.ThrowIfCancellationRequested();

            int idx = -1; string type = "", codec = ""; int ch = 0; string? lang = null, title = null;
            void Flush()
            {
                if (idx >= 0 && type == "audio") audios.Add((idx, codec, ch, lang, title));
                idx = -1; type = ""; codec = ""; ch = 0; lang = null; title = null;
            }
            foreach (var raw in outp.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("index=")) { Flush(); int.TryParse(Val(line), out idx); }
                else if (line.StartsWith("codec_type=")) type = Val(line);
                else if (line.StartsWith("codec_name=")) codec = Val(line);
                else if (line.StartsWith("channels=")) int.TryParse(Val(line), out ch);
                else if (line.StartsWith("TAG:language=")) lang = Val(line);
                else if (line.StartsWith("TAG:title=")) title = Val(line);
                else if (line.StartsWith("duration=") && double.TryParse(Val(line),
                             System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec))
                    durMs = sec * 1000.0;
            }
            Flush();
        }
        return (audios.Select(a => (a.Item1, a.Item2, a.Item3, a.Item4, a.Item5)).ToList(), durMs);
    }

    private static string Val(string line) => line.Substring(line.IndexOf('=') + 1);
    private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    private static string DirOf(string entryName)   // "SEGMENTS/S0001/seg.json" → "SEGMENTS/S0001"
    {
        string n = Norm(entryName);
        int i = n.LastIndexOf('/');
        return i > 0 ? n.Substring(0, i) : n;
    }
    private static string LastSegOf(string dir)     // "SEGMENTS/S0001" → "S0001"
    {
        int i = dir.LastIndexOf('/');
        return i >= 0 ? dir.Substring(i + 1) : dir;
    }

    private static string? NormLang(string? lang)
        => string.IsNullOrWhiteSpace(lang) || lang.Equals("und", StringComparison.OrdinalIgnoreCase) ? null : lang;

    private static string NormCodec(string c) => c.ToLowerInvariant() switch
    {
        "opus" => "opus",
        "ac3" or "ac-3" or "eac3" or "e-ac-3" => "ac3",
        "aac" or "aac_latm" => "aac",
        "dts" => "dts",
        _ => c.ToLowerInvariant(),
    };

}
