namespace PlainClip.Core.Localization;

/// <summary>
/// Web の案内ページと公開リポジトリ（V2.0.0。PROJECT.md「ライセンス表記」）。設定画面の下部から開く。
/// Web ページはサイト（cloudflare/www/public/plainclip/）の構成に合わせる。変えるときはここだけ直す。
/// </summary>
public static class AppLinks
{
    /// <summary>紹介ページ（日本語。英語は /plainclip/en/ の予定）。</summary>
    public const string Website = "https://www.iwanaga.net/plainclip/";

    public const string WebsiteEnglish = "https://www.iwanaga.net/plainclip/en/";

    /// <summary>プライバシーポリシー（Microsoft Store の申請にも使う）。</summary>
    public const string Privacy = "https://www.iwanaga.net/plainclip/privacy/";

    /// <summary>ソースを MIT License で公開する公開リポジトリ（設定画面の「ソースコード」）。</summary>
    public const string Repository = "https://github.com/ja1bus/PlainClip";

    /// <summary>表示言語に合わせた紹介ページ。日本語以外は英語。</summary>
    public static string WebsiteFor(System.Globalization.CultureInfo uiCulture) =>
        uiCulture.TwoLetterISOLanguageName == UiLanguage.Japanese ? Website : WebsiteEnglish;
}
