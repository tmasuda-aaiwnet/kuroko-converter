using System.IO;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Kuroko.Converter;

// 解析・復旧のレポートを表示する（コピー・保存）。
public partial class ReportWindow : Window
{
    private readonly string _suggestedName;

    public ReportWindow() : this("", "report.txt") { } // 画面の設計ツール用

    public ReportWindow(string text, string suggestedName)
    {
        InitializeComponent();
        ReportText.Text = text;
        _suggestedName = suggestedName;
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard is { } cb) await cb.SetTextAsync(ReportText.Text ?? ""); } catch { }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var path = await Dialogs.SaveFile(this, "レポートを保存", _suggestedName, ".txt", Dialogs.Type("テキスト", "*.txt"));
        if (path is null) return;
        try { File.WriteAllText(path, ReportText.Text ?? ""); }
        catch (Exception ex) { await Dialogs.Show(this, "保存に失敗しました", ex.Message); }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
