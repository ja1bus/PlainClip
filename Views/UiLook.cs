using System.Windows;
using Microsoft.Win32;

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
}
