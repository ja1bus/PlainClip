using System.Text.Json.Serialization;
using PlainClip.Core.History;
using PlainClip.Core.Input;

namespace PlainClip.Core.Settings;

/// <summary>機能の呼び出し方法。各機能につき 1 つだけ選ぶ。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TriggerMode>))]
public enum TriggerMode
{
    /// <summary>修飾キーのダブルタップ（「クリップボードを書式なしで貼り付け」だけで選べる。キーは <see cref="AppSettings.DoubleTapKey"/>）。</summary>
    DoubleTap,

    /// <summary>通常のグローバルショートカット（RegisterHotKey）。</summary>
    Shortcut,

    Disabled,
}

/// <summary>
/// 設定（%LOCALAPPDATA%\PlainClip\settings.json）。履歴そのものは保存しない。
/// 自動起動は HKCU の Run キー（MSIX 版はスタートアップタスク）の状態を正とし、ここには持たない。
/// 項目を追加するときは既定値・<see cref="SettingsNormalizer"/>・設定画面・PROJECT.md をそろえる。
/// 項目名は V1 の設定ファイルとの互換のため変えない（PasteLatest* は V2.0.0 から「現在のクリップボードを書式なしで貼り付け」の意味）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>「クリップボードを書式なしで貼り付け」の呼び出し方法。</summary>
    public TriggerMode PasteLatestTrigger { get; set; } = TriggerMode.DoubleTap;

    /// <summary>ダブルタップに使うキー（V2.0.0。既定は左 Shift）。</summary>
    public DoubleTapKey DoubleTapKey { get; set; } = DoubleTapKey.LeftShift;

    /// <summary>"Ctrl+Alt+P" 形式。既定は空（未割当）。</summary>
    public string PasteLatestShortcut { get; set; } = "";

    /// <summary>Shortcut / Disabled のみ（ダブルタップは V1.1 で廃止）。既定は無効。</summary>
    public TriggerMode ShowHistoryTrigger { get; set; } = TriggerMode.Disabled;

    public string ShowHistoryShortcut { get; set; } = "";

    /// <summary>数字ショートカットの修飾キー（V2.0.0。"Ctrl+Alt" 形式。<see cref="NumberPaste.Choices"/> のいずれか）。既定は空（使わない）。</summary>
    public string NumberPasteModifiers { get; set; } = "";

    public int MaxHistoryCount { get; set; } = ClipHistory.DefaultMaxCount;

    public bool MergeDuplicates { get; set; } = true;

    /// <summary>表示言語（V2.0.0）。"" = Windows の表示言語に合わせる / "en" / "ja"。変更は再起動後に反映する。</summary>
    public string Language { get; set; } = "";

    /// <summary>
    /// サポート用の診断ログを記録する期限（V2.1.0。UTC。null = 記録しない（既定））。設定画面のバージョン表示を Ctrl+Shift+クリックして有効にする。
    /// 期限を過ぎたら自動で止める（<see cref="Diagnostics.DiagnosticMode"/>）。
    /// </summary>
    public DateTime? DiagnosticLogUntil { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    [JsonIgnore]
    public Shortcut PasteLatestShortcutValue =>
        Shortcut.TryParse(PasteLatestShortcut, out var s) ? s : Shortcut.None;

    [JsonIgnore]
    public Shortcut ShowHistoryShortcutValue =>
        Shortcut.TryParse(ShowHistoryShortcut, out var s) ? s : Shortcut.None;

    [JsonIgnore]
    public KeyModifiers NumberPasteModifiersValue =>
        NumberPaste.TryParse(NumberPasteModifiers, out var m) ? m : KeyModifiers.None;

    /// <summary>
    /// 設定画面で選べる項目がすべて同じか（V2.2.0。変更が無ければ［適用］を押せなくする）。
    /// ショートカット・数字ショートカット・表示言語は解釈した値で比べ、書き方の違いと使えない値（保存時に空欄になる）は区別しない。
    /// 設定画面で選ばない項目（診断ログの期限）と自動起動（設定ファイルの外）は比べない。
    /// </summary>
    public bool SameChoicesAs(AppSettings other) =>
        PasteLatestTrigger == other.PasteLatestTrigger
        && DoubleTapKey == other.DoubleTapKey
        && ShowHistoryTrigger == other.ShowHistoryTrigger
        && Usable(PasteLatestShortcutValue) == Usable(other.PasteLatestShortcutValue)
        && Usable(ShowHistoryShortcutValue) == Usable(other.ShowHistoryShortcutValue)
        && NumberPasteModifiersValue == other.NumberPasteModifiersValue
        && MaxHistoryCount == other.MaxHistoryCount
        && MergeDuplicates == other.MergeDuplicates
        && Localization.UiLanguage.Normalize(Language) == Localization.UiLanguage.Normalize(other.Language);

    private static Shortcut Usable(Shortcut shortcut) =>
        shortcut.Validate() == ShortcutValidation.Ok ? shortcut : Shortcut.None;
}
