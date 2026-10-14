using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PlainClip.Core.History;
using PlainClip.Core.Localization;
using PlainClip.Services;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Views;

/// <summary>一覧の 1 行。</summary>
public sealed class HistoryRow
{
    private const int ToolTipMaxChars = 800;

    public HistoryRow(ClipItem item, int index, bool isCurrent)
    {
        Item = item;
        Number = index < 9 ? (index + 1).ToString() : "";
        LineInfo = item.LineCount > 1 ? string.Format(Strings.HistoryLinesFormat, item.LineCount) : "";
        IsCurrent = isCurrent;
    }

    public ClipItem Item { get; }
    public string Number { get; }
    public string Preview => Item.Preview;
    public string LineInfo { get; }

    /// <summary>現在のクリップボードと同じ内容か（印を付ける）。</summary>
    public bool IsCurrent { get; }

    public Visibility MarkVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>複数行・長文か（一覧の下に内容を表示する）。</summary>
    public bool NeedsPreview => Item.LineCount > 1 || Item.Text.Length > Core.Text.PreviewText.MaxChars;

    /// <summary>複数行・長文のときだけ全文（の先頭）をツールチップに出す。</summary>
    public object? ToolTipText =>
        NeedsPreview
            ? new TextBlock
            {
                Text = Item.Text.Length > ToolTipMaxChars ? Item.Text[..ToolTipMaxChars] + "…" : Item.Text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 560,
                FontFamily = new FontFamily("Consolas, Yu Gothic UI, Segoe UI"),
            }
            : null;

    /// <summary>UI オートメーション（読み上げ等）での項目名。</summary>
    public string AutomationName =>
        string.Join(" ", new[] { Number, Preview, LineInfo, IsCurrent ? Strings.HistoryCurrentMark : "" }.Where(s => s.Length > 0));

    public override string ToString() => AutomationName;
}

/// <summary>
/// 履歴ポップアップ（PROJECT.md「履歴ポップアップ」）。1 つのインスタンスを隠して再利用し、表示を速くする。
/// マウス位置の近くに出し、最新（1 番目）を選択した状態で開く。キーボード操作はウィンドウ自体で受ける
/// （一覧の項目にフォーカスを持たせないので、↑↓ Enter Delete Esc 1～9 が常に同じ動きになる）。
/// 他のウィンドウをクリックする等で非アクティブになったら閉じる。
/// 貼り付け先が無いとき（タスクトレイから開いた等）は「コピー」として表示する（V2.0.0）。
/// </summary>
public partial class HistoryWindow : Window
{
    public static readonly DependencyProperty SmallFontSizeProperty =
        DependencyProperty.Register(nameof(SmallFontSize), typeof(double), typeof(HistoryWindow), new PropertyMetadata(11.0));

    private const double BaseWidth = 460;
    private const int PreviewMaxChars = 600;
    private const int PreviewMaxLines = 8;

    private readonly Dictionary<string, object> _defaultColors = new();
    private bool _allowClose;
    private bool _choosing;
    private bool _positionPending;
    private string? _currentText;

    public HistoryWindow()
    {
        InitializeComponent();
        foreach (var key in Resources.Keys.OfType<string>().Where(k => Resources[k] is SolidColorBrush))
            _defaultColors[key] = Resources[key];
        Deactivated += (_, _) =>
        {
            // 右クリックメニューを開いている間は閉じない（メニューは別ウィンドウ）。
            if (IsVisible && !_choosing && List.ContextMenu is not { IsOpen: true }) HideWindow(restoreFocus: false);
        };
        DpiChanged += (_, _) =>
        {
            // 拡大率の違うモニターへ移すと大きさが変わるので、もう一度画面内に収める。
            if (_positionPending) Dispatcher.BeginInvoke(PositionNearCursor);
        };
    }

    /// <summary>項目が選ばれた。copyOnly は「クリップボードへコピー」、triggerVk は選択に使ったキー（マウスは 0）。</summary>
    public event Action<ClipItem, bool, int>? ItemChosen;

