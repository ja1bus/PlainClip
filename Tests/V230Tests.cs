using System.Text;
using PlainClip.Core.Diagnostics;
using PlainClip.Core.Input;
using PlainClip.Core.Layout;
using PlainClip.Core.Settings;
using PlainClip.Core.Text;

namespace PlainClip.Tests;

/// <summary>
/// V2.3.0（試験表 docs/verification.md の 20261118-v230 の T-02〜T-12）:
/// クリップボードを書式なしに変換（設定・判断）、2 回押しの停止中の表示、履歴一覧の位置、
/// 設定ファイルの未知の列挙値、error.log の上限、マウスのフックだけ失敗したときの監視。
/// </summary>
public sealed class V230Tests
{
    // --- T-02 ショートカットの機能の順と番号 ---

    [Fact]
    public void Hotkey_actions_are_registered_in_order_and_convert_is_not_a_number()
    {
        HotkeyAction[] expected =
        [
            HotkeyAction.PasteLatest, HotkeyAction.ShowHistory, HotkeyAction.ConvertClipboard,
            .. Enumerable.Range(1, 9).Select(HotkeyActions.NumberAction),
        ];
        Assert.Equal(expected, HotkeyActions.All);
        Assert.Equal(12, HotkeyActions.All.Count);
        Assert.Equal(0, HotkeyAction.ConvertClipboard.NumberOf());
        Assert.Equal(0, HotkeyAction.PasteLatest.NumberOf());
        Assert.Equal(0, HotkeyAction.ShowHistory.NumberOf());
        Assert.Equal(1, HotkeyAction.Number1.NumberOf());
        Assert.Equal(9, HotkeyAction.Number9.NumberOf());
    }

    // --- T-03 変換の呼び出し方法の補正 ---

    [Theory]
    [InlineData(TriggerMode.Shortcut, "", TriggerMode.Disabled, "")]
    [InlineData(TriggerMode.Shortcut, "Ctrl+Alt+K", TriggerMode.Shortcut, "Ctrl+Alt+K")]
    [InlineData(TriggerMode.DoubleTap, "Ctrl+Alt+K", TriggerMode.Disabled, "Ctrl+Alt+K")] // 入力済みのキーは残す
    [InlineData((TriggerMode)99, "Ctrl+Alt+K", TriggerMode.Disabled, "Ctrl+Alt+K")]
    [InlineData((TriggerMode)(-1), "Ctrl+Alt+K", TriggerMode.Disabled, "Ctrl+Alt+K")]
    [InlineData(TriggerMode.Disabled, "", TriggerMode.Disabled, "")]
    [InlineData(TriggerMode.Shortcut, "K", TriggerMode.Disabled, "")]
    [InlineData(TriggerMode.Shortcut, "Ctrl+", TriggerMode.Disabled, "")]
    public void Convert_trigger_is_normalized(TriggerMode trigger, string shortcut, TriggerMode expectedTrigger, string expectedShortcut)
    {
        var s = new AppSettings
        {
            ConvertClipboardTrigger = trigger,
            ConvertClipboardShortcut = shortcut,
            ShowHistoryTrigger = TriggerMode.Shortcut,
            ShowHistoryShortcut = "Ctrl+Alt+J",
            MaxHistoryCount = 20,
        };
        SettingsNormalizer.Normalize(s);
        Assert.Equal(expectedTrigger, s.ConvertClipboardTrigger);
        Assert.Equal(expectedShortcut, s.ConvertClipboardShortcut);
        Assert.Equal(TriggerMode.Shortcut, s.ShowHistoryTrigger);
        Assert.Equal("Ctrl+Alt+J", s.ShowHistoryShortcut);
        Assert.Equal(20, s.MaxHistoryCount);
    }

    // --- T-04 以前の設定ファイル ---

