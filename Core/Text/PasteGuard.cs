namespace PlainClip.Core.Text;

/// <summary>
/// 貼り付けの直前確認（V2.0.1。PROJECT.md「貼り付け処理」）。Win32 に依存しない（単体テスト対象）。
/// PlainClip がクリップボードへ設定した（または読んだ）後、前面化・キーが離されるのを待つ間に他のアプリや利用者が
/// クリップボードを変えたら、選んだものと違う内容を貼らないよう Ctrl+V を送らない。新しい内容は上書きしない。
/// </summary>
public static class PasteGuard
{
    /// <param name="expected">PlainClip が書き込んだ直後、または読み取ったときのシーケンス番号。0 は不明。</param>
    /// <param name="current">Ctrl+V を送る直前のシーケンス番号。</param>
    /// <returns>変わっていなければ true。番号が不明（0）なら確かめられないため false。</returns>
    public static bool ClipboardUnchanged(uint expected, uint current) => expected != 0 && expected == current;
}
