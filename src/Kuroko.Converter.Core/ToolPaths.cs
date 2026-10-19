using System.IO;
using System.Text.Json;

namespace Kuroko.Converter;

// 外部の実行ファイル（ffmpeg・ffprobe・mkvmerge）の場所。この変換器はこれらを同梱しない（使う人が入れる）。
// 一度使った場所を覚えておき（各 OS のアプリのデータの場所の KurokoConverter/tools.json）、次からはそれを既定にする。
// 覚えたものが無ければ、PATH と、よくあるインストール先から探す：
//   Windows：Program Files の MKVToolNix
//   macOS  ：Homebrew（/opt/homebrew/bin・/usr/local/bin）と /Applications/MKVToolNix-*.app
//            （画面のアプリは端末の PATH を引き継がないので、Homebrew の場所は明示して探す）
//   Linux  ：/usr/bin・/usr/local/bin
internal static class ToolPaths
{
    private sealed class Saved
    {
        public string? Ffmpeg { get; set; }
        public string? Mkvmerge { get; set; }
    }

    // 実行ファイルの名前（Windows だけ .exe が付く）。
    public static string ExeName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KurokoConverter", "tools.json");

    private static Saved Load()
    {
        try { return JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) ?? new Saved(); }
        catch { return new Saved(); }
    }

    // ffmpeg の既定：覚えた場所 → PATH・よくある場所 → "ffmpeg"（見つからなければ、実行時のエラーで知らせる）。
    public static string DefaultFfmpeg()
        => Load().Ffmpeg is { } f && File.Exists(f) ? f : Find(ExeName("ffmpeg")) ?? "ffmpeg";

    // ffprobe は ffmpeg と同じフォルダのものを使う。
    public static string FfprobeNextTo(string ffmpeg)
    {
        string dir = Path.GetDirectoryName(ffmpeg) ?? "";
        string p = Path.Combine(dir, ExeName("ffprobe"));
        return dir.Length > 0 && File.Exists(p) ? p : Find(ExeName("ffprobe")) ?? "ffprobe";
    }

    // mkvmerge の既定：覚えた場所 → PATH・よくある場所 → MKVToolNix の標準のインストール先 → "mkvmerge"。
    public static string DefaultMkvmerge()
    {
        if (Load().Mkvmerge is { } m && File.Exists(m)) return m;
        if (Find(ExeName("mkvmerge")) is { } found) return found;
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string p = Path.Combine(Environment.GetFolderPath(root), "MKVToolNix", "mkvmerge.exe");
                if (File.Exists(p)) return p;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            // MKVToolNix の macOS 版はアプリ（MKVToolNix-<版>.app）の中に mkvmerge を持つ。新しい版を優先する。
            try
            {
                foreach (var app in Directory.EnumerateDirectories("/Applications", "MKVToolNix*.app").OrderByDescending(d => d))
                {
                    string p = Path.Combine(app, "Contents", "MacOS", "mkvmerge");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
        }
        return "mkvmerge";
    }

    // 変換に使った場所を覚える（実在するものだけ）。
    public static void Remember(string? ffmpeg, string? mkvmerge)
    {
        try
        {
            var s = Load();
            if (ffmpeg is not null && File.Exists(ffmpeg)) s.Ffmpeg = ffmpeg;
            if (mkvmerge is not null && File.Exists(mkvmerge)) s.Mkvmerge = mkvmerge;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // PATH と、OS ごとのよくある場所から探す。
    private static string? Find(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        if (OperatingSystem.IsMacOS()) dirs.AddRange(new[] { "/opt/homebrew/bin", "/usr/local/bin" });
        else if (OperatingSystem.IsLinux()) dirs.AddRange(new[] { "/usr/bin", "/usr/local/bin" });
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string p = Path.Combine(dir.Trim(), name);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
