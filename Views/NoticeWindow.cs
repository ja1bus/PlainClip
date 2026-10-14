using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Views;

/// <summary>
/// 作業を止めない短い案内（V2.0.0。PROJECT.md「案内の表示」）。マウス位置の近くに 1 行～数行を約 2 秒だけ表示する。
/// フォーカスを奪わず（WS_EX_NOACTIVATE）、クリックも素通りさせる（WS_EX_TRANSPARENT）。新しい案内は前の案内を置き換える。
/// Windows のトースト通知（トレイのバルーン）は集中モードで表示されない・通知センターに溜まるため、操作のたびの案内には使わない。
/// </summary>
public sealed class NoticeWindow : Window
{
    private static NoticeWindow? _current;
    private readonly DispatcherTimer _timer;

    private NoticeWindow(string message, TimeSpan duration)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Left = -32000; // 大きさが決まってからマウスの近くへ移す
        Top = -32000;
        var scale = UiLook.Apply(this, 12.5);

        var highContrast = UiLook.HighContrast;
        Content = new Border
        {
            Background = highContrast ? SystemColors.InfoBrush : new SolidColorBrush(Color.FromArgb(0xF0, 0x2B, 0x2B, 0x2B)),
            BorderBrush = highContrast ? SystemColors.InfoTextBrush : Brushes.Transparent,
            BorderThickness = new Thickness(highContrast ? 1 : 0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 7, 12, 8),
            MaxWidth = 420 * scale,
            Child = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = highContrast ? SystemColors.InfoTextBrush : Brushes.White,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(this, message);

        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => FadeOut();
    }

    /// <summary>案内を表示する（UI スレッド）。</summary>
    public static void Display(string message, double seconds = 2.2)
    {
        _current?.Close();
        var notice = new NoticeWindow(message, TimeSpan.FromSeconds(seconds));
        _current = notice;
        notice.Closed += (_, _) => { if (_current == notice) _current = null; };
        notice.Show();
    }

    public static void CloseCurrent() => _current?.Close();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT));
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PositionNearCursor();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void FadeOut()
    {
        _timer.Stop();
        var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>マウス位置の右下（作業領域からはみ出すなら内側）に置く。座標は物理ピクセル。</summary>
    private void PositionNearCursor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var cursor) || !GetWindowRect(hwnd, out var rect)) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info)) return;
        var work = info.rcWork;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        const int offset = 16;
        var x = Math.Clamp(cursor.X + offset, work.Left, Math.Max(work.Left, work.Right - width));
        var y = cursor.Y + offset;
        if (y + height > work.Bottom) y = Math.Max(work.Top, cursor.Y - height - offset);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
