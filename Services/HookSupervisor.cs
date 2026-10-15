using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using PlainClip.Core.Input;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Services;

/// <summary>
/// キー監視の回復（V2.1.0。PROJECT.md「キー監視の回復」）。UI スレッドで動き、いつ登録し直すかは
/// <see cref="HookRecoveryController"/>（Core。単体テストの対象）が決め、実際の登録し直しは <see cref="KeyboardHook.Refresh"/> が行う。
///  - 1 秒ごとのタイマーで予約（登録失敗の再試行・起動後の予防の再登録・復帰時の再登録）を進め、5 秒ごとに受信を確かめる。
///  - スリープからの復帰・ロック解除（SystemEvents）を受けて、まとめて 1 回登録し直す。終了時に購読を外す。
///  - 経過を診断の記録（<see cref="DiagnosticLog"/>）へ書く。「登録に成功した」と「登録後に入力を受け取った」は分けて書く。
/// 回復の処理は診断ログの有無にかかわらず常に動く（ファイルは作らない）。
/// </summary>
public sealed class HookSupervisor : IDisposable
{
    /// <summary>集計を記録する間隔。</summary>
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly KeyboardHook _hook;
    private readonly Dispatcher _dispatcher;
    private readonly Action<HookNotice, int> _notify;
    private readonly HookRecoveryController _controller = new();
    private readonly DispatcherTimer _timer;
    private bool _tap;
    private bool _capture;
    private bool _disposed;

    // 登録後の初回受信の記録用。
    private long _keyboardBase = -1;
    private long _mouseBase = -1;
    private long _registeredAt;
    private HookCheck _lastLoggedCheck = HookCheck.Healthy;

    // 集計の記録用。
    private long _lastSummary;
    private long _lastFlush;
    private long _summaryKeyboard;
    private long _summaryMouse;

