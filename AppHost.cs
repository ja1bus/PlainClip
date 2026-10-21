using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using PlainClip.Core.Diagnostics;
using PlainClip.Core.History;
using PlainClip.Core.Input;
using PlainClip.Core.Localization;
using PlainClip.Core.Settings;
using PlainClip.Core.Text;
using PlainClip.Services;
using PlainClip.Views;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip;

/// <summary>
/// アプリの構成ルート。履歴・クリップボード監視・ダブルタップ・ショートカット・トレイ・画面をつなぐ。
/// すべて UI スレッドで動く（フックのスレッドからは Dispatcher 経由で届く）。
///
/// 役割の分担（V2.0.0）:
///  - ダブルタップ（既定は左 Shift ×2）: 現在のクリップボードのテキストを書式なしで貼る。テキストが無ければ何も貼らず、短い案内を出す。
///  - 数字ショートカット: 指定番号のテキスト履歴を貼る（画像をコピーした後でも 1 番で最後のテキストを貼れる）。
///  - 履歴から貼っても並べ替えない（番号を固定する）。現在のクリップボードと同じ項目には一覧で印を付ける。
/// </summary>
public sealed class AppHost : ISettingsHost, IDisposable
{
    private readonly Application _app;
    private readonly CultureInfo _windowsUiCulture;
    private readonly SettingsStore _store = SettingsStore.CreateDefault();
    private readonly ClipHistory _history = new();
    private CultureInfo _uiCulture;
    private AppSettings _settings = new();
    private ClipboardService? _clipboard;
    private PasteService? _paste;
    private KeyboardHook? _hook;
    private HookSupervisor? _hookSupervisor;
    private System.Windows.Threading.DispatcherTimer? _diagnosticsTimer;
    private GlobalHotkeyService? _hotkeys;
    private TrayService? _tray;
    private HistoryWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private GuideWindow? _guideWindow;
    private IntPtr _historyTarget;
    /// <summary>現在のクリップボードのテキスト（履歴一覧の印用）。テキストでない・除外指定付き・不明なら null。</summary>
    private string? _currentClipText;
    private bool _pasting;
    private bool _disposed;

    public AppHost(Application app, CultureInfo windowsUiCulture)
    {
        _app = app;
        _windowsUiCulture = windowsUiCulture;
        // 設定を読む前に出る可能性のあるメッセージ用に、まず Windows の表示言語で決める。
        _uiCulture = UiLanguage.Resolve(UiLanguage.System, windowsUiCulture);
        UiLanguage.Apply(_uiCulture);
    }

    public AppSettings Settings => _settings.Clone();

    public bool AutoStartEnabled => AutoStartService.IsEnabled();

    public event EventHandler? HotkeyStatusChanged;

    public event Action<Shortcut>? ShortcutCaptured;

