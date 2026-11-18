namespace PlainClip.Core.Layout;

/// <summary>画面座標の矩形（物理ピクセル。右・下は含まない）。</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>履歴一覧を出す基準（V2.3.0。仕様書 §11「表示位置」）。</summary>
public enum PopupAnchorKind
{
    /// <summary>マウス位置の右下（トレイから開いた・貼り付け先が無い）。基準の矩形はマウスの位置（幅・高さ 0）。</summary>
    Cursor,

    /// <summary>キャレットの左下（キーボードで開き、キャレットが取れた）。基準の矩形はキャレット。</summary>
    Caret,

    /// <summary>ウィンドウの中央（キーボードで開き、キャレットが取れない）。基準の矩形はウィンドウ。</summary>
    WindowCenter,
}

/// <summary>基準の種類・基準の矩形・収める作業領域。</summary>
public readonly record struct PopupAnchor(PopupAnchorKind Kind, PixelRect Reference, PixelRect WorkArea);

/// <summary>
/// 履歴一覧の位置の計算（V2.3.0。UI・Win32 に依存しない。単体テストの対象）。
/// どの基準でも、作業領域の内側に収める（作業領域より大きい場合は左上をそろえる）。
/// </summary>
public static class PopupPlacement
{
    /// <summary>マウス位置からのずれ（従来の値）。</summary>
    public const int CursorOffset = 8;

    /// <summary>キャレットとの間隔。</summary>
    public const int CaretGap = 2;

    /// <summary>一覧の左上の位置を返す。</summary>
    public static (int X, int Y) Place(PopupAnchor anchor, int width, int height) => anchor.Kind switch
    {
        PopupAnchorKind.Caret => BelowCaret(anchor.Reference, anchor.WorkArea, width, height),
        PopupAnchorKind.WindowCenter => CenterOn(anchor.Reference, anchor.WorkArea, width, height),
        _ => NearCursor(anchor.Reference.Left, anchor.Reference.Top, anchor.WorkArea, width, height),
    };

    /// <summary>マウス位置の右下。右にはみ出すならマウスの左、下にはみ出すなら作業領域の下端に合わせる（V2.2.1 までと同じ）。</summary>
    public static (int X, int Y) NearCursor(int cursorX, int cursorY, PixelRect work, int width, int height)
    {
        var x = cursorX + CursorOffset;
        var y = cursorY + CursorOffset;
        if (x + width > work.Right) x = Math.Max(work.Left, cursorX - width - CursorOffset);
        if (y + height > work.Bottom) y = Math.Max(work.Top, work.Bottom - height);
        return (ClampX(x, work, width), ClampY(y, work, height));
    }

    /// <summary>キャレットの左下。下にはみ出すならキャレットの上、上にも収まらなければ作業領域に収める。</summary>
    public static (int X, int Y) BelowCaret(PixelRect caret, PixelRect work, int width, int height)
    {
        var x = caret.Left;
        var y = caret.Bottom + CaretGap;
        if (y + height > work.Bottom)
        {
            var above = caret.Top - CaretGap - height;
            y = above >= work.Top ? above : work.Bottom - height;
        }
        return (ClampX(x, work, width), ClampY(y, work, height));
    }

    /// <summary>ウィンドウの中央。</summary>
    public static (int X, int Y) CenterOn(PixelRect window, PixelRect work, int width, int height)
    {
        var x = window.Left + (window.Width - width) / 2;
        var y = window.Top + (window.Height - height) / 2;
        return (ClampX(x, work, width), ClampY(y, work, height));
    }

    /// <summary>
    /// キャレットの画面座標（物理ピクセル）。<paramref name="caretClient"/> はキャレットのウィンドウのクライアント座標（そのウィンドウの論理座標）、
    /// <paramref name="originX"/>・<paramref name="originY"/> はクライアント領域の左上の画面座標（物理）。DPI に対応していないウィンドウは
    /// モニターの DPI ÷ ウィンドウの DPI 倍に拡大して表示されるので、左上からのずれをその倍率で直す（対応しているウィンドウは 1 倍）。
    /// </summary>
    public static PixelRect CaretOnScreen(PixelRect caretClient, int originX, int originY, uint windowDpi, uint monitorDpi)
    {
        int Scale(int v) => windowDpi == 0 || windowDpi == monitorDpi ? v : (int)Math.Round(v * (double)monitorDpi / windowDpi, MidpointRounding.AwayFromZero);
        var left = originX + Scale(caretClient.Left);
        var top = originY + Scale(caretClient.Top);
        var right = originX + Scale(caretClient.Right);
        var bottom = originY + Scale(caretClient.Bottom);
        return new PixelRect(left, top, Math.Max(left, right), bottom);
    }

    private static int ClampX(int x, PixelRect work, int width) => Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));

    private static int ClampY(int y, PixelRect work, int height) => Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
}
