using PlainClip.Core.Input;

namespace PlainClip.Tests;

/// <summary>
/// V2.1.1: 2 回押しが成立しなかった理由と、入力の種類（実入力・合成扱い）の集計（診断ログ用）。
/// 判定そのもの（成立するか）は V2.1.0 と同じであることも確かめる。
/// </summary>
public sealed class V211DiagnosticsTests
{
    private const int Shift = 0xA0;
    private const int A = 0x41;

    private static DoubleTapDetector Detector() => new(DoubleTapKey.LeftShift);

    [Fact]
    public void Successful_double_tap_reports_first_tap_then_fired()
    {
        var d = Detector();
        d.OnKey(Shift, true, false, 0, false);
        Assert.False(d.OnKey(Shift, false, false, 80, false));
        Assert.Equal(TapOutcome.FirstTap, d.LastOutcome);
        d.OnKey(Shift, true, false, 200, false);
        Assert.True(d.OnKey(Shift, false, false, 280, false));
        Assert.Equal(TapOutcome.Fired, d.LastOutcome);
    }

    [Fact]
    public void Press_while_other_input_is_held_reports_others_held()
    {
        var d = Detector();
        d.OnKey(Shift, true, false, 0, othersHeld: true);
        Assert.False(d.OnKey(Shift, false, false, 80, false));
        Assert.Equal(TapOutcome.OthersHeld, d.LastOutcome);
    }

    [Fact]
    public void Other_input_during_the_press_reports_interrupted()
    {
        var d = Detector();
        d.OnKey(Shift, true, false, 0, false);
        d.OnKey(A, true, false, 30, false);
        Assert.False(d.OnKey(Shift, false, false, 80, false));
        Assert.Equal(TapOutcome.Interrupted, d.LastOutcome);

        var m = Detector();
        m.OnKey(Shift, true, false, 0, false);
        m.OnOtherInput(); // マウスボタン・ホイール
        m.OnKey(Shift, false, false, 80, false);
        Assert.Equal(TapOutcome.Interrupted, m.LastOutcome);
    }

    [Fact]
    public void Key_repeat_and_long_press_are_reported()
    {
        var d = Detector();
        d.OnKey(Shift, true, false, 0, false);
        d.OnKey(Shift, true, false, 30, false);
        d.OnKey(Shift, false, false, 60, false);
        Assert.Equal(TapOutcome.KeyRepeat, d.LastOutcome);

        var l = Detector();
        l.OnKey(Shift, true, false, 0, false);
        l.OnKey(Shift, false, false, 1000, false);
        Assert.Equal(TapOutcome.TooLong, l.LastOutcome);
    }

    [Fact]
    public void Up_without_down_is_reported()
    {
        var d = Detector();
        d.OnKey(Shift, false, false, 0, false);
        Assert.Equal(TapOutcome.UpWithoutDown, d.LastOutcome);
    }

    [Fact]
    public void Reason_is_cleared_for_the_next_press_and_reset()
    {
        var d = Detector();
        d.OnKey(Shift, true, false, 0, othersHeld: true);
        d.OnKey(Shift, false, false, 80, false);
        Assert.Equal(TapOutcome.OthersHeld, d.LastOutcome);
        d.OnKey(Shift, true, false, 1000, false);
        d.OnKey(Shift, false, false, 1080, false);
        Assert.Equal(TapOutcome.FirstTap, d.LastOutcome);
        d.Reset();
        d.OnKey(Shift, true, false, 2000, false);
        d.OnKey(A, true, false, 2010, false);
        d.OnKey(Shift, false, false, 2080, false);
        Assert.Equal(TapOutcome.Interrupted, d.LastOutcome);
    }

    [Fact]
    public void Every_outcome_and_held_kind_maps_to_a_stat()
    {
        foreach (var o in Enum.GetValues<TapOutcome>().Where(o => o != TapOutcome.None)) Assert.NotNull(HookStats.FromOutcome(o));
        foreach (var h in Enum.GetValues<HeldInput>().Where(h => h != HeldInput.None)) Assert.NotNull(HookStats.FromHeld(h));
        Assert.Null(HookStats.FromOutcome(TapOutcome.None));
        Assert.Null(HookStats.FromHeld(HeldInput.None));
    }

    [Fact]
    public void Only_injected_needs_no_real_keys_and_enough_injected_keys()
    {
        var d = new long[HookStats.Count];
        d[(int)HookStat.InjectedKeys] = HookStats.OnlyInjectedThreshold;
        Assert.True(HookStats.OnlyInjected(d));
        d[(int)HookStat.RealKeys] = 1;
        Assert.False(HookStats.OnlyInjected(d));
        d[(int)HookStat.RealKeys] = 0;
        d[(int)HookStat.InjectedKeys] = HookStats.OnlyInjectedThreshold - 1;
        Assert.False(HookStats.OnlyInjected(d));
        Assert.True(HookStats.HasKeyActivity(d));
        Assert.False(HookStats.HasKeyActivity(new long[HookStats.Count]));
    }

    [Fact]
    public void Delta_and_description_contain_only_counts()
    {
        var before = new long[HookStats.Count];
        var now = new long[HookStats.Count];
        for (var i = 0; i < now.Length; i++) now[i] = i + 10;
        before[(int)HookStat.RealKeys] = 4;
        var delta = HookStats.Delta(now, before);
        Assert.Equal(6, delta[(int)HookStat.RealKeys]);
        var text = HookStats.Describe(delta);
        Assert.StartsWith("キー 実入力=6 合成扱い=11 PlainClip=12", text);
        Assert.Contains("古いキー=24", text);
        Assert.DoesNotContain('\n', text);
        // 項目の値以外の数字（キーコードなど）を含まない: 数字の並びはすべて集計値。
        var numbers = System.Text.RegularExpressions.Regex.Matches(text, @"\d+").Select(m => long.Parse(m.Value)).ToList();
        Assert.Equal(HookStats.Count + 2, numbers.Count); // 項目ごとの値＋見出しの「2回押し」の 2 と「1回目」の 1
    }
}
