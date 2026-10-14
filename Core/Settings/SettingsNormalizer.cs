using PlainClip.Core.History;
using PlainClip.Core.Input;
using PlainClip.Core.Localization;

namespace PlainClip.Core.Settings;

/// <summary>読み込んだ設定の範囲外・不正な値を補正する。補正内容（ログ用）を返す。</summary>
public static class SettingsNormalizer
{
    public static IReadOnlyList<string> Normalize(AppSettings s)
    {
        var fixes = new List<string>();

        if (!Enum.IsDefined(s.PasteLatestTrigger))
        {
            fixes.Add($"PasteLatestTrigger={s.PasteLatestTrigger} → DoubleTap");
            s.PasteLatestTrigger = TriggerMode.DoubleTap;
        }
        // 履歴表示のダブルタップ（左 Ctrl ×2）は V1.1 で廃止。以前の設定ファイルは無効として読む。
        if (!Enum.IsDefined(s.ShowHistoryTrigger) || s.ShowHistoryTrigger == TriggerMode.DoubleTap)
        {
            fixes.Add($"ShowHistoryTrigger={s.ShowHistoryTrigger} → Disabled");
            s.ShowHistoryTrigger = TriggerMode.Disabled;
        }

        s.PasteLatestShortcut = FixShortcut(s.PasteLatestShortcut, nameof(s.PasteLatestShortcut), fixes);
        s.ShowHistoryShortcut = FixShortcut(s.ShowHistoryShortcut, nameof(s.ShowHistoryShortcut), fixes);

        // ショートカット方式なのにキーが無い場合は無効にする。
        if (s.PasteLatestTrigger == TriggerMode.Shortcut && s.PasteLatestShortcut.Length == 0)
        {
            fixes.Add("PasteLatestTrigger=Shortcut（キー未設定） → Disabled");
            s.PasteLatestTrigger = TriggerMode.Disabled;
        }
        if (s.ShowHistoryTrigger == TriggerMode.Shortcut && s.ShowHistoryShortcut.Length == 0)
        {
            fixes.Add("ShowHistoryTrigger=Shortcut（キー未設定） → Disabled");
            s.ShowHistoryTrigger = TriggerMode.Disabled;
        }

        if (!Enum.IsDefined(s.DoubleTapKey))
        {
            fixes.Add($"DoubleTapKey={s.DoubleTapKey} → LeftShift");
            s.DoubleTapKey = DoubleTapKey.LeftShift;
        }

        if (NumberPaste.TryParse(s.NumberPasteModifiers, out var numberModifiers))
        {
            s.NumberPasteModifiers = NumberPaste.Format(numberModifiers); // 表記をそろえる
        }
        else
        {
            fixes.Add($"NumberPasteModifiers={s.NumberPasteModifiers} → (none)");
            s.NumberPasteModifiers = "";
        }

        var language = UiLanguage.Normalize(s.Language);
        if (language != s.Language)
        {
            if (language.Length == 0 && !string.IsNullOrWhiteSpace(s.Language)) fixes.Add($"Language={s.Language} → (Windows)");
            s.Language = language;
        }

        var count = Math.Clamp(s.MaxHistoryCount, ClipHistory.MinMaxCount, ClipHistory.MaxMaxCount);
        if (count != s.MaxHistoryCount)
        {
            fixes.Add($"MaxHistoryCount={s.MaxHistoryCount} → {count}");
            s.MaxHistoryCount = count;
        }
        return fixes;
    }

    /// <summary>空は未割当として許す。不正な値は空（未割当）にする。正しい値は表記をそろえる。</summary>
    private static string FixShortcut(string? text, string name, List<string> fixes)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (Shortcut.TryParse(text, out var shortcut) && shortcut.Validate() == ShortcutValidation.Ok)
            return shortcut.ToString();
        fixes.Add($"{name}={text} → 未割当");
        return "";
    }
}
