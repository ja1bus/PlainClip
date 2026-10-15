using System.Reflection;
using System.Runtime.CompilerServices;
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
///
/// キー監視の回復（V2.1.0。PROJECT.md「キー監視の回復」）:
///  - Windows は応答の遅いフックを通知なしに外す（LowLevelHooksTimeout）。外されてもハンドルは 0 にならず、ここからは区別できない。
///    <see cref="Refresh"/> はハンドルの有無にかかわらず外して登録し直す（フックのスレッドで行う）。いつ登録し直すかは HookSupervisor が決める。
///  - コールバックは件数と時刻を数えるだけ（Interlocked）。ファイル・UI には触れない。数えた値は HookSupervisor が読んで確認・記録する。
///  - 起動直後の初回呼び出しの遅れで外されないよう、登録の前にコールバックの経路を準備（JIT・呼び出しの橋渡しの生成）しておく。
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
    private const int MsgRefresh = WM_APP + 3;

    /// <summary>キーが押されている間は登録し直すのを待つ間隔と上限（離しだけが他アプリへ渡る・押下の取りこぼしを避ける）。計 5 秒まで。</summary>
    private const uint DeferStepMs = 200;
    private const int MaxDeferSteps = 25;

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

    // 登録し直すのを待っている依頼（フックのスレッドだけが触る）。
    private UIntPtr _deferTimer;
    private HookRefreshReason _deferReason;
    private int _deferAttempt;
    private int _deferSteps;

    // コールバックの集計（コールバックで更新し、UI スレッドで読む）。時刻は Environment.TickCount（GetTickCount と同じ基準）。
    private long _keyboardCallbacks;
    private long _mouseCallbacks;
    private long _callbackErrors;
    private int _lastKeyboardTick;
    private int _lastMouseTick;
    private int _registeredTick;
    private volatile bool _keyboardInstalled;

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

    /// <summary>フックの登録・解除の結果（UI スレッド）。失敗も含む。</summary>
    public event Action<HookReport>? Registered;

    /// <summary>キーボードのフックを登録済みか（ハンドルがあるだけで、入力を受け取れている証拠ではない）。</summary>
    public bool KeyboardInstalled => _keyboardInstalled;

    public long KeyboardCallbacks => Interlocked.Read(ref _keyboardCallbacks);

    public long MouseCallbacks => Interlocked.Read(ref _mouseCallbacks);

    /// <summary>コールバックの中で起きた例外の件数（入力全体へ影響させないため、コールバックでは握りつぶして数えるだけ）。</summary>
    public long CallbackErrors => Interlocked.Read(ref _callbackErrors);

    /// <summary>フックが最後に入力を受け取った時刻。登録後にまだ無ければ登録した時刻（GetTickCount 系）。</summary>
    public uint LastSeenTick
    {
        get
        {
            var latest = (uint)Volatile.Read(ref _registeredTick);
            var keyboard = (uint)Volatile.Read(ref _lastKeyboardTick);
            var mouse = (uint)Volatile.Read(ref _lastMouseTick);
            if ((int)(keyboard - latest) > 0) latest = keyboard;
            if ((int)(mouse - latest) > 0) latest = mouse;
            return latest;
        }
    }

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

    /// <summary>
    /// 今のフックを外して登録し直す（UI スレッドから。処理はフックのスレッド）。ハンドルが残っていても作り直す。
    /// 監視が不要な設定なら何も登録しない。キーが押されている間は、離されるまで少し待つ（最大約 5 秒）。
    /// </summary>
    public void Refresh(HookRefreshReason reason, int attempt) =>
        PostThreadMessage(_threadId, MsgRefresh, (IntPtr)(int)reason, (IntPtr)attempt);

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
        // コールバックが他の処理に待たされて Windows の待ち時間を超えないよう、優先度を上げる（メッセージを待つ間は CPU を使わない）。
        Thread.CurrentThread.Priority = ThreadPriority.Highest;
        _ready.Set();
        DiagnosticLog.Add("キー監視のスレッドを開始");
        Prewarm();
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == MsgConfigure) ApplyHooks(HookRefreshReason.Configure, 0, force: false, 0);
                else if (msg.message == MsgRefresh) RequestRefresh((HookRefreshReason)(int)msg.wParam, (int)msg.lParam);
                else if (msg.message == WM_TIMER && msg.hwnd == IntPtr.Zero && (nuint)msg.wParam == (nuint)_deferTimer) OnDeferTimer();
                else if (msg.message == MsgSendMask) SendMaskKey();
            }
        }
        finally
        {
            // 終了時に確実に解除する。
            if (_deferTimer != UIntPtr.Zero) KillTimer(IntPtr.Zero, _deferTimer);
            _deferTimer = UIntPtr.Zero;
            RemoveHooks();
            DiagnosticLog.Add("キー監視のスレッドを終了");
        }
    }

    /// <summary>
    /// 登録の前に、コールバックから呼ぶメソッドのコンパイルと、ネイティブからの呼び出しの橋渡しを済ませておく。
    /// 起動直後（特にサインイン直後の混雑時）の初回のコールバックが遅れ、Windows にフックを外されるのを防ぐ。入力は合成しない。
    /// </summary>
    private void Prewarm()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            foreach (var name in new[] { nameof(KeyboardProc), nameof(MouseProc), nameof(OnKeyboard), nameof(TryCapture), nameof(HeldModifiers), nameof(IsOwnForeground), nameof(AnyOtherInputHeld) })
                RuntimeHelpers.PrepareMethod(typeof(KeyboardHook).GetMethod(name, flags)!.MethodHandle);
            foreach (var name in new[] { nameof(DoubleTapDetector.OnKey), nameof(DoubleTapDetector.OnOtherInput) })
                RuntimeHelpers.PrepareMethod(typeof(DoubleTapDetector).GetMethod(name)!.MethodHandle);
            Marshal.GetFunctionPointerForDelegate(_keyboardProc);
            Marshal.GetFunctionPointerForDelegate(_mouseProc);
            // P/Invoke の準備と、判定の処理を一通り通しておく（捨てる判定器で。入力は送らない）。
            GetAsyncKeyState(0x10);
            GetForegroundWindow();
            var warm = new DoubleTapDetector(_tapKey);
            warm.OnKey(warm.TargetVk, true, false, 1, false);
            warm.OnKey(warm.TargetVk, false, false, 2, false);
            warm.OnOtherInput();
        }
        catch (Exception ex)
        {
            // 準備できなくても動作は変わらない（初回が遅くなりうるだけ）。
            DiagnosticLog.Add($"コールバックの事前準備に失敗: {ex.GetType().Name}");
        }
    }

    /// <summary>登録し直す依頼。キーが押されている間は少し待つ。待っている間に届いた依頼は 1 回にまとめる。</summary>
    private void RequestRefresh(HookRefreshReason reason, int attempt)
    {
        _deferReason = reason;
        _deferAttempt = attempt;
        if (_deferTimer != UIntPtr.Zero) return; // 待っている依頼に含める
        _deferSteps = 0;
        OnDeferTimer();
    }

    private void OnDeferTimer()
    {
        if (_deferTimer != UIntPtr.Zero)
        {
            KillTimer(IntPtr.Zero, _deferTimer);
            _deferTimer = UIntPtr.Zero;
        }
        if (_deferSteps < MaxDeferSteps && InputHeld())
        {
            _deferSteps++;
            _deferTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, DeferStepMs, IntPtr.Zero);
            if (_deferTimer != UIntPtr.Zero) return;
        }
        ApplyHooks(_deferReason, _deferAttempt, force: true, (int)(_deferSteps * DeferStepMs));
    }

    /// <summary>修飾キーが押されている、またはショートカット入力で横取りしたキーの離しを待っているか。</summary>
    private bool InputHeld()
    {
        if (Array.IndexOf(_swallowed, true) >= 0) return true;
        return GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 ||
               GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
    }

    /// <summary>
    /// 設定に合わせてフックを足し引きする。force のときは、ハンドルが残っていても外して登録し直す
    /// （ダブルタップの判定と押下状態は初期化する。ショートカット入力で横取りしたキーの記録は残し、その離しも横取りする）。
    /// </summary>
    private void ApplyHooks(HookRefreshReason reason, int attempt, bool force, int deferredMs)
    {
        _tap = new DoubleTapDetector(_tapKey);
        var module = GetModuleHandle(null);
        var wantKeyboard = _tapEnabled || _captureEnabled;
        var wantMouse = _tapEnabled;
        if (force)
        {
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            _keyboardHook = _mouseHook = IntPtr.Zero;
            Array.Clear(_keyDown);
        }

        HookResult keyboard, mouse;
        int keyboardError = 0, mouseError = 0;
        if (wantKeyboard)
        {
            if (_keyboardHook == IntPtr.Zero)
            {
                if (!force)
                {
                    Array.Clear(_keyDown);
                    Array.Clear(_swallowed);
                }
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
                keyboardError = _keyboardHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                keyboard = _keyboardHook == IntPtr.Zero ? HookResult.Failed : HookResult.Installed;
            }
            else keyboard = HookResult.Kept;
        }
        else if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            Array.Clear(_swallowed);
            keyboard = HookResult.Removed;
        }
        else keyboard = HookResult.NotNeeded;

        if (wantMouse)
        {
            if (_mouseHook == IntPtr.Zero)
            {
                // マウスは「Shift+クリック」をタップと誤認しないためだけに使う。失敗してもキー側は動かす。
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
                mouseError = _mouseHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                mouse = _mouseHook == IntPtr.Zero ? HookResult.Failed : HookResult.Installed;
            }
            else mouse = HookResult.Kept;
        }
        else if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            mouse = HookResult.Removed;
        }
        else mouse = HookResult.NotNeeded;

        _keyboardInstalled = _keyboardHook != IntPtr.Zero;
        if (keyboard == HookResult.Installed || mouse == HookResult.Installed) Volatile.Write(ref _registeredTick, Environment.TickCount);
        if (keyboardError != 0) ErrorLog.Write($"キーボードフックの導入に失敗（エラー {keyboardError}、理由 {reason}、試行 {attempt}）");
        if (mouseError != 0) ErrorLog.Write($"マウスフックの導入に失敗（エラー {mouseError}、理由 {reason}）");

        // 設定の反映で何も変わらなかったときは知らせない。
        if (!force && keyboard is HookResult.Kept or HookResult.NotNeeded && mouse is HookResult.Kept or HookResult.NotNeeded) return;
        var report = new HookReport(reason, attempt, keyboard, keyboardError, mouse, mouseError, deferredMs, KeyboardCallbacks, MouseCallbacks);
        _dispatcher.BeginInvoke(() => Registered?.Invoke(report));
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = _mouseHook = IntPtr.Zero;
        _keyboardInstalled = false;
    }

    private unsafe IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 受け取った証拠として件数と時刻だけを数える（入力の内容は残さない）。
        Interlocked.Increment(ref _keyboardCallbacks);
        Volatile.Write(ref _lastKeyboardTick, Environment.TickCount);
        if (nCode >= 0)
        {
            try
            {
                if (OnKeyboard((int)wParam, (KBDLLHOOKSTRUCT*)lParam)) return new IntPtr(1); // 横取り
            }
            catch
            {
                // フック内の例外で入力全体へ影響させない（ログ書き込みもここではしない。件数だけ数える）。
                Interlocked.Increment(ref _callbackErrors);
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
        Interlocked.Increment(ref _mouseCallbacks);
        Volatile.Write(ref _lastMouseTick, Environment.TickCount);
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
                Interlocked.Increment(ref _callbackErrors);
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