    [Fact]
    public void Settings_without_convert_items_load_as_disabled()
    {
        WithFolder(dir =>
        {
            var result = Load(dir, """
                {
                  "PasteLatestTrigger": "DoubleTap",
                  "DoubleTapKey": "LeftShift",
                  "PasteLatestShortcut": "",
                  "ShowHistoryTrigger": "Shortcut",
                  "ShowHistoryShortcut": "Ctrl+Alt+J",
                  "NumberPasteModifiers": "Ctrl+Alt",
                  "MaxHistoryCount": 20,
                  "MergeDuplicates": true,
                  "Language": "ja",
                  "DiagnosticLogUntil": null
                }
                """);
            Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
            Assert.Equal(TriggerMode.Disabled, result.Settings.ConvertClipboardTrigger);
            Assert.Equal("", result.Settings.ConvertClipboardShortcut);
            Assert.Equal("Ctrl+Alt+J", result.Settings.ShowHistoryShortcut);
            Assert.Equal(20, result.Settings.MaxHistoryCount);
            Assert.Equal("ja", result.Settings.Language);
            Assert.Empty(Directory.GetFiles(dir, "settings.json.broken-*"));
        });
    }

    // --- T-05 変更の有無 ---

    [Fact]
    public void Convert_choices_are_compared()
    {
        var baseline = new AppSettings();
        var changed = baseline.Clone();
        changed.ConvertClipboardTrigger = TriggerMode.Shortcut;
        changed.ConvertClipboardShortcut = "Ctrl+Alt+K";
        Assert.False(baseline.SameChoicesAs(changed));

        var k = new AppSettings { ConvertClipboardTrigger = TriggerMode.Shortcut, ConvertClipboardShortcut = "Ctrl+Alt+K" };
        var l = k.Clone();
        l.ConvertClipboardShortcut = "Ctrl+Alt+L";
        Assert.False(k.SameChoicesAs(l));
        Assert.True(k.SameChoicesAs(k.Clone()));

        var spelled = k.Clone();
        spelled.ConvertClipboardShortcut = "alt+ctrl+k";
        Assert.True(k.SameChoicesAs(spelled));
    }

    // --- T-06・T-07・T-08 設定ファイルの列挙値 ---

    public static TheoryData<string, string> UnknownEnumValues => new()
    {
        { "PasteLatestTrigger", "\"Hold\"" },
        { "ShowHistoryTrigger", "\"Hold\"" },
        { "ConvertClipboardTrigger", "\"Hold\"" },
        { "DoubleTapKey", "\"Middle\"" },
        { "DoubleTapKey", "\"MiddleShift\"" },
        { "ConvertClipboardTrigger", "0" },
        { "DoubleTapKey", "0" },
        { "PasteLatestTrigger", "\"1\"" },
        { "ConvertClipboardTrigger", "\"Shortcut, Disabled\"" },
        { "DoubleTapKey", "\"\"" },
        { "PasteLatestTrigger", "null" },
        { "ConvertClipboardTrigger", "null" },
        { "DoubleTapKey", "null" },
        { "ShowHistoryTrigger", "true" },
        { "DoubleTapKey", "false" },
        { "PasteLatestTrigger", "1.5" },
        { "ConvertClipboardTrigger", "{\"a\":1}" },
        { "DoubleTapKey", "[1,2]" },
        { "ShowHistoryTrigger", "99" },
        { "ConvertClipboardTrigger", "99" },
        { "PasteLatestTrigger", "99" },
        { "DoubleTapKey", "99" },
    };

    [Theory]
    [MemberData(nameof(UnknownEnumValues))]
    public void Unknown_enum_value_falls_back_only_for_that_item(string property, string value)
    {
        WithFolder(dir =>
        {
            var result = Load(dir, Json(property, value));
            Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
            AssertOtherItemsKept(result.Settings);
            Assert.Empty(Directory.GetFiles(dir, "settings.json.broken-*"));
            var s = result.Settings;
            switch (property)
            {
                case "PasteLatestTrigger": Assert.Equal(TriggerMode.DoubleTap, s.PasteLatestTrigger); break;
                case "ShowHistoryTrigger": Assert.Equal(TriggerMode.Disabled, s.ShowHistoryTrigger); break;
                case "ConvertClipboardTrigger": Assert.Equal(TriggerMode.Disabled, s.ConvertClipboardTrigger); break;
                default: Assert.Equal(DoubleTapKey.LeftShift, s.DoubleTapKey); break;
            }
        });
    }

