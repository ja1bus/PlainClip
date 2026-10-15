using System.Runtime.InteropServices;
using System.Text;
using PlainClip.Core.Text;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Services;

public enum PasteResult
{
    /// <summary>クリップボードへ設定し、Ctrl+V を送った。</summary>
    Pasted,

    /// <summary>クリップボードへ設定しただけ（貼り付け先が無い・キーが離されない等）。</summary>
    CopiedOnly,

    /// <summary>クリップボードへ設定できなかった。</summary>
    Failed,

    /// <summary>
    /// 貼り付け先が管理者として実行中（PlainClip は通常の権限）。UIPI により Ctrl+V が届かないことが多い。
    /// Ctrl+V は送ったうえで（判定が外れても貼れるように）、利用者に Ctrl+V を押すよう案内する。
    /// </summary>
    TargetElevated,

    /// <summary>
    /// 待っている間に他のアプリ・利用者がクリップボードを変えたため、Ctrl+V を送らなかった（V2.0.1）。新しい内容は上書きしない。
    /// </summary>
    ClipboardChanged,
}

/// <summary>
/// PlainClip からの貼り付け（PROJECT.md「貼り付け処理」）。
/// 1. Unicode プレーンテキストをクリップボードへ設定 → 2. 元のウィンドウを前面に戻す →
/// 3. 修飾キー（ショートカットで押している Ctrl・Alt 等）が離されるのを待つ → 4. SendInput で Ctrl+V。
/// 貼り付け後に元の HTML/RTF 付きクリップボードは復元しない（V1 仕様）。
/// Ctrl+V の直前に、クリップボードが設定した（読んだ）ときのままかをシーケンス番号で確かめ、変わっていたら送らない（V2.0.1）。
/// </summary>
public sealed class PasteService
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const int FocusTimeoutMs = 500;
    private const int KeyReleaseTimeoutMs = 2000;
    private const int PollMs = 10;
    /// <summary>前面になった直後の貼り付け先がキー入力を受け付けられるまでの余裕。</summary>
    private const int SettleMs = 40;

    private static readonly int[] HeldKeysToWait = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift・Ctrl・Alt・左右 Win

    private readonly ClipboardService _clipboard;

    public PasteService(ClipboardService clipboard)
    {
        _clipboard = clipboard;
    }

    /// <param name="text">クリップボードへ設定する本文。null ならクリップボードはそのまま（すでにプレーンテキスト）で Ctrl+V だけを送る。</param>
    /// <param name="protection">設定するときに付ける保護指定（パスワード管理ソフト等の「履歴に残さない」、クラウド同期の禁止）。</param>
    /// <param name="target">貼り付け先のトップレベルウィンドウ。貼り付け先として不適切なら設定だけ行う。</param>
    /// <param name="triggerVk">操作に使ったキー（Enter・数字キー等）。離されるまで Ctrl+V を送らない。0 なら無し。</param>
    /// <param name="sourceSequence"><paramref name="text"/> が null のとき、クリップボードを読んだ時点のシーケンス番号。</param>
    public async Task<PasteResult> PasteAsync(string? text, ClipProtection protection, IntPtr target, int triggerVk = 0, uint sourceSequence = 0)
    {
        var expected = sourceSequence;
        if (text is not null)
        {
            if (await _clipboard.SetTextAsync(text, protection) is not { } written) return PasteResult.Failed;
            expected = written;
        }
        if (!WindowHelper.IsPasteTarget(target)) return CopiedOnly();
        var elevated = WindowHelper.IsElevatedTarget(target);

        if (!await ActivateAsync(target))
        {
            ErrorLog.Write("貼り付け先のウィンドウを前面にできませんでした（クリップボードへの設定のみ）");
            return CopiedOnly();
        }
        if (!await WaitForKeysReleasedAsync(triggerVk))
        {
            ErrorLog.Write("修飾キーが離されなかったため Ctrl+V を送りませんでした（クリップボードへの設定のみ）");
            return CopiedOnly();
        }
        await Task.Delay(SettleMs);
        if (WindowHelper.RootOf(GetForegroundWindow()) != WindowHelper.RootOf(target)) return CopiedOnly();
        // 送信と貼り付け先の読み取りを不可分にはできないが、前面化・キー待ちの長い間に変わったものは貼らない。
        if (!PasteGuard.ClipboardUnchanged(expected, GetClipboardSequenceNumber()))
        {
            ErrorLog.Write("待っている間にクリップボードが変わったため Ctrl+V を送りませんでした");
            return PasteResult.ClipboardChanged;
        }
        if (!SendCtrlV()) return elevated ? PasteResult.TargetElevated : PasteResult.CopiedOnly;
        // UIPI で遮られても SendInput は成功を返すため、管理者のウィンドウは結果を区別して案内する。
        return elevated ? PasteResult.TargetElevated : PasteResult.Pasted;

        // 貼らずに終えるとき、待っている間にクリップボードが変わっていたら「Ctrl+V を押して」とは案内しない。
        PasteResult CopiedOnly() =>
            PasteGuard.ClipboardUnchanged(expected, GetClipboardSequenceNumber()) ? PasteResult.CopiedOnly : PasteResult.ClipboardChanged;
    }

    private static async Task<bool> ActivateAsync(IntPtr target)
    {
        var root = WindowHelper.RootOf(target);
        if (WindowHelper.RootOf(GetForegroundWindow()) == root) return true;
        if (IsIconic(root)) ShowWindow(root, SW_RESTORE);
        WindowHelper.ForceForeground(root);
        for (var waited = 0; waited < FocusTimeoutMs; waited += PollMs)
        {
            if (WindowHelper.RootOf(GetForegroundWindow()) == root) return true;
            await Task.Delay(PollMs);
        }
        return false;
    }

    private static async Task<bool> WaitForKeysReleasedAsync(int triggerVk)
    {
        for (var waited = 0; waited < KeyReleaseTimeoutMs; waited += PollMs)
        {
            if (!AnyHeld(triggerVk)) return true;
            await Task.Delay(PollMs);
        }
        return false;
    }

    private static bool AnyHeld(int triggerVk)
    {
        foreach (var vk in HeldKeysToWait)
            if (GetAsyncKeyState(vk) < 0) return true;
        return triggerVk != 0 && GetAsyncKeyState(triggerVk) < 0;
    }

    private static bool SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, up: false),
            Key(VK_V, up: false),
            Key(VK_V, up: true),
            Key(VK_CONTROL, up: true),
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length) return true;
        // 管理者として動くウィンドウ（UIPI）などへは送れない。
        ErrorLog.Write($"SendInput に失敗（送信 {sent}/{inputs.Length}、エラー {Marshal.GetLastWin32Error()}）");
        return false;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                dwExtraInfo = KeyboardHook.InjectionSignature,
            },
        },
    };
}

