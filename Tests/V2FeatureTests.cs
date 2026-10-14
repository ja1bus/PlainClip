using PlainClip.Core.Input;
using PlainClip.Core.Settings;
using PlainClip.Core.Text;

namespace PlainClip.Tests;

/// <summary>ダブルタップのキーの選択（V2.0.0）。右 Ctrl は拡張フラグ付きで届く。</summary>
public class DoubleTapKeyTests
{
    private const int LShift = 0xA0;
    private const int RShift = 0xA1;
    private const int LCtrl = 0xA2;
    private const int RCtrl = 0xA3;

    /// <summary>対象キーを 2 回タップして成立した回数。</summary>
    private static int TapTwice(DoubleTapKey key, int vk, bool extended)
    {
        var d = new DoubleTapDetector(key);
        var fired = 0;
        uint t = 1000;
        for (var i = 0; i < 2; i++)
        {
            d.OnKey(vk, true, extended, t += 50, false);
            if (d.OnKey(vk, false, extended, t += 60, false)) fired++;
        }
        return fired;
    }

    [Theory]
    [InlineData(DoubleTapKey.LeftShift, LShift, false)]
    [InlineData(DoubleTapKey.RightShift, RShift, false)]
    [InlineData(DoubleTapKey.RightCtrl, RCtrl, true)]
    [InlineData(DoubleTapKey.LeftCtrl, LCtrl, false)]
    public void 選んだキーの2回で成立する(DoubleTapKey key, int vk, bool extended)
    {
        Assert.Equal(1, TapTwice(key, vk, extended));
    }

    [Fact]
    public void 拡張フラグが本来と違う入力は数えない()
    {
        // キーボードが送る「偽の Shift」（拡張フラグ付き）
        Assert.Equal(0, TapTwice(DoubleTapKey.LeftShift, LShift, extended: true));
        Assert.Equal(0, TapTwice(DoubleTapKey.RightCtrl, RCtrl, extended: false));
    }

    [Fact]
    public void 選んでいない側のキーでは成立しない()
    {
        Assert.Equal(0, TapTwice(DoubleTapKey.RightShift, LShift, false));
        Assert.Equal(0, TapTwice(DoubleTapKey.RightCtrl, LCtrl, false));
        Assert.Equal(0, TapTwice(DoubleTapKey.LeftShift, RShift, false));
    }

    [Fact]
    public void 候補は左右のShiftとCtrlだけ()
    {
        Assert.Equal([DoubleTapKey.LeftShift, DoubleTapKey.RightShift, DoubleTapKey.RightCtrl, DoubleTapKey.LeftCtrl], DoubleTapKeys.All);
        Assert.Equal([LShift, RShift, RCtrl, LCtrl], DoubleTapKeys.All.Select(k => k.VirtualKey()));
    }
}

/// <summary>数字ショートカット（V2.0.0）。</summary>
public class NumberPasteTests
{
    [Fact]
    public void 修飾キー付きの1から9()
    {
        var all = NumberPaste.All(KeyModifiers.Control | KeyModifiers.Alt).ToList();
        Assert.Equal(9, all.Count);
        Assert.Equal("Ctrl+Alt+1", all[0].ToString());
        Assert.Equal("Ctrl+Alt+9", all[8].ToString());
        Assert.All(all, s => Assert.Equal(ShortcutValidation.Ok, s.Validate()));
        Assert.Empty(NumberPaste.All(KeyModifiers.None));
    }

    [Fact]
    public void 番号を取り出す()
    {
        var m = KeyModifiers.Alt | KeyModifiers.Shift;
        Assert.Equal(3, NumberPaste.NumberOf(m, new Shortcut(m, '3')));
        Assert.Equal(0, NumberPaste.NumberOf(m, new Shortcut(m, '0')));
        Assert.Equal(0, NumberPaste.NumberOf(m, new Shortcut(KeyModifiers.Control | KeyModifiers.Alt, '3')));
        Assert.Equal(0, NumberPaste.NumberOf(KeyModifiers.None, new Shortcut(KeyModifiers.None, '3')));
    }

