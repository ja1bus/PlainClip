namespace PlainClip.Core.Text;

/// <summary>「クリップボードを書式なしで貼り付け」で、現在のクリップボードをどう扱うか。</summary>
public enum PasteSourceKind
{
    /// <summary>貼り付けできるテキストがある。</summary>
    Text,

    /// <summary>テキストが無い（画像だけ・HTML/RTF だけ等）。何も貼らない。</summary>
    NoText,

    /// <summary>エクスプローラーでのファイルのコピー。何も貼らない。</summary>
    Files,

    /// <summary>テキストが空。何も貼らない。</summary>
    Empty,

    /// <summary>長すぎて書式を外せない（プレーンテキスト以外の形式を含み、上限を超える）。何も貼らない。</summary>
    TooLong,

    /// <summary>クリップボードを開けない・読めない。何も貼らない（過去の履歴で代わりに貼らない）。</summary>
    Unavailable,
}

/// <param name="Kind">扱い。</param>
/// <param name="Text">クリップボードへ設定し直す本文（<see cref="NeedsRewrite"/> のときだけ使う）。</param>
/// <param name="NeedsRewrite">
/// プレーンテキストへ書き直す必要があるか。すでにプレーンテキストだけ（かつ正規化で変わらない）なら書き直さずに Ctrl+V だけを送る
/// （Win+V の履歴に同じ内容が二重に入らず、パスワード管理ソフト等がクリップボードの所有者のままになる）。
/// </param>
/// <param name="Protection">
/// コピー元の保護指定（パスワード管理ソフト等の「履歴に残さない」、クラウド同期の禁止。V2.0.1）。書き直すときも同じ指定を付ける。
/// </param>
public sealed record PasteSource(PasteSourceKind Kind, string Text = "", bool NeedsRewrite = false, ClipProtection Protection = ClipProtection.None)
{
    public static readonly PasteSource Unavailable = new(PasteSourceKind.Unavailable);

    /// <summary>「履歴に残さない」指定付き（PlainClip の履歴・「現在のクリップボード」の印にも使わない）。</summary>
    public bool Sensitive => Protection.HasFlag(ClipProtection.ExcludeFromHistory);

    /// <summary>
    /// 読み取った時点のクリップボードのシーケンス番号（V2.0.1）。書き直さずに貼るとき、Ctrl+V を送る直前に変わっていないことを確かめる。
    /// </summary>
    public uint Sequence { get; init; }
}

/// <summary>
/// 現在のクリップボードの判定（V2.0.0。PROJECT.md「クリップボードを書式なしで貼り付け」）。Win32 に依存しない（単体テスト対象）。
/// 「画像があるか」ではなく「Unicode テキストがあるか」で決める（Excel のセル等は画像とテキストを同時に置くため）。
/// </summary>
public static class PasteSourceRules
{
    /// <summary>プレーンテキストの形式（CF_TEXT・CF_OEMTEXT・CF_UNICODETEXT・CF_LOCALE。OS が相互に合成する）。</summary>
    public static readonly IReadOnlySet<uint> PlainTextFormats = new HashSet<uint> { 1, 7, 13, 16 };

    /// <summary>
    /// クリップボードの形式がプレーンテキストと、除外指定などの目印（<paramref name="markerFormats"/>）だけか。
    /// </summary>
    public static bool OnlyPlainText(IEnumerable<uint> formats, IReadOnlySet<uint> markerFormats) =>
        formats.All(f => PlainTextFormats.Contains(f) || markerFormats.Contains(f));

    /// <param name="hasFileDrop">CF_HDROP があるか。</param>
    /// <param name="hasUnicodeText">CF_UNICODETEXT があるか（CF_TEXT だけでも OS が合成する）。</param>
    /// <param name="rawText">読んだ本文（NUL 終端まで。<see cref="PlainTextNormalizer.MaxLength"/> + 1 文字で打ち切ってよい）。読めなければ null。</param>
    /// <param name="protection">コピー元の保護指定。</param>
    /// <param name="onlyPlainText"><see cref="OnlyPlainText"/> の結果。</param>
    public static PasteSource Decide(bool hasFileDrop, bool hasUnicodeText, string? rawText, ClipProtection protection, bool onlyPlainText)
    {
        if (hasFileDrop) return new PasteSource(PasteSourceKind.Files);
        if (!hasUnicodeText) return new PasteSource(PasteSourceKind.NoText);
        if (rawText is null) return PasteSource.Unavailable;
        if (rawText.Length > PlainTextNormalizer.MaxLength)
        {
            // すでにプレーンテキストなら書き直さずに貼れる。書式付きは書き直せないので貼らない。
            return onlyPlainText
                ? new PasteSource(PasteSourceKind.Text, "", NeedsRewrite: false, protection)
                : new PasteSource(PasteSourceKind.TooLong);
        }
        var text = PlainTextNormalizer.Normalize(rawText);
        if (text is null) return new PasteSource(PasteSourceKind.Empty);
        return new PasteSource(PasteSourceKind.Text, text, NeedsRewrite: !onlyPlainText || text != rawText, protection);
    }
}
