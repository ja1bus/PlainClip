using PlainClip.Core.History;
using PlainClip.Core.Input;
using PlainClip.Core.Text;

namespace PlainClip.Tests;

/// <summary>
/// コピー元の保護指定（V2.0.1。2026-10-14 のコードレビュー指摘 1・3）。履歴に含めない指定とクラウド同期の禁止は独立していて、
/// 書き直すときに弱めない。
/// </summary>
public class ClipProtectionTests
{
    [Fact]
    public void 指定が無ければ保護しない()
    {
        Assert.Equal(ClipProtection.None, ClipProtectionRules.FromMarkers(false, false, MarkerValue.Absent, MarkerValue.Absent));
        Assert.Equal(ClipProtection.None, ClipProtectionRules.FromMarkers(false, false, MarkerValue.Allowed, MarkerValue.Allowed));
    }

    [Fact]
    public void 履歴は許可_クラウド同期だけ禁止を読み落とさない()
    {
        // レビュー指摘 1 の条件: CanIncludeInClipboardHistory = 1、CanUploadToCloudClipboard = 0。
        var p = ClipProtectionRules.FromMarkers(false, false, MarkerValue.Allowed, MarkerValue.Denied);
        Assert.Equal(ClipProtection.NoCloudUpload, p);
        Assert.False(p.HasFlag(ClipProtection.ExcludeFromHistory)); // PlainClip の履歴には入れてよい
    }

    [Theory]
    [InlineData(true, false, MarkerValue.Absent)]
    [InlineData(false, true, MarkerValue.Absent)]
    [InlineData(false, false, MarkerValue.Denied)]
    public void 履歴に含めない指定はクラウド同期の禁止も含む(bool exclude, bool viewerIgnore, MarkerValue canInclude)
    {
        var p = ClipProtectionRules.FromMarkers(exclude, viewerIgnore, canInclude, MarkerValue.Absent);
        Assert.Equal(ClipProtection.ExcludeFromHistory | ClipProtection.NoCloudUpload, p);
    }

    [Fact]
    public void 値を読めない指定は禁止とみなす()
    {
        Assert.True(ClipProtectionRules.FromMarkers(false, false, MarkerValue.Unreadable, MarkerValue.Absent).HasFlag(ClipProtection.ExcludeFromHistory));
        Assert.Equal(ClipProtection.NoCloudUpload, ClipProtectionRules.FromMarkers(false, false, MarkerValue.Absent, MarkerValue.Unreadable));
    }

    [Fact]
    public void 書き込む目印は読んだ指定を弱めない()
    {
        Assert.Equal((false, false, false), ClipProtectionRules.MarkersToWrite(ClipProtection.None));
        // クラウド同期の禁止だけ: 履歴は許可されているので、CanUploadToCloudClipboard = 0 だけを付ける。
        Assert.Equal((false, false, true), ClipProtectionRules.MarkersToWrite(ClipProtection.NoCloudUpload));
        // 履歴に含めない: Windows の「クリップボードの履歴」と同じ 3 つ。
        Assert.Equal((true, true, true), ClipProtectionRules.MarkersToWrite(ClipProtection.ExcludeFromHistory));
        Assert.Equal((true, true, true), ClipProtectionRules.MarkersToWrite(ClipProtection.ExcludeFromHistory | ClipProtection.NoCloudUpload));
    }

    [Fact]
    public void 現在のクリップボードを書き直すときも同期禁止を引き継ぐ()
    {
        // HTML 付き（書き直しが必要）で、クラウド同期だけ禁止。
        var s = PasteSourceRules.Decide(false, true, "LOCAL-ONLY", ClipProtection.NoCloudUpload, onlyPlainText: false);
        Assert.True(s.NeedsRewrite);
        Assert.Equal(ClipProtection.NoCloudUpload, s.Protection);
        Assert.False(s.Sensitive);
        Assert.Equal((false, false, true), ClipProtectionRules.MarkersToWrite(s.Protection));
    }

