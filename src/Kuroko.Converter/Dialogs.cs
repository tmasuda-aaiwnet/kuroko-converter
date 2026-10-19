using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Kuroko.Converter;

// 画面の共通部品：知らせる・確かめるダイアログ（Avalonia には標準の MessageBox が無い）と、ファイル・フォルダの選択。
internal static class Dialogs
{
    // 知らせる（OK だけ）。
    public static Task Show(Window owner, string title, string message) => Ask(owner, title, message, "OK", null);

    // 確かめる（OK／キャンセル）。OK なら true。
    public static async Task<bool> Confirm(Window owner, string title, string message)
        => await Ask(owner, title, message, "OK", "キャンセル");

    private static async Task<bool> Ask(Window owner, string title, string message, string ok, string? cancel)
    {
        bool result = false;
        var win = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var okButton = new Button { Content = ok, MinWidth = 90, IsDefault = true };
        okButton.Click += (_, _) => { result = true; win.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(okButton);
        if (cancel is not null)
        {
            var cancelButton = new Button { Content = cancel, MinWidth = 90, IsCancel = true };
            cancelButton.Click += (_, _) => win.Close();
            buttons.Children.Add(cancelButton);
        }
        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 480,
            Content = new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        });
        win.Content = panel;
        await win.ShowDialog(owner);
        return result;
    }

    public static FilePickerFileType Type(string name, params string[] patterns) => new(name) { Patterns = patterns };

    // 実行ファイルの選択肢（Windows は *.exe、ほかの OS は拡張子が無いので全部）。
    public static FilePickerFileType Executable(string name)
        => OperatingSystem.IsWindows() ? Type(name, "*.exe") : Type(name, "*");

    public static async Task<IReadOnlyList<string>> OpenFiles(Window owner, string title, bool multiple, params FilePickerFileType[] types)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = types,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> OpenFile(Window owner, string title, params FilePickerFileType[] types)
        => (await OpenFiles(owner, title, false, types)).FirstOrDefault();

    public static async Task<string?> OpenFolder(Window owner, string title)
    {
        var dirs = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return dirs.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> SaveFile(Window owner, string title, string suggestedName, string extension, FilePickerFileType type)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = new[] { type },
        });
        return file?.TryGetLocalPath();
    }
}