/// <summary>ウィンドウの判定と前面化の小道具。</summary>
public static class WindowHelper
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    public static IntPtr RootOf(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == OwnProcessId;
    }

    /// <summary>
    /// 貼り付け先として扱えるか。PlainClip 自身・タスクバー・デスクトップは対象外
    /// （タスクトレイのメニューから履歴を開いた場合など。このときはクリップボードへの設定だけ行う）。
    /// </summary>
    public static bool IsPasteTarget(IntPtr hwnd)
    {
        var root = RootOf(hwnd);
        if (root == IntPtr.Zero || !IsWindow(root) || !IsWindowVisible(root) || IsOwnWindow(root)) return false;
        var sb = new StringBuilder(64);
        GetClassName(root, sb, sb.Capacity);
        return sb.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "NotifyIconOverflowWindow");
    }

    private static readonly bool SelfElevated = IsProcessElevated(GetCurrentProcess(), out _);

    /// <summary>
    /// 貼り付け先が管理者として実行中で、PlainClip（通常の権限）からは Ctrl+V が届かない可能性が高いか。
    /// トークンを読めない（アクセス拒否）場合も、昇格したプロセスとみなす。
    /// </summary>
    public static bool IsElevatedTarget(IntPtr hwnd)
    {
        if (SelfElevated) return false;
        var root = RootOf(hwnd);
        if (root == IntPtr.Zero) return false;
        GetWindowThreadProcessId(root, out var pid);
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            return IsProcessElevated(process, out var accessDenied) || accessDenied;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool IsProcessElevated(IntPtr process, out bool accessDenied)
    {
        accessDenied = false;
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
        {
            accessDenied = Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            return false;
        }
        try
        {
            return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) && elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// 前面化の制限を受けずにウィンドウを前面へ出す。直前の入力が他アプリ宛て（ダブルタップ・トレイ操作）だと
    /// SetForegroundWindow が拒否されるため、移動量 0 のマウス入力を合成してから呼ぶ（副作用のない入力）。
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var input = new[]
        {
            new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwExtraInfo = KeyboardHook.InjectionSignature } } },
        };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;

        // 拒否された場合（利用者が他アプリを操作中など）: 前面スレッドの入力状態に一時的に接続して前面化する。
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var ownThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == ownThread)
        {
            SetForegroundWindow(hwnd);
            return;
        }
        if (!AttachThreadInput(ownThread, foregroundThread, true)) return;
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            AttachThreadInput(ownThread, foregroundThread, false);
        }
    }
}
