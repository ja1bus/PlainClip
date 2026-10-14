using PlainClip.Core.Input;

namespace PlainClip.Tests;

/// <summary>左 Shift / 左 Ctrl のダブルタップ判定（PROJECT.md「ダブルタップ判定方法」の各条件）。</summary>
public class DoubleTapDetectorTests
{
    private const int LShift = 0xA0;
    private const int RShift = 0xA1;
    private const int LCtrl = 0xA2;
    private const int RCtrl = 0xA3;
    private const int A = 0x41;
    private const int C = 0x43;

    /// <summary>時刻を進めながら入力を流し、成立した回数を返す小さなスクリプト。</summary>
    private sealed class Script(int target)
    {
        private readonly DoubleTapDetector _d = new(target);
        private uint _t = 1000;
        public int Fired { get; private set; }

        public Script Down(int vk, uint after = 50, bool ext = false, bool others = false) => Key(vk, true, after, ext, others);
        public Script Up(int vk, uint after = 50, bool ext = false) => Key(vk, false, after, ext, false);
        public Script Tap(int vk, uint hold = 60, uint after = 50) => Down(vk, after).Up(vk, hold);
        public Script Other()
        {
            _d.OnOtherInput();
            return this;
        }

        private Script Key(int vk, bool down, uint after, bool ext, bool others)
        {
            _t += after;
            if (_d.OnKey(vk, down, ext, _t, others)) Fired++;
            return this;
        }
    }

    [Fact]
    public void 左Shiftの単独タップ2回で成立()
    {
        Assert.Equal(1, new Script(LShift).Tap(LShift).Tap(LShift).Fired);
    }

    [Fact]
    public void 左Ctrlの単独タップ2回で成立()
    {
        Assert.Equal(1, new Script(LCtrl).Tap(LCtrl).Tap(LCtrl).Fired);
    }

    [Fact]
    public void 右Shift右Ctrlでは成立しない()
    {
        Assert.Equal(0, new Script(LShift).Tap(RShift).Tap(RShift).Fired);
        Assert.Equal(0, new Script(LCtrl).Tap(RCtrl).Tap(RCtrl).Fired);
    }

    [Fact]
    public void 左右を混ぜても成立しない()
    {
        Assert.Equal(0, new Script(LShift).Tap(LShift).Tap(RShift).Fired);
        Assert.Equal(0, new Script(LShift).Tap(RShift).Tap(LShift).Fired);
    }

    [Fact]
    public void 間隔が400msを超えると成立しない()
    {
        Assert.Equal(0, new Script(LShift).Tap(LShift).Tap(LShift, after: 401).Fired);
        Assert.Equal(1, new Script(LShift).Tap(LShift).Tap(LShift, after: 400).Fired);
    }

    [Fact]
    public void 長押しは数えない()
    {
        Assert.Equal(0, new Script(LShift).Tap(LShift, hold: 351).Tap(LShift).Fired);
        Assert.Equal(0, new Script(LShift).Tap(LShift).Tap(LShift, hold: 351).Fired);
        Assert.Equal(1, new Script(LShift).Tap(LShift, hold: 350).Tap(LShift, hold: 350).Fired);
    }

    [Fact]
    public void キーリピートは数えない()
    {
        // 押しっぱなし（Down が連続）→ 離す → もう一度タップ
        var s = new Script(LShift).Down(LShift).Down(LShift, 30).Down(LShift, 30).Up(LShift, 30).Tap(LShift);
        Assert.Equal(0, s.Fired);
        // 2 回目がリピートを含む
        Assert.Equal(0, new Script(LShift).Tap(LShift).Down(LShift).Down(LShift, 30).Up(LShift, 30).Fired);
    }

    [Fact]
    public void Shift押下中に他のキーを押したら数えない()
    {
        // LShift Down, A, LShift Up（大文字入力）→ 1 回目として数えない
        var s = new Script(LShift).Down(LShift).Down(A).Up(A).Up(LShift).Tap(LShift);
        Assert.Equal(0, s.Fired);
        // 2 回目の押下中に他のキー
        Assert.Equal(0, new Script(LShift).Tap(LShift).Down(LShift).Down(A).Up(A).Up(LShift).Fired);
    }

    [Fact]
    public void CtrlCやCtrlVでは成立しない()
    {
        var s = new Script(LCtrl)
            .Down(LCtrl).Down(C).Up(C).Up(LCtrl)
            .Down(LCtrl).Down(0x56).Up(0x56).Up(LCtrl);
        Assert.Equal(0, s.Fired);
        // Ctrl+C の直後の単独タップ 1 回でも成立しない
        Assert.Equal(0, new Script(LCtrl).Down(LCtrl).Down(C).Up(C).Up(LCtrl).Tap(LCtrl).Fired);
    }

    [Fact]
    public void タップの間に他のキーが入ったら取り消す()
    {
        Assert.Equal(0, new Script(LShift).Tap(LShift).Tap(A).Tap(LShift).Fired);
    }

    [Fact]
    public void 他のキーを押したまま始めた押下は数えない()
    {
        // Ctrl を押したまま Shift（Ctrl+Shift の入力切替など）
        Assert.Equal(0, new Script(LShift).Down(LShift, others: true).Up(LShift).Tap(LShift).Fired);
    }

    [Fact]
    public void マウスボタンやホイールが入ったら取り消す()
    {
        // Shift+クリック を 2 回
        var s = new Script(LShift).Down(LShift).Other().Up(LShift).Down(LShift).Other().Up(LShift);
        Assert.Equal(0, s.Fired);
        // タップの間にクリック
        Assert.Equal(0, new Script(LCtrl).Tap(LCtrl).Other().Tap(LCtrl).Fired);
    }

    [Fact]
    public void 拡張フラグ付きの対象キーは他のキーとして扱う()
    {
        Assert.Equal(0, new Script(LShift).Tap(LShift).Down(LShift, ext: true).Up(LShift, ext: true).Tap(LShift).Fired);
    }

    [Fact]
    public void 三連打では1回だけ成立し四連打で2回()
    {
        Assert.Equal(1, new Script(LShift).Tap(LShift).Tap(LShift).Tap(LShift).Fired);
        Assert.Equal(2, new Script(LShift).Tap(LShift).Tap(LShift).Tap(LShift).Tap(LShift).Fired);
    }

    [Fact]
    public void Downを見ていないUpは数えない()
    {
        Assert.Equal(0, new Script(LShift).Up(LShift).Tap(LShift).Fired);
    }

    [Fact]
    public void 時刻のラップアラウンドでも判定できる()
    {
        var d = new DoubleTapDetector(LShift);
        var t = uint.MaxValue - 100;
        Assert.False(d.OnKey(LShift, true, false, t, false));
        Assert.False(d.OnKey(LShift, false, false, t + 60, false));
        Assert.False(d.OnKey(LShift, true, false, unchecked(t + 150), false));
        Assert.True(d.OnKey(LShift, false, false, unchecked(t + 210), false));
    }

    [Fact]
    public void Resetで進行中の1回目を捨てる()
    {
        var d = new DoubleTapDetector(LShift);
        d.OnKey(LShift, true, false, 100, false);
        d.OnKey(LShift, false, false, 150, false);
        d.Reset();
        d.OnKey(LShift, true, false, 200, false);
        Assert.False(d.OnKey(LShift, false, false, 250, false));
    }
}
