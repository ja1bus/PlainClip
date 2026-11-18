namespace PlainClip.Core.Input;

/// <summary>ショートカットで呼ぶ機能（V2.3.0 で Services から Core へ移した。単体テストの対象）。値は RegisterHotKey の ID。Number1～Number9 は数字ショートカット（V2.0.0）。</summary>
public enum HotkeyAction
{
    PasteLatest = 1,
    ShowHistory = 2,

    /// <summary>クリップボードを書式なしに変換（V2.3.0。貼り付けない）。</summary>
    ConvertClipboard = 3,

    Number1 = 11,
    Number2,
    Number3,
    Number4,
    Number5,
    Number6,
    Number7,
    Number8,
    Number9,
}

public static class HotkeyActions
{
    /// <summary>登録する順（先に登録したものが優先。同じキーは後の機能が「重複」で失敗する）。</summary>
    public static readonly IReadOnlyList<HotkeyAction> All =
        [HotkeyAction.PasteLatest, HotkeyAction.ShowHistory, HotkeyAction.ConvertClipboard, .. Numbers()];

    public static IEnumerable<HotkeyAction> Numbers() =>
        Enumerable.Range(1, NumberPaste.Count).Select(NumberAction);

    public static HotkeyAction NumberAction(int number) => (HotkeyAction)((int)HotkeyAction.Number1 + number - 1);

    /// <summary>数字ショートカットなら番号（1～9）、それ以外は 0。</summary>
    public static int NumberOf(this HotkeyAction action) =>
        action is >= HotkeyAction.Number1 and <= HotkeyAction.Number9 ? action - HotkeyAction.Number1 + 1 : 0;
}