    [Theory]
    [InlineData("PasteLatestTrigger", "\"shortcut\"", "Shortcut")]
    [InlineData("ShowHistoryTrigger", "\"SHORTCUT\"", "Shortcut")]
    [InlineData("ConvertClipboardTrigger", "\"shortcut\"", "Shortcut")]
    [InlineData("PasteLatestTrigger", "\"DISABLED\"", "Disabled")]
    [InlineData("DoubleTapKey", "\"rightctrl\"", "RightCtrl")]
    [InlineData("PasteLatestTrigger", "1", "Shortcut")]
    [InlineData("ConvertClipboardTrigger", "1", "Shortcut")]
    [InlineData("DoubleTapKey", "2", "RightCtrl")]
    public void Known_enum_value_is_read_case_insensitively_or_as_number_and_saved_as_name(string property, string value, string expectedName)
    {
        WithFolder(dir =>
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var result = Load(dir, Json(property, value));
            Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
            AssertOtherItemsKept(result.Settings);
            Assert.Equal(expectedName, ValueOf(result.Settings, property));

            store.Save(result.Settings);
            var saved = File.ReadAllText(store.FilePath, Encoding.UTF8);
            Assert.Contains($"\"{property}\": \"{expectedName}\"", saved);
            foreach (var name in new[] { "PasteLatestTrigger", "ShowHistoryTrigger", "ConvertClipboardTrigger", "DoubleTapKey" })
                Assert.Matches($"\"{name}\": \"[A-Za-z]+\"", saved); // 数値ではなく名前で書く

            var again = store.Load();
            Assert.Equal(SettingsLoadStatus.Loaded, again.Status);
            Assert.True(result.Settings.SameChoicesAs(again.Settings));
        });
    }

    [Fact]
    public void Broken_json_is_still_corrupt()
    {
        WithFolder(dir =>
        {
            const string broken = "{\"ConvertClipboardTrigger\": \"Shortcut\",";
            var result = Load(dir, broken);
            Assert.Equal(SettingsLoadStatus.Corrupt, result.Status);
            var backup = Assert.Single(Directory.GetFiles(dir, "settings.json.broken-*"));
            Assert.Equal(Encoding.UTF8.GetBytes(broken), File.ReadAllBytes(backup));
            Assert.Equal(TriggerMode.Disabled, result.Settings.ConvertClipboardTrigger);
        });
    }

    private static string Json(string property, string value) =>
        "{ \"" + property + "\": " + value + ", \"PasteLatestShortcut\": \"Ctrl+Alt+P\", \"ShowHistoryShortcut\": \"Ctrl+Alt+J\", " +
        "\"ConvertClipboardShortcut\": \"Ctrl+Alt+K\", \"MaxHistoryCount\": 20, \"Language\": \"ja\" }";

    private static void AssertOtherItemsKept(AppSettings s)
    {
        Assert.Equal(20, s.MaxHistoryCount);
        Assert.Equal("ja", s.Language);
        Assert.Equal("Ctrl+Alt+J", s.ShowHistoryShortcut);
        Assert.Equal("Ctrl+Alt+K", s.ConvertClipboardShortcut);
        Assert.Equal("Ctrl+Alt+P", s.PasteLatestShortcut);
    }

    private static string ValueOf(AppSettings s, string property) => property switch
    {
        "PasteLatestTrigger" => s.PasteLatestTrigger.ToString(),
        "ShowHistoryTrigger" => s.ShowHistoryTrigger.ToString(),
        "ConvertClipboardTrigger" => s.ConvertClipboardTrigger.ToString(),
        _ => s.DoubleTapKey.ToString(),
    };

    private static SettingsLoadResult Load(string dir, string json)
    {
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return new SettingsStore(path).Load();
    }

    private static void WithFolder(Action<string> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PlainClipTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            test(dir);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // --- T-09 error.log の上限（ErrorLog と同じ RollingLogFile の既定の上限） ---

    [Fact]
    public void Error_log_rolls_over_at_512KB_and_keeps_multi_line_entries_together()
    {
        const long max = RollingLogFile.DefaultMaxBytes;
        Assert.Equal(524_288, max);
        WithFolder(dir =>
        {
            var log = new RollingLogFile(Path.Combine(dir, "error.log"));
            Assert.EndsWith("error.old.log", log.OldPath);

            // (a)(b) ちょうど上限までは切り替えない（記録は本文＋改行 2 バイト）。
            Fill(log.FilePath, max - 100);
            log.Append([new string('a', 98)]);
            Assert.False(File.Exists(log.OldPath));
            Assert.Equal(max, new FileInfo(log.FilePath).Length);

            // (c) 1 バイトでも超えるなら切り替える。
            var before = File.ReadAllBytes(log.FilePath);
            log.Append(["b"]);
            Assert.Equal(before, File.ReadAllBytes(log.OldPath));
            Assert.Equal("b\r\n", File.ReadAllText(log.FilePath));

            // (d) 例外の詳細を含む複数行の 1 件は分かれない。
            Fill(log.FilePath, 524_000);
            var entry = "2026-11-18 12:00:00.000 失敗\r\nSystem.Exception: x\r\n   at A.B()";
            var filler = File.ReadAllBytes(log.FilePath);
            log.Append([entry + new string('c', 400)]);
            Assert.Equal(entry + new string('c', 400) + "\r\n", File.ReadAllText(log.FilePath));
            Assert.Equal(filler, File.ReadAllBytes(log.OldPath));

            // (e) 2 回目の切り替えでは 1 回目の内容は残らない（2 つのファイルだけ）。
            var current = File.ReadAllBytes(log.FilePath);
            log.Append([new string('d', (int)max - 10)]);
            Assert.Equal(current, File.ReadAllBytes(log.OldPath));
            Assert.Equal(2, Directory.GetFiles(dir).Length);
            foreach (var file in Directory.GetFiles(dir)) Assert.True(new FileInfo(file).Length <= max);
        });
    }

    private static void Fill(string path, long bytes)
    {
        // ASCII の行（CRLF）で指定のバイト数ちょうどにする。
        var sb = new StringBuilder();
        while (sb.Length + 40 <= bytes) sb.Append(new string('x', 38)).Append("\r\n");
        sb.Append(new string('y', (int)(bytes - sb.Length)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Assert.Equal(bytes, new FileInfo(path).Length);
    }

    // --- T-10 マウスのフックだけ失敗 ---

    [Fact]
    public void Without_mouse_hook_missed_input_is_not_recovered()
    {
        var m = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Suspect, m.Check(0, new HookSnapshot(true, 1000, 5000, true, MouseInstalled: true)));
        Assert.Equal(HookCheck.NotObservable, m.Check(5000, new HookSnapshot(true, 1000, 9000, true, MouseInstalled: false)));
        // 疑いは取り消されているので、いきなり回復しない。
        Assert.Equal(HookCheck.Suspect, m.Check(10_000, new HookSnapshot(true, 1000, 13_000, true, MouseInstalled: true)));

        var n = new HookLivenessMonitor();
        for (var i = 0; i < 12; i++)
            Assert.Equal(HookCheck.NotObservable, n.Check(i * 5000, new HookSnapshot(true, 1000, (uint)(5000 + i * 5000), true, MouseInstalled: false)));
        Assert.Equal(0, n.Recoveries);

        // キーボードのフックが無いときは従来どおり（登録失敗の再試行の側で扱う）。
        Assert.Equal(HookCheck.Healthy, new HookLivenessMonitor().Check(0, new HookSnapshot(false, 0, 5000, true, MouseInstalled: false)));
        // 省略時はマウスありとして扱う（既存の呼び出しと同じ結果）。
        var o = new HookLivenessMonitor();
        Assert.Equal(HookCheck.Suspect, o.Check(0, new HookSnapshot(true, 1000, 5000, true)));
        Assert.Equal(HookCheck.Recover, o.Check(5000, new HookSnapshot(true, 1000, 9000, true)));
    }

    // --- T-11 2 回押しの停止中の表示 ---

    private static HookReport Report(HookResult keyboard, HookResult mouse = HookResult.Installed) =>
        new(HookRefreshReason.Configure, 0, keyboard, keyboard == HookResult.Failed ? 5 : 0, mouse, mouse == HookResult.Failed ? 5 : 0, 0, 0, 0);

    [Fact]
    public void Tap_monitoring_state_follows_registration_reports()
    {
        var s = new TapMonitoringState();
        Assert.True(s.Active);
        Assert.False(s.Configure(true)); // 報告を待つ
        Assert.True(s.Active);
        Assert.True(s.OnReport(Report(HookResult.Failed)));
        Assert.False(s.Active);
        Assert.False(s.OnReport(Report(HookResult.Failed))); // 再試行の失敗: 変化なし
        Assert.True(s.OnReport(Report(HookResult.Installed)));
        Assert.True(s.Active);
        Assert.True(s.OnReport(Report(HookResult.Failed)));
        Assert.True(s.OnReport(Report(HookResult.Kept)));
        Assert.True(s.Active);
        Assert.True(s.OnReport(Report(HookResult.Failed)));
        Assert.True(s.Configure(false)); // 使わない設定にしたら通常へ
        Assert.True(s.Active);
        Assert.False(s.OnReport(Report(HookResult.Failed))); // 使わない設定では停止中にしない
        Assert.True(s.Active);
        Assert.False(s.Configure(true));
        Assert.True(s.Active);
        Assert.False(s.OnReport(Report(HookResult.Installed, mouse: HookResult.Failed))); // マウスだけの失敗では停止中にしない
        Assert.True(s.Active);
    }

    // --- T-12 履歴一覧の位置 ---

    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Popup_is_placed_below_the_caret()
    {
        var caret = new PixelRect(500, 300, 502, 320);
        var (x, y) = PopupPlacement.BelowCaret(caret, Work, 460, 400);
        Assert.Equal(500, x);
        Assert.True(y >= 320);

        // 下端にちょうど収まるならキャレットの下、1 ピクセル超えるならキャレットの上。
        var fits = new PixelRect(500, 600, 502, 1040 - 400 - PopupPlacement.CaretGap);
        Assert.Equal(fits.Bottom + PopupPlacement.CaretGap, PopupPlacement.BelowCaret(fits, Work, 460, 400).Y);
        var over = fits with { Top = fits.Top + 1, Bottom = fits.Bottom + 1 };
        var above = PopupPlacement.BelowCaret(over, Work, 460, 400);
        Assert.True(above.Y + 400 <= over.Top);

        // 右にはみ出すなら内側へ。
        var right = PopupPlacement.BelowCaret(new PixelRect(1800, 300, 1802, 320), Work, 460, 400);
        Assert.True(right.X + 460 <= 1920 && right.X >= 0);

        // 上にも下にも収まらない高さは作業領域の内側。
        var tall = PopupPlacement.BelowCaret(new PixelRect(500, 500, 502, 520), Work, 460, 1000);
        Assert.InRange(tall.Y, 0, 1040 - 1000);

        // 負の座標のモニターでも同じ相対位置。
        var left = new PixelRect(-1920, 0, 0, 1040);
        var (lx, ly) = PopupPlacement.BelowCaret(new PixelRect(500 - 1920, 300, 502 - 1920, 320), left, 460, 400);
        Assert.Equal((x - 1920, y), (lx, ly));
    }

    [Fact]
    public void Popup_is_centered_on_the_window_or_near_the_cursor()
    {
        var (cx, cy) = PopupPlacement.CenterOn(new PixelRect(100, 100, 900, 700), Work, 460, 400);
        Assert.InRange(cx + 230 - 500, -1, 1);
        Assert.InRange(cy + 200 - 400, -1, 1);

        var (ox, oy) = PopupPlacement.CenterOn(new PixelRect(1700, 900, 2300, 1300), Work, 460, 400);
        Assert.True(ox >= 0 && ox + 460 <= 1920 && oy >= 0 && oy + 400 <= 1040);

        var (nx, ny) = PopupPlacement.NearCursor(100, 100, Work, 460, 400);
        Assert.True(nx >= 100 && ny >= 100);

        // 右にはみ出すならマウスの左へ。下にはみ出すなら作業領域の下端に合わせる（2.2.1 までと同じ。§11）。
        var (ex, ey) = PopupPlacement.NearCursor(1900, 1030, Work, 460, 400);
        Assert.Equal(1900 - 460 - PopupPlacement.CursorOffset, ex);
        Assert.Equal(1040 - 400, ey);

        var anchor = new PopupAnchor(PopupAnchorKind.WindowCenter, new PixelRect(100, 100, 900, 700), Work);
        Assert.Equal((cx, cy), PopupPlacement.Place(anchor, 460, 400));
    }

    // --- §28 の判断 ---

    [Fact]
    public void Conversion_decision_follows_the_clipboard()
    {
        var html = new PasteSource(PasteSourceKind.Text, "abc", NeedsRewrite: true) { Sequence = 7 };
        Assert.Equal(ConversionAction.Rewrite, ClipboardConversion.Decide(html, 7));
        Assert.Equal(ConversionAction.Changed, ClipboardConversion.Decide(html, 8));
        var plain = new PasteSource(PasteSourceKind.Text, "abc", NeedsRewrite: false) { Sequence = 7 };
        Assert.Equal(ConversionAction.AlreadyPlain, ClipboardConversion.Decide(plain, 8));
        foreach (var kind in new[] { PasteSourceKind.NoText, PasteSourceKind.Files, PasteSourceKind.Empty, PasteSourceKind.TooLong, PasteSourceKind.Unavailable })
            Assert.Equal(ConversionAction.NoText, ClipboardConversion.Decide(new PasteSource(kind), 0));

        // プレーンテキストだけで長すぎるものは書き直さない（§8 と同じ）。
        var longPlain = PasteSourceRules.Decide(false, true, new string('a', PlainTextNormalizer.MaxLength + 1), ClipProtection.None, onlyPlainText: true);
        Assert.Equal(ConversionAction.AlreadyPlain, ClipboardConversion.Decide(longPlain, longPlain.Sequence));
    }

    [Fact]
    public void Caret_of_a_window_that_is_not_dpi_aware_is_scaled_to_physical_pixels()
    {
        // DPI に対応していないウィンドウ（96）を 150% のモニター（144）に出したとき: クライアントの左上 (1000,200) からのずれを 1.5 倍。
        var caret = PopupPlacement.CaretOnScreen(new PixelRect(160, 100, 161, 120), 1000, 200, 96, 144);
        Assert.Equal(new PixelRect(1240, 350, 1242, 380), caret);

        // 200%（192）。
        Assert.Equal(new PixelRect(1320, 400, 1322, 440), PopupPlacement.CaretOnScreen(new PixelRect(160, 100, 161, 120), 1000, 200, 96, 192));

        // DPI に対応しているウィンドウ（ウィンドウの DPI = モニターの DPI）はそのまま。負の座標のモニターでも同じ。
        Assert.Equal(new PixelRect(-1760, 300, -1759, 320), PopupPlacement.CaretOnScreen(new PixelRect(160, 100, 161, 120), -1920, 200, 144, 144));

        // システム DPI（144）に対応したウィンドウを 100% のモニターに出したとき（縮小表示）。
        Assert.Equal(new PixelRect(1000 + 107, 200 + 67, 1000 + 107, 200 + 80), PopupPlacement.CaretOnScreen(new PixelRect(160, 100, 161, 120), 1000, 200, 144, 96));
    }
}
