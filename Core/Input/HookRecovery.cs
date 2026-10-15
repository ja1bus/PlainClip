namespace PlainClip.Core.Input;

/// <summary>キー監視（低レベルフック）を登録し直す理由（V2.1.0。PROJECT.md「キー監視の回復」）。診断ログにも書く。</summary>
public enum HookRefreshReason
{
    /// <summary>設定の反映（必要なフックだけを足し引きする。登録済みのフックは作り直さない）。</summary>
    Configure,

    /// <summary>登録に失敗した後の再試行。</summary>
    RetryAfterFailure,

    /// <summary>起動後に一度だけ行う予防の再登録（故障を検出したわけではない）。</summary>
    StartupRefresh,

    /// <summary>システムには入力があったのに、フックに届いていないと判定した。</summary>
    InputNotReceived,

    /// <summary>セッションのロック解除・接続。</summary>
    SessionUnlock,

    /// <summary>スリープ・休止状態からの復帰。</summary>
    Resume,

    /// <summary>トレイの「キー監視を再設定」。</summary>
    Manual,
}

/// <summary>1 つのフックの登録結果。</summary>
public enum HookResult
{
    /// <summary>使わない設定なので入っていない。</summary>
    NotNeeded,

    /// <summary>登録済みのまま。</summary>
    Kept,

    Installed,
    Failed,

    /// <summary>使わなくなったので外した。</summary>
    Removed,
}

/// <summary>
/// フックのスレッドで行った登録の結果（UI スレッドへ渡す）。エラーは失敗直後の Win32 エラー番号。
/// KeyboardBase / MouseBase は登録した時点のコールバックの累計（その後に増えれば、登録後に入力を受け取った証拠）。
/// </summary>
public sealed record HookReport(
    HookRefreshReason Reason,
    int Attempt,
    HookResult Keyboard,
    int KeyboardError,
    HookResult Mouse,
    int MouseError,
    int DeferredMs,
    long KeyboardBase,
    long MouseBase)
{
    public bool KeyboardFailed => Keyboard == HookResult.Failed;

    public bool KeyboardActive => Keyboard is HookResult.Installed or HookResult.Kept;

    /// <summary>診断ログの 1 行（数値と種類だけ。入力内容は含まない）。</summary>
    public string Describe() =>
        $"登録 理由={Reason} 試行={Attempt} キーボード={Keyboard}{Error(KeyboardError)} マウス={Mouse}{Error(MouseError)}" +
        (DeferredMs > 0 ? $" 待機={DeferredMs}ms" : "");

    private static string Error(int error) => error == 0 ? "" : $"(エラー {error})";
}

/// <summary>監視の確認に使う、その時点の状態（UI スレッドで集める）。時刻は GetTickCount 系の 32 ビット値。</summary>
/// <param name="KeyboardInstalled">キーボードのフックを登録済みか（ハンドルがあるだけで、受信できている証拠ではない）。</param>
/// <param name="LastSeenTick">フックが最後に入力を受け取った時刻。まだ無ければ登録した時刻。</param>
/// <param name="LastSystemInputTick">システム全体で最後に入力があった時刻（GetLastInputInfo）。</param>
/// <param name="Observable">今の入力がフックに届くはずの状態か（前面が管理者のウィンドウ・ロック画面・UAC の画面のときは false）。</param>
public readonly record struct HookSnapshot(bool KeyboardInstalled, uint LastSeenTick, uint LastSystemInputTick, bool Observable);

/// <summary>監視の確認の結果。</summary>
public enum HookCheck
{
    Healthy,

    /// <summary>届いていない入力が 1 回分ある。次の入力でも届かなければ回復する（1 回だけでは判定しない）。</summary>
    Suspect,

    /// <summary>登録し直す。</summary>
    Recover,

    /// <summary>届いていないが、直前に回復したばかりなので間隔を空ける。</summary>
    Deferred,

