namespace PlainClip.Core.Input;

/// <summary>
/// フックが受け取った入力の集計の項目（V2.1.1。診断ログ用。PROJECT.md「診断ログ」）。
/// 件数だけを数える。キーの種類・並び・入力した文字は数えない（対象キーは設定した 2 回押しのキーの件数だけ）。
/// </summary>
public enum HookStat
{
    /// <summary>実際のキー入力（合成の印が無い）。押下と離しを数える。</summary>
    RealKeys,

    /// <summary>他のプログラムが合成した（または中継した）キー入力（LLKHF_INJECTED）。</summary>
    InjectedKeys,

    /// <summary>PlainClip 自身が送ったキー入力（貼り付けの Ctrl+V など）。</summary>
    OwnKeys,

    /// <summary>他のプログラムが合成したマウス入力（LLMHF_INJECTED）。</summary>
    InjectedMouse,

    /// <summary>2 回押しのキーの実際の押下。</summary>
    TargetRealDowns,

    /// <summary>2 回押しのキーの合成扱いの押下（2 回押しには数えない）。</summary>
    TargetInjectedDowns,

    /// <summary>2 回押しが成立した。</summary>
    Fired,

    /// <summary>有効な 1 回目のタップ。</summary>
    FirstTaps,

    /// <summary>不成立: 押した時点で他のキー・マウスボタンが押されていた。</summary>
    RejectedOthersHeld,

    /// <summary>不成立: 押している間に他の入力があった。</summary>
    RejectedInterrupted,

    /// <summary>不成立: 押しっぱなし。</summary>
    RejectedKeyRepeat,

    /// <summary>不成立: 押していた時間が長すぎた。</summary>
    RejectedTooLong,

    /// <summary>押下を見ていない離し。</summary>
    UpWithoutDown,

    /// <summary>押した時点で「押されている」と判定した他のキー（最近押されたもの）。</summary>
    HeldKey,

    /// <summary>
    /// 押した時点で「押されている」と判定した他のキーのうち、2 秒以上新しい押下（キーリピート）が届いていないもの。
    /// 離しが届かず、押されたままと誤って判定している可能性がある。
    /// </summary>
    HeldStaleKey,

    /// <summary>押した時点で押されていたマウスボタン。</summary>
    HeldMouseButton,
}

/// <summary>2 回押しのキーの押下時に「他の入力が押されている」と判定した理由（V2.1.1。判定は V2.1.0 と同じ）。</summary>
public enum HeldInput
{
    None,
    Key,
    StaleKey,
    MouseButton,
}

/// <summary>集計の扱い（V2.1.1）。</summary>
public static class HookStats
{
    public static readonly int Count = Enum.GetValues<HookStat>().Length;

    /// <summary>キーリピートが止まってからこの時間を過ぎて「押されている」キーは、古い（離しが届いていない）可能性がある。</summary>
    public const int StaleHeldMs = 2000;

    /// <summary>1 分間に実際のキー入力が 0 件で、合成扱いのキー入力がこの件数以上なら「合成扱いだけが届いている」とみなす。</summary>
    public const int OnlyInjectedThreshold = 10;

    public static long[] Delta(IReadOnlyList<long> now, IReadOnlyList<long> before)
    {
        var d = new long[Count];
        for (var i = 0; i < Count; i++) d[i] = now[i] - before[i];
        return d;
    }

    public static HookStat? FromOutcome(TapOutcome outcome) => outcome switch
    {
        TapOutcome.Fired => HookStat.Fired,
        TapOutcome.FirstTap => HookStat.FirstTaps,
        TapOutcome.OthersHeld => HookStat.RejectedOthersHeld,
        TapOutcome.Interrupted => HookStat.RejectedInterrupted,
        TapOutcome.KeyRepeat => HookStat.RejectedKeyRepeat,
        TapOutcome.TooLong => HookStat.RejectedTooLong,
        TapOutcome.UpWithoutDown => HookStat.UpWithoutDown,
        _ => null,
    };

    public static HookStat? FromHeld(HeldInput held) => held switch
    {
        HeldInput.Key => HookStat.HeldKey,
        HeldInput.StaleKey => HookStat.HeldStaleKey,
        HeldInput.MouseButton => HookStat.HeldMouseButton,
        _ => null,
    };

    /// <summary>キー入力があったか（1 分ごとの記録を書くか）。</summary>
    public static bool HasKeyActivity(IReadOnlyList<long> d) =>
        d[(int)HookStat.RealKeys] + d[(int)HookStat.InjectedKeys] + d[(int)HookStat.OwnKeys] > 0;

    /// <summary>実際のキー入力が届かず、合成扱いのキー入力だけが届いている（他のソフトに先にキーを取られている可能性）。</summary>
    public static bool OnlyInjected(IReadOnlyList<long> d) =>
        d[(int)HookStat.RealKeys] == 0 && d[(int)HookStat.InjectedKeys] >= OnlyInjectedThreshold;

    /// <summary>診断ログの 1 行（件数だけ）。</summary>
    public static string Describe(IReadOnlyList<long> d)
    {
        long V(HookStat s) => d[(int)s];
        return $"キー 実入力={V(HookStat.RealKeys)} 合成扱い={V(HookStat.InjectedKeys)} PlainClip={V(HookStat.OwnKeys)}" +
               $" / 2回押しのキー 実押下={V(HookStat.TargetRealDowns)} 合成扱い={V(HookStat.TargetInjectedDowns)}" +
               $" / 成立={V(HookStat.Fired)} 1回目={V(HookStat.FirstTaps)}" +
               $" 不成立(他の入力が押下中={V(HookStat.RejectedOthersHeld)} 途中で他の入力={V(HookStat.RejectedInterrupted)}" +
               $" 押しっぱなし={V(HookStat.RejectedKeyRepeat)} 長押し={V(HookStat.RejectedTooLong)} 押下なしの離し={V(HookStat.UpWithoutDown)})" +
               $" / 押下中と判定 キー={V(HookStat.HeldKey)} 古いキー={V(HookStat.HeldStaleKey)} マウスボタン={V(HookStat.HeldMouseButton)}" +
               $" / マウス合成扱い={V(HookStat.InjectedMouse)}";
    }
}
