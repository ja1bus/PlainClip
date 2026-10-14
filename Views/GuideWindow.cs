using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlainClip.Core.Localization;

namespace PlainClip.Views;

/// <summary>使い方の画面に表示する操作（設定に合わせた内容）。</summary>
/// <param name="PasteKey">「クリップボードを書式なしで貼り付け」の操作（例: "左Shift を 2 回"）。無効なら null。</param>
/// <param name="HistoryShortcut">履歴一覧のショートカット。無ければ null。</param>
/// <param name="NumberKeys">数字ショートカット（例: "Ctrl+Alt+1～9"）。使わないなら null。</param>
public sealed record GuideContent(string? PasteKey, string? HistoryShortcut, string? NumberKeys);

/// <summary>
/// 使い方の小さな画面（V2.0.0。PROJECT.md「使い方の画面」）。初回起動時、2 回目の起動（スタートメニュー等から起動したとき）、
/// トレイメニューの「使い方」で表示する。PlainClip は常駐して画面を出さないため、起動したことと操作方法をここで示す。
/// </summary>
public sealed class GuideWindow : Window
{
    public GuideWindow(GuideContent content, Action openSettings)
    {
        Title = Strings.GuideTitle;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        var scale = UiLook.Apply(this);
        Width = 500 * scale;
        if (!UiLook.HighContrast)
        {
            Background = Brushes.White;
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        }

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(new TextBlock
        {
            Text = Strings.GuideHeading,
            FontSize = FontSize * 1.25,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(new TextBlock { Text = Strings.GuideIntro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150 * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        AddRow(grid, content.PasteKey ?? Strings.GuidePasteDisabledKey,
            content.PasteKey is null ? Strings.GuidePasteDisabled : Strings.GuidePaste);
        AddRow(grid, content.HistoryShortcut is null ? Strings.GuideTrayKey : string.Format(Strings.GuideTrayOrShortcutFormat, content.HistoryShortcut),
            Strings.GuideHistory);
        AddRow(grid, content.NumberKeys ?? Strings.GuideNumberOffKey,
            content.NumberKeys is null ? Strings.GuideNumberOff : Strings.GuideNumber);
        AddRow(grid, "Ctrl+V", Strings.GuideCtrlV);
        root.Children.Add(grid);

        root.Children.Add(new TextBlock
        {
            Text = Strings.GuideNote,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Opacity = UiLook.HighContrast ? 1 : 0.8,
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var settings = new Button { Content = Strings.GuideOpenSettings, MinWidth = 110 * scale, Padding = new Thickness(10, 3, 10, 3) };
        settings.Click += (_, _) =>
        {
            Close();
            openSettings();
        };
        var close = new Button { Content = Strings.Close, MinWidth = 90 * scale, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(settings);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => close.Focus();
    }

    private void AddRow(Grid grid, string key, string description)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var keyText = new TextBlock { Text = key, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 12, 4) };
        var descText = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetRow(keyText, row);
        Grid.SetRow(descText, row);
        Grid.SetColumn(descText, 1);
        grid.Children.Add(keyText);
        grid.Children.Add(descText);
    }
}
