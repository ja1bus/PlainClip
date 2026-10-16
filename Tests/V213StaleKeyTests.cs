using PlainClip.Core.Input;

namespace PlainClip.Tests;

/// <summary>
/// V2.1.3: 離しが失われて押されたままになったキー（キーリピートが 2 秒以上届いていない古いキー）は、2 回押しを妨げない。
/// 問題の PC の V2.1.1 の診断ログでは、2 回押しのキーの実押下がすべて「他の入力が押下中（古いキー）」で不成立になっていた。
/// </summary>
public sealed class V213StaleKeyTests
{
    private const int Shift = 0xA0;

    [Fact]
    public void Key_is_stale_only_after_the_threshold()
    {
        Assert.False(HookStats.IsStale(10_000, 10_000));
        Assert.False(HookStats.IsStale(10_000 + HookStats.StaleHeldMs, 10_000));
        Assert.True(HookStats.IsStale(10_000 + HookStats.StaleHeldMs + 1, 10_000));
    }

    [Fact]
    public void Staleness_survives_tick_count_wraparound()
    {
        // Environment.TickCount は約 24.9 日で負へ回り込む。
        Assert.False(HookStats.IsStale(int.MinValue + 100, int.MaxValue - 100));
        Assert.True(HookStats.IsStale(int.MinValue + HookStats.StaleHeldMs + 100, int.MaxValue - 100));
    }

    [Fact]
    public void Only_recent_keys_and_mouse_buttons_block_a_tap()
    {
        Assert.True(HookStats.Blocks(HeldInput.Key));
        Assert.True(HookStats.Blocks(HeldInput.MouseButton));
        Assert.False(HookStats.Blocks(HeldInput.StaleKey));
        Assert.False(HookStats.Blocks(HeldInput.None));
    }

    [Fact]
    public void Double_tap_fires_when_only_a_stale_key_is_held()
    {
        var d = new DoubleTapDetector(DoubleTapKey.LeftShift);
        var held = HookStats.Blocks(HeldInput.StaleKey);
        d.OnKey(Shift, true, false, 0, held);
        d.OnKey(Shift, false, false, 80, false);
        d.OnKey(Shift, true, false, 200, held);
        Assert.True(d.OnKey(Shift, false, false, 280, false));
        Assert.Equal(TapOutcome.Fired, d.LastOutcome);
    }

    [Fact]
    public void Double_tap_is_still_rejected_while_a_recent_key_is_held()
    {
        var d = new DoubleTapDetector(DoubleTapKey.LeftShift);
        var held = HookStats.Blocks(HeldInput.Key);
        d.OnKey(Shift, true, false, 0, held);
        d.OnKey(Shift, false, false, 80, false);
        d.OnKey(Shift, true, false, 200, held);
        Assert.False(d.OnKey(Shift, false, false, 280, false));
        Assert.Equal(TapOutcome.OthersHeld, d.LastOutcome);
    }

    [Fact]
    public void Log_line_lists_stale_keys_as_cleared_not_as_held()
    {
        var d = new long[HookStats.Count];
        d[(int)HookStat.HeldStaleKey] = 3;
        var text = HookStats.Describe(d);
        Assert.Contains("押下の記録を消した 古いキー=3", text);
        Assert.Contains("押下中と判定 キー=0 マウスボタン=0", text);
    }
}
