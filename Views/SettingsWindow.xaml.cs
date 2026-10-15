using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlainClip.Core.Input;
using PlainClip.Core.Licensing;
using PlainClip.Core.Localization;
using PlainClip.Core.Settings;
using PlainClip.Services;

namespace PlainClip.Views;

/// <summary>設定画面から AppHost へ渡す操作。</summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }

    bool AutoStartEnabled { get; }

    /// <summary>設定を反映して保存する（ショートカットは旧解除 → 新登録）。保存・自動起動の失敗はメッセージを返す。</summary>
    string? ApplySettings(AppSettings settings, bool autoStart);

    HotkeyRegistration GetHotkey(HotkeyAction action);

    /// <summary>
    /// ショートカットが登録できるか確かめる。0 = 登録できる（PlainClip がいま登録しているキーと同じ場合も 0）、
    /// それ以外は Win32 エラーコード。
    /// </summary>
    int ProbeShortcut(Shortcut shortcut);

    event EventHandler? HotkeyStatusChanged;

    /// <summary>入力欄の入力開始: 登録中のショートカットを一時解除し、低レベルフックでキーを横取りする。</summary>
    void BeginShortcutInput();

    void EndShortcutInput();

    /// <summary>入力中に押された組み合わせ（低レベルフックで横取りしたもの）。</summary>
    event Action<Shortcut>? ShortcutCaptured;

    /// <summary>保存した表示言語が今と違えば再起動を尋ね、再起動するなら true（この画面も閉じられる）。</summary>
    bool OfferRestartIfLanguageChanged(Window owner);
}

