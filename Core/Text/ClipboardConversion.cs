namespace PlainClip.Core.Text;

/// <summary>「クリップボードを書式なしに変換」で行うこと（V2.3.0。仕様書 §28）。</summary>
public enum ConversionAction
{
    /// <summary>プレーンテキストに書き直す（コピー元の保護指定を付け直す）。</summary>
    Rewrite,

    /// <summary>すでにプレーンテキストだけ。書き直さない。</summary>
    AlreadyPlain,

    /// <summary>テキストが無い・ファイル・空・長すぎる・読めない。何もしない（§8 と同じ案内）。</summary>
    NoText,

    /// <summary>読んでから書き直すまでの間に他のアプリがクリップボードを変えた。上書きしない。</summary>
    Changed,
}

/// <summary>「クリップボードを書式なしに変換」の判断（V2.3.0。Win32 に依存しない。単体テストの対象）。Ctrl+V は送らない。</summary>
public static class ClipboardConversion
{
    /// <param name="source">現在のクリップボードの判定（<see cref="PasteSourceRules.Decide"/>）。</param>
    /// <param name="currentSequence">書き直す直前のクリップボードのシーケンス番号。</param>
    public static ConversionAction Decide(PasteSource source, uint currentSequence)
    {
        if (source.Kind != PasteSourceKind.Text) return ConversionAction.NoText;
        if (!source.NeedsRewrite) return ConversionAction.AlreadyPlain;
        return currentSequence == source.Sequence ? ConversionAction.Rewrite : ConversionAction.Changed;
    }
}
