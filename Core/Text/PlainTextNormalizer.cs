using System.Text;

namespace PlainClip.Core.Text;

/// <summary>
/// クリップボードから取り出したプレーンテキストの最小限の正規化（PROJECT.md「プレーンテキスト化仕様」）。
/// 書式（HTML・RTF）はそもそも取り出さない（CF_UNICODETEXT だけを読む）ため、ここでは文字単位の置換だけを行う。
/// 意味が変わる加工（前後の空白・改行の削除、改行コードの変換、全角半角変換など）はしない。
/// </summary>
public static class PlainTextNormalizer
{
    /// <summary>これより長いテキストは履歴に入れない（メモリと UI の保護。約 2MB）。</summary>
    public const int MaxLength = 1_000_000;

    /// <summary>
    /// 貼り付け先で通常の空白と扱いが変わりやすい特殊空白 → 半角スペース。
    /// U+00A0 NO-BREAK SPACE（Outlook / ブラウザの &amp;nbsp;）、U+2007 FIGURE SPACE、U+202F NARROW NO-BREAK SPACE。
    /// 全角スペース（U+3000）は日本語の文章で意味を持つため変えない。ゼロ幅文字も絵文字の結合などに使われるため変えない。
    /// </summary>
    private static bool IsSpecialSpace(char c) => c is ' ' or ' ' or ' ';

    /// <summary>正規化する。結果が空（または長すぎる）なら null（履歴に入れない）。</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // 末尾の NUL（アプリによってはバッファの余りが付く）を落とす。
        var end = text.Length;
        while (end > 0 && text[end - 1] == '\0') end--;
        if (end == 0 || end > MaxLength) return null;

        var span = text.AsSpan(0, end);
        if (span.IndexOfAny(' ', ' ', ' ') < 0)
            return end == text.Length ? text : span.ToString();

        var sb = new StringBuilder(end);
        foreach (var c in span) sb.Append(IsSpecialSpace(c) ? ' ' : c);
        return sb.ToString();
    }
}