    [Theory]
    [InlineData("", true, KeyModifiers.None)]
    [InlineData(null, true, KeyModifiers.None)]
    [InlineData("Ctrl+Alt", true, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData("alt + shift", true, KeyModifiers.Alt | KeyModifiers.Shift)]
    [InlineData("Shift+Ctrl", true, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Ctrl+Alt+Shift", true, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)]
    [InlineData("Ctrl", false, KeyModifiers.None)]          // 候補にない
    [InlineData("Win+Alt", false, KeyModifiers.None)]       // Win はタスクバーが使う
    [InlineData("Ctrl+Ctrl", false, KeyModifiers.None)]
    [InlineData("Ctrl+X", false, KeyModifiers.None)]
    public void 設定値の読み取り(string? text, bool ok, KeyModifiers expected)
    {
        Assert.Equal(ok, NumberPaste.TryParse(text, out var m));
        Assert.Equal(expected, m);
    }

    [Fact]
    public void 表記は修飾キーの順にそろえる()
    {
        Assert.Equal("Ctrl+Alt+Shift", NumberPaste.Format(KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Control));
        Assert.Equal("", NumberPaste.Format(KeyModifiers.None));
    }
}

/// <summary>「クリップボードを書式なしで貼り付け」の判定（V2.0.0）。</summary>
public class PasteSourceTests
{
    private static readonly IReadOnlySet<uint> Markers = new HashSet<uint> { 0xC100, 0xC101 };

    [Fact]
    public void テキストがあれば貼る()
    {
        var s = PasteSourceRules.Decide(false, true, "abc", false, onlyPlainText: false);
        Assert.Equal(PasteSourceKind.Text, s.Kind);
        Assert.Equal("abc", s.Text);
        Assert.True(s.NeedsRewrite); // 書式付き → プレーンテキストに書き直す
    }

    [Fact]
    public void すでにプレーンテキストなら書き直さない()
    {
        var s = PasteSourceRules.Decide(false, true, "abc", false, onlyPlainText: true);
        Assert.Equal(PasteSourceKind.Text, s.Kind);
        Assert.False(s.NeedsRewrite);
    }

    [Fact]
    public void プレーンテキストでも正規化で変わるなら書き直す()
    {
        var s = PasteSourceRules.Decide(false, true, "a\u00A0b", false, onlyPlainText: true);
        Assert.True(s.NeedsRewrite);
        Assert.Equal("a b", s.Text);
    }

    [Fact]
    public void 画像だけ_ファイル_空は貼らない()
    {
        Assert.Equal(PasteSourceKind.NoText, PasteSourceRules.Decide(false, false, null, false, false).Kind);
        Assert.Equal(PasteSourceKind.Files, PasteSourceRules.Decide(true, true, "C:\\a.txt", false, false).Kind);
        Assert.Equal(PasteSourceKind.Empty, PasteSourceRules.Decide(false, true, "", false, true).Kind);
        Assert.Equal(PasteSourceKind.Unavailable, PasteSourceRules.Decide(false, true, null, false, true).Kind);
    }

    [Fact]
    public void 除外指定付きも貼り_書き直すときは指定を引き継ぐ()
    {
        var s = PasteSourceRules.Decide(false, true, "secret", sensitive: true, onlyPlainText: false);
        Assert.Equal(PasteSourceKind.Text, s.Kind);
        Assert.True(s.Sensitive);
        Assert.True(s.NeedsRewrite);
    }

    [Fact]
    public void 長すぎる書式付きは貼らず_プレーンテキストならそのまま貼る()
    {
        var huge = new string('x', PlainTextNormalizer.MaxLength + 1);
        Assert.Equal(PasteSourceKind.TooLong, PasteSourceRules.Decide(false, true, huge, false, onlyPlainText: false).Kind);
        var plain = PasteSourceRules.Decide(false, true, huge, false, onlyPlainText: true);
        Assert.Equal(PasteSourceKind.Text, plain.Kind);
        Assert.False(plain.NeedsRewrite);
    }

    [Fact]
    public void プレーンテキストだけかの判定()
    {
        Assert.True(PasteSourceRules.OnlyPlainText([13, 16, 1, 7], Markers));
        Assert.True(PasteSourceRules.OnlyPlainText([13, 0xC100], Markers)); // 除外指定の目印は本文と同じ扱い
        Assert.False(PasteSourceRules.OnlyPlainText([13, 0xC0FF], Markers)); // HTML Format など
        Assert.False(PasteSourceRules.OnlyPlainText([13, 2], Markers));      // CF_BITMAP（Excel のセル等）
    }
}

/// <summary>V2.0.0 で追加した設定項目。</summary>
public class V2SettingsTests
{
    [Fact]
    public void 既定値()
    {
        var s = new AppSettings();
        Assert.Equal(DoubleTapKey.LeftShift, s.DoubleTapKey);
        Assert.Equal("", s.NumberPasteModifiers); // 既定は数字ショートカットを使わない
        Assert.Equal(KeyModifiers.None, s.NumberPasteModifiersValue);
        Assert.Equal("", s.Language);
        Assert.Empty(SettingsNormalizer.Normalize(s));
    }

    [Fact]
    public void 不正値を補正する()
    {
        var s = new AppSettings { DoubleTapKey = (DoubleTapKey)99, NumberPasteModifiers = "Win+Alt", Language = "fr" };
        var fixes = SettingsNormalizer.Normalize(s);
        Assert.Equal(DoubleTapKey.LeftShift, s.DoubleTapKey);
        Assert.Equal("", s.NumberPasteModifiers);
        Assert.Equal("", s.Language);
        Assert.Equal(3, fixes.Count);
    }

    [Fact]
    public void 表記をそろえる()
    {
        var s = new AppSettings { NumberPasteModifiers = "alt+ctrl", Language = " JA " };
        Assert.Empty(SettingsNormalizer.Normalize(s));
        Assert.Equal("Ctrl+Alt", s.NumberPasteModifiers);
        Assert.Equal("ja", s.Language);
    }

    [Fact]
    public void V1の設定ファイルを読める()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PlainClipTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """
                {
                  "PasteLatestTrigger": "DoubleTap",
                  "PasteLatestShortcut": "",
                  "ShowHistoryTrigger": "Shortcut",
                  "ShowHistoryShortcut": "Ctrl+Alt+J",
                  "MaxHistoryCount": 10,
                  "MergeDuplicates": true
                }
                """);
            var loaded = new SettingsStore(path).Load();
            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Empty(loaded.Fixes);
            Assert.Equal(DoubleTapKey.LeftShift, loaded.Settings.DoubleTapKey);
            Assert.Equal("", loaded.Settings.NumberPasteModifiers);
            Assert.Equal("Ctrl+Alt+J", loaded.Settings.ShowHistoryShortcut);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 保存して読み戻せる()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PlainClipTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(new AppSettings { DoubleTapKey = DoubleTapKey.RightCtrl, NumberPasteModifiers = "Alt+Shift", Language = "en" });
            var loaded = store.Load();
            Assert.Equal(DoubleTapKey.RightCtrl, loaded.Settings.DoubleTapKey);
            Assert.Equal(KeyModifiers.Alt | KeyModifiers.Shift, loaded.Settings.NumberPasteModifiersValue);
            Assert.Equal("en", loaded.Settings.Language);
            Assert.Contains("\"RightCtrl\"", File.ReadAllText(store.FilePath)); // 列挙は名前で保存する
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