    public void Start()
    {
        var load = _store.Load();
        _settings = load.Settings;
        // 画面・トレイを作る前に、設定の表示言語を適用する。
        _uiCulture = UiLanguage.Resolve(_settings.Language, _windowsUiCulture);
        UiLanguage.Apply(_uiCulture);
        foreach (var fix in load.Fixes) ErrorLog.Write("設定値を補正: " + fix);
        DiagnosticLog.Add($"起動 v{AppVersion} Store版={PackageInfo.IsPackaged} OS={Environment.OSVersion.Version} " +
                          $"Windows の起動から={Environment.TickCount64 / 1000}s 設定={_settings.PasteLatestTrigger}/{_settings.DoubleTapKey}");
        _history.MaxCount = _settings.MaxHistoryCount;
        _history.MergeDuplicates = _settings.MergeDuplicates;
        _history.Changed += (_, _) => OnHistoryChanged();

        _tray = new TrayService();
        _tray.ShowHistoryRequested += (_, _) => ShowHistory(IntPtr.Zero);
        _tray.ClearRequested += (_, _) => _history.Clear();
        _tray.GuideRequested += (_, _) => ShowGuide();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ExitRequested += (_, _) => Exit();

        _clipboard = new ClipboardService(_app.Dispatcher);
        _clipboard.TextCopied += (text, protection) => _history.Add(text, protection);
        _clipboard.ContentChanged += text =>
        {
            _currentClipText = text;
            RefreshHistoryWindow();
        };
        _paste = new PasteService(_clipboard);

        _historyWindow = new HistoryWindow();
        new WindowInteropHelper(_historyWindow).EnsureHandle(); // 初回表示を速くするため先に作っておく
        _historyWindow.ItemChosen += OnItemChosen;
        _historyWindow.DeleteRequested += item => _history.Remove(item);
        _historyWindow.Cancelled += () => RestoreFocus(_historyTarget);

        _hook = new KeyboardHook(_app.Dispatcher);
        _hook.DoubleTapped += foreground => _ = PasteCurrentAsync(foreground);
        _hook.ShortcutCaptured += shortcut => ShortcutCaptured?.Invoke(shortcut);
        // 登録失敗の再試行・無通知で外されたときの回復（V2.1.0）。再試行しても登録できなければ案内する。
        _hookSupervisor = new HookSupervisor(_hook, _app.Dispatcher, OnHookNotice);

        _hotkeys = new GlobalHotkeyService();
        _hotkeys.Pressed += OnHotkeyPressed;
        _hotkeys.StatusChanged += (_, _) => HotkeyStatusChanged?.Invoke(this, EventArgs.Empty);

        AutoStartService.RefreshPathIfMissing();
        ApplyTriggers();
        _hookSupervisor.Started();
        OnHistoryChanged();
        StartDiagnosticsIfActive(load.Fixes.Any(f => f.StartsWith(nameof(AppSettings.DiagnosticLogUntil), StringComparison.Ordinal)));

        if (load.Status == SettingsLoadStatus.Missing)
        {
            // 初回起動: 常駐して画面を出さないため、起動したことと操作方法を使い方の画面で示す。
            // 既定値を保存し、次回からは初回として扱わない（使い方はトレイの「使い方」でいつでも開ける）。
            try
            {
                _store.Save(_settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write($"設定の保存に失敗: {_store.FilePath}", ex);
            }
            ShowGuide();
        }
        if (load.Status == SettingsLoadStatus.Corrupt)
        {
            ErrorLog.Write($"設定ファイルを読めませんでした（既定値で起動）: {_store.FilePath} {load.Error} 退避先: {load.BackupPath}");
            _tray.ShowBalloon("PlainClip", string.Format(Strings.SettingsCorruptFormat, _store.FilePath), warning: true);
        }
        if (!_clipboard.IsListening)
            _tray.ShowBalloon("PlainClip", Strings.ClipboardListenFailed, warning: true);
        var failed = FailedHotkeys();
        if (failed.Length > 0)
            _tray.ShowBalloon("PlainClip", string.Format(Strings.HotkeysFailedFormat, failed), warning: true);
    }

    /// <summary>2 つ目の起動（スタートメニュー等から起動した）から通知された: 使い方の画面を表示する。</summary>
    public void NotifyAlreadyRunning() => ShowGuide();

    // --- 呼び出し ---

    private void OnHotkeyPressed(HotkeyAction action)
    {
        var foreground = GetForegroundWindow();
        if (action == HotkeyAction.PasteLatest) _ = PasteCurrentAsync(foreground);
        else if (action == HotkeyAction.ShowHistory) ToggleHistory(foreground);
        else if (action.NumberOf() > 0) _ = PasteNumberAsync(action.NumberOf(), foreground);
    }

    private void ToggleHistory(IntPtr foreground)
    {
        if (_historyWindow is { IsVisible: true })
        {
            _historyWindow.HideWindow(restoreFocus: true);
            return;
        }
        // PlainClip の画面（設定画面など）が前面のときは開かない。
        if (WindowHelper.IsOwnWindow(foreground)) return;
        ShowHistory(foreground);
    }

    private void ShowHistory(IntPtr target)
    {
        if (_historyWindow is null) return;
        _historyTarget = target;
        _historyWindow.ShowHistory(_history.Items, copyOnly: !WindowHelper.IsPasteTarget(target), _currentClipText);
    }

    /// <summary>
    /// 現在のクリップボードのテキストを書式なしで貼る（ダブルタップ・通常ショートカット）。
    /// テキストが無い（画像・ファイル等）・読めないときは何も貼らず、過去の履歴で代わりに貼ることもしない。
    /// </summary>
    private async Task PasteCurrentAsync(IntPtr target)
    {
        if (_paste is null || _clipboard is null || _pasting) return;
        if (WindowHelper.IsOwnWindow(target)) return;
        _pasting = true;
        try
        {
            var source = await _clipboard.ReadCurrentAsync();
            if (source.Kind != PasteSourceKind.Text)
            {
                NoticeWindow.Display(DescribeNoText(source.Kind));
                return;
            }
            var text = source.NeedsRewrite ? source.Text : null;
            var result = await _paste.PasteAsync(text, source.Protection, target, sourceSequence: source.Sequence);
            if (text is not null && Wrote(result)) SetCurrentClipText(source.Sensitive ? null : text);
            Report(result);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("貼り付けに失敗", ex);
            NoticeWindow.Display(Strings.NoticeFailed);
        }
        finally
        {
            _pasting = false;
        }
    }

    /// <summary>数字ショートカット: 指定番号（1 = 最新）の履歴を貼る。</summary>
    private async Task PasteNumberAsync(int number, IntPtr target)
    {
        if (_paste is null || _pasting) return;
        if (WindowHelper.IsOwnWindow(target)) return;
        var item = _history.ItemAt(number);
        if (item is null)
        {
            NoticeWindow.Display(string.Format(Strings.NoticeNoHistoryFormat, number));
            return;
        }
        _pasting = true;
        try
        {
            var result = await _paste.PasteAsync(item.Text, item.Protection, target, '0' + number);
            if (Wrote(result)) SetCurrentClipText(item.Text);
            Report(result);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("貼り付けに失敗", ex);
            NoticeWindow.Display(Strings.NoticeFailed);
        }
        finally
        {
            _pasting = false;
        }
    }

    private async void OnItemChosen(ClipItem item, bool copyOnly, int triggerVk)
    {
        if (_historyWindow is null || _paste is null || _clipboard is null || _pasting) return;
        var target = _historyTarget;
        var pasteMode = !copyOnly && !_historyWindow.CopyOnlyMode;
        _historyWindow.HideWindow(restoreFocus: false);
        _pasting = true;
        try
        {
            // 貼り付け・コピーしても履歴は並べ替えない（V2.0.0。番号を固定する）。
            if (pasteMode)
            {
                var result = await _paste.PasteAsync(item.Text, item.Protection, target, triggerVk);
                if (Wrote(result)) SetCurrentClipText(item.Text);
                Report(result);
            }
            else
            {
                var ok = await _clipboard.SetTextAsync(item.Text, item.Protection) is not null;
                RestoreFocus(target);
                if (ok) SetCurrentClipText(item.Text);
                NoticeWindow.Display(ok ? Strings.NoticeCopied : Strings.NoticeFailed);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("貼り付けに失敗", ex);
            NoticeWindow.Display(Strings.NoticeFailed);
        }
        finally
        {
            _pasting = false;
        }
    }

    /// <summary>貼り付けの結果のうち、貼れなかった場合だけ案内する（貼れたときは何も出さない）。</summary>
    private static void Report(PasteResult result)
    {
        var message = result switch
        {
            PasteResult.CopiedOnly => Strings.NoticeCopiedOnly,
            PasteResult.Failed => Strings.NoticeFailed,
            PasteResult.TargetElevated => Strings.NoticeElevated,
            PasteResult.ClipboardChanged => Strings.NoticeClipboardChanged,
            _ => null,
        };
        if (message is not null) NoticeWindow.Display(message, seconds: 3.5);
    }

    /// <summary>PlainClip が書いた内容がクリップボードに残っているか（失敗・待っている間に他のアプリが変えた場合は残っていない）。</summary>
    private static bool Wrote(PasteResult result) => result is not (PasteResult.Failed or PasteResult.ClipboardChanged);

    private static string DescribeNoText(PasteSourceKind kind) => kind switch
    {
        PasteSourceKind.Files => Strings.NoticeFiles,
        PasteSourceKind.Empty => Strings.NoticeEmpty,
        PasteSourceKind.TooLong => Strings.NoticeTooLong,
        PasteSourceKind.Unavailable => Strings.NoticeUnavailable,
        _ => Strings.NoticeNoText,
    };

    /// <summary>PlainClip が書き込んだ内容を「現在のクリップボード」にする（自分の書き込みは監視から通知されないため）。</summary>
    private void SetCurrentClipText(string? text)
    {
        _currentClipText = text;
        RefreshHistoryWindow();
    }

    private static void RestoreFocus(IntPtr target)
    {
        if (WindowHelper.IsPasteTarget(target)) SetForegroundWindow(WindowHelper.RootOf(target));
    }

    private void OnHistoryChanged()
    {
        RefreshHistoryWindow();
        _tray?.UpdateStatus(DescribeShowHistoryTrigger(), _history.Count);
    }

    private void RefreshHistoryWindow() => _historyWindow?.Refresh(_history.Items, _currentClipText);

    // --- 使い方・設定 ---

    private void ShowGuide()
    {
        if (_guideWindow is not null)
        {
            _guideWindow.Activate();
            return;
        }
        _historyWindow?.HideWindow(restoreFocus: false);
        _guideWindow = new GuideWindow(BuildGuideContent(), ShowSettings);
        _guideWindow.Closed += (_, _) => _guideWindow = null;
        _guideWindow.Show();
        WindowHelper.ForceForeground(new WindowInteropHelper(_guideWindow).Handle);
        _guideWindow.Activate();
    }

    private GuideContent BuildGuideContent()
    {
        var paste = _settings.PasteLatestTrigger switch
        {
            TriggerMode.DoubleTap => _settings.DoubleTapKey.TwiceText(),
            TriggerMode.Shortcut => _settings.PasteLatestShortcutValue.ToString(),
            _ => null,
        };
        var history = _settings.ShowHistoryTrigger == TriggerMode.Shortcut ? _settings.ShowHistoryShortcutValue.ToString() : null;
        var modifiers = _settings.NumberPasteModifiersValue;
        var numbers = modifiers == KeyModifiers.None ? null : string.Format(Strings.NumberKeysFormat, NumberPaste.Format(modifiers));
        return new GuideContent(paste, history, numbers);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _historyWindow?.HideWindow(restoreFocus: false);
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        WindowHelper.ForceForeground(new WindowInteropHelper(_settingsWindow).Handle);
        _settingsWindow.Activate();
    }

    public string? ApplySettings(AppSettings settings, bool autoStart)
    {
        _settings = settings.Clone();
        _history.MergeDuplicates = _settings.MergeDuplicates;
        _history.MaxCount = _settings.MaxHistoryCount;
        ApplyTriggers();
        OnHistoryChanged();

        var errors = new List<string>();
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"設定の保存に失敗: {_store.FilePath}", ex);
            errors.Add(string.Format(Strings.SettingsSaveFailedFormat, _store.FilePath, ex.Message));
        }
        if (autoStart != AutoStartService.IsEnabled() && AutoStartService.SetEnabled(autoStart) is { } autoError)
            errors.Add(string.Format(Strings.AutoStartFailedFormat, autoError));
        return errors.Count == 0 ? null : string.Join("\n", errors);
    }

