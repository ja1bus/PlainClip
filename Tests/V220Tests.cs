using PlainClip.Core.Diagnostics;
using PlainClip.Core.Input;
using PlainClip.Core.Settings;

namespace PlainClip.Tests;

/// <summary>
/// V2.2.0: 設定画面の［適用］は変更があるときだけ押せる（<see cref="AppSettings.SameChoicesAs"/>）。
/// 記録を止めた診断ログは、最後に書いてから 7 日で削除する（<see cref="DiagnosticMode.ShouldDelete"/>）。
/// </summary>
public sealed class V220Tests
{
    // --- 設定の変更の有無 ---

    [Fact]
    public void Same_settings_have_no_changes()
    {
        var a = new AppSettings();
        Assert.True(a.SameChoicesAs(a.Clone()));
    }

    [Fact]
    public void Each_setting_on_the_screen_is_a_change()
    {
        var baseline = new AppSettings();
        var changes = new Action<AppSettings>[]
        {
            s => s.PasteLatestTrigger = TriggerMode.Disabled,
            s => s.DoubleTapKey = DoubleTapKey.LeftCtrl,
            s => s.ShowHistoryTrigger = TriggerMode.Shortcut,
            s => s.PasteLatestShortcut = "Ctrl+Alt+P",
            s => s.ShowHistoryShortcut = "Ctrl+Alt+H",
            s => s.NumberPasteModifiers = NumberPaste.Format(NumberPaste.Choices.First(m => m != KeyModifiers.None)),
            s => s.MaxHistoryCount = baseline.MaxHistoryCount + 5,
            s => s.MergeDuplicates = !baseline.MergeDuplicates,
            s => s.Language = "ja",
        };
        foreach (var change in changes)
        {
            var changed = baseline.Clone();
            change(changed);
            Assert.False(baseline.SameChoicesAs(changed));
        }
    }

    [Fact]
    public void Spelling_unusable_values_and_diagnostic_period_are_not_changes()
    {
        var a = new AppSettings { PasteLatestShortcut = "Ctrl+Alt+P", Language = "" };
        var b = a.Clone();
        b.PasteLatestShortcut = Shortcut.TryParse("ctrl+alt+p", out var parsed) ? parsed.ToString() : "ctrl+alt+p";
        b.Language = "  ";
        b.DiagnosticLogUntil = DateTime.UtcNow;
        Assert.True(a.SameChoicesAs(b));

        // 使えないショートカット（Shift だけ＋文字）は保存時に空欄になるので、空欄と同じ。
        var c = new AppSettings { ShowHistoryShortcut = "Shift+A" };
        Assert.True(c.SameChoicesAs(new AppSettings { ShowHistoryShortcut = "" }));
    }

    // --- 記録を止めた後の診断ログの削除 ---

    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Log_is_deleted_seven_days_after_the_last_write()
    {
        Assert.Equal(TimeSpan.FromDays(7), DiagnosticMode.Retention);
        Assert.False(DiagnosticMode.ShouldDelete(false, Now - TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1), Now));
        Assert.True(DiagnosticMode.ShouldDelete(false, Now - TimeSpan.FromDays(7), Now));
        Assert.Equal(Now + TimeSpan.FromDays(7), DiagnosticMode.DeleteAt(Now));
    }

    [Fact]
    public void Log_is_not_deleted_while_recording_or_when_absent()
    {
        Assert.False(DiagnosticMode.ShouldDelete(true, Now - TimeSpan.FromDays(30), Now));
        Assert.False(DiagnosticMode.ShouldDelete(false, null, Now));
    }

    [Fact]
    public void Log_written_unnaturally_in_the_future_is_deleted()
    {
        // 時計を戻した場合などに、残り続けないようにする。
        Assert.False(DiagnosticMode.ShouldDelete(false, Now + TimeSpan.FromHours(1), Now));
        Assert.True(DiagnosticMode.ShouldDelete(false, Now + TimeSpan.FromDays(2), Now));
    }
}
