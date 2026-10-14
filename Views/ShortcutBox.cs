using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlainClip.Core.Input;
using PlainClip.Core.Localization;

namespace PlainClip.Views;

/// <summary>
/// ショートカットの入力欄。フォーカスしてキーを押すと、その組み合わせ（"Ctrl+Alt+S" 形式）が入る。
/// Backspace / Delete を単独で押すと未割当にする。
/// フォーカス中は <see cref="CaptureStarted"/> / <see cref="CaptureEnded"/> で既存のグローバルショートカットを一時解除してもらう
/// （今のショートカットを押しても、機能が動かずに入力できるように）。
/// </summary>
public sealed class ShortcutBox : TextBox
{
    private Shortcut _shortcut;

    public ShortcutBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        ToolTip = Strings.ShortcutBoxToolTip;
        VerticalContentAlignment = VerticalAlignment.Center;
        UpdateText();
    }

    public event EventHandler? CaptureStarted;
    public event EventHandler? CaptureEnded;
    public event EventHandler? ShortcutChanged;

    public Shortcut Shortcut
    {
        get => _shortcut;
        set
        {
            if (_shortcut == value) return;
            _shortcut = value;
            UpdateText();
            ShortcutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        // 修飾なしの Tab はフォーカス移動に使う。
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return;
        e.Handled = true;

        var modifiers = ToModifiers(Keyboard.Modifiers);
        if (modifiers == KeyModifiers.None && key is Key.Back or Key.Delete)
        {
            Shortcut = Shortcut.None;
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || KeyNames.IsModifierKey(vk)) return; // 修飾キーだけの間は確定しない
        Shortcut = new Shortcut(modifiers, vk);
    }

    private void UpdateText() => Text = _shortcut.IsEmpty ? Strings.ShortcutUnassigned : _shortcut.ToString();

    private static KeyModifiers ToModifiers(ModifierKeys m)
    {
        var r = KeyModifiers.None;
        if (m.HasFlag(ModifierKeys.Control)) r |= KeyModifiers.Control;
        if (m.HasFlag(ModifierKeys.Alt)) r |= KeyModifiers.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) r |= KeyModifiers.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) r |= KeyModifiers.Win;
        return r;
    }
}
