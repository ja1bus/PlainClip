namespace PlainClip.Core.Input;

/// <summary>
/// 数字ショートカット（V2.0.0。PROJECT.md「数字ショートカット」）: 共通の修飾キー＋1～9 で、その番号の履歴を一覧を開かずに貼る。
/// 番号は履歴一覧の番号と同じ（1 = 最新）。テンキーの数字は対象外（最上段の数字キーだけ）。
/// 修飾キーは他のアプリ・Windows と重なりにくい組み合わせの候補から選ぶ（Win を含む組み合わせはタスクバーが使うため候補にしない）。
/// 既定は「使わない」（どのキーも予約しない）。
/// </summary>
public static class NumberPaste
{
    public const int Count = 9;

    /// <summary>設定画面の選択肢の順（None = 使わない）。</summary>
    public static readonly IReadOnlyList<KeyModifiers> Choices =
    [
        KeyModifiers.None,
        KeyModifiers.Control | KeyModifiers.Alt,
        KeyModifiers.Alt | KeyModifiers.Shift,
        KeyModifiers.Control | KeyModifiers.Shift,
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift,
    ];

    /// <summary>番号（1～9）のショートカット。</summary>
    public static Shortcut For(KeyModifiers modifiers, int number) => new(modifiers, '0' + number);

    /// <summary>1～9 のショートカットすべて（修飾キーが None なら空）。</summary>
    public static IEnumerable<Shortcut> All(KeyModifiers modifiers) =>
        modifiers == KeyModifiers.None ? [] : Enumerable.Range(1, Count).Select(n => For(modifiers, n));

    /// <summary>ショートカットがこの修飾キーの数字ショートカットなら番号（1～9）、それ以外は 0。</summary>
    public static int NumberOf(KeyModifiers modifiers, Shortcut shortcut) =>
        modifiers != KeyModifiers.None && shortcut.Modifiers == modifiers && shortcut.Key is >= '1' and <= '9' ? shortcut.Key - '0' : 0;

    /// <summary>"Ctrl+Alt" 形式（設定ファイル・表示用）。None は空文字。</summary>
    public static string Format(KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.None) return "";
        var parts = new List<string>(3);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return string.Join("+", parts);
    }

    /// <summary>設定値を読む。空は None。候補にない組み合わせ・不正な値は false。</summary>
    public static bool TryParse(string? text, out KeyModifiers modifiers)
    {
        modifiers = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var result = KeyModifiers.None;
        foreach (var token in text.Split('+', StringSplitOptions.TrimEntries))
        {
            var m = token.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => KeyModifiers.Control,
                "ALT" => KeyModifiers.Alt,
                "SHIFT" => KeyModifiers.Shift,
                "WIN" or "WINDOWS" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
            if (m == KeyModifiers.None || result.HasFlag(m)) return false;
            result |= m;
        }
        if (!Choices.Contains(result)) return false;
        modifiers = result;
        return true;
    }
}
