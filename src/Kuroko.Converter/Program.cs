using Avalonia;

namespace Kuroko.Converter;

internal static class Program
{
    // 引数なし＝画面。引数あり＝CLI（ConverterCli。変換と、調べるための診断の命令）。
    // Avalonia の初期化より前に、Avalonia や SynchronizationContext に頼る処理を置かないこと。
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0) return ConverterCli.Run(args);
        ConverterCli.PrepareEnvironment();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // 画面の設計ツール（プレビュー）からも使われる。消さないこと。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
