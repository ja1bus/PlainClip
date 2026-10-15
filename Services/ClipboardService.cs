using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using PlainClip.Core.Text;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Services;

/// <summary>
/// クリップボードの監視と読み書き（PROJECT.md「Clipboard 監視方式」）。
///  - 監視: メッセージ専用ウィンドウ＋AddClipboardFormatListener（WM_CLIPBOARDUPDATE。ポーリングしない）。
///  - 読み取り: CF_UNICODETEXT だけを Win32 API で読む（HTML・RTF・画像は読まない）。コピー元の遅延レンダリングで
///    待たされても UI が固まらないよう、スレッドプールで読む。ロック中は短い間隔で数回リトライする。
///  - 監視はクリップボードを一切書き換えない。書き込むのは PlainClip から貼り付け・コピーするときだけ。
///  - 自分が書き込んだ内容の更新通知は、シーケンス番号と所有ウィンドウで見分けて履歴に入れない（ループ防止）。
///  - 「クリップボードを書式なしで貼り付け」では、押した時点のクリップボードを直接読む（<see cref="ReadCurrentAsync"/>。V2.0.0）。
///  - コピー元の保護指定（履歴に含めない・クラウドへ同期しない）を読み、書き直すときは弱めずに付け直す（V2.0.1）。
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private const int DebounceMs = 60;
    private const int OpenRetryCount = 10;
    private const int OpenRetryDelayMs = 25;

    private static readonly uint ExcludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint ViewerIgnoreFormat = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint CanIncludeInHistoryFormat = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadFormat = RegisterClipboardFormat("CanUploadToCloudClipboard");

    /// <summary>除外指定などの目印の形式（「プレーンテキストだけか」の判定では本文の形式と同じに扱う）。</summary>
    private static readonly IReadOnlySet<uint> MarkerFormats =
        new HashSet<uint>(new[] { ExcludeFormat, ViewerIgnoreFormat, CanIncludeInHistoryFormat, CanUploadFormat }.Where(f => f != 0));

    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _debounce;
    private readonly bool _listening;
    private uint _ownSequence;
    private bool _reading;
    private bool _readAgain;
    private bool _disposed;

    public ClipboardService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var p = new HwndSourceParameters("PlainClip.Clipboard") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartRead();
        };
        _listening = AddClipboardFormatListener(_source.Handle);
        if (!_listening) ErrorLog.Write($"AddClipboardFormatListener に失敗（エラー {Marshal.GetLastWin32Error()}）");
    }

    /// <summary>
    /// テキストのコピーを検知した（UI スレッド。正規化済みの本文と、コピー元の保護指定）。履歴の対象外のものは届かない
    /// （届くのはクラウド同期の禁止だけ。V2.0.1）。
    /// </summary>
    public event Action<string, ClipProtection>? TextCopied;

    /// <summary>
    /// 他のアプリによってクリップボードの内容が変わった（UI スレッド）。引数は履歴に入れるテキストならその本文、
    /// それ以外（画像・ファイル・除外指定付き等）は null。履歴一覧の「現在のクリップボード」の印に使う。
    /// </summary>
    public event Action<string?>? ContentChanged;

    public bool IsListening => _listening;

    /// <summary>
    /// Unicode プレーンテキストをクリップボードへ設定する（UI スレッド）。他プロセスが開いていれば短時間リトライする。
    /// <paramref name="protection"/> の保護指定（履歴に残さない・クラウドへ同期しない）も付ける。保護指定を付けられなければ
    /// 本文も残さずに失敗にする（保護されていない本文を他のアプリ・Windows の履歴や同期へ渡さない。V2.0.1）。
    /// 成功したら書き込み後のシーケンス番号（貼り付けの直前確認に使う）、失敗したら null。
    /// </summary>
    public async Task<uint?> SetTextAsync(string text, ClipProtection protection = ClipProtection.None)
    {
        var markers = ClipProtectionRules.MarkersToWrite(protection);
        if ((markers.ExcludeFromMonitor && ExcludeFormat == 0) || (markers.CanIncludeInHistoryZero && CanIncludeInHistoryFormat == 0)
            || (markers.CanUploadToCloudZero && CanUploadFormat == 0))
        {
            ErrorLog.Write("保護指定の形式を登録できないため、クリップボードへ書き込みませんでした");
            return null;
        }
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (attempt > 0) await Task.Delay(OpenRetryDelayMs);
            if (!OpenClipboard(_source.Handle)) continue;
            var ok = false;
            try
            {
                // 目印を先に置き、本文はその後。途中で失敗したら閉じる前に空にする（他のアプリが読めるのは閉じた後）。
                ok = EmptyClipboard()
                    && (!markers.ExcludeFromMonitor || PutDword(ExcludeFormat, 0))
                    && (!markers.CanIncludeInHistoryZero || PutDword(CanIncludeInHistoryFormat, 0))
                    && (!markers.CanUploadToCloudZero || PutDword(CanUploadFormat, 0))
                    && PutUnicodeText(text);
                if (!ok) EmptyClipboard();
            }
            finally
            {
                CloseClipboard();
            }
            // 自分の書き込みによる WM_CLIPBOARDUPDATE を履歴に入れないため、書き込み後の番号を覚える。
            _ownSequence = GetClipboardSequenceNumber();
            if (ok) return _ownSequence;
            ErrorLog.Write($"クリップボードへの書き込みに失敗（エラー {Marshal.GetLastWin32Error()}）");
            return null;
        }
        ErrorLog.Write("クリップボードを開けませんでした（他のアプリが使用中）");
        return null;
    }

    /// <summary>
    /// 現在のクリップボードを読んで、書式なしで貼れるか判定する（スレッドプールで読む）。
    /// PlainClip 自身が書き込んだ内容も対象にする（履歴から貼った内容を続けて貼れるように）。
    /// 除外指定付き（パスワード管理ソフト等）も貼れる（履歴には入れない。書き直すときは同じ指定を付ける）。
    /// 読んだ時点のシーケンス番号も返す（書き直さずに貼るときの直前確認。V2.0.1）。
    /// </summary>
    public Task<PasteSource> ReadCurrentAsync() => Task.Run(ReadCurrent);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        if (_listening) RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            handled = true;
            if (GetClipboardSequenceNumber() != _ownSequence)
            {
                // コピー元が短時間に何度も書き換えることがあるため、少し待ってから最後の状態を 1 回だけ読む。
                _debounce.Stop();
                _debounce.Start();
            }
        }
        return IntPtr.Zero;
    }

    private void StartRead()
    {
        if (_disposed) return;
        if (_reading)
        {
            _readAgain = true;
            return;
        }
        _reading = true;
        var owner = _source.Handle;
        var ownSequence = _ownSequence;
        Task.Run(() => ReadText(owner, ownSequence)).ContinueWith(t =>
        {
            _reading = false;
            if (_disposed) return;
            if (t.IsFaulted) ErrorLog.Write("クリップボードの読み取りに失敗", t.Exception?.GetBaseException());
            else if (!t.Result.Ignore)
            {
                if (t.Result.Text is { } text) TextCopied?.Invoke(text, t.Result.Protection);
                ContentChanged?.Invoke(t.Result.Text);
            }
            if (_readAgain)
            {
                _readAgain = false;
                StartRead();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// 現在のクリップボードからプレーンテキストを読む（スレッドプール）。履歴に入れないものは Text = null。
    /// PlainClip 自身の書き込み・開けなかった場合は Ignore = true（「現在のクリップボード」の印も変えない）。
    /// </summary>
    private static (bool Ignore, string? Text, ClipProtection Protection) ReadText(IntPtr ownWindow, uint ownSequence)
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (attempt > 0) Thread.Sleep(OpenRetryDelayMs);
            if (GetClipboardSequenceNumber() == ownSequence) return (true, null, ClipProtection.None);
            if (!OpenClipboard(IntPtr.Zero)) continue;
            try
            {
                if (GetClipboardOwner() == ownWindow) return (true, null, ClipProtection.None); // PlainClip 自身が書いた内容
                var protection = ReadProtection();
                if (protection.HasFlag(ClipProtection.ExcludeFromHistory)) return (false, null, protection);
                // ファイルのコピー（エクスプローラー）はテキストとして扱わない。
                if (IsClipboardFormatAvailable(CF_HDROP)) return (false, null, protection);
                // CF_TEXT / CF_OEMTEXT しか無い場合も、OS が CF_UNICODETEXT を合成して返す。
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return (false, null, protection);
                return (false, PlainTextNormalizer.Normalize(GetUnicodeText()), protection);
            }
            finally
            {
                CloseClipboard();
            }
        }
        ErrorLog.Write("クリップボードを開けませんでした（読み取り。他のアプリが使用中）");
        return (true, null, ClipProtection.None);
    }

    /// <summary>「クリップボードを書式なしで貼り付け」用に現在の内容を判定する（スレッドプール）。</summary>
    private static PasteSource ReadCurrent()
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (attempt > 0) Thread.Sleep(OpenRetryDelayMs);
            if (!OpenClipboard(IntPtr.Zero)) continue;
            try
            {
                var hasFiles = IsClipboardFormatAvailable(CF_HDROP);
                var hasText = IsClipboardFormatAvailable(CF_UNICODETEXT);
                if (hasFiles || !hasText) return PasteSourceRules.Decide(hasFiles, hasText, null, ClipProtection.None, false);
                var source = PasteSourceRules.Decide(false, true, GetUnicodeText(), ReadProtection(),
                    PasteSourceRules.OnlyPlainText(EnumerateFormats(), MarkerFormats));
                // 本文を読んだ（遅延レンダリングがあれば済んだ）後の番号。開いている間は他のアプリが変えられない。
                return source with { Sequence = GetClipboardSequenceNumber() };
            }
            finally
            {
                CloseClipboard();
            }
        }
        ErrorLog.Write("クリップボードを開けませんでした（貼り付け。他のアプリが使用中）");
        return PasteSource.Unavailable;
    }

    /// <summary>クリップボードにある形式（開いている間だけ呼ぶ）。</summary>
    private static List<uint> EnumerateFormats()
    {
        var formats = new List<uint>();
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0) formats.Add(format);
        return formats;
    }

    /// <summary>
    /// パスワードマネージャー等がコピーに付けた保護指定（Windows の「クリップボードの履歴」と同じ規約。開いている間だけ呼ぶ）。
    /// 履歴に含めない指定と、クラウドへ同期しない指定は独立している（V2.0.1）。値を読めない指定は禁止とみなす。
    /// </summary>
    private static ClipProtection ReadProtection() => ClipProtectionRules.FromMarkers(
        ExcludeFormat != 0 && IsClipboardFormatAvailable(ExcludeFormat),
        ViewerIgnoreFormat != 0 && IsClipboardFormatAvailable(ViewerIgnoreFormat),
        ReadDword(CanIncludeInHistoryFormat),
        ReadDword(CanUploadFormat));

    /// <summary>DWORD の目印（0 = 禁止）を読む。</summary>
    private static MarkerValue ReadDword(uint format)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format)) return MarkerValue.Absent;
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return MarkerValue.Unreadable;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return MarkerValue.Unreadable;
        try
        {
            if ((ulong)GlobalSize(handle) < 4) return MarkerValue.Unreadable;
            return Marshal.ReadInt32(ptr) == 0 ? MarkerValue.Denied : MarkerValue.Allowed;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? GetUnicodeText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return null;
        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            // NUL 終端まで。ただし確保サイズを超えて読まない。
            var maxChars = (long)(ulong)GlobalSize(handle) / 2;
            if (maxChars <= 0) return null;
            var limit = (int)Math.Min(maxChars, PlainTextNormalizer.MaxLength + 1L);
            unsafe
            {
                var span = new ReadOnlySpan<char>((void*)ptr, limit);
                var nul = span.IndexOf('\0');
                return (nul >= 0 ? span[..nul] : span).ToString();
            }
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool PutUnicodeText(string text)
    {
        var bytes = (text.Length + 1) * 2;
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        if (hMem == IntPtr.Zero) return false;
        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        try
        {
            unsafe
            {
                var dest = new Span<char>((void*)ptr, text.Length + 1);
                text.AsSpan().CopyTo(dest);
                dest[^1] = '\0';
            }
        }
        finally
        {
            GlobalUnlock(hMem);
        }
        if (SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero) return true; // 以後の解放はシステムが行う
        GlobalFree(hMem);
        return false;
    }

    private static bool PutDword(uint format, int value)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)4);
        if (hMem == IntPtr.Zero) return false;
        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        Marshal.WriteInt32(ptr, value);
        GlobalUnlock(hMem);
        if (SetClipboardData(format, hMem) != IntPtr.Zero) return true;
        GlobalFree(hMem);
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);
}