    /// <summary>入力がフックに届かない状態（管理者のウィンドウ・ロック画面など）なので判定しない。</summary>
    NotObservable,
}

/// <summary>
/// フックが無通知で外された（または入力が届かない）ことの間接的な検知（V2.1.0）。
/// Windows はフックを外しても通知しない。そこで「システムには入力があったのに、フックには届いていない」ことを根拠にする。
/// 長い間入力が無いことだけでは判定しない。1 回の取りこぼし（前面が管理者のウィンドウだった直後など）では回復せず、
/// その後の新しい入力も届かなかったときだけ回復する。回復は間隔を空けて繰り返し、短い間隔で無制限に登録し直さない。
/// </summary>
public sealed class HookLivenessMonitor
{
    /// <summary>
    /// システムの最終入力時刻が、フックの最終受信時刻よりこれだけ新しければ「届いていない」とみなす。
    /// コールバックは入力の処理中に同期で呼ばれるため通常は差が出ない。時刻の分解能（約 16ms）と処理の遅れを十分に上回る値にする。
    /// </summary>
    public const int MissMarginMs = 2000;

    /// <summary>
    /// 検知による回復の間隔（回数ごと）。最初はすぐ、以後は 1 分・5 分・15 分・30 分（以後 30 分ごと）。
    /// 誤検知（タッチ入力など、フックを通らない入力）が続いても 1 時間に数回の再登録にとどめる。
    /// </summary>
    public static readonly TimeSpan[] RecoveryIntervals =
        [TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];

    /// <summary>この時間、検知による回復が無ければ間隔を最初に戻す。</summary>
    public static readonly TimeSpan ResetAfter = TimeSpan.FromHours(1);

    private uint? _suspectInput;
    private long? _lastRecovery;
    private int _level;

    public int Suspicions { get; private set; }

    public int Recoveries { get; private set; }

    public int Deferrals { get; private set; }

    /// <summary>判定する。nowMs は単調な時計（Environment.TickCount64）。</summary>
    public HookCheck Check(long nowMs, HookSnapshot s)
    {
        if (_lastRecovery is { } last && nowMs - last >= ResetAfter.TotalMilliseconds) _level = 0;
        if (!s.KeyboardInstalled)
        {
            _suspectInput = null;
            return HookCheck.Healthy; // 登録されていない状態は、登録失敗の再試行の側で扱う
        }
        if (!s.Observable)
        {
            // 管理者のウィンドウ・ロック画面への入力はフックに届かないのが正常。疑いも取り消す。
            _suspectInput = null;
            return HookCheck.NotObservable;
        }
        var missed = (int)(s.LastSystemInputTick - s.LastSeenTick) > MissMarginMs;
        if (!missed)
        {
            _suspectInput = null;
            return HookCheck.Healthy;
        }
        if (_suspectInput is not { } suspect || (int)(s.LastSystemInputTick - suspect) <= 0)
        {
            // 初めて見つけた、または疑ってから新しい入力がまだ無い。
            if (_suspectInput is null) Suspicions++;
            _suspectInput ??= s.LastSystemInputTick;
            return HookCheck.Suspect;
        }
        // 疑った後の新しい入力も届いていない。
        var interval = RecoveryIntervals[Math.Min(_level, RecoveryIntervals.Length - 1)];
        if (_lastRecovery is { } previous && nowMs - previous < interval.TotalMilliseconds)
        {
            Deferrals++;
            return HookCheck.Deferred;
        }
        _level++;
        _lastRecovery = nowMs;
        _suspectInput = null;
        Recoveries++;
        return HookCheck.Recover;
    }
}

/// <summary>登録し直す依頼（理由と、登録失敗の再試行なら何回目か）。</summary>
public readonly record struct HookRefreshRequest(HookRefreshReason Reason, int Attempt);

/// <summary>利用者へ知らせること。</summary>
public enum HookNotice
{
    None,

    /// <summary>再試行しても登録できなかった（回復の方法と、通常ショートカットへの切り替えを案内する）。</summary>
    Failed,