    public HotkeyRegistration GetHotkey(HotkeyAction action) =>
        _hotkeys?.Get(action) ?? HotkeyRegistration.NotUsed;

    public int ProbeShortcut(Shortcut shortcut)
    {
        if (_hotkeys is null) return 0;
        // PlainClip 自身がいま登録しているキー（適用時に解除される）は「空いている」扱いにする。
        return _hotkeys.IsRegisteredByUs(shortcut) ? 0 : _hotkeys.Probe(shortcut);
    }

    public void BeginShortcutInput()
    {
        _hotkeys?.Suspend();
        _hookSupervisor?.SetCapture(true);
    }

    public void EndShortcutInput()
    {
        _hookSupervisor?.SetCapture(false);
        _hotkeys?.Resume();
    }

    /// <summary>呼び出し方法を反映する: ダブルタップはフック、ショートカットは RegisterHotKey（旧解除 → 新登録）。</summary>
    private void ApplyTriggers()
    {
        var tap = _settings.PasteLatestTrigger == TriggerMode.DoubleTap;
        _hookSupervisor?.SetDoubleTap(tap, _settings.DoubleTapKey);
        _hotkeys?.Apply(
            _settings.PasteLatestTrigger == TriggerMode.Shortcut ? _settings.PasteLatestShortcutValue : Shortcut.None,
            _settings.ShowHistoryTrigger == TriggerMode.Shortcut ? _settings.ShowHistoryShortcutValue : Shortcut.None,
            _settings.NumberPasteModifiersValue);
    }