    /// <summary>Delete・右クリックの「削除」。</summary>
    public event Action<ClipItem>? DeleteRequested;

    /// <summary>Esc で閉じた（貼り付け先へフォーカスを戻す）。</summary>
    public event Action? Cancelled;

    /// <summary>貼り付け先が無い（タスクトレイから開いた等）なら true。選ぶとクリップボードへコピーだけ行う。</summary>
    public bool CopyOnlyMode { get; private set; }

    public double SmallFontSize
    {
        get => (double)GetValue(SmallFontSizeProperty);
        set => SetValue(SmallFontSizeProperty, value);
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <param name="items">履歴（先頭 = 最新）。</param>
    /// <param name="copyOnly">貼り付け先が無い。</param>
    /// <param name="currentText">現在のクリップボードの本文（印を付ける）。テキストでなければ null。</param>
    public void ShowHistory(IReadOnlyList<ClipItem> items, bool copyOnly, string? currentText)
    {
        ApplyLook();
        CopyOnlyMode = copyOnly;
        _currentText = currentText;
        CopyOnlyBar.Visibility = copyOnly ? Visibility.Visible : Visibility.Collapsed;
        MenuPaste.Visibility = copyOnly ? Visibility.Collapsed : Visibility.Visible;
        MenuCopy.InputGestureText = copyOnly ? "Enter" : "";
        SetItems(items, selectIndex: 0);
        _choosing = false;

        if (!IsVisible)
        {
            Left = -32000; // 位置を決めるまで画面外に置く（大きさを測るため一度表示する）
            Top = -32000;
            Show();
        }
        UpdateLayout();
        _positionPending = true;
        PositionNearCursor();
        WindowHelper.ForceForeground(Handle);
        Activate();
        Focus();
    }

    /// <summary>表示中に履歴・現在のクリップボードが変わった（削除・新しいコピー）ときの更新。選択位置はなるべく保つ。</summary>
    public void Refresh(IReadOnlyList<ClipItem> items, string? currentText)
    {
        _currentText = currentText;
        if (!IsVisible) return;
        var index = Math.Max(0, List.SelectedIndex);
        SetItems(items, Math.Min(index, items.Count - 1));
    }

    public void HideWindow(bool restoreFocus)
    {
        if (!IsVisible) return;
        _positionPending = false;
        List.ContextMenu.IsOpen = false;
        Hide();
        if (restoreFocus) Cancelled?.Invoke();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideWindow(restoreFocus: false);
        }
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var count = List.Items.Count;
        switch (key)
        {
            case Key.Escape:
                HideWindow(restoreFocus: true);
                break;
            case Key.Up:
                if (count > 0) Select(Math.Max(0, List.SelectedIndex - 1));
                break;
            case Key.Down:
                if (count > 0) Select(Math.Min(count - 1, List.SelectedIndex + 1));
                break;
            case Key.Home:
                if (count > 0) Select(0);
                break;
            case Key.End:
                if (count > 0) Select(count - 1);
                break;
            case Key.Enter:
                Choose(List.SelectedIndex, copyOnly: false, KeyInterop.VirtualKeyFromKey(key));
                break;
            case Key.Delete:
                if (List.SelectedItem is HistoryRow row) DeleteRequested?.Invoke(row.Item);
                break;
            case >= Key.D1 and <= Key.D9 when Keyboard.Modifiers == ModifierKeys.None:
                Choose(key - Key.D1, copyOnly: false, KeyInterop.VirtualKeyFromKey(key));
                break;
            case >= Key.NumPad1 and <= Key.NumPad9 when Keyboard.Modifiers == ModifierKeys.None:
                Choose(key - Key.NumPad1, copyOnly: false, KeyInterop.VirtualKeyFromKey(key));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>文字の大きさ・フォント・幅（テキストのサイズの設定）と、ハイコントラストの色を反映する。</summary>
    private void ApplyLook()
    {
        var scale = UiLook.Apply(this);
        SmallFontSize = 11 * scale;
        Width = BaseWidth * scale;
        List.MaxHeight = 420 * scale;
        PreviewBody.MaxHeight = 150 * scale;
        ApplyColors(UiLook.HighContrast);
    }

    private void ApplyColors(bool highContrast)
    {
        foreach (var (key, brush) in _defaultColors) Resources[key] = brush;
        if (!highContrast) return;
        Resources["WindowBrush"] = SystemColors.WindowBrush;
        Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Resources["FrameBrush"] = SystemColors.WindowTextBrush;
        Resources["HeaderBrush"] = SystemColors.WindowBrush;
        Resources["LineBrush"] = SystemColors.WindowTextBrush;
        Resources["SelectedBrush"] = SystemColors.HighlightBrush;
        Resources["SelectedTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["HoverBrush"] = SystemColors.WindowBrush;
        Resources["SubtleText"] = SystemColors.WindowTextBrush;
        Resources["MarkBrush"] = SystemColors.WindowTextBrush;
        Resources["CopyOnlyBrush"] = SystemColors.InfoBrush;
        Resources["CopyOnlyText"] = SystemColors.InfoTextBrush;
    }

    private void SetItems(IReadOnlyList<ClipItem> items, int selectIndex)
    {
        var rows = new List<HistoryRow>(items.Count);
        for (var i = 0; i < items.Count; i++) rows.Add(new HistoryRow(items[i], i, _currentText is not null && items[i].Text == _currentText));
        List.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = rows.Count == 0 ? Strings.HistoryHintEmpty
            : CopyOnlyMode ? Strings.HistoryHintCopy
            : Strings.HistoryHintPaste;
        if (rows.Count > 0) Select(Math.Clamp(selectIndex, 0, rows.Count - 1));
        else UpdatePreview();
    }

    private void Select(int index)
    {
        List.SelectedIndex = index;
        List.ScrollIntoView(List.SelectedItem);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    /// <summary>選んだ項目が複数行・長文なら、一覧の下に先頭の数行を表示する。</summary>
    private void UpdatePreview()
    {
        if (List.SelectedItem is not HistoryRow { NeedsPreview: true } row)
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewBody.Text = Excerpt(row.Item.Text);
        PreviewPanel.Visibility = Visibility.Visible;
    }

    private static string Excerpt(string text)
    {
        var lines = text.Length > PreviewMaxChars ? text[..PreviewMaxChars] : text;
        var split = lines.Replace("\r\n", "\n").Split('\n');
        var shown = string.Join("\n", split.Take(PreviewMaxLines));
        return split.Length > PreviewMaxLines || text.Length > PreviewMaxChars ? shown + "\n…" : shown;
    }

    private void Choose(int index, bool copyOnly, int triggerVk)
    {
        if (index < 0 || index >= List.Items.Count || List.Items[index] is not HistoryRow row) return;
        _choosing = true;
        ItemChosen?.Invoke(row.Item, copyOnly, triggerVk);
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(List, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (container is null) return;
        e.Handled = true;
        Choose(List.ItemContainerGenerator.IndexFromContainer(container), copyOnly: false, triggerVk: 0);
    }

    private void MenuPaste_Click(object sender, RoutedEventArgs e) => Choose(List.SelectedIndex, copyOnly: false, 0);

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => Choose(List.SelectedIndex, copyOnly: true, 0);

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is HistoryRow row) DeleteRequested?.Invoke(row.Item);
    }

    /// <summary>マウス位置の右下に出し、作業領域（タスクバーを除く）からはみ出すなら内側へずらす。座標は物理ピクセル。</summary>
    private void PositionNearCursor()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var cursor) || !GetWindowRect(hwnd, out var rect)) return;
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info)) return;
        var work = info.rcWork;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        const int offset = 8;
        var x = cursor.X + offset;
        var y = cursor.Y + offset;
        if (x + width > work.Right) x = Math.Max(work.Left, cursor.X - width - offset);
        if (y + height > work.Bottom) y = Math.Max(work.Top, work.Bottom - height);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