    public HookSupervisor(KeyboardHook hook, Dispatcher dispatcher, Action<HookNotice, int> notify)
    {
        _hook = hook;
        _dispatcher = dispatcher;
        _notify = notify;
        _hook.Registered += OnRegistered;
        _lastSummary = _lastFlush = Environment.TickCount64;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>ダブルタップを使うか（設定の反映）。使わなくなったら予約済みの再試行・再登録を取り消す。</summary>
    public void SetDoubleTap(bool enabled, DoubleTapKey key)
    {
        if (_disposed) return;
        _tap = enabled;
        _hook.SetDoubleTap(enabled, key);
        _controller.Configure(_tap, _capture);
    }

    /// <summary>ショートカット入力欄の入力中か。</summary>
    public void SetCapture(bool enabled)
    {
        if (_disposed) return;
        _capture = enabled;
        _hook.SetCapture(enabled);
        _controller.Configure(_tap, _capture);
    }

    /// <summary>起動が終わった（起動後の予防の再登録を予約する）。</summary>
    public void Started() => _controller.Started(Environment.TickCount64);

    /// <summary>トレイの「キー監視を再設定」。監視を使わない設定なら false。結果は通知（<see cref="HookNotice"/>）で返す。</summary>
    public bool ResetManually()
    {
        if (_disposed || _controller.Manual() is not { } request) return false;
        DiagnosticLog.Add("手動の再設定を依頼");
        _hook.Refresh(request.Reason, request.Attempt);
        return true;
    }

    /// <summary>今の状態（診断ログの見出し用。件数と状態だけ）。</summary>
    public string DescribeState()
    {
        var sb = new StringBuilder();
        sb.Append($"状態 ダブルタップ={(_tap ? "使う" : "使わない")} キーボード登録={(_hook.KeyboardInstalled ? "あり" : "なし")}");
        sb.Append($" 受信 キーボード={_hook.KeyboardCallbacks} マウス={_hook.MouseCallbacks} 例外={_hook.CallbackErrors}");
        sb.Append($" 最後の受信から={Elapsed(_hook.LastSeenTick)}ms 最後の入力から={Elapsed(LastSystemInput())}ms");
        var m = _controller.Monitor;
        sb.Append($" 未受信の疑い={m.Suspicions} 検知による再登録={m.Recoveries} 見送り={m.Deferrals} 連続失敗={_controller.Failures}");
        return sb.ToString();
    }

    private void OnRegistered(HookReport report)
    {
        if (_disposed) return;
        var now = Environment.TickCount64;
        DiagnosticLog.Add(report.Describe());
        if (report.Keyboard == HookResult.Installed)
        {
            _keyboardBase = report.KeyboardBase;
            _registeredAt = now;
        }
        if (report.Mouse == HookResult.Installed) _mouseBase = report.MouseBase;
        var notice = _controller.OnReport(report, now);
        if (report.KeyboardFailed)
        {
            DiagnosticLog.Add(notice == HookNotice.Failed
                ? $"登録の再試行を終了（{_controller.Failures} 回失敗）。利用者に案内"
                : $"登録に失敗（{_controller.Failures} 回目）。再試行を予約");
        }
        if (notice != HookNotice.None) _notify(notice, report.KeyboardError);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var now = Environment.TickCount64;
        if (_controller.Tick(now, Snapshot) is { } request)
        {
            DiagnosticLog.Add(request.Reason == HookRefreshReason.InputNotReceived
                ? "システムの入力がフックに届いていないと判定。登録し直す"
                : $"再登録を依頼 理由={request.Reason} 試行={request.Attempt}");
            _hook.Refresh(request.Reason, request.Attempt);
        }
        LogCheckChange();
        LogFirstInput(now);

        if (now - _lastSummary >= SummaryInterval.TotalMilliseconds)
        {
            _lastSummary = now;
            var keyboard = _hook.KeyboardCallbacks;
            var mouse = _hook.MouseCallbacks;
            DiagnosticLog.Add($"集計（{SummaryInterval.TotalMinutes:0} 分） キーボード受信 +{keyboard - _summaryKeyboard} マウス受信 +{mouse - _summaryMouse} / " + DescribeState());
            _summaryKeyboard = keyboard;
            _summaryMouse = mouse;
        }
        if (now - _lastFlush >= FlushInterval.TotalMilliseconds)
        {
            _lastFlush = now;
            DiagnosticLog.Flush();
        }
    }

    /// <summary>確認の結果が変わったときだけ記録する（毎回は書かない）。</summary>
    private void LogCheckChange()
    {
        var check = _controller.LastCheck;
        if (check == _lastLoggedCheck || check == HookCheck.NotObservable) return;
        _lastLoggedCheck = check;
        if (check == HookCheck.Suspect) DiagnosticLog.Add("フックに届いていない入力を検出（次の入力で確かめる）");
        else if (check == HookCheck.Deferred) DiagnosticLog.Add("フックに届いていないが、直前に登録し直したため間隔を空ける");
        else if (check == HookCheck.Healthy) DiagnosticLog.Add("フックへの入力の到達を確認");
    }

    private void LogFirstInput(long now)
    {
        if (_keyboardBase >= 0 && _hook.KeyboardCallbacks > _keyboardBase)
        {
            DiagnosticLog.Add($"登録後に初めてキーボードの入力を受信（登録から約 {now - _registeredAt} ms 以内）");
            _keyboardBase = -1;
        }
        if (_mouseBase >= 0 && _hook.MouseCallbacks > _mouseBase)
        {
            DiagnosticLog.Add("登録後に初めてマウスの入力を受信");
            _mouseBase = -1;
        }
    }

    private HookSnapshot Snapshot() =>
        new(_hook.KeyboardInstalled, _hook.LastSeenTick, LastSystemInput(), IsInputObservable());

    private static uint LastSystemInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : (uint)Environment.TickCount;
    }

    private static long Elapsed(uint tick) => (uint)Environment.TickCount - tick;

    /// <summary>
    /// 今の入力がフックに届くはずの状態か。ロック画面・UAC の画面（入力デスクトップが Default でない）、
    /// 前面が無い・管理者として実行中のウィンドウ（UIPI で通常の権限のフックには届かない）のときは false。
    /// </summary>
    private static bool IsInputObservable()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(64);
            if (!GetUserObjectInformation(desktop, UOI_NAME, name, name.Capacity * 2, out _)) return false;
            if (!string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase)) return false;
        }
        finally
        {
            CloseDesktop(desktop);
        }
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero && !WindowHelper.IsElevatedTarget(foreground);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            DiagnosticLog.Add($"セッションの切り替え: {reason}");
            if (reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect or SessionSwitchReason.SessionLogon)
                _controller.OnSystemEvent(HookRefreshReason.SessionUnlock, Environment.TickCount64);
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        var mode = e.Mode;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            DiagnosticLog.Add($"電源の状態: {mode}");
            if (mode == PowerModes.Resume) _controller.OnSystemEvent(HookRefreshReason.Resume, Environment.TickCount64);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _hook.Registered -= OnRegistered;
        _controller.Dispose();
    }
}
