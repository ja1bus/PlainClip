using System.Globalization;

namespace PlainClip.Core.Localization;

/// <summary>
/// 表示言語（PROJECT.md「英語・日本語」）。設定値は "" = Windows の表示言語に合わせる / "en" / "ja"。
/// Windows の表示言語が日本語なら日本語、それ以外は英語（既定の言語）で表示する。
/// 言語は起動時に決めて、終了まで変えない（変更は再起動後に反映する）。
/// </summary>
public static class UiLanguage
{
    public const string System = "";
    public const string English = "en";
    public const string Japanese = "ja";

    /// <summary>設定画面の選択肢の順。</summary>
    public static readonly IReadOnlyList<string> All = [System, English, Japanese];

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo(English);
    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo(Japanese);

    /// <summary>未知の値は "" にする（設定ファイルの補正用）。</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        English => English,
        Japanese => Japanese,
        _ => System,
    };

    /// <summary>設定値と Windows の表示言語から、実際に使う言語（en / ja）を決める。</summary>
    public static CultureInfo Resolve(string? setting, CultureInfo windowsUiCulture) => Normalize(setting) switch
    {
        English => EnglishCulture,
        Japanese => JapaneseCulture,
        _ => windowsUiCulture.TwoLetterISOLanguageName == Japanese ? JapaneseCulture : EnglishCulture,
    };

    /// <summary>
    /// 表示言語を適用する。以降に作る画面・メッセージと、スレッドプールなど新しいスレッドに効く。
    /// 作成済みの画面は変わらない（そのため再起動で反映する）。
    /// </summary>
    public static void Apply(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>設定画面の選択肢の表示名。言語名は今の表示言語にかかわらずその言語自身で書く（誤って選んでも戻せるように）。</summary>
    public static string DisplayName(string setting) => Normalize(setting) switch
    {
        English => "English",
        Japanese => "日本語",
        _ => Strings.LanguageSystem,
    };

    /// <summary>指定した言語での文字列（再起動の確認を、選んだ言語で出すため）。</summary>
    public static string Get(string key, CultureInfo culture) =>
        Strings.ResourceManager.GetString(key, culture) ?? key;
}
