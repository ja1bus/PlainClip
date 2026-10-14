using System.Runtime.InteropServices;
using System.Windows.Threading;
using PlainClip.Core.Input;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Services;

/// <summary>
/// 低レベルフック（PROJECT.md「ダブルタップ判定方法」「ショートカットの入力と空き確認」）。役割は 2 つ:
///  1. 修飾キー（既定は左 Shift。設定で右 Shift・右 Ctrl・左 Ctrl）のダブルタップ検出（クリップボードを書式なしで貼り付け）。入力は観測するだけで握りつぶさない。
///  2. 設定画面のショートカット入力欄にフォーカスがある間だけ、修飾キー付きのキー入力を横取りして入力欄へ渡す。
///     他アプリのグローバルショートカットは RegisterHotKey の処理より先に低レベルフックへ届くため、
///     使用中の組み合わせも入力欄に入り、「使用中」と判定できる。横取り中の組み合わせは他アプリへ届かない。
///     PlainClip のウィンドウが前面にあるときだけ横取りする。
///
///  - WH_KEYBOARD_LL（キー）と WH_MOUSE_LL（ボタン・ホイールだけを見る。ダブルタップ用）を専用スレッドのメッセージループで導入する。
///  - コールバック内は O(1) の判定だけで、結果は Dispatcher へ渡す。例外はコールバック内で握りつぶす。
///  - PlainClip が SendInput した入力は dwExtraInfo の署名で見分けて完全に無視する。
///  - どちらの役割も不要なときはフック自体を外す。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>PlainClip が SendInput で合成する入力に付ける目印（"PlCp"）。</summary>
    public static readonly UIntPtr InjectionSignature = new(0x506C4370);

    /// <summary>
    /// 開発時の自動テスト専用: 環境変数 PLAINCLIP_TEST_ACCEPT_INJECTED=1 のときだけ、他アプリが合成した入力も実入力として扱う
    /// （SendInput でダブルタップの動作を確かめるため）。PlainClip 自身の入力は常に無視する。
    /// </summary>
    private static readonly bool AcceptInjected = Environment.GetEnvironmentVariable("PLAINCLIP_TEST_ACCEPT_INJECTED") == "1";

    /// <summary>横取り後に Alt / Win 単独押しと解釈されないよう送る、未割当の仮想キー（メニュー・スタートを開かせない）。</summary>
    private const ushort VK_MASK = 0xE8;
    private const int MsgConfigure = WM_APP + 1;
    private const int MsgSendMask = WM_APP + 2;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly LowLevelHookProc _keyboardProc;
    private readonly LowLevelHookProc _mouseProc;
    // フックのスレッドだけが使う。対象キーの変更はフックのスレッドで作り直す（ApplyHooks）。
    private DoubleTapDetector _tap = new(DoubleTapKey.LeftShift);
    private readonly int _ownProcessId = Environment.ProcessId;

    // 押されているキー（フックのスレッドだけが触る）。取りこぼしで残った値は GetAsyncKeyState で確かめて捨てる。
    private readonly bool[] _keyDown = new bool[256];
    // 横取りした主キー（Up も横取りするため）。
    private readonly bool[] _swallowed = new bool[256];

    private int _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private volatile bool _tapEnabled;
    private volatile DoubleTapKey _tapKey = DoubleTapKey.LeftShift;
    private volatile bool _captureEnabled;

    public KeyboardHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _keyboardProc = KeyboardProc; // デリゲートを GC から守るためフィールドで保持する
        _mouseProc = MouseProc;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "PlainClip.KeyboardHook" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>ダブルタップが成立した（UI スレッド）。引数は成立時点のフォアグラウンドウィンドウ。</summary>
    public event Action<IntPtr>? DoubleTapped;

    /// <summary>ショートカット入力中に組み合わせが押された（UI スレッド）。</summary>
    public event Action<Shortcut>? ShortcutCaptured;

    /// <summary>フックの導入に失敗した（UI スレッド。引数はエラーコード）。</summary>
    public event Action<int>? HookFailed;

    /// <summary>ダブルタップの有効・無効と対象キー（UI スレッド）。</summary>
    public void SetDoubleTap(bool enabled, DoubleTapKey key)
    {
        _tapKey = key;
        _tapEnabled = enabled;
        PostThreadMessage(_threadId, MsgConfigure, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>ショートカット入力欄の入力中か（UI スレッド）。</summary>
    public void SetCapture(bool enabled)
    {
        _captureEnabled = enabled;
        PostThreadMessage(_threadId, MsgConfigure, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_threadId != 0 && _thread.IsAlive)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _ready.Dispose();
    }

    private void ThreadMain()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // このスレッドのメッセージキューを作る
        _ready.Set();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == MsgConfigure) ApplyHooks();
                else if (msg.message == MsgSendMask) SendMaskKey();
            }
        }
        finally
        {
            // 終了時に確実に解除する。
            RemoveHooks();
        }
    }

    private void ApplyHooks()
    {
        _tap = new DoubleTapDetector(_tapKey);
        var module = GetModuleHandle(null);
        if (_tapEnabled || _captureEnabled)
        {
            if (_keyboardHook == IntPtr.Zero)
            {
                Array.Clear(_keyDown);
                Array.Clear(_swallowed);
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                if (_keyboardHook == IntPtr.Zero) ReportFailure();
            }
        }
        else if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_tapEnabled)
        {
            if (_mouseHook == IntPtr.Zero)
            {
                // マウスは「Shift+クリック」をタップと誤認しないためだけに使う。失敗してもキー側は動かす。
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
                if (_mouseHook == IntPtr.Zero) ErrorLog.Write($"マウスフックの導入に失敗（エラー {Marshal.GetLastWin32Error()}）");
            }
        }
        else if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
    }

    private void ReportFailure()
    {
        var error = Marshal.GetLastWin32Error();
        ErrorLog.Write($"キーボードフックの導入に失敗（エラー {error}）");
        _dispatcher.BeginInvoke(() => HookFailed?.Invoke(error));
    }

    private unsafe IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                if (OnKeyboard((int)wParam, (KBDLLHOOKSTRUCT*)lParam)) return new IntPtr(1); // 横取り
            }
            catch
            {
                // フック内の例外で入力全体へ影響させない（ログ書き込みもここではしない）。
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>キー入力を処理する。横取りする（他へ渡さない）なら true。</summary>
    private unsafe bool OnKeyboard(int msg, KBDLLHOOKSTRUCT* info)
    {
        var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!isDown && msg is not (WM_KEYUP or WM_SYSKEYUP)) return false;
        if (info->dwExtraInfo == InjectionSignature) return false; // PlainClip 自身の入力

        var vk = (int)(info->vkCode & 0xFF);
        if (_captureEnabled && TryCapture(vk, isDown)) return true;
        if (!_tapEnabled) return false;

        if ((info->flags & LLKHF_INJECTED) != 0 && !AcceptInjected)
        {
            // 他アプリが合成した入力: タップには数えないが、進行中の判定は取り消す。
            _tap.OnOtherInput();
            return false;
        }

        var extended = (info->flags & LLKHF_EXTENDED) != 0;
        var othersHeld = isDown && vk == _tap.TargetVk && AnyOtherInputHeld(vk);
        _keyDown[vk] = isDown;
        if (_tap.OnKey(vk, isDown, extended, info->time, othersHeld))
        {
            var foreground = GetForegroundWindow();
            _dispatcher.BeginInvoke(DispatcherPriority.Input, () => DoubleTapped?.Invoke(foreground));
        }
        return false;
    }

    /// <summary>ショートカット入力中の横取り。PlainClip のウィンドウが前面のときだけ。</summary>
    private bool TryCapture(int vk, bool isDown)
    {
        if (!isDown)
        {
            if (!_swallowed[vk]) return false;
            _swallowed[vk] = false;
            return true;
        }
        var held = HeldModifiers();
        if (!Shortcut.ShouldCapture(vk, held) || !IsOwnForeground()) return false;

        _swallowed[vk] = true;
        var shortcut = new Shortcut(held, vk);
        _dispatcher.BeginInvoke(() => ShortcutCaptured?.Invoke(shortcut));
        // Alt / Win を離したときにメニュー・スタートが開かないよう、フックを抜けた後に目印のキーを送る。
        if ((held & (KeyModifiers.Alt | KeyModifiers.Win)) != 0)
            PostThreadMessage(_threadId, MsgSendMask, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private static KeyModifiers HeldModifiers()
    {
        var m = KeyModifiers.None;
        if (GetAsyncKeyState(0x11) < 0) m |= KeyModifiers.Control;
        if (GetAsyncKeyState(0x12) < 0) m |= KeyModifiers.Alt;
        if (GetAsyncKeyState(0x10) < 0) m |= KeyModifiers.Shift;
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) m |= KeyModifiers.Win;
        return m;
    }

    private bool IsOwnForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == _ownProcessId;
    }

    private static void SendMaskKey()
    {
        var inputs = new INPUT[2];
        for (var i = 0; i < 2; i++)
        {
            inputs[i].type = INPUT_KEYBOARD;
            inputs[i].u.ki = new KEYBDINPUT { wVk = VK_MASK, dwFlags = i == 1 ? KEYEVENTF_KEYUP : 0, dwExtraInfo = InjectionSignature };
        }
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>対象キー以外のキー・マウスボタンが押されているか（対象キーの押下時だけ呼ぶ）。</summary>
    private bool AnyOtherInputHeld(int targetVk)
    {
        for (var vk = 0; vk < _keyDown.Length; vk++)
        {
            if (!_keyDown[vk] || vk == targetVk) continue;
            // フックが Up を取りこぼした（ロック画面・UAC など）値は実際の状態で確かめて捨てる。
            if (GetAsyncKeyState(vk) < 0) return true;
            _keyDown[vk] = false;
        }
        // 1:左 2:右 4:中 5,6:X ボタン
        return GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0 || GetAsyncKeyState(0x04) < 0 ||
               GetAsyncKeyState(0x05) < 0 || GetAsyncKeyState(0x06) < 0;
    }

    private unsafe IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var msg = (int)wParam;
                // 移動は無視（何もしないで次へ渡す）。ボタンの押下とホイールだけ「他の入力」として扱う。
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL &&
                    ((MSLLHOOKSTRUCT*)lParam)->dwExtraInfo != InjectionSignature)
                {
                    _tap.OnOtherInput();
                }
            }
            catch
            {
                // 入力全体へ影響させない。
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
