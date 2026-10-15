namespace PlainClip.Core.Input;

/// <summary>ダブルタップ判定の時間条件（V1 は固定値。PROJECT.md「ダブルタップ判定方法」）。</summary>
public sealed record DoubleTapTiming(uint MaxHoldMs, uint MaxGapMs)
{
    /// <summary>1 回の押下は 350ms 以内（長押しを除外）、1 回目の離しから 2 回目の押下まで 400ms 以内。</summary>
    public static readonly DoubleTapTiming Default = new(350, 400);
}

/// <summary>
/// 対象キーの「離し」の時点での判定結果（V2.1.1。診断ログの集計用。判定そのものには使わない）。
/// </summary>
public enum TapOutcome
{
    None,

    /// <summary>有効な 1 回目のタップ。</summary>
    FirstTap,

    /// <summary>2 回目のタップでダブルタップが成立した。</summary>
    Fired,

    /// <summary>押した時点で他のキー・マウスボタンが押されていた。</summary>
    OthersHeld,

    /// <summary>押している間に他のキー・マウスボタン・ホイールの入力があった。</summary>
    Interrupted,

    /// <summary>押しっぱなし（キーリピート）。</summary>
    KeyRepeat,

    /// <summary>押していた時間が長すぎた。</summary>
    TooLong,

    /// <summary>押下を見ていない離し（押下がフックに届かなかった等）。</summary>
    UpWithoutDown,
}

/// <summary>
/// 1 つの修飾キー（左右の Shift・Ctrl。<see cref="DoubleTapKey"/>）の「単独タップ 2 回」を判定する状態機械。Win32 に依存しない（単体テスト対象）。
/// 低レベルフックのスレッドから、すべてのキー・マウスボタン入力を時刻付きで順に渡す。処理は O(1) で重い処理はしない。
///
/// 成立条件（すべて満たしたときだけ 2 回目の「離し」で true を返す）:
///  - 対象キーの Down → Up が 2 回続き、その間に他のキー・マウスボタン・ホイール入力が 1 つも無い
///  - 各押下の開始時に他のキー・マウスボタンが押されていない（Ctrl を押したまま Shift、など）
///  - 各押下は <see cref="DoubleTapTiming.MaxHoldMs"/> 以内（長押し・キーリピートを除外）
///  - 1 回目の Up から 2 回目の Down まで <see cref="DoubleTapTiming.MaxGapMs"/> 以内
///  - 拡張キーフラグが対象キーの本来の状態と違う入力（Shift・左 Ctrl に拡張フラグ付きで届く「偽の Shift」など）は他のキーとして扱う
/// 成立したら状態を捨てるので、3 回目のタップは新しい 1 回目になる（3 連打で 2 回発動しない）。
/// </summary>
public sealed class DoubleTapDetector
{
    private readonly int _targetVk;
    private readonly bool _targetExtended;
    private readonly DoubleTapTiming _timing;

    private bool _down;
    private bool _pressDirty;
    private TapOutcome _dirtyReason;
    private uint _downTime;
    private bool _hasFirstTap;
    private uint _firstUpTime;

    /// <param name="targetVk">対象キー（左右を区別した仮想キーコード）。</param>
    /// <param name="targetExtended">対象キーが拡張フラグ付きで届くキー（右 Ctrl）なら true。拡張フラグが一致しない入力は他のキーとして扱う。</param>
    public DoubleTapDetector(int targetVk, bool targetExtended = false, DoubleTapTiming? timing = null)
    {
        _targetVk = targetVk;
        _targetExtended = targetExtended;
        _timing = timing ?? DoubleTapTiming.Default;
    }

    public DoubleTapDetector(DoubleTapKey key, DoubleTapTiming? timing = null)
        : this(key.VirtualKey(), key.IsExtended(), timing)
    {
    }

    public int TargetVk => _targetVk;

    /// <summary>直近の対象キーの離しでの判定結果（V2.1.1。診断用）。</summary>
    public TapOutcome LastOutcome { get; private set; }

    /// <summary>
    /// キー入力を 1 つ渡す。
    /// </summary>
    /// <param name="vk">左右を区別した仮想キーコード（WH_KEYBOARD_LL の vkCode）。</param>
    /// <param name="isDown">押下なら true、離しなら false。</param>
    /// <param name="isExtended">LLKHF_EXTENDED。</param>
    /// <param name="time">イベント時刻（ms。GetTickCount 系で 32bit ラップする）。</param>
    /// <param name="othersHeld">このイベントの時点で、対象キー以外のキー・マウスボタンが押されているか。</param>
    /// <returns>ダブルタップが成立したら true。</returns>
    public bool OnKey(int vk, bool isDown, bool isExtended, uint time, bool othersHeld)
    {
        if (vk != _targetVk || isExtended != _targetExtended)
        {
            OnOtherInput();
            return false;
        }

        if (isDown)
        {
            if (_down)
            {
                // 押しっぱなしによるキーリピート。この押下は無効。
                _pressDirty = true;
                _dirtyReason = TapOutcome.KeyRepeat;
                _hasFirstTap = false;
                return false;
            }
            _down = true;
            _downTime = time;
            _pressDirty = othersHeld;
            _dirtyReason = othersHeld ? TapOutcome.OthersHeld : TapOutcome.None;
            if (_hasFirstTap && Elapsed(_firstUpTime, time) > _timing.MaxGapMs) _hasFirstTap = false;
            return false;
        }

        if (!_down)
        {
            // Down を見ていない Up（フック導入前から押されていた等）。数えない。
            _hasFirstTap = false;
            LastOutcome = TapOutcome.UpWithoutDown;
            return false;
        }
        _down = false;

        var validTap = !_pressDirty && Elapsed(_downTime, time) <= _timing.MaxHoldMs;
        if (!validTap)
        {
            _hasFirstTap = false;
            LastOutcome = _pressDirty ? (_dirtyReason == TapOutcome.None ? TapOutcome.Interrupted : _dirtyReason) : TapOutcome.TooLong;
            return false;
        }
        if (_hasFirstTap && Elapsed(_firstUpTime, _downTime) <= _timing.MaxGapMs)
        {
            _hasFirstTap = false;
            LastOutcome = TapOutcome.Fired;
            return true;
        }
        LastOutcome = TapOutcome.FirstTap;
        _hasFirstTap = true;
        _firstUpTime = time;
        return false;
    }

    /// <summary>対象キー以外の入力（他のキー、マウスボタン、ホイール、他アプリが合成した入力）。進行中の判定を取り消す。</summary>
    public void OnOtherInput()
    {
        if (_down)
        {
            _pressDirty = true;
            if (_dirtyReason == TapOutcome.None) _dirtyReason = TapOutcome.Interrupted;
        }
        _hasFirstTap = false;
    }

    /// <summary>状態を初期化する（設定変更・フックの再導入時）。</summary>
    public void Reset()
    {
        _down = false;
        _pressDirty = false;
        _dirtyReason = TapOutcome.None;
        _hasFirstTap = false;
    }

    private static uint Elapsed(uint from, uint to) => unchecked(to - from);
}