    /// <summary>手動の再設定が成功した。</summary>
    ManualDone,
}

/// <summary>
/// キー監視の回復の予定を決める（V2.1.0。UI・Win32 に依存しない。時刻は引数で受け取る）。
/// UI スレッドから 1 秒ごとに <see cref="Tick"/> を呼び、返された依頼をフックのスレッドへ渡す。
///  - 登録失敗: 1・3・5 秒後に再試行（計 3 回）。それでも失敗したら知らせる。
///  - 起動後 20 秒で一度だけ予防の再登録。
///  - 復帰・ロック解除: 2 秒間まとめてから 1 回だけ再登録。
///  - 5 秒ごとに <see cref="HookLivenessMonitor"/> で確認。
///  - 監視が不要な設定（ダブルタップもショートカット入力も使わない）・終了後は何もしない。予約も取り消す。
/// </summary>
public sealed class HookRecoveryController
{
    /// <summary>
    /// 登録に失敗したときの再試行の間隔。サインイン直後はデスクトップの準備・他の常駐アプリの起動で一時的に失敗しうるため、
    /// すぐ（1 秒）・少し待って（3 秒）・さらに待って（5 秒）の 3 回、計 9 秒まで試す。それ以上待っても直る見込みは低く、
    /// 利用者へ早めに知らせて手動の再設定・通常ショートカットへの切り替えを選べるようにする。
    /// </summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5)];

    /// <summary>
    /// 起動後に一度だけ行う予防の再登録までの時間。サインイン直後の自動起動では、他の常駐アプリ・セキュリティ製品の起動と重なり、
    /// 最初の数回のコールバックが遅れてフックが外されやすい。混雑が落ち着く頃に一度だけ登録し直す。
    /// </summary>
    public static readonly TimeSpan StartupRefreshDelay = TimeSpan.FromSeconds(20);

    /// <summary>復帰・ロック解除の通知をまとめる時間（復帰ではロック解除・電源の通知が続けて届く）。</summary>
    public static readonly TimeSpan SystemEventSettle = TimeSpan.FromSeconds(2);

    /// <summary>届いているかを確かめる間隔。GetLastInputInfo と時刻の比較だけで、負荷はほぼ無い。</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly HookLivenessMonitor _monitor = new();
    private bool _tap;
    private bool _capture;
    private bool _disposed;
    private int _failures;
    private long? _retryAt;
    private long? _startupRefreshAt;
    private bool _startupRefreshUsed;
    private long? _settleAt;
    private HookRefreshReason _settleReason;
    private long? _lastCheck;
    private bool _manualPending;

    public HookLivenessMonitor Monitor => _monitor;

    /// <summary>直近の連続した登録失敗の回数。</summary>
    public int Failures => _failures;

    /// <summary>最後の確認結果（診断用）。</summary>
    public HookCheck LastCheck { get; private set; } = HookCheck.Healthy;

    private bool Needed => !_disposed && (_tap || _capture);

    public bool HasPendingWork => _retryAt is not null || _startupRefreshAt is not null || _settleAt is not null;

    /// <summary>設定の反映（ダブルタップを使うか・ショートカット入力中か）。不要になった予約は取り消す。</summary>
    public void Configure(bool tap, bool capture)
    {
        if (_disposed) return;
        _tap = tap;
        _capture = capture;
        if (!Needed)
        {
            _retryAt = null;
            _settleAt = null;
            _failures = 0;
        }
        if (!_tap)
        {
            // 予防の再登録は起動直後の 1 回だけ。無効にしたら取り消し、後から有効にしても予約し直さない。
            _startupRefreshAt = null;
            _startupRefreshUsed = true;
        }
    }

    /// <summary>起動が終わった（予防の再登録を予約する）。</summary>
    public void Started(long nowMs)
    {
        if (_disposed || !_tap || _startupRefreshUsed) return;
        _startupRefreshUsed = true;
        _startupRefreshAt = nowMs + (long)StartupRefreshDelay.TotalMilliseconds;
    }

