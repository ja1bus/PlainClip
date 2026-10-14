using System.Globalization;

namespace PlainClip.Core.Input;

/// <summary>仮想キーコード ⇔ 設定ファイル上の名前。一覧に無いキーは "VK_2C" のような 16 進表記。</summary>
public static class KeyNames
{
    private static readonly Dictionary<int, string> Names = BuildNames();
    private static readonly Dictionary<string, int> Codes =
        Names.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static string GetName(int vk) =>
        Names.TryGetValue(vk, out var name) ? name : $"VK_{vk:X2}";

    public static bool TryParse(string token, out int vk)
    {
        if (Codes.TryGetValue(token, out vk)) return true;
        if (token.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(token.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk) &&
            vk is > 0 and < 0xFF)
            return true;
        vk = 0;
        return false;
    }

    /// <summary>Shift / Ctrl / Alt / Win（左右の区別付きを含む）。</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>修飾キーなしで割り当ててもよいキー（F1～F24・PrintScreen・Pause・ScrollLock）。</summary>
    public static bool IsStandaloneKey(int vk) => vk is (>= 0x70 and <= 0x87) or 0x2C or 0x13 or 0x91;

    private static Dictionary<int, string> BuildNames()
    {
        var d = new Dictionary<int, string>();
        for (var c = 'A'; c <= 'Z'; c++) d[c] = c.ToString();
        for (var c = '0'; c <= '9'; c++) d[c] = c.ToString();
        for (var i = 1; i <= 24; i++) d[0x6F + i] = $"F{i}";
        for (var i = 0; i <= 9; i++) d[0x60 + i] = $"Num{i}";
        d[0x6A] = "NumMultiply";
        d[0x6B] = "NumAdd";
        d[0x6D] = "NumSubtract";
        d[0x6E] = "NumDecimal";
        d[0x6F] = "NumDivide";
        d[0x2C] = "PrintScreen";
        d[0x13] = "Pause";
        d[0x91] = "ScrollLock";
        d[0x2D] = "Insert";
        d[0x2E] = "Delete";
        d[0x24] = "Home";
        d[0x23] = "End";
        d[0x21] = "PageUp";
        d[0x22] = "PageDown";
        d[0x25] = "Left";
        d[0x26] = "Up";
        d[0x27] = "Right";
        d[0x28] = "Down";
        d[0x20] = "Space";
        d[0x0D] = "Enter";
        d[0x09] = "Tab";
        d[0x1B] = "Esc";
        d[0x08] = "Backspace";
        return d;
    }
}
