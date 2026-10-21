using System.Windows.Forms;
using PlainClip.Core.Localization;

namespace PlainClip.Services;

/// <summary>
/// タスクトレイのアイコンとメニュー。WinForms の NotifyIcon を使う（WindowsDesktop 共有フレームワーク内）。
/// メニュー: 履歴を表示 / 履歴をクリア / 使い方 / 設定 / 終了（V2.2.0 で「キー監視を再設定」を廃止）。アイコンのクリックでも履歴を表示する。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Drawing.Icon? _ownedIcon;
    private readonly ToolStripMenuItem _showItem;

    public TrayService()
    {
        _ownedIcon = LoadAppIcon();
        var menu = new ContextMenuStrip();
        _showItem = new ToolStripMenuItem(Strings.TrayShowHistory, null, (_, _) => ShowHistoryRequested?.Invoke(this, EventArgs.Empty))
        {
            Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold),
        };
        menu.Items.Add(_showItem);
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayClearHistory, null, (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayGuide, null, (_, _) => GuideRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripMenuItem(Strings.TraySettings, null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.TrayExit, null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _icon = new NotifyIcon
        {
            Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application,
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
    public event EventHandler? GuideRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

    /// <summary>ツールチップとメニューに現在の呼び出し方法と件数を表示する。</summary>
    public void UpdateStatus(string showHistoryTrigger, int count)
    {
        _showItem.ShortcutKeyDisplayString = showHistoryTrigger;
        var tip = string.Format(Strings.TrayTooltipFormat, count);
        _icon.Text = tip.Length > 127 ? tip[..127] : tip; // NotifyIcon.Text は 127 文字まで
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
    }

    /// <summary>exe に埋め込まれたアイコン（csproj の ApplicationIcon）を通知領域の大きさで取り出す。</summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;
            return System.Drawing.Icon.ExtractIcon(path, 0, SystemInformation.SmallIconSize.Width);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            return null;
        }
    }
}
