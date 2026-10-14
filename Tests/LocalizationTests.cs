using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using PlainClip.Core.Input;
using PlainClip.Core.Localization;
using PlainClip.Core.Text;

namespace PlainClip.Tests;

/// <summary>テストの間だけ表示言語（CurrentUICulture）を切り替える。</summary>
public sealed class UiCultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

    public UiCultureScope(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentUICulture = _previous;
}

/// <summary>表示言語（英語・日本語）の文字列と、言語の決め方（V2.0.0。PROJECT.md「英語・日本語」）。</summary>
public sealed partial class LocalizationTests
{
    private static Dictionary<string, string> Load(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"{culture.Name} の文字列がありません（ja\\PlainClip.Core.resources.dll）");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static readonly Dictionary<string, string> English = Load(CultureInfo.InvariantCulture);
    private static readonly Dictionary<string, string> Japanese = Load(CultureInfo.GetCultureInfo("ja"));

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]")]
    private static partial Regex JapaneseChars();

    private static int[] Placeholders(string s) =>
        Placeholder().Matches(s.Replace("{{", "").Replace("}}", "")).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToArray();

    [Fact]
    public void Japanese_has_exactly_the_same_keys_as_English()
    {
        Assert.NotEmpty(English);
        Assert.Empty(English.Keys.Except(Japanese.Keys)); // 訳し忘れ
        Assert.Empty(Japanese.Keys.Except(English.Keys)); // 英語側に無いキー
    }

    [Fact]
    public void No_string_is_empty()
    {
        Assert.Empty(English.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
        Assert.Empty(Japanese.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
    }

    [Fact]
    public void Format_placeholders_match_between_languages()
    {
        foreach (var (key, en) in English)
        {
            Assert.True(Placeholders(en).SequenceEqual(Placeholders(Japanese[key])), $"{key}: 埋め込み位置 {{n}} が英語と日本語で違う");
        }
    }

    [Fact]
    public void Format_strings_can_be_formatted()
    {
        foreach (var (key, en) in English.Where(p => p.Key.EndsWith("Format", StringComparison.Ordinal)))
        {
            var count = Placeholders(en).Length;
            Assert.True(count > 0, $"{key}: 名前が Format で終わるのに {{0}} が無い");
            var args = Enumerable.Range(0, count).Select(i => (object)$"<{i}>").ToArray();
            Assert.Contains("<0>", string.Format(CultureInfo.InvariantCulture, en, args));
            Assert.Contains("<0>", string.Format(CultureInfo.InvariantCulture, Japanese[key], args));
        }
    }

    [Fact]
    public void Japanese_text_is_not_left_in_English_strings()
    {
        Assert.Empty(English.Where(p => JapaneseChars().IsMatch(p.Value)).Select(p => p.Key));
    }

    [Theory]
    [InlineData("", "ja-JP", "ja")]
    [InlineData("", "ja", "ja")]
    [InlineData("", "en-US", "en")]
    [InlineData("", "de-DE", "en")] // 英語・日本語以外は英語
    [InlineData("en", "ja-JP", "en")]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("JA", "en-US", "ja")]
    [InlineData("fr", "ja-JP", "ja")] // 未知の値は「Windows に合わせる」
    [InlineData(null, "fr-FR", "en")]
    public void Resolve_uses_setting_then_windows_language(string? setting, string windows, string expected)
    {
        Assert.Equal(expected, UiLanguage.Resolve(setting, CultureInfo.GetCultureInfo(windows)).Name);
    }

    [Fact]
    public void Language_names_are_written_in_their_own_language()
    {
        using (new UiCultureScope("en"))
        {
            Assert.Equal("English", UiLanguage.DisplayName(UiLanguage.English));
            Assert.Equal("日本語", UiLanguage.DisplayName(UiLanguage.Japanese));
            Assert.Equal(English[nameof(Strings.LanguageSystem)], UiLanguage.DisplayName(UiLanguage.System));
        }
        using (new UiCultureScope("ja"))
        {
            Assert.Equal(Japanese[nameof(Strings.LanguageSystem)], UiLanguage.DisplayName(UiLanguage.System));
        }
    }

    [Fact]
    public void Get_returns_text_in_the_requested_language()
    {
        using var _ = new UiCultureScope("ja");
        Assert.Equal(English[nameof(Strings.RestartPrompt)], UiLanguage.Get(nameof(Strings.RestartPrompt), CultureInfo.GetCultureInfo("en")));
        Assert.Equal(Japanese[nameof(Strings.RestartPrompt)], UiLanguage.Get(nameof(Strings.RestartPrompt), CultureInfo.GetCultureInfo("ja")));
    }

    [Fact]
    public void Core_messages_follow_the_ui_language()
    {
        using (new UiCultureScope("en"))
        {
            Assert.Equal("Left Shift twice", DoubleTapKey.LeftShift.TwiceText());
            Assert.Equal("(whitespace only)", PreviewText.Create("   "));
            Assert.StartsWith("Combine with", Shortcut.Describe(ShortcutValidation.RequiresModifier));
        }
        using (new UiCultureScope("ja"))
        {
            Assert.Equal("左Shift を 2 回", DoubleTapKey.LeftShift.TwiceText());
            Assert.Equal("(空白のみ)", PreviewText.Create("   "));
        }
        using (new UiCultureScope("de-DE"))
        {
            Assert.Equal("Right Ctrl twice", DoubleTapKey.RightCtrl.TwiceText());
        }
    }

    [Fact]
    public void Saved_shortcut_text_does_not_depend_on_ui_language()
    {
        using var _ = new UiCultureScope("ja");
        Assert.Equal("Ctrl+Alt+1", NumberPaste.For(KeyModifiers.Control | KeyModifiers.Alt, 1).ToString());
        Assert.Equal("Alt+Shift", NumberPaste.Format(KeyModifiers.Alt | KeyModifiers.Shift));
    }
}
