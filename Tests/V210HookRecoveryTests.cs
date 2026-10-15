using System.Text;
using PlainClip.Core.Diagnostics;
using PlainClip.Core.Input;
using PlainClip.Core.Settings;

namespace PlainClip.Tests;

/// <summary>
/// V2.1.0: キー監視の回復（登録失敗の再試行・起動後の予防の再登録・復帰時の再登録・届いていないことの検知・手動の再設定）と診断ログ。
/// OS（フック・SystemEvents・GetLastInputInfo）に触れない判定部分（Core）を、時刻を与えて確かめる。
/// </summary>
public sealed class V210HookRecoveryTests
{
    private static HookReport Report(HookRefreshReason reason, HookResult keyboard, int error = 0, int attempt = 0) =>
        new(reason, attempt, keyboard, error, HookResult.Installed, 0, 0, 0, 0);

    private static readonly HookSnapshot Quiet = new(true, 1000, 1000, true);

    private static HookRefreshRequest? Tick(HookRecoveryController c, long now, HookSnapshot? s = null) => c.Tick(now, () => s ?? Quiet);

    // --- 登録失敗の再試行 ---

    [Fact]
    public void Failed_registration_is_retried_after_1_3_5_seconds_then_reported()
    {
        var c = new HookRecoveryController();
        c.Configure(tap: true, capture: false);

        Assert.Equal(HookNotice.None, c.OnReport(Report(HookRefreshReason.Configure, HookResult.Failed, 5), 0));
        Assert.Null(Tick(c, 999));
        Assert.Equal(new HookRefreshRequest(HookRefreshReason.RetryAfterFailure, 1), Tick(c, 1000));

        Assert.Equal(HookNotice.None, c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Failed, 5, 1), 1000));
        Assert.Null(Tick(c, 3999));
        Assert.Equal(new HookRefreshRequest(HookRefreshReason.RetryAfterFailure, 2), Tick(c, 4000));

        Assert.Equal(HookNotice.None, c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Failed, 5, 2), 4000));
        Assert.Equal(new HookRefreshRequest(HookRefreshReason.RetryAfterFailure, 3), Tick(c, 9000));

        // 4 回目の失敗で再試行をやめ、利用者に知らせる。以後は自動で登録し直さない（短い間隔で繰り返さない）。
        Assert.Equal(HookNotice.Failed, c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Failed, 5, 3), 9000));
        Assert.False(c.HasPendingWork);
        for (var t = 9000; t < 120_000; t += 1000) Assert.Null(Tick(c, t, new HookSnapshot(false, 0, 0, true)));
    }

    [Fact]
    public void Retry_success_resets_the_failure_count()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.OnReport(Report(HookRefreshReason.Configure, HookResult.Failed, 5), 0);
        Assert.NotNull(Tick(c, 1000));
        Assert.Equal(HookNotice.None, c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Installed, 0, 1), 1000));
        Assert.Equal(0, c.Failures);
        Assert.False(c.HasPendingWork);
    }

    [Fact]
    public void Disabling_monitoring_cancels_pending_retries_and_never_refreshes()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.Started(0);
        c.OnReport(Report(HookRefreshReason.Configure, HookResult.Failed, 5), 0);
        c.OnSystemEvent(HookRefreshReason.Resume, 0);

        c.Configure(tap: false, capture: false);
        Assert.False(c.HasPendingWork);
        Assert.Null(c.Manual());
        c.OnSystemEvent(HookRefreshReason.SessionUnlock, 100);
        // 届いていない入力があっても、監視を使わない設定では登録し直さない。
        var missing = new HookSnapshot(true, 0, 100_000, true);
        for (var t = 0; t < 300_000; t += 1000) Assert.Null(Tick(c, t, missing));
    }

    [Fact]
    public void Capture_only_retries_registration_but_does_not_check_liveness()
    {
        var c = new HookRecoveryController();
        c.Configure(tap: false, capture: true);
        c.OnReport(Report(HookRefreshReason.Configure, HookResult.Failed, 5), 0);
        Assert.Equal(HookRefreshReason.RetryAfterFailure, Tick(c, 1000)?.Reason);
        c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Installed, 0, 1), 1000);
        var called = false;
        for (var t = 2000; t < 60_000; t += 1000) Assert.Null(c.Tick(t, () => { called = true; return Quiet; }));
        Assert.False(called);
    }

    // --- 起動後の予防の再登録 ---

    [Fact]
    public void Startup_refresh_happens_once_after_20_seconds()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.Started(0);
        c.Started(5000); // 2 回目の予約はしない
        Assert.Null(Tick(c, 19_999));
        Assert.Equal(new HookRefreshRequest(HookRefreshReason.StartupRefresh, 0), Tick(c, 20_000));
        for (var t = 21_000; t < 200_000; t += 1000) Assert.NotEqual(HookRefreshReason.StartupRefresh, Tick(c, t)?.Reason);
    }

    [Fact]
    public void Startup_refresh_is_cancelled_when_double_tap_is_turned_off_and_not_rescheduled()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.Started(0);
        c.Configure(false, false);
        c.Configure(true, false);
        c.Started(1000);
        for (var t = 0; t < 60_000; t += 1000) Assert.NotEqual(HookRefreshReason.StartupRefresh, Tick(c, t)?.Reason);
    }

    [Fact]
    public void Startup_refresh_is_not_scheduled_when_double_tap_is_off_at_start()
    {
        var c = new HookRecoveryController();
        c.Configure(false, false);
        c.Started(0);
        Assert.False(c.HasPendingWork);
    }

    // --- 復帰・ロック解除 ---

    [Fact]
    public void Burst_of_resume_and_unlock_events_becomes_one_refresh()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.OnSystemEvent(HookRefreshReason.Resume, 10_000);
        c.OnSystemEvent(HookRefreshReason.SessionUnlock, 10_500);
        c.OnSystemEvent(HookRefreshReason.SessionUnlock, 11_000);
        Assert.Null(Tick(c, 12_999));
        Assert.Equal(new HookRefreshRequest(HookRefreshReason.SessionUnlock, 0), Tick(c, 13_000));
        Assert.Null(Tick(c, 14_000));
        Assert.False(c.HasPendingWork);
    }

    [Fact]
    public void Resume_during_startup_window_replaces_the_startup_refresh()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.Started(0);
        c.OnSystemEvent(HookRefreshReason.Resume, 5000);
        Assert.Equal(HookRefreshReason.Resume, Tick(c, 7000)?.Reason);
        for (var t = 8000; t < 60_000; t += 1000) Assert.NotEqual(HookRefreshReason.StartupRefresh, Tick(c, t)?.Reason);
    }

    // --- 手動の再設定 ---

    [Fact]
    public void Manual_reset_reports_done_on_success()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        Assert.Equal(new HookRefreshRequest(HookRefreshReason.Manual, 0), c.Manual());
        Assert.Equal(HookNotice.ManualDone, c.OnReport(Report(HookRefreshReason.Manual, HookResult.Installed), 0));
    }

    [Fact]
    public void Manual_reset_failure_is_retried_and_the_retry_success_is_reported_as_done()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.Manual();
        Assert.Equal(HookNotice.None, c.OnReport(Report(HookRefreshReason.Manual, HookResult.Failed, 5), 0));
        Assert.Equal(HookRefreshReason.RetryAfterFailure, Tick(c, 1000)?.Reason);
        Assert.Equal(HookNotice.ManualDone, c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Installed, 0, 1), 1000));
    }

    [Fact]
    public void Manual_reset_after_retries_ran_out_gets_a_fresh_set_of_retries()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        for (var i = 0; i < 4; i++) c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Failed, 5, i), 0);
        Assert.Equal(4, c.Failures);
        c.Manual();
        Assert.Equal(0, c.Failures);
        Assert.Equal(HookNotice.None, c.OnReport(Report(HookRefreshReason.Manual, HookResult.Failed, 5), 0));
        Assert.True(c.HasPendingWork);
    }

    // --- 終了 ---

    [Fact]
    public void After_dispose_nothing_is_scheduled_or_reported()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        c.Started(0);
        c.OnReport(Report(HookRefreshReason.Configure, HookResult.Failed, 5), 0);
        c.OnSystemEvent(HookRefreshReason.Resume, 0);
        c.Dispose();
        Assert.False(c.HasPendingWork);
        Assert.Null(c.Manual());
        Assert.Equal(HookNotice.None, c.OnReport(Report(HookRefreshReason.RetryAfterFailure, HookResult.Failed, 5), 0));
        c.OnSystemEvent(HookRefreshReason.SessionUnlock, 0);
        for (var t = 0; t < 60_000; t += 1000) Assert.Null(Tick(c, t, new HookSnapshot(true, 0, 50_000, true)));
    }

    // --- 届いていないことの検知 ---

    [Fact]
    public void Input_reaching_the_hook_is_healthy_and_long_idle_is_not_a_failure()
    {
        var m = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Healthy, m.Check(0, new HookSnapshot(true, 5000, 5000, true)));
        // 何時間も入力が無いだけでは故障と判定しない。
        Assert.Equal(HookCheck.Healthy, m.Check(10_000_000, new HookSnapshot(true, 5000, 5000, true)));
        Assert.Equal(0, m.Suspicions);
    }

    [Fact]
    public void One_missed_input_is_only_a_suspicion_and_a_later_missed_input_recovers()
    {
        var m = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Suspect, m.Check(0, new HookSnapshot(true, 1000, 5000, true)));
        // 新しい入力が無ければ疑いのまま。
        Assert.Equal(HookCheck.Suspect, m.Check(5000, new HookSnapshot(true, 1000, 5000, true)));
        Assert.Equal(HookCheck.Recover, m.Check(10_000, new HookSnapshot(true, 1000, 9000, true)));
        Assert.Equal(1, m.Recoveries);
    }

    [Fact]
    public void Suspicion_is_cleared_when_the_hook_sees_input_again()
    {
        var m = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Suspect, m.Check(0, new HookSnapshot(true, 1000, 5000, true)));
        Assert.Equal(HookCheck.Healthy, m.Check(5000, new HookSnapshot(true, 9000, 9000, true)));
        Assert.Equal(HookCheck.Suspect, m.Check(10_000, new HookSnapshot(true, 9000, 20_000, true)));
        Assert.Equal(0, m.Recoveries);
    }

    [Fact]
    public void Input_to_elevated_window_or_lock_screen_is_not_judged()
    {
        var m = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Suspect, m.Check(0, new HookSnapshot(true, 1000, 5000, true)));
        // 前面が管理者のウィンドウ・ロック画面の間は判定せず、疑いも取り消す。
        Assert.Equal(HookCheck.NotObservable, m.Check(5000, new HookSnapshot(true, 1000, 9000, false)));
        Assert.Equal(HookCheck.Suspect, m.Check(10_000, new HookSnapshot(true, 1000, 9000, true)));
        Assert.Equal(0, m.Recoveries);
    }

    [Fact]
    public void Small_differences_within_the_margin_are_not_missed_input()
    {
        var m = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Healthy, m.Check(0, new HookSnapshot(true, 1000, 1000 + HookLivenessMonitor.MissMarginMs, true)));
        Assert.Equal(HookCheck.Suspect, m.Check(0, new HookSnapshot(true, 1000, 1001 + HookLivenessMonitor.MissMarginMs, true)));
    }

    [Fact]
    public void Not_installed_is_left_to_the_retry_logic()
    {
        var m = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Healthy, m.Check(0, new HookSnapshot(false, 0, 100_000, true)));
    }

    [Fact]
    public void Tick_count_wraparound_is_handled()
    {
        var m = new HookLivenessMonitor();
        const uint nearMax = uint.MaxValue - 500;
        Assert.Equal(HookCheck.Healthy, m.Check(0, new HookSnapshot(true, nearMax, 1000, true) with { LastSeenTick = 500 }));
        Assert.Equal(HookCheck.Suspect, m.Check(0, new HookSnapshot(true, nearMax, 3000, true)));
    }

    [Fact]
    public void Repeated_detection_backs_off_and_resets_after_an_hour()
    {
        var m = new HookLivenessMonitor();
        uint input = 10_000;
        long now = 0;
        HookCheck Missed()
        {
            input += 3000;
            now += 5000;
            return m.Check(now, new HookSnapshot(true, 0, input, true));
        }
        // 1 回目はすぐ回復する。
        Assert.Equal(HookCheck.Suspect, Missed());
        Assert.Equal(HookCheck.Recover, Missed());
        var recoveredAt = now;
        // 2 回目は 1 分空ける。
        Assert.Equal(HookCheck.Suspect, Missed());
        while (now - recoveredAt + 5000 < 60_000) Assert.Equal(HookCheck.Deferred, Missed());
        Assert.Equal(HookCheck.Recover, Missed());
        Assert.Equal(2, m.Recoveries);

        // 続けて届かない場合でも、1 時間あたりの回復は数回に収まる。
        var before = m.Recoveries;
        var start = now;
        while (now - start < 3_600_000) Missed();
        Assert.InRange(m.Recoveries - before, 1, 4);

        // 1 時間回復が無ければ間隔は最初に戻る。
        var fresh = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Suspect, fresh.Check(0, new HookSnapshot(true, 0, 5000, true)));
        Assert.Equal(HookCheck.Recover, fresh.Check(5000, new HookSnapshot(true, 0, 8000, true)));
        Assert.Equal(HookCheck.Suspect, fresh.Check(3_700_000, new HookSnapshot(true, 0, 20_000, true)));
        Assert.Equal(HookCheck.Recover, fresh.Check(3_705_000, new HookSnapshot(true, 0, 25_000, true)));
    }

    [Fact]
    public void Controller_checks_liveness_every_5_seconds_and_requests_recovery()
    {
        var c = new HookRecoveryController();
        c.Configure(true, false);
        var calls = 0;
        uint input = 10_000;
        HookSnapshot Snap()
        {
            calls++;
            input += 3000;
            return new HookSnapshot(true, 0, input, true);
        }
        HookRefreshRequest? result = null;
        for (var t = 0; t <= 10_000 && result is null; t += 1000) result = c.Tick(t, Snap);
        Assert.Equal(new HookRefreshRequest(HookRefreshReason.InputNotReceived, 0), result);
        Assert.Equal(2, calls); // 0 秒（疑い）・5 秒（新しい入力も届かない → 回復）。間の 1〜4 秒は確かめない
    }

    // --- 登録結果の記録 ---

    [Fact]
    public void Report_description_has_only_reason_result_and_error_number()
    {
        var text = new HookReport(HookRefreshReason.InputNotReceived, 2, HookResult.Failed, 1428, HookResult.Installed, 0, 400, 12, 34).Describe();
        Assert.Equal("登録 理由=InputNotReceived 試行=2 キーボード=Failed(エラー 1428) マウス=Installed 待機=400ms", text);
    }

    // --- 診断ログ ---

    [Fact]
    public void Journal_keeps_only_recent_lines_in_memory_and_writes_nothing_until_recording()
    {
        var j = new DiagnosticJournal();
        for (var i = 0; i < DiagnosticJournal.MemoryLines + 50; i++) j.Add(new DateTime(2026, 10, 15), $"行 {i}");
        Assert.Equal(DiagnosticJournal.MemoryLines, j.Recent().Count);
        Assert.EndsWith($"行 {DiagnosticJournal.MemoryLines + 49}", j.Recent()[^1]);
        Assert.Empty(j.TakePending());
        Assert.False(j.Recording);
    }

    [Fact]
    public void Starting_recording_writes_header_and_the_earlier_memory_then_new_lines()
    {
        var j = new DiagnosticJournal();
        j.Add(new DateTime(2026, 10, 15, 9, 0, 0), "起動");
        j.StartRecording(["見出し"]);
        j.Add(new DateTime(2026, 10, 15, 9, 1, 0), "登録");
        var lines = j.TakePending();
        Assert.Equal("見出し", lines[0]);
        Assert.Contains(lines, l => l.EndsWith(" 起動"));
        Assert.EndsWith(" 登録", lines[^1]);
        Assert.Empty(j.TakePending());
        j.StopRecording();
        j.Add(DateTime.Now, "停止後");
        Assert.Empty(j.TakePending());
    }

    [Fact]
    public void Journal_lines_are_single_line_and_length_limited()
    {
        var j = new DiagnosticJournal();
        j.Add(new DateTime(2026, 10, 15), "a\r\nb" + new string('x', 1000));
        var line = j.Recent()[0];
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.True(line.Length <= 24 + DiagnosticJournal.MaxLineLength + 1);
    }

    [Fact]
    public void Pending_lines_are_capped_when_writing_keeps_failing()
    {
        var j = new DiagnosticJournal();
        j.StartRecording([]);
        for (var i = 0; i < DiagnosticJournal.MaxPendingLines * 2; i++) j.Add(DateTime.Now, "x");
        Assert.Equal(DiagnosticJournal.MaxPendingLines, j.TakePending().Count);
    }

    [Fact]
    public void Rolling_log_file_stays_within_twice_the_limit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plainclip-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new RollingLogFile(Path.Combine(dir, "diagnostic.log"), maxBytes: 4096);
            for (var i = 0; i < 500; i++) file.Append([$"2026-10-15 09:00:00.000 集計 {i} " + new string('x', 40)]);
            var current = new FileInfo(file.FilePath).Length;
            var old = new FileInfo(file.OldPath).Length;
            Assert.InRange(current, 1, 4096);
            Assert.InRange(old, 1, 4096);
            Assert.Equal(Path.Combine(dir, "diagnostic.old.log"), file.OldPath);
            Assert.Contains("集計 499 ", File.ReadAllText(file.FilePath, Encoding.UTF8));

            // 1 回で上限を超える分は新しい行を残す。
            var big = Enumerable.Range(0, 1000).Select(i => $"行 {i:0000} " + new string('y', 40)).ToList();
            file.Append(big);
            var text = File.ReadAllText(file.FilePath, Encoding.UTF8);
            Assert.Contains("行 0999", text);
            Assert.DoesNotContain("行 0000", text);
            Assert.InRange(new FileInfo(file.FilePath).Length, 1, 4096);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Diagnostic_mode_lasts_seven_days_and_rejects_unnatural_deadlines()
    {
        var now = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        var until = DiagnosticMode.NewUntil(now);
        Assert.Equal(now.AddDays(7), until);
        Assert.True(DiagnosticMode.IsActive(until, now));
        Assert.True(DiagnosticMode.IsActive(until, now.AddDays(6.9)));
        Assert.False(DiagnosticMode.IsActive(until, now.AddDays(7)));
        Assert.False(DiagnosticMode.IsActive(null, now));
        Assert.False(DiagnosticMode.IsActive(now.AddDays(30), now)); // 手書きの遠い期限
    }

    [Fact]
    public void Settings_default_to_no_diagnostic_log_and_expired_deadline_is_cleared()
    {
        Assert.Null(new AppSettings().DiagnosticLogUntil);

        var expired = new AppSettings { DiagnosticLogUntil = DateTime.UtcNow.AddDays(-1) };
        Assert.Contains(SettingsNormalizer.Normalize(expired), f => f.StartsWith("DiagnosticLogUntil"));
        Assert.Null(expired.DiagnosticLogUntil);

        var active = new AppSettings { DiagnosticLogUntil = DateTime.UtcNow.AddDays(3) };
        Assert.DoesNotContain(SettingsNormalizer.Normalize(active), f => f.StartsWith("DiagnosticLogUntil"));
        Assert.NotNull(active.DiagnosticLogUntil);
    }

    [Fact]
    public void Settings_file_round_trips_the_diagnostic_deadline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plainclip-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var until = DateTime.UtcNow.AddDays(2);
            store.Save(new AppSettings { DiagnosticLogUntil = until });
            var loaded = store.Load();
            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(until, loaded.Settings.DiagnosticLogUntil!.Value.ToUniversalTime());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
