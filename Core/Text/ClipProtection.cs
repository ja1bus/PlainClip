namespace PlainClip.Core.Text;

/// <summary>
/// コピー元がクリップボードに付けた保護指定（V2.0.1。PROJECT.md「Clipboard 監視方式」）。Windows の規約では
/// 「履歴に含めない」と「クラウドへ同期しない」は独立している。PlainClip が書き直すときは、読んだ指定を弱めずに付け直す。
/// </summary>
[Flags]
public enum ClipProtection
{
    None = 0,

    /// <summary>端末間のクリップボード同期（クラウド）に送らない（CanUploadToCloudClipboard = 0）。</summary>
    NoCloudUpload = 1,

    /// <summary>
    /// 履歴・監視の対象外（ExcludeClipboardContentFromMonitorProcessing、Clipboard Viewer Ignore、CanIncludeInClipboardHistory = 0）。
    /// PlainClip の履歴にも入れない。書き直すときはクラウド同期の禁止も付ける。
    /// </summary>
    ExcludeFromHistory = 2,
}

/// <summary>DWORD の目印の形式（CanIncludeInClipboardHistory 等）の読み取り結果。</summary>
public enum MarkerValue
{
    /// <summary>形式が無い。</summary>
    Absent,

    /// <summary>値が 0 以外（許可）。</summary>
    Allowed,

    /// <summary>値が 0（禁止）。</summary>
    Denied,

    /// <summary>形式はあるが値を読めない（データ・ロックの失敗、4 バイト未満）。禁止とみなす。</summary>
    Unreadable,
}

/// <summary>保護指定の判定（Win32 に依存しない。単体テスト対象）。</summary>
public static class ClipProtectionRules
{
    /// <param name="excludeFromMonitor">ExcludeClipboardContentFromMonitorProcessing があるか（値は問わない）。</param>
    /// <param name="viewerIgnore">Clipboard Viewer Ignore があるか（値は問わない）。</param>
    /// <param name="canIncludeInHistory">CanIncludeInClipboardHistory。</param>
    /// <param name="canUploadToCloud">CanUploadToCloudClipboard。</param>
    public static ClipProtection FromMarkers(bool excludeFromMonitor, bool viewerIgnore, MarkerValue canIncludeInHistory, MarkerValue canUploadToCloud)
    {
        var p = ClipProtection.None;
        if (excludeFromMonitor || viewerIgnore || IsDenied(canIncludeInHistory)) p |= ClipProtection.ExcludeFromHistory | ClipProtection.NoCloudUpload;
        if (IsDenied(canUploadToCloud)) p |= ClipProtection.NoCloudUpload;
        return p;
    }

    /// <summary>
    /// 書き込むときに付ける目印。履歴の対象外なら 3 つ（Windows の「クリップボードの履歴」と同じ規約）、
    /// クラウド同期の禁止だけなら CanUploadToCloudClipboard = 0 だけ（履歴は許可されているため）。
    /// </summary>
    public static (bool ExcludeFromMonitor, bool CanIncludeInHistoryZero, bool CanUploadToCloudZero) MarkersToWrite(ClipProtection protection)
    {
        var exclude = protection.HasFlag(ClipProtection.ExcludeFromHistory);
        return (exclude, exclude, exclude || protection.HasFlag(ClipProtection.NoCloudUpload));
    }

    private static bool IsDenied(MarkerValue value) => value is MarkerValue.Denied or MarkerValue.Unreadable;
}
