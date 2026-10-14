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

    /// <summary>テキストのコピーを検知した（UI スレッド。正規化済みの本文）。除外指定付きのものは届かない。</summary>
    public event Action<string>? TextCopied;

    /// <summary>
    /// 他のアプリによってクリップボードの内容が変わった（UI スレッド）。引数は履歴に入れるテキストならその本文、
    /// それ以外（画像・ファイル・除外指定付き等）は null。履歴一覧の「現在のクリップボード」の印に使う。
    /// </summary>
    public event Action<string?>? ContentChanged;

    public bool IsListening => _listening;

    /// <summary>
    /// Unicode プレーンテキストをクリップボードへ設定する（UI スレッド）。他プロセスが開いていれば短時間リトライする。
    /// <paramref name="sensitive"/> なら「履歴に残さない」指定（Windows のクリップボードの履歴・クラウド同期の対象外）も付ける。
    /// 成功したら true。
    /// </summary>
    public async Task<bool> SetTextAsync(string text, bool sensitive = false)
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (attempt > 0) await Task.Delay(OpenRetryDelayMs);
            if (!OpenClipboard(_source.Handle)) continue;
            var ok = false;
            try
            {
                ok = EmptyClipboard() && PutUnicodeText(text) && (!sensitive || PutSensitiveMarkers());
            }
            finally
            {
                CloseClipboard();
            }
            // 自分の書き込みによる WM_CLIPBOARDUPDATE を履歴に入れないため、書き込み後の番号を覚える。
            _ownSequence = GetClipboardSequenceNumber();
            if (ok) return true;
            ErrorLog.Write($"クリップボードへの書き込みに失敗（エラー {Marshal.GetLastWin32Error()}）");
            return false;
        }
        ErrorLog.Write("クリップボードを開けませんでした（他のアプリが使用中）");
        return false;
    }

    /// <summary>
    /// 現在のクリップボードを読んで、書式なしで貼れるか判定する（スレッドプールで読む）。
    /// PlainClip 自身が書き込んだ内容も対象にする（履歴から貼った内容を続けて貼れるように）。
    /// 除外指定付き（パスワード管理ソフト等）も貼れる（履歴には入れない。書き直すときは同じ指定を付ける）。
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
                if (t.Result.Text is { } text) TextCopied?.Invoke(text);
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
    private static (bool Ignore, string? Text) ReadText(IntPtr ownWindow, uint ownSequence)
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (attempt > 0) Thread.Sleep(OpenRetryDelayMs);
            if (GetClipboardSequenceNumber() == ownSequence) return (true, null);
            if (!OpenClipboard(IntPtr.Zero)) continue;
            try
            {
                if (GetClipboardOwner() == ownWindow) return (true, null); // PlainClip 自身が書いた内容
                if (IsExcluded()) return (false, null);
                // ファイルのコピー（エクスプローラー）はテキストとして扱わない。
                if (IsClipboardFormatAvailable(CF_HDROP)) return (false, null);
                // CF_TEXT / CF_OEMTEXT しか無い場合も、OS が CF_UNICODETEXT を合成して返す。
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return (false, null);
                return (false, PlainTextNormalizer.Normalize(GetUnicodeText()));
            }
            finally
            {
                CloseClipboard();
            }
        }
        ErrorLog.Write("クリップボードを開けませんでした（読み取り。他のアプリが使用中）");
        return (true, null);
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
                if (hasFiles || !hasText) return PasteSourceRules.Decide(hasFiles, hasText, null, false, false);
                return PasteSourceRules.Decide(false, true, GetUnicodeText(), IsExcluded(),
                    PasteSourceRules.OnlyPlainText(EnumerateFormats(), MarkerFormats));
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
    /// パスワードマネージャー等が「履歴に残さない」と指定したコピーか（Windows の「クリップボードの履歴」と同じ規約）。
    /// </summary>
    private static bool IsExcluded()
    {
        if (ExcludeFormat != 0 && IsClipboardFormatAvailable(ExcludeFormat)) return true;
        if (ViewerIgnoreFormat != 0 && IsClipboardFormatAvailable(ViewerIgnoreFormat)) return true;
        if (CanIncludeInHistoryFormat != 0 && IsClipboardFormatAvailable(CanIncludeInHistoryFormat))
        {
            // DWORD 0 = 履歴に含めない。
            var handle = GetClipboardData(CanIncludeInHistoryFormat);
            if (handle == IntPtr.Zero) return false;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return false;
            try
            {
                return (ulong)GlobalSize(handle) >= 4 && Marshal.ReadInt32(ptr) == 0;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        return false;
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

    /// <summary>
    /// 「履歴に残さない」指定を付ける（パスワード管理ソフト等の内容を書き直すとき）。Windows の「クリップボードの履歴」と同じ規約:
    /// ExcludeClipboardContentFromMonitorProcessing、CanIncludeInClipboardHistory = 0、CanUploadToCloudClipboard = 0。
    /// </summary>
    private static bool PutSensitiveMarkers()
    {
        var ok = true;
        foreach (var format in new[] { ExcludeFormat, CanIncludeInHistoryFormat, CanUploadFormat })
        {
            if (format != 0) ok &= PutDword(format, 0);
        }
        return ok;
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
