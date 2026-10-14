using System.Globalization;
using System.Windows.Media;
using PlainClip.Core.Localization;

namespace PlainClip.Views;

/// <summary>
/// 画面の文字のフォント（表示言語ごと）。日本語は Yu Gothic UI。英語は Segoe UI
/// （Yu Gothic UI は "\" を "¥" で描くため、英語の画面でパスが読みにくくなる）。
/// XAML からは {x:Static v:UiFont.Family} で使う。表示言語は起動時に決まるため、画面を作るときに読めばよい。
/// </summary>
public static class UiFont
{
    private static readonly FontFamily Japanese = new("Yu Gothic UI");
    private static readonly FontFamily English = new("Segoe UI");

    public static FontFamily Family =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == UiLanguage.Japanese ? Japanese : English;
}