    [Fact]
    public void 履歴は同期禁止を保持し_履歴から書き直すときに付け直せる()
    {
        var h = new ClipHistory();
        h.Add("LOCAL-ONLY", ClipProtection.NoCloudUpload);
        h.Add("normal");
        var item = h.ItemAt(2)!;
        Assert.Equal("LOCAL-ONLY", item.Text);
        Assert.Equal(ClipProtection.NoCloudUpload, item.Protection);
        Assert.Equal(ClipProtection.None, h.ItemAt(1)!.Protection);
    }

    [Fact]
    public void 同じ本文を指定なしで再度コピーしても保護指定は弱めない()
    {
        var h = new ClipHistory();
        h.Add("same", ClipProtection.NoCloudUpload);
        h.Add("same"); // 直前と同じ
        Assert.Equal(ClipProtection.NoCloudUpload, h.Latest!.Protection);

        h.Add("other");
        h.Add("same"); // 重複をまとめて先頭へ
        Assert.Equal(1, h.Items.Count(i => i.Text == "same"));
        Assert.Equal(ClipProtection.NoCloudUpload, h.Latest!.Protection);
    }

    [Fact]
    public void 同じ本文を同期禁止付きで再度コピーしたら強める()
    {
        var h = new ClipHistory();
        h.Add("same");
        h.Add("other");
        h.Add("same", ClipProtection.NoCloudUpload);
        Assert.Equal(ClipProtection.NoCloudUpload, h.Latest!.Protection);

        h.Add("same"); // 直前と同じでも弱めない
        Assert.Equal(ClipProtection.NoCloudUpload, h.Latest!.Protection);
    }
}

/// <summary>貼り付けの直前確認（V2.0.1。レビュー指摘 2）。待っている間にクリップボードが変わったら Ctrl+V を送らない。</summary>
public class PasteGuardTests
{
    [Fact]
    public void 書き込んだときのままなら送る() => Assert.True(PasteGuard.ClipboardUnchanged(41, 41));

    [Fact]
    public void 待っている間に変わったら送らない() => Assert.False(PasteGuard.ClipboardUnchanged(41, 42));

    [Fact]
    public void 番号が不明なら送らない() => Assert.False(PasteGuard.ClipboardUnchanged(0, 0));

    [Fact]
    public void 書き直さずに貼るときは読んだときの番号を引き継ぐ()
    {
        var s = PasteSourceRules.Decide(false, true, "plain", ClipProtection.None, onlyPlainText: true) with { Sequence = 77 };
        Assert.False(s.NeedsRewrite);
        Assert.True(PasteGuard.ClipboardUnchanged(s.Sequence, 77));
        Assert.False(PasteGuard.ClipboardUnchanged(s.Sequence, 78)); // 読んだ後に HTML・画像等へ変わった
    }
}

/// <summary>ショートカットの入力欄の Esc（V2.0.1。レビュー指摘 5）。</summary>
public class ShortcutCancelKeyTests
{
    private const int VK_ESCAPE = 0x1B;

    [Fact]
    public void 修飾なしのEscはキャンセルに渡す() => Assert.True(Shortcut.IsCancelKey(VK_ESCAPE, KeyModifiers.None));

    [Theory]
    [InlineData(KeyModifiers.Shift)]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt)]
    public void 修飾付きのEscは値として扱う(KeyModifiers modifiers) => Assert.False(Shortcut.IsCancelKey(VK_ESCAPE, modifiers));

    [Fact]
    public void Esc以外は値として扱う() => Assert.False(Shortcut.IsCancelKey('A', KeyModifiers.None));

    [Fact]
    public void 修飾なしのEscはフックでも横取りしない()
    {
        // 横取りしないので、入力欄（WPF）へ届き、IsCancel のボタンが閉じる。
        Assert.False(Shortcut.ShouldCapture(VK_ESCAPE, KeyModifiers.None));
    }
}