    /// <summary>キー監視の回復の結果を知らせる（V2.1.0）。</summary>
    private void OnHookNotice(HookNotice notice, int error)
    {
        if (notice == HookNotice.Failed) _tray?.ShowBalloon("PlainClip", string.Format(Strings.HookFailedFormat, error), warning: true);
    }

    // --- 診断ログ（V2.1.0。PROJECT.md「診断ログ」） ---

    private static string AppVersion => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public DateTime? DiagnosticLogUntil => DiagnosticMode.IsActive(_settings.DiagnosticLogUntil, DateTime.UtcNow) ? _settings.DiagnosticLogUntil : null;

    public string DiagnosticLogFolder => DiagnosticLog.Folder;

    /// <summary>記録を止めた後に残っている診断ログを自動で削除する時刻（UTC。V2.2.0）。記録中・ログが無いときは null。</summary>
    public DateTime? DiagnosticLogDeleteAt =>
        !DiagnosticLog.Recording && DiagnosticLog.LastWriteUtc() is { } last ? DiagnosticMode.DeleteAt(last) : null;

    public event EventHandler? DiagnosticLogChanged;

    /// <summary>起動時: 記録の期間中なら記録を続ける。期限切れで止めた（設定を補正した）なら保存し直す。</summary>
    private void StartDiagnosticsIfActive(bool expiredAtLoad)
    {
        if (expiredAtLoad) SaveSettingsQuietly();
        if (DiagnosticLogUntil is not null) DiagnosticLog.Start(DiagnosticHeader());
        DeleteExpiredDiagnosticLog();
        // 期限の確認（10 分ごと）。止め忘れても 7 日で止まり、止めてから 7 日でログを消す。
        _diagnosticsTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, _app.Dispatcher)
        {
            Interval = TimeSpan.FromMinutes(10),
        };
        _diagnosticsTimer.Tick += (_, _) =>
        {
            if (DiagnosticLog.Recording && DiagnosticLogUntil is null) SetDiagnosticLog(false, "期限切れ");
            DeleteExpiredDiagnosticLog();
        };
        _diagnosticsTimer.Start();
    }

    /// <summary>診断ログの記録を始める・止める。設定の保存に失敗したらメッセージを返す（記録の開始・停止自体は行う）。</summary>
    public string? SetDiagnosticLog(bool enable) => SetDiagnosticLog(enable, "利用者の操作");

    private string? SetDiagnosticLog(bool enable, string reason)
    {
        if (enable)
        {
            _settings.DiagnosticLogUntil = DiagnosticMode.NewUntil(DateTime.UtcNow);
            DiagnosticLog.Start(DiagnosticHeader());
        }
        else
        {
            _settings.DiagnosticLogUntil = null;
            if (DiagnosticLog.Recording) DiagnosticLog.Stop(reason);
        }
        var error = SaveSettingsQuietly();
        DiagnosticLogChanged?.Invoke(this, EventArgs.Empty);
        return error is null ? null : string.Format(Strings.SettingsSaveFailedFormat, _store.FilePath, error);
    }

    /// <summary>残っている診断ログを削除する（設定画面の「削除」）。消せなければメッセージを返す。</summary>
    public string? DeleteDiagnosticLog()
    {
        var error = DiagnosticLog.Delete();
        DiagnosticLogChanged?.Invoke(this, EventArgs.Empty);
        return error is null ? null : string.Format(Strings.DiagnosticsDeleteFailedFormat, DiagnosticLog.Folder, error);
    }

    /// <summary>記録を止めてから 7 日たったログを削除する（起動時と 10 分ごと）。</summary>
    private void DeleteExpiredDiagnosticLog()
    {
        if (!DiagnosticMode.ShouldDelete(DiagnosticLog.Recording, DiagnosticLog.LastWriteUtc(), DateTime.UtcNow)) return;
        DiagnosticLog.Delete();
        DiagnosticLogChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>診断ログの見出し（版・環境・今の状態。本文・入力・ウィンドウ名・パスは含めない）。</summary>
    private IEnumerable<string> DiagnosticHeader()
    {
        var now = DateTime.Now;
        yield return "==== PlainClip 診断ログ ====";
        yield return $"記録開始 {now:yyyy-MM-dd HH:mm:ss}（期限 {_settings.DiagnosticLogUntil?.ToLocalTime():yyyy-MM-dd HH:mm}）";
#if MSIX
        const string build = "Store 用（MSIX）";
#else
        const string build = "通常";
#endif
        yield return $"PlainClip v{AppVersion} ビルド={build} Store版として実行={PackageInfo.IsPackaged} .NET={Environment.Version} OS={Environment.OSVersion.Version} 64bit={Environment.Is64BitProcess}";
        yield return $"表示言語={_uiCulture.Name} 貼り付けの呼び出し={_settings.PasteLatestTrigger} キー={_settings.DoubleTapKey} 自動起動={AutoStartService.IsEnabled()}";
        yield return $"Windows の起動から={Environment.TickCount64 / 1000}s PlainClip の起動から={(int)(now - Process.GetCurrentProcess().StartTime).TotalSeconds}s";
        if (_hookSupervisor is not null) yield return _hookSupervisor.DescribeState();
    }

    /// <summary>設定を保存する。失敗したら理由を返す。</summary>
    private string? SaveSettingsQuietly()
    {
        try
        {
            _store.Save(_settings);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"設定の保存に失敗: {_store.FilePath}", ex);
            return ex.Message;
        }
    }

    /// <summary>保存先をエクスプローラーで開く（ファイルがあれば選んだ状態で）。開けなければ理由を返す。</summary>
    public string? OpenDiagnosticLogFolder()
    {
        try
        {
            DiagnosticLog.Flush();
            Directory.CreateDirectory(DiagnosticLog.Folder);
            var args = File.Exists(DiagnosticLog.FilePath) ? $"/select,\"{DiagnosticLog.FilePath}\"" : $"\"{DiagnosticLog.Folder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ErrorLog.Write($"診断ログの保存先を開けませんでした: {DiagnosticLog.Folder}", ex);
            return ex.Message;
        }
    }

    private string DescribeShowHistoryTrigger() =>
        _settings.ShowHistoryTrigger == TriggerMode.Shortcut ? _settings.ShowHistoryShortcutValue.ToString() : "";

    private string FailedHotkeys() => string.Join(", ",
        HotkeyActions.All
            .Select(GetHotkey)
            .Where(r => r.Status == HotkeyStatus.Failed)
            .Select(r => r.Shortcut.ToString()));

    // --- 表示言語 ---

    public bool OfferRestartIfLanguageChanged(Window owner)
    {
        var wanted = UiLanguage.Resolve(_settings.Language, _windowsUiCulture);
        if (wanted.Name == _uiCulture.Name) return false;

        // 選んだ言語で尋ねる（今の言語が読めない利用者でも分かるように）。履歴はメモリ上だけなので消えることも伝える。
        var message = UiLanguage.Get(nameof(Strings.RestartPrompt), wanted);
        var answer = MessageBox.Show(owner, message, "PlainClip", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer == MessageBoxResult.Yes && Restart();
    }

    /// <summary>
    /// 新しいプロセスを --restart 付きで起動してから終了する。
    /// 新しいプロセスは、このプロセスが多重起動防止を解放するまで待ってから起動する。
    /// </summary>
    private bool Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("The executable path is not available.");
            Process.Start(new ProcessStartInfo(exe, App.RestartArgument) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            ErrorLog.Write("再起動に失敗", ex);
            MessageBox.Show(Strings.RestartFailed, "PlainClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        ErrorLog.Write($"表示言語の変更のため再起動: {_uiCulture.Name} → {UiLanguage.Resolve(_settings.Language, _windowsUiCulture).Name}");
        Exit();
        return true;
    }

    // --- 終了 ---

    /// <summary>画面操作中の想定外の例外の後、ポップアップを閉じて待機状態へ戻す。</summary>
    public void RecoverFromError()
    {
        _pasting = false;
        try { _historyWindow?.HideWindow(restoreFocus: false); }
        catch (Exception ex) { ErrorLog.Write("エラー後の復帰に失敗", ex); }
    }

    private void Exit()
    {
        Dispose();
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 回復の予約・購読を止めてから、フックとショートカットを確実に解除する。
        DiagnosticLog.Add("終了");
        _diagnosticsTimer?.Stop();
        _hookSupervisor?.Dispose();
        _hook?.Dispose();
        _hotkeys?.Dispose();
        DiagnosticLog.Flush();
        _clipboard?.Dispose();
        NoticeWindow.CloseCurrent();
        _guideWindow?.Close();
        _settingsWindow?.Close();
        _historyWindow?.CloseForExit();
        _tray?.Dispose();
        _history.Clear(); // 履歴はメモリ上だけ。終了時に破棄する
    }
}
