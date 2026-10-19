namespace Kuroko.Media.Ffmpeg;

// 同梱 FFmpeg（LGPL shared）の共有ライブラリの探し方。
//
// 結合層（NativeFfmpegInterop）は [DllImport] を使わず NativeLibrary.Load ＋ 関数ポインタで解決するので、
// **探すファイルの名前と置き場所さえ差し替えれば、他プラットフォームでもそのまま動く**。その差し替え点。
//
// 既定はプラットフォームごとの命名規則で実行ファイルの近くを探す。プラットフォーム側で別の探し方が
// 必要なら Resolver を差し替える。とくに **iOS/tvOS では、動的ライブラリは `.framework` に包んで
// アプリバンドルへ埋め込む必要があり、ワイルドカード探索も避けるべき**（審査で問題になるのは
// 「ダウンロードしたデータで dlopen の引数を変える」形なので、固定パス解決にする）。
public static class FfmpegLibraryLocator
{
    // ライブラリ名（"avcodec" "avutil" "swscale" "avfilter"）→ ロードするパス。null＝見つからない。
    public static Func<string, string?>? Resolver { get; set; }

    public static string? Resolve(string library) => (Resolver ?? DefaultResolve)(library);

    // 呼び出し側が明示するディレクトリを先に見たうえで解決する（Kuroko 再生・変換器の ffmpegDir 指定）。
    // Resolver が設定されていればそちらを優先する（従来どおり）。
    public static string? Resolve(string library, string? extraDirectory)
    {
        if (Resolver is null && ResolveIn(library, extraDirectory) is { } hit) return hit;
        return Resolve(library);
    }

    // **指定ディレクトリだけ**を見る。見つからなければ null（他は探さない）。
    // Resolver の実装から呼んでよい（Resolve / Resolver を経由しないので再帰しない）。
    // 注意：Resolver の中から Resolve(...) を呼ぶと Resolver(lib) → Resolve → Resolver(lib) … と
    // 無限再帰する（macOS 側で ffmpegDirectoryUtf8 を指定した経路で実際に踏んだ）。
    public static string? ResolveIn(string library, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try
        {
            if (!Directory.Exists(directory)) return null;
            return Directory.GetFiles(directory, FileNamePattern(library))
                .OrderByDescending(Path.GetFileName).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    // 既定の探索（Resolver を無視する）。Resolver の実装から「見つからなければ既定へ倒す」ときに使う。
    public static string? ResolveDefault(string library) => DefaultResolve(library);

    // 探索した場所の説明（見つからなかったときのログ用）。
    public static string SearchDescription
        => Resolver is not null
            ? "プラットフォーム側の Resolver"
            : $"{string.Join(" / ", EnumerateSearchDirectories())}（{FileNamePattern("avcodec")}）";

    private static string? DefaultResolve(string library)
    {
        var pattern = FileNamePattern(library);
        foreach (var directory in EnumerateSearchDirectories())
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                // 同名で版違いが並ぶことがあるので、名前の降順＝新しい版を採る。
                var path = Directory.GetFiles(directory, pattern).OrderByDescending(Path.GetFileName).FirstOrDefault();
                if (path is not null) return path;
            }
            catch
            {
            }
        }
        return null;
    }

    // プラットフォームごとの共有ライブラリ名。
    //   Windows … avcodec-62.dll       （版番号がファイル名に付く）
    //   Apple   … libavcodec.62.dylib
    //   その他  … libavcodec.so.62     （Linux / Android）
    private static string FileNamePattern(string library)
        => OperatingSystem.IsWindows() ? $"{library}-*.dll"
         : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() ? $"lib{library}.*.dylib"
         : $"lib{library}.so*";

    private static IEnumerable<string> EnumerateSearchDirectories()
    {
        var env = Environment.GetEnvironmentVariable("TSPLAYER_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            yield return env;

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "ffmpeg");
        yield return baseDir;
        // NuGet のネイティブ配置（RID 別）。分離前は win-x64 決め打ちだったので、それも残したうえで
        // 実行中の RID でも引く（他プラットフォームで効くように）。
        yield return Path.Combine(baseDir, "runtimes", "win-x64", "native");
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrEmpty(rid) && rid != "win-x64")
            yield return Path.Combine(baseDir, "runtimes", rid, "native");
        yield return Directory.GetCurrentDirectory();
    }
}
