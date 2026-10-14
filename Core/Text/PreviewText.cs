using System.Text;
using PlainClip.Core.Localization;

namespace PlainClip.Core.Text;

/// <summary>履歴一覧の 1 行表示を作る。長文でも先頭の一部しか走査しない。</summary>
public static class PreviewText
{
    public const int MaxChars = 120;

    /// <summary>改行 → " ↵ "、タブ → "→"、連続する空白類は 1 つにまとめ、先頭の空白は除く。長ければ末尾に "…"。</summary>
    public static string Create(string text)
    {
        var sb = new StringBuilder(MaxChars + 4);
        var pendingBreak = false;
        var truncated = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                pendingBreak = sb.Length > 0;
                continue;
            }
            if (pendingBreak)
            {
                pendingBreak = false;
                sb.Append(" ↵ ");
            }
            if (c == '\t') sb.Append('→');
            else if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            else sb.Append(c);

            if (sb.Length >= MaxChars)
            {
                truncated = i + 1 < text.Length;
                break;
            }
        }
        var result = sb.ToString().TrimEnd();
        if (result.Length == 0) return Strings.PreviewWhitespaceOnly;
        return truncated ? result + "…" : result;
    }

    /// <summary>行数（末尾の改行は数えない）。</summary>
    public static int CountLines(string text)
    {
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if ((c is '\r' or '\n') && i + 1 < text.Length) lines++;
        }
        return lines;
    }
}
