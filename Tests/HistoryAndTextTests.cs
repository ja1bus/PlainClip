using PlainClip.Core.History;
using PlainClip.Core.Input;
using PlainClip.Core.Settings;
using PlainClip.Core.Text;

namespace PlainClip.Tests;

public class ClipHistoryTests
{
    [Fact]
    public void 先頭が最新で10件を超えると最古が消える()
    {
        var h = new ClipHistory();
        for (var i = 1; i <= 11; i++) h.Add($"item{i}");
        Assert.Equal(10, h.Count);
        Assert.Equal("item11", h.Latest!.Text);
        Assert.Equal("item2", h.Items[^1].Text);
        Assert.DoesNotContain(h.Items, x => x.Text == "item1");
    }

    [Fact]
    public void 同じ文字列の再コピーは先頭へ移動し重複しない()
    {
        var h = new ClipHistory();
        h.Add("a");
        h.Add("b");
        h.Add("c");
        Assert.True(h.Add("a"));
        Assert.Equal(["a", "c", "b"], h.Items.Select(x => x.Text));
    }

    [Fact]
    public void 直前と同じコピーは変化なし()
    {
        var h = new ClipHistory();
        var changed = 0;
        h.Changed += (_, _) => changed++;
        h.Add("a");
        Assert.False(h.Add("a"));
        Assert.Equal(1, h.Count);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void まとめない設定では重複を残すが直前と同じは重ねない()
    {
        var h = new ClipHistory { MergeDuplicates = false };
        h.Add("a");
        h.Add("b");
        h.Add("a");
        h.Add("a");
        Assert.Equal(["a", "b", "a"], h.Items.Select(x => x.Text));
    }

    [Fact]
    public void 空文字は追加しない()
    {
        var h = new ClipHistory();
        Assert.False(h.Add(""));
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void 最大件数を減らすと古い側を削る()
    {
        var h = new ClipHistory();
        for (var i = 0; i < 10; i++) h.Add($"x{i}");
        h.MaxCount = 3;
        Assert.Equal(["x9", "x8", "x7"], h.Items.Select(x => x.Text));
        h.MaxCount = 999;
        Assert.Equal(ClipHistory.MaxMaxCount, h.MaxCount);
    }

    [Fact]
    public void 番号で取り出しRemoveとClear()
    {
        var h = new ClipHistory();
        h.Add("a");
        h.Add("b");
        h.Add("c");
        Assert.Equal("c", h.ItemAt(1)!.Text);
        Assert.Equal("a", h.ItemAt(3)!.Text);
        Assert.Null(h.ItemAt(0));
        Assert.Null(h.ItemAt(4));
        Assert.True(h.Remove(h.Items[1]));
        Assert.Equal(["c", "a"], h.Items.Select(x => x.Text));
        h.Clear();
        Assert.Equal(0, h.Count);
        Assert.Null(h.Latest);
    }

    [Fact]
    public void 日本語やUnicodeや改行タブを全文保持する()
    {
        var text = "日本語テキスト😀\r\n2行目\tタブ\n𠮷野家";
        var h = new ClipHistory();
        h.Add(text);
        Assert.Equal(text, h.Latest!.Text);
        Assert.Equal(3, h.Latest.LineCount);
    }
}

public class PlainTextNormalizerTests
{
    [Fact]
    public void ノーブレークスペース類だけを半角スペースにする()
    {
        Assert.Equal("a b c d", PlainTextNormalizer.Normalize("a b c d"));
    }

    [Fact]
    public void 全角スペース改行タブ前後の空白は変えない()
    {
        var text = "  全角　スペース\r\n\tタブ  \n";
        Assert.Same(text, PlainTextNormalizer.Normalize(text));
    }

    [Fact]
    public void 末尾のNULを落とし空なら履歴に入れない()
    {
        Assert.Equal("abc", PlainTextNormalizer.Normalize("abc\0\0"));
        Assert.Null(PlainTextNormalizer.Normalize("\0"));
        Assert.Null(PlainTextNormalizer.Normalize(""));
        Assert.Null(PlainTextNormalizer.Normalize(null));
    }

    [Fact]
    public void 長すぎるテキストは履歴に入れない()
    {
        Assert.Null(PlainTextNormalizer.Normalize(new string('x', PlainTextNormalizer.MaxLength + 1)));
        Assert.NotNull(PlainTextNormalizer.Normalize(new string('x', PlainTextNormalizer.MaxLength)));
    }
}

public class PreviewTextTests
{
    [Fact]
    public void 改行とタブを記号化して1行にする()
    {
        Assert.Equal("line1 ↵ line2→x", PreviewText.Create("line1\r\nline2\tx"));
    }

    [Fact]
    public void 先頭の空白や空行を飛ばす()
    {
        Assert.Equal("abc", PreviewText.Create("\r\n\r\n   abc\r\n"));
    }

    [Fact]
    public void 長文は省略する()
    {
        var preview = PreviewText.Create(new string('あ', 5000));
        Assert.Equal(PreviewText.MaxChars + 1, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void 空白だけなら表示用の文言()
    {
        Assert.Equal(PlainClip.Core.Localization.Strings.PreviewWhitespaceOnly, PreviewText.Create(" \r\n "));
    }

    [Fact]
    public void 行数は末尾の改行を数えない()
    {
        Assert.Equal(1, PreviewText.CountLines("abc\r\n"));
        Assert.Equal(2, PreviewText.CountLines("a\nb"));
        Assert.Equal(3, PreviewText.CountLines("a\r\n\r\nb"));
    }
}

public class SettingsTests
{
    [Fact]
    public void 既定値は仕様どおり()
    {
        var s = new AppSettings();
        Assert.Equal(TriggerMode.DoubleTap, s.PasteLatestTrigger);
        Assert.Equal(TriggerMode.Disabled, s.ShowHistoryTrigger);
        Assert.Equal("", s.PasteLatestShortcut); // 既定ではどのキーも予約しない
        Assert.Equal("", s.ShowHistoryShortcut);
        Assert.True(s.PasteLatestShortcutValue.IsEmpty);
        Assert.Equal(10, s.MaxHistoryCount);
        Assert.True(s.MergeDuplicates);
        Assert.Empty(SettingsNormalizer.Normalize(s)); // 既定値は補正対象にならない
    }

    [Fact]
    public void 不正値を補正する()
    {
        var s = new AppSettings
        {
            PasteLatestTrigger = (TriggerMode)99,
            PasteLatestShortcut = "Alt",
            ShowHistoryTrigger = TriggerMode.Shortcut,
            ShowHistoryShortcut = "ctrl+shift+v",
            MaxHistoryCount = 0,
        };
        var fixes = SettingsNormalizer.Normalize(s);
        Assert.Equal(TriggerMode.DoubleTap, s.PasteLatestTrigger);
        Assert.Equal("", s.PasteLatestShortcut); // 不正なキーは未割当
        Assert.Equal(TriggerMode.Shortcut, s.ShowHistoryTrigger);
        Assert.Equal("Ctrl+Shift+V", s.ShowHistoryShortcut); // 正しい値は表記だけそろえる（補正扱いにしない）
        Assert.Equal(1, s.MaxHistoryCount);
        Assert.Equal(3, fixes.Count);
    }

    [Fact]
    public void 旧設定の履歴表示ダブルタップは無効になる()
    {
        var s = new AppSettings { ShowHistoryTrigger = TriggerMode.DoubleTap, ShowHistoryShortcut = "Ctrl+Alt+V" };
        SettingsNormalizer.Normalize(s);
        Assert.Equal(TriggerMode.Disabled, s.ShowHistoryTrigger);
        Assert.Equal("Ctrl+Alt+V", s.ShowHistoryShortcut); // 入力値は残す
    }

    [Fact]
    public void ショートカット方式なのにキーが無ければ無効()
    {
        var s = new AppSettings { PasteLatestTrigger = TriggerMode.Shortcut, ShowHistoryTrigger = TriggerMode.Shortcut };
        SettingsNormalizer.Normalize(s);
        Assert.Equal(TriggerMode.Disabled, s.PasteLatestTrigger);
        Assert.Equal(TriggerMode.Disabled, s.ShowHistoryTrigger);
    }

    [Fact]
    public void 保存して読み戻せる()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PlainClipTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            Assert.Equal(SettingsLoadStatus.Missing, store.Load().Status);
            store.Save(new AppSettings
            {
                PasteLatestTrigger = TriggerMode.Shortcut,
                ShowHistoryTrigger = TriggerMode.Disabled,
                PasteLatestShortcut = "Ctrl+Alt+B",
                MaxHistoryCount = 20,
                MergeDuplicates = false,
            });
            Assert.Contains("\"Shortcut\"", File.ReadAllText(store.FilePath)); // 列挙値は文字列で保存
            var loaded = store.Load();
            Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
            Assert.Equal(TriggerMode.Shortcut, loaded.Settings.PasteLatestTrigger);
            Assert.Equal(TriggerMode.Disabled, loaded.Settings.ShowHistoryTrigger);
            Assert.Equal(new Shortcut(KeyModifiers.Control | KeyModifiers.Alt, 'B'), loaded.Settings.PasteLatestShortcutValue);
            Assert.Equal(20, loaded.Settings.MaxHistoryCount);
            Assert.False(loaded.Settings.MergeDuplicates);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 壊れた設定は退避して既定値()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PlainClipTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ \"PasteLatestTrigger\": \"Unknown\" ");
            var loaded = new SettingsStore(path).Load();
            Assert.Equal(SettingsLoadStatus.Corrupt, loaded.Status);
            Assert.NotNull(loaded.BackupPath);
            Assert.True(File.Exists(loaded.BackupPath));
            Assert.Equal(TriggerMode.DoubleTap, loaded.Settings.PasteLatestTrigger);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ショートカットの表記()
    {
        Assert.True(Shortcut.TryParse("Ctrl+Alt+V", out var s));
        Assert.Equal(new Shortcut(KeyModifiers.Control | KeyModifiers.Alt, 'V'), s);
        Assert.Equal(ShortcutValidation.RequiresModifier, new Shortcut(KeyModifiers.Shift, 'A').Validate());
    }

    [Fact]
    public void 入力欄で横取りするキー()
    {
        Assert.True(Shortcut.ShouldCapture('K', KeyModifiers.Control | KeyModifiers.Alt));
        Assert.True(Shortcut.ShouldCapture('V', KeyModifiers.Win));
        Assert.True(Shortcut.ShouldCapture(0x78, KeyModifiers.None)); // F9 は単独で割り当て可
        Assert.False(Shortcut.ShouldCapture('A', KeyModifiers.None));
        Assert.False(Shortcut.ShouldCapture('A', KeyModifiers.Shift));
        Assert.False(Shortcut.ShouldCapture(0x09, KeyModifiers.None)); // Tab はフォーカス移動に使う
        Assert.False(Shortcut.ShouldCapture(0x08, KeyModifiers.None)); // Backspace は未割当にする操作
        Assert.False(Shortcut.ShouldCapture(0xA2, KeyModifiers.Control)); // 修飾キーだけは確定しない
    }
}
