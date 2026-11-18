using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PlainClip.Core.Localization;
using static PlainClip.Interop.NativeMethods;

namespace PlainClip.Services;

/// <summary>
/// タスクトレイのアイコンとメニュー。WinForms の NotifyIcon を使う（WindowsDesktop 共有フレームワーク内）。
/// メニュー: 履歴を表示 / 履歴をクリア / クリップボードを書式なしに変換（V2.3.0）/ 使い方 / 設定 / 終了
/// （V2.2.0 で「キー監視を再設定」を廃止）。アイコンのクリックでも履歴を表示する。
/// 2 回押しの監視が停止中のときは、灰色＋右下の赤い印のアイコンとツールチップで示す（V2.3.0）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private readonly Icon? _stoppedIcon;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _convertItem;
    private bool _stopped;

    public TrayService()
    {
        _ownedIcon = LoadAppIcon();
        _stoppedIcon = CreateStoppedIcon(_ownedIcon);
        var menu = new ContextMenuStrip();
        _showItem = new ToolStripMenuItem(Strings.TrayShowHistory, null, (_, _) => ShowHistoryRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        };
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayClearHistory, null, (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty)));
        _convertItem = new ToolStripMenuItem(Strings.TrayConvertClipboard, null, (_, _) => ConvertRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_convertItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayGuide, null, (_, _) => GuideRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem(Strings.TraySettings, null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = NormalIcon,
            Text = "PlainClip",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowHistoryRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? ShowHistoryRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? ConvertRequested;
    public event EventHandler? GuideRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    private Icon NormalIcon => _ownedIcon ?? SystemIcons.Application;

    /// <summary>
    /// ツールチップとメニューに現在の呼び出し方法と件数を表示する。
    /// <paramref name="tapStopped"/> は 2 回押しの監視が停止中か（V2.3.0。2 回押しを使わない設定では false）。
    /// </summary>
    public void UpdateStatus(string showHistoryTrigger, string convertTrigger, int count, bool tapStopped)
    {
        _showItem.ShortcutKeyDisplayString = showHistoryTrigger;
        _convertItem.ShortcutKeyDisplayString = convertTrigger;
        var tip = string.Format(tapStopped ? Strings.TrayTooltipStoppedFormat : Strings.TrayTooltipFormat, count);
        _icon.Text = tip.Length > 127 ? tip[..127] : tip; // NotifyIcon.Text は 127 文字まで
        if (tapStopped != _stopped)
        {
            _stopped = tapStopped;
            _icon.Icon = tapStopped ? _stoppedIcon ?? NormalIcon : NormalIcon;
        }
    }

    public void ShowBalloon(string title, string text, bool warning)
    {
        _icon.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _ownedIcon?.Dispose();
        _stoppedIcon?.Dispose();
    }

    /// <summary>exe に埋め込まれたアイコン（csproj の ApplicationIcon）を通知領域の大きさで取り出す。</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            return Icon.ExtractIcon(path, 0, SystemInformation.SmallIconSize.Width);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 停止中のアイコン（V2.3.0）: アプリのアイコンを灰色にし、右下に赤い円を重ねる。起動時に 1 回だけ作る。
    /// 作れなければ null（通常のアイコンのまま、ツールチップだけで示す）。
    /// </summary>
    private static Icon? CreateStoppedIcon(Icon? source)
    {
        var size = SystemInformation.SmallIconSize;
        IntPtr handle = IntPtr.Zero;
        try
        {
            using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var original = (source ?? SystemIcons.Application).ToBitmap())
                using (var attributes = new ImageAttributes())
                {
                    // 輝度で灰色にし、少し薄くする。
                    attributes.SetColorMatrix(new ColorMatrix(
                    [
                        [0.30f, 0.30f, 0.30f, 0, 0],
                        [0.59f, 0.59f, 0.59f, 0, 0],
                        [0.11f, 0.11f, 0.11f, 0, 0],
                        [0, 0, 0, 0.85f, 0],
                        [0.05f, 0.05f, 0.05f, 0, 1],
                    ]));
                    g.DrawImage(original, new Rectangle(0, 0, size.Width, size.Height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel, attributes);
                }
                var dot = Math.Max(6, size.Width * 7 / 16);
                var bounds = new Rectangle(size.Width - dot, size.Height - dot, dot - 1, dot - 1);
                using var red = new SolidBrush(Color.FromArgb(0xD1, 0x34, 0x38));
                using var edge = new Pen(Color.White, Math.Max(1f, size.Width / 16f));
                g.FillEllipse(red, bounds);
                g.DrawEllipse(edge, bounds);
            }
            handle = bitmap.GetHicon();
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone(); // 複製（自分のハンドルを持つ）を残し、GetHicon のハンドルは下で解放する
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException or OutOfMemoryException)
        {
            ErrorLog.Write("停止中のアイコンを作れませんでした", ex);
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) DestroyIcon(handle);
        }
    }
}
