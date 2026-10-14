using System.Runtime.InteropServices;
using System.Windows.Interop;
using PlainClip.Core.Input;
using PlainClip.Core.Localization;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Services;

/// <summary>ショートカットで呼ぶ機能。値は RegisterHotKey の ID。Number1～Number9 は数字ショートカット（V2.0.0）。</summary>
public enum HotkeyAction
{
    PasteLatest = 1,
    ShowHistory = 2,
    Number1 = 11,
    Number2,
    Number3,
    Number4,
    Number5,
    Number6,
    Number7,
    Number8,
    Number9,
}

public static class HotkeyActions
{
    /// <summary>登録する順（先に登録したものが優先。同じキーは後の機能が「重複」で失敗する）。</summary>
    public static readonly IReadOnlyList<HotkeyAction> All =
        [HotkeyAction.PasteLatest, HotkeyAction.ShowHistory, .. Numbers()];

    public static IEnumerable<HotkeyAction> Numbers() =>
        Enumerable.Range(1, NumberPaste.Count).Select(NumberAction);

    public static HotkeyAction NumberAction(int number) => (HotkeyAction)((int)HotkeyAction.Number1 + number - 1);

    /// <summary>数字ショートカットなら番号（1～9）、それ以外は 0。</summary>
    public static int NumberOf(this HotkeyAction action) =>
        action is >= HotkeyAction.Number1 and <= HotkeyAction.Number9 ? action - HotkeyAction.Number1 + 1 : 0;
}

public enum HotkeyStatus
{
    /// <summary>この機能はショートカット方式ではない。</summary>
    NotUsed,
    Registered,
    /// <summary>他のアプリまたは Windows が使用中などで登録できなかった。</summary>
    Failed,
    /// <summary>設定画面での入力中のため一時的に解除している。</summary>
    Suspended,
}

public sealed record HotkeyRegistration(Shortcut Shortcut, HotkeyStatus Status, int ErrorCode = 0)
{
    /// <summary>PlainClip の別の機能と同じキー（登録しない）。</summary>
    public const int DuplicateError = -1;

    public static readonly HotkeyRegistration NotUsed = new(Shortcut.None, HotkeyStatus.NotUsed);

    public string Describe() => Status switch
    {
        HotkeyStatus.NotUsed => "",
        HotkeyStatus.Registered => string.Format(Strings.HotkeyRegisteredFormat, Shortcut),
        HotkeyStatus.Suspended => Strings.HotkeySuspended,
        _ => DescribeError(Shortcut, ErrorCode),
    };

    public static string DescribeError(Shortcut shortcut, int errorCode) => errorCode switch
    {
        ERROR_HOTKEY_ALREADY_REGISTERED => string.Format(Strings.HotkeyInUseFormat, shortcut),
        DuplicateError => string.Format(Strings.HotkeyDuplicateFormat, shortcut),
        _ => string.Format(Strings.HotkeyErrorFormat, shortcut, errorCode),
    };
}

