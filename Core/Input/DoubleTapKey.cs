using PlainClip.Core.Localization;
using System.Text.Json.Serialization;

namespace PlainClip.Core.Input;

/// <summary>
/// ダブルタップに使うキー（V2.0.0。PROJECT.md「ダブルタップ」）。通常の入力と衝突しにくい修飾キーだけを選べる。
/// Alt は 1 回押すだけで多くのアプリのメニューにフォーカスが移るため候補にしない。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DoubleTapKey>))]
public enum DoubleTapKey
{
    LeftShift,
    RightShift,
    RightCtrl,
    /// <summary>PowerToys「マウスを探す」の既定と重なる（V1.1 で履歴一覧の左 Ctrl ×2 を廃止した経緯）。設定画面で注記する。</summary>
    LeftCtrl,
}

public static class DoubleTapKeys
{
    /// <summary>設定画面の選択肢の順。</summary>
    public static readonly IReadOnlyList<DoubleTapKey> All =
        [DoubleTapKey.LeftShift, DoubleTapKey.RightShift, DoubleTapKey.RightCtrl, DoubleTapKey.LeftCtrl];

    /// <summary>低レベルフックの vkCode（左右を区別する）。</summary>
    public static int VirtualKey(this DoubleTapKey key) => key switch
    {
        DoubleTapKey.RightShift => 0xA1,
        DoubleTapKey.LeftCtrl => 0xA2,
        DoubleTapKey.RightCtrl => 0xA3,
        _ => 0xA0,
    };

    /// <summary>
    /// 実際のキーが拡張フラグ（LLKHF_EXTENDED）付きで届くか。右 Ctrl は拡張キーとして届く。
    /// Shift と左 Ctrl では、拡張フラグ付きの入力はキーボードが送る「偽の Shift」等なので他のキーとして扱う。
    /// </summary>
    public static bool IsExtended(this DoubleTapKey key) => key == DoubleTapKey.RightCtrl;

    /// <summary>表示名（例: 「左Shift」）。</summary>
    public static string DisplayName(this DoubleTapKey key) => key switch
    {
        DoubleTapKey.RightShift => Strings.KeyRightShift,
        DoubleTapKey.RightCtrl => Strings.KeyRightCtrl,
        DoubleTapKey.LeftCtrl => Strings.KeyLeftCtrl,
        _ => Strings.KeyLeftShift,
    };

    /// <summary>操作の表記（例: 「左Shift を 2 回」）。</summary>
    public static string TwiceText(this DoubleTapKey key) => string.Format(Strings.DoubleTapTwiceFormat, key.DisplayName());
}