/// <summary>
/// 設定画面（PROJECT.md「設定画面」）。
///  - クリップボードを書式なしで貼り付け: キーを 2 回（左右の Shift・Ctrl から選ぶ） / 通常ショートカット / 無効
///  - 履歴一覧を表示: 通常ショートカット / 無効（既定は無効。トレイのクリックで表示できる）
///  - 数字ショートカット: 使わない（既定） / 修飾キー＋1～9（候補から選ぶ）
///  - ショートカットは既定で空欄。入力・選択した時点で空きを確認し、登録できる場合だけ OK / 適用を押せる。
///  - 自動起動・最大履歴件数・重複のまとめ・表示言語。バージョンと © ja1bus を表示する。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly int[] CountChoices = [5, 10, 15, 20, 30, 50];

    private readonly ISettingsHost _host;
    private bool _loading;
    private bool _inputActive;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        var scale = UiLook.Apply(this);
        Width = 540 * scale;
        UiLook.FitToWorkArea(this);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"PlainClip v{version?.ToString(3)}  © ja1bus";
        SourceLink.ToolTip = AppLinks.Repository;
        WebsiteLink.ToolTip = AppLinks.WebsiteFor(CultureInfo.CurrentUICulture);
        PrivacyLink.ToolTip = AppLinks.Privacy;

        DoubleTapKeyCombo.ItemsSource = DoubleTapKeys.All.Select(k => new Choice<DoubleTapKey>(k, k.DisplayName())).ToList();
        NumberCombo.ItemsSource = NumberPaste.Choices.Select(m => new Choice<KeyModifiers>(m, DescribeNumberChoice(m))).ToList();
        LanguageCombo.ItemsSource = UiLanguage.All.Select(v => new Choice<string>(v, UiLanguage.DisplayName(v))).ToList();

        foreach (var box in new[] { PasteShortcutBox, HistoryShortcutBox })
        {
            // 入力中は今のショートカットを押しても機能が動かないよう一時解除し、他アプリに取られる組み合わせも拾えるようにする。
            box.CaptureStarted += (_, _) => BeginInput();
            box.CaptureEnded += (_, _) => EndInput();
        }
        PasteShortcutBox.ShortcutChanged += (_, _) => OnShortcutEdited(PasteShortcut);
        HistoryShortcutBox.ShortcutChanged += (_, _) => OnShortcutEdited(HistoryShortcut);
        foreach (var radio in new[] { PasteDoubleTap, PasteShortcut, PasteDisabled, HistoryShortcut, HistoryDisabled })
            radio.Checked += (_, _) => Evaluate();
        DoubleTapKeyCombo.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            if (PasteDoubleTap.IsChecked == true) Evaluate();
            else PasteDoubleTap.IsChecked = true; // Checked から Evaluate が呼ばれる
        };
        NumberCombo.SelectionChanged += (_, _) => Evaluate();

        Load(_host.Settings);
        AutoStart.IsChecked = _host.AutoStartEnabled;
        Evaluate();

        _host.HotkeyStatusChanged += OnHotkeyStatusChanged;
        _host.ShortcutCaptured += OnShortcutCaptured;
        Closed += (_, _) =>
        {
            // 入力欄にフォーカスがあるまま閉じても、ショートカットの一時解除とキーの横取りを必ず戻す。
            EndInput();
            _host.HotkeyStatusChanged -= OnHotkeyStatusChanged;
            _host.ShortcutCaptured -= OnShortcutCaptured;
        };
    }

    /// <summary>コンボボックスの選択肢（値と表示名）。</summary>
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static Brush ErrorBrush => UiLook.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static Brush OkBrush => UiLook.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    private static Brush WarnBrush => UiLook.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x00));
    private static Brush NeutralBrush => SystemColors.WindowTextBrush;

    private static string DescribeNumberChoice(KeyModifiers modifiers) =>
        modifiers == KeyModifiers.None ? Strings.SettingsNumberNone : string.Format(Strings.NumberKeysFormat, NumberPaste.Format(modifiers));

    private void Load(AppSettings s)
    {
        _loading = true;
        PasteDoubleTap.IsChecked = s.PasteLatestTrigger == TriggerMode.DoubleTap;
        PasteShortcut.IsChecked = s.PasteLatestTrigger == TriggerMode.Shortcut;
        PasteDisabled.IsChecked = s.PasteLatestTrigger == TriggerMode.Disabled;
        DoubleTapKeyCombo.SelectedIndex = Math.Max(0, DoubleTapKeys.All.ToList().IndexOf(s.DoubleTapKey));
        HistoryShortcut.IsChecked = s.ShowHistoryTrigger == TriggerMode.Shortcut;
        HistoryDisabled.IsChecked = s.ShowHistoryTrigger != TriggerMode.Shortcut;
        PasteShortcutBox.Shortcut = s.PasteLatestShortcutValue;
        HistoryShortcutBox.Shortcut = s.ShowHistoryShortcutValue;
        NumberCombo.SelectedIndex = Math.Max(0, NumberPaste.Choices.ToList().IndexOf(s.NumberPasteModifiersValue));

        MaxCount.ItemsSource = CountChoices.Contains(s.MaxHistoryCount)
            ? CountChoices
            : CountChoices.Append(s.MaxHistoryCount).Order().ToArray();
        MaxCount.SelectedItem = s.MaxHistoryCount;
        MergeDuplicates.IsChecked = s.MergeDuplicates;
        LanguageCombo.SelectedIndex = Math.Max(0, UiLanguage.All.ToList().IndexOf(UiLanguage.Normalize(s.Language)));
        _loading = false;
    }

    private DoubleTapKey SelectedTapKey => (DoubleTapKeyCombo.SelectedItem as Choice<DoubleTapKey>)?.Value ?? DoubleTapKey.LeftShift;

    private KeyModifiers SelectedNumberModifiers => (NumberCombo.SelectedItem as Choice<KeyModifiers>)?.Value ?? KeyModifiers.None;

    /// <summary>入力欄にキーが入ったら、その機能を「通常ショートカット」に切り替えて空きを確認する。</summary>
    private void OnShortcutEdited(RadioButton shortcutRadio)
    {
        if (_loading) return;
        if (shortcutRadio.IsChecked == true) Evaluate();
        else shortcutRadio.IsChecked = true; // Checked から Evaluate が呼ばれる
    }

    /// <summary>低レベルフックで横取りした組み合わせを、フォーカスのある入力欄へ入れる。</summary>
    private void OnShortcutCaptured(Shortcut shortcut)
    {
        if (PasteShortcutBox.IsKeyboardFocused) PasteShortcutBox.Shortcut = shortcut;
        else if (HistoryShortcutBox.IsKeyboardFocused) HistoryShortcutBox.Shortcut = shortcut;
    }

    private void OnHotkeyStatusChanged(object? sender, EventArgs e) => Evaluate();

    /// <summary>各機能の入力内容を確認して表示し、OK / 適用を押せるかを決める。</summary>
    private void Evaluate()
    {
        if (_loading) return;
        UpdateDoubleTapNote();
        var paste = PasteShortcut.IsChecked == true ? PasteShortcutBox.Shortcut : Shortcut.None;
        var history = HistoryShortcut.IsChecked == true ? HistoryShortcutBox.Shortcut : Shortcut.None;
        var pasteOk = EvaluateOne(HotkeyAction.PasteLatest, PasteShortcut.IsChecked == true, PasteShortcutBox.Shortcut, history, PasteStatus);
        var historyOk = EvaluateOne(HotkeyAction.ShowHistory, HistoryShortcut.IsChecked == true, HistoryShortcutBox.Shortcut, paste, HistoryStatus);
        var numbersOk = EvaluateNumbers(SelectedNumberModifiers, paste, history);
        OkButton.IsEnabled = ApplyButton.IsEnabled = pasteOk && historyOk && numbersOk;
    }

    /// <summary>左 Ctrl を選んだときだけ、PowerToys「マウスを探す」と重なることを注記する。</summary>
    private void UpdateDoubleTapNote()
    {
        if (PasteDoubleTap.IsChecked == true && SelectedTapKey == DoubleTapKey.LeftCtrl)
            SetStatus(DoubleTapNote, Strings.SettingsLeftCtrlWarning, WarnBrush);
        else
            SetStatus(DoubleTapNote, "", NeutralBrush);
    }

    /// <summary>1 機能分。ショートカット方式でなければ常に OK。</summary>
    private bool EvaluateOne(HotkeyAction action, bool shortcutMode, Shortcut shortcut, Shortcut other, TextBlock status)
    {
        if (!shortcutMode)
        {
            SetStatus(status, "", NeutralBrush);
            return true;
        }
        if (shortcut.IsEmpty)
        {
            SetStatus(status, Strings.SettingsPressKey, ErrorBrush);
            return false;
        }
        var validation = shortcut.Validate();
        if (validation != ShortcutValidation.Ok)
        {
            SetStatus(status, Shortcut.Describe(validation), ErrorBrush);
            return false;
        }
        if (shortcut == other)
        {
            SetStatus(status, string.Format(Strings.SettingsSameAsOtherFormat, shortcut), ErrorBrush);
            return false;
        }
        if (NumberPaste.NumberOf(SelectedNumberModifiers, shortcut) > 0)
        {
            SetStatus(status, string.Format(Strings.SettingsSameAsNumberFormat, shortcut), ErrorBrush);
            return false;
        }
        var registration = _host.GetHotkey(action);
        if (registration.Status == HotkeyStatus.Registered && registration.Shortcut == shortcut)
        {
            SetStatus(status, string.Format(Strings.SettingsRegisteredFormat, shortcut), OkBrush);
            return true;
        }
        var error = _host.ProbeShortcut(shortcut);
        if (error == 0)
        {
            SetStatus(status, string.Format(Strings.SettingsAvailableFormat, shortcut), OkBrush);
            return true;
        }
        SetStatus(status, HotkeyRegistration.DescribeError(shortcut, error), ErrorBrush);
        return false;
    }

    /// <summary>数字ショートカット 1～9 の空きをまとめて確認する。1 つでも使えなければ OK / 適用を押せない。</summary>
    private bool EvaluateNumbers(KeyModifiers modifiers, Shortcut paste, Shortcut history)
    {
        if (modifiers == KeyModifiers.None)
        {
            SetStatus(NumberStatus, "", NeutralBrush);
            return true;
        }
        var problems = new List<string>();
        foreach (var shortcut in NumberPaste.All(modifiers))
        {
            // 他の 2 機能と同じキーは、その機能の欄で知らせる。
            if (shortcut == paste || shortcut == history) continue;
            var error = _host.ProbeShortcut(shortcut);
            if (error != 0) problems.Add(shortcut.ToString());
        }
        var keys = string.Format(Strings.NumberKeysFormat, NumberPaste.Format(modifiers));
        if (problems.Count > 0)
        {
            SetStatus(NumberStatus, string.Format(Strings.SettingsNumberInUseFormat, string.Join(", ", problems)), ErrorBrush);
            return false;
        }
        if (NumberPaste.NumberOf(modifiers, paste) > 0 || NumberPaste.NumberOf(modifiers, history) > 0)
        {
            SetStatus(NumberStatus, string.Format(Strings.SettingsNumberConflictFormat, keys), ErrorBrush);
            return false;
        }
        var registered = HotkeyActions.Numbers().All(a => _host.GetHotkey(a) is { Status: HotkeyStatus.Registered } r && r.Shortcut.Modifiers == modifiers);
        SetStatus(NumberStatus, string.Format(registered ? Strings.SettingsRegisteredFormat : Strings.SettingsAvailableFormat, keys), OkBrush);
        return true;
    }

    private static void SetStatus(TextBlock block, string text, Brush brush)
    {
        block.Text = text;
        block.Foreground = brush;
        block.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>反映する。反映できたら true（登録に失敗した場合は画面に表示して false）。</summary>
    private bool Apply()
    {
        ShowError(null);
        Evaluate();
        if (!OkButton.IsEnabled) return false;

        var s = _host.Settings.Clone();
        s.PasteLatestTrigger = PasteDoubleTap.IsChecked == true ? TriggerMode.DoubleTap
            : PasteShortcut.IsChecked == true ? TriggerMode.Shortcut
            : TriggerMode.Disabled;
        s.DoubleTapKey = SelectedTapKey;
        s.ShowHistoryTrigger = HistoryShortcut.IsChecked == true ? TriggerMode.Shortcut : TriggerMode.Disabled;
        // 使わない側の入力値も、正しければ次回のために保存する（空欄なら未割当）。
        s.PasteLatestShortcut = ToSetting(PasteShortcutBox.Shortcut);
        s.ShowHistoryShortcut = ToSetting(HistoryShortcutBox.Shortcut);
        s.NumberPasteModifiers = NumberPaste.Format(SelectedNumberModifiers);
        if (MaxCount.SelectedItem is int count) s.MaxHistoryCount = count;
        s.MergeDuplicates = MergeDuplicates.IsChecked == true;
        s.Language = (LanguageCombo.SelectedItem as Choice<string>)?.Value ?? UiLanguage.System;

        var error = _host.ApplySettings(s, AutoStart.IsChecked == true);
        AutoStart.IsChecked = _host.AutoStartEnabled;
        Evaluate();
        if (error is not null)
        {
            ShowError(error);
            return false;
        }
        // 確認後に他アプリが先に取った場合など、実際の登録に失敗したら閉じない。
        foreach (var action in HotkeyActions.All)
        {
            var r = _host.GetHotkey(action);
            if (r.Status == HotkeyStatus.Failed)
            {
                ShowError(r.Describe());
                return false;
            }
        }
        // 保存に成功した後、表示言語が変わる場合だけ再起動を尋ねる。
        if (_host.OfferRestartIfLanguageChanged(this)) return false;
        return true;
    }

    private static string ToSetting(Shortcut shortcut) =>
        shortcut.Validate() == ShortcutValidation.Ok ? shortcut.ToString() : "";

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.Foreground = ErrorBrush;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Apply()) Close();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Apply();

    /// <summary>入力欄の入力開始・終了（開始と終了が必ず 1 回ずつ対になるようにする）。</summary>
    private void BeginInput()
    {
        if (_inputActive) return;
        _inputActive = true;
        _host.BeginShortcutInput();
    }

    private void EndInput()
    {
        if (!_inputActive) return;
        _inputActive = false;
        _host.EndShortcutInput();
    }

    /// <summary>反映せずに閉じる（適用済みの内容はそのまま）。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // --- ライセンス表記・Web（V2.0.0。PROJECT.md「ライセンス表記」） ---

    /// <summary>同梱の LICENSE（MIT License の全文）をこの画面で表示する。読めなければフルパスを示す。</summary>
    private void OnShowLicense(object sender, RoutedEventArgs e)
    {
        var text = LicenseFiles.Read(LicenseFiles.LicenseName, out var problem);
        if (text is null)
        {
            ErrorLog.Write($"ライセンスのファイルを読めませんでした: {problem}");
            MessageBox.Show(this, string.Format(Strings.SettingsLicenseUnreadableFormat, problem),
                "PlainClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(this, text, Strings.SettingsLicenseLink, MessageBoxButton.OK, MessageBoxImage.None);
    }

    private void OnOpenSource(object sender, RoutedEventArgs e) => OpenUrl(AppLinks.Repository);

    private void OnOpenWebsite(object sender, RoutedEventArgs e) => OpenUrl(AppLinks.WebsiteFor(CultureInfo.CurrentUICulture));

    private void OnOpenPrivacy(object sender, RoutedEventArgs e) => OpenUrl(AppLinks.Privacy);

    /// <summary>既定のブラウザーで開く。開けなければ URL を見せる。</summary>
    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write($"ブラウザーで開けませんでした: {url}", ex);
            MessageBox.Show(this, string.Format(Strings.SettingsLinkOpenFailedFormat, ex.Message, url),
                "PlainClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 同梱の THIRD-PARTY-NOTICES.md をメモ帳で開く（.md は関連付けが無いことがあるため）。
    /// 開けなければフルパスを示す（暗号化などで読めない場合に利用者が確かめられるように）。
    /// </summary>
    private void OnOpenNotices(object sender, RoutedEventArgs e)
    {
        var path = LicenseFiles.PathOf(LicenseFiles.NoticesName);
        string? problem = null;
        if (!File.Exists(path)) problem = path;
        else
        {
            try
            {
                var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                psi.ArgumentList.Add(path);
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                problem = $"{path}{Environment.NewLine}{ex.Message}";
            }
        }
        if (problem is null) return;
        ErrorLog.Write($"第三者のソフトウェアの表記を開けませんでした: {problem}");
        MessageBox.Show(this, string.Format(Strings.SettingsNoticesOpenFailedFormat, problem),
            "PlainClip", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