/// <summary>
/// Win32 RegisterHotKey による通常のグローバルショートカット（PROJECT.md「通常グローバルショートカット」「数字ショートカット」）。
/// メッセージ専用ウィンドウで WM_HOTKEY を受ける。登録失敗でも例外にせず、状態として返す。
/// 変更時は「旧ショートカットを解除 → 新ショートカットを登録 → 結果を返す」の順に行う。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<HotkeyAction, Shortcut> _wanted = new();
    private readonly Dictionary<HotkeyAction, HotkeyRegistration> _current = new();
    private readonly HashSet<HotkeyAction> _registered = [];
    private int _suspendCount;

    public GlobalHotkeyService()
    {
        var p = new HwndSourceParameters("PlainClip.Hotkey") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>ショートカットが押された（UI スレッド）。</summary>
    public event Action<HotkeyAction>? Pressed;

    /// <summary>登録状態が変わった。</summary>
    public event EventHandler? StatusChanged;

    public HotkeyRegistration Get(HotkeyAction action) =>
        _current.TryGetValue(action, out var r) ? r : HotkeyRegistration.NotUsed;

    /// <summary>PlainClip がいま登録しているキー（設定画面の空き確認で「空いている」扱いにする）。</summary>
    public bool IsRegisteredByUs(Shortcut shortcut) =>
        _registered.Any(a => _wanted.GetValueOrDefault(a) == shortcut);

    /// <summary>
    /// すべての機能のショートカットを差し替えて登録し直す（使わない機能は <see cref="Shortcut.None"/>、
    /// 数字ショートカットを使わないなら <paramref name="numberModifiers"/> = None）。
    /// </summary>
    public void Apply(Shortcut pasteLatest, Shortcut showHistory, KeyModifiers numberModifiers)
    {
        _wanted[HotkeyAction.PasteLatest] = pasteLatest;
        _wanted[HotkeyAction.ShowHistory] = showHistory;
        for (var n = 1; n <= NumberPaste.Count; n++)
            _wanted[HotkeyActions.NumberAction(n)] = numberModifiers == KeyModifiers.None ? Shortcut.None : NumberPaste.For(numberModifiers, n);
        if (_suspendCount > 0) MarkSuspended();
        else RegisterAll();
    }

    /// <summary>
    /// 試しに登録してすぐ解除し、空いているか確かめる（設定画面の入力時）。0 なら登録できる、それ以外は Win32 エラーコード。
    /// 分かるのは他アプリ・Windows のグローバルショートカットとの重なりだけ（アプリ内だけのショートカットは分からない）。
    /// 入力中は PlainClip 自身の登録を一時解除しているので、自分の現在の割り当てとは重ならない。
    /// </summary>
    public int Probe(Shortcut shortcut)
    {
        const int probeId = 99;
        if (shortcut.IsEmpty) return 0;
        if (!RegisterHotKey(_source.Handle, probeId, (int)shortcut.Modifiers | MOD_NOREPEAT, shortcut.Key))
            return Marshal.GetLastWin32Error();
        UnregisterHotKey(_source.Handle, probeId);
        return 0;
    }

    /// <summary>一時的に解除する（設定画面のショートカット入力中）。<see cref="Resume"/> と対で呼ぶ。</summary>
    public void Suspend()
    {
        if (_suspendCount++ > 0) return;
        UnregisterAll();
        MarkSuspended();
    }

    public void Resume()
    {
        if (_suspendCount == 0 || --_suspendCount > 0) return;
        RegisterAll();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private void RegisterAll()
    {
        UnregisterAll();
        foreach (var action in HotkeyActions.All)
        {
            var shortcut = _wanted.GetValueOrDefault(action);
            if (shortcut.IsEmpty)
            {
                _current[action] = HotkeyRegistration.NotUsed;
                continue;
            }
            if (_registered.Any(a => _wanted[a] == shortcut))
            {
                _current[action] = new HotkeyRegistration(shortcut, HotkeyStatus.Failed, HotkeyRegistration.DuplicateError);
                continue;
            }
            // MOD_NOREPEAT: 押しっぱなしの自動リピートで連続実行しない。
            if (RegisterHotKey(_source.Handle, (int)action, (int)shortcut.Modifiers | MOD_NOREPEAT, shortcut.Key))
            {
                _registered.Add(action);
                _current[action] = new HotkeyRegistration(shortcut, HotkeyStatus.Registered);
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                ErrorLog.Write($"ショートカット {shortcut} の登録に失敗（エラー {error}）");
                _current[action] = new HotkeyRegistration(shortcut, HotkeyStatus.Failed, error);
            }
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered) UnregisterHotKey(_source.Handle, (int)action);
        _registered.Clear();
    }

    private void MarkSuspended()
    {
        foreach (var (action, shortcut) in _wanted)
            _current[action] = shortcut.IsEmpty ? HotkeyRegistration.NotUsed : new HotkeyRegistration(shortcut, HotkeyStatus.Suspended);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var action = (HotkeyAction)wParam.ToInt32();
            if (_registered.Contains(action))
            {
                handled = true;
                Pressed?.Invoke(action);
            }
        }
        return IntPtr.Zero;
    }
}