    /// <summary>フックのスレッドからの登録結果。</summary>
    public HookNotice OnReport(HookReport report, long nowMs)
    {
        if (_disposed) return HookNotice.None;
        var manual = report.Reason == HookRefreshReason.Manual || _manualPending && report.Reason == HookRefreshReason.RetryAfterFailure;
        if (report.KeyboardFailed)
        {
            _failures++;
            if (Needed && _failures <= RetryDelays.Length)
            {
                _retryAt = nowMs + (long)RetryDelays[_failures - 1].TotalMilliseconds;
                return HookNotice.None;
            }
            _retryAt = null;
            _manualPending = false;
            return Needed ? HookNotice.Failed : HookNotice.None;
        }
        if (report.KeyboardActive)
        {
            _failures = 0;
            _retryAt = null;
            if (manual)
            {
                _manualPending = false;
                return HookNotice.ManualDone;
            }
        }
        return HookNotice.None;
    }

    /// <summary>スリープからの復帰・ロック解除。少し待ってまとめて 1 回だけ登録し直す。</summary>
    public void OnSystemEvent(HookRefreshReason reason, long nowMs)
    {
        if (!Needed) return;
        _settleReason = reason;
        _settleAt = nowMs + (long)SystemEventSettle.TotalMilliseconds;
    }

    /// <summary>トレイの「キー監視を再設定」。登録し直すなら依頼を返す（監視を使わない設定なら null）。</summary>
    public HookRefreshRequest? Manual()
    {
        if (!Needed) return null;
        // 手動のときは失敗の回数を数え直し、再試行の機会を改めて与える。他の予約はこの再登録に含まれる。
        _failures = 0;
        _retryAt = null;
        _settleAt = null;
        _manualPending = true;
        return new HookRefreshRequest(HookRefreshReason.Manual, 0);
    }

    /// <summary>
    /// 1 秒ごとに呼ぶ。今行う再登録があれば返す（1 回の呼び出しで高々 1 件。重なった予約はその 1 件にまとめる）。
    /// snapshot は確認の時期だけ呼ばれる。
    /// </summary>
    public HookRefreshRequest? Tick(long nowMs, Func<HookSnapshot> snapshot)
    {
        if (!Needed) return null;

        if (_retryAt is { } retry && nowMs >= retry)
        {
            _retryAt = null;
            _settleAt = null; // 再試行の登録でまとめて済む
            return new HookRefreshRequest(HookRefreshReason.RetryAfterFailure, _failures);
        }
        if (_retryAt is not null) return null; // 失敗の再試行中は、他の理由の再登録をしない

        if (_settleAt is { } settle && nowMs >= settle)
        {
            _settleAt = null;
            if (_startupRefreshAt is not null && _settleReason is HookRefreshReason.SessionUnlock or HookRefreshReason.Resume)
                _startupRefreshAt = null; // 直後に重ねて登録し直さない
            return new HookRefreshRequest(_settleReason, 0);
        }
        if (_startupRefreshAt is { } startup && nowMs >= startup)
        {
            _startupRefreshAt = null;
            return _tap ? new HookRefreshRequest(HookRefreshReason.StartupRefresh, 0) : null;
        }

        if (!_tap || _lastCheck is { } lastCheck && nowMs - lastCheck < CheckInterval.TotalMilliseconds) return null;
        _lastCheck = nowMs;
        LastCheck = _monitor.Check(nowMs, snapshot());
        return LastCheck == HookCheck.Recover ? new HookRefreshRequest(HookRefreshReason.InputNotReceived, 0) : null;
    }

    /// <summary>終了。以後は何も返さない。</summary>
    public void Dispose()
    {
        _disposed = true;
        _retryAt = null;
        _startupRefreshAt = null;
        _settleAt = null;
    }
}
