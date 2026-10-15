using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Views;

/// <summary>
/// 画面の見やすさの共通設定（V2.0.0。PROJECT.md「見やすさ」）。
///  - 文字の大きさ: Windows の［アクセシビリティ］→［テキストのサイズ］（HKCU\Software\Microsoft\Accessibility の TextScaleFactor、100～225%）。
///    WPF は自動では追従しないため、画面を表示するたびに読んで文字の大きさと幅に掛ける。
///  - ハイコントラスト: <see cref="SystemParameters.HighContrast"/> のときは固定色を使わず、システムの色にする（各画面で対応）。
///  - フォント: 表示言語ごと（<see cref="UiFont"/>）。
/// </summary>
public static class UiLook
{
    public const double BaseFontSize = 13;

    /// <summary>テキストのサイズの倍率（1.0～2.25）。読めなければ 1.0。</summary>
    public static double TextScale
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
                if (key?.GetValue("TextScaleFactor") is int percent) return Math.Clamp(percent, 100, 225) / 100.0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                // 既定の大きさで表示する。
            }
            return 1.0;
        }
    }

    public static bool HighContrast => SystemParameters.HighContrast;

    /// <summary>フォントと文字の大きさを設定する。戻り値は使った倍率（幅などの調整用）。</summary>
    public static double Apply(Window window, double baseFontSize = BaseFontSize)
    {
        var scale = TextScale;
        window.FontFamily = UiFont.Family;
        window.FontSize = baseFontSize * scale;
        return scale;
    }

    /// <summary>
    /// 画面の幅・高さの上限を、マウスのあるモニターの作業領域（タスクバーを除く）に合わせる（V2.0.1）。
    /// 文字を拡大した・小さい画面でもはみ出さず、内容はスクロールで読めるようにする（表示する画面がスクロール領域を持つこと）。
    /// 中央に出す画面・マウスの近くに出す履歴一覧とも、表示されるのはこのモニター。
    /// </summary>
    public static void FitToWorkArea(Window window)
    {
        if (!GetCursorPos(out var cursor)) return;
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        // Per-Monitor V2 では、そのモニターに置いたウィンドウの DIP = 物理ピクセル × 96 / モニターの DPI。
        var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX : 96u;
        var work = info.rcWork;
        window.MaxWidth = (work.Right - work.Left) * 96.0 / dpi;
        window.MaxHeight = (work.Bottom - work.Top) * 96.0 / dpi;
        if (window.Width > window.MaxWidth) window.Width = window.MaxWidth;
    }
}
