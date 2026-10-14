using Microsoft.Win32;
using PlainClip.Core.Localization;

namespace PlainClip.Services;

/// <summary>
/// Windows ログイン時の自動起動（PROJECT.md「設定画面・設定保存」）。HKCU\Software\Microsoft\Windows\CurrentVersion\Run の値 "PlainClip" で管理する。
/// レジストリの状態をそのまま正とし、設定ファイルには保存しない（タスクマネージャーのスタートアップで無効化された場合とも矛盾しない）。
/// MSIX 版（パッケージで動いているとき）は、マニフェストのスタートアップタスク "PlainClipStartup" で管理する（PROJECT.md「Microsoft Store（MSIX）」）。
/// </summary>
public static partial class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PlainClip";

    public static bool IsEnabled()
    {
        if (PackageInfo.IsPackaged) return PackagedIsEnabled();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>有効・無効を切り替える。失敗したらエラーメッセージを返す。</summary>
    public static string? SetEnabled(bool enabled)
    {
        if (PackageInfo.IsPackaged) return PackagedSetEnabled(enabled);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return Strings.AutoStartNoPath;
                key.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 自動起動が有効なのに登録先の exe が存在しない場合（exe を移動した）だけ、今の exe へ更新する。
    /// 登録先が存在する場合は変えない（開発中に別の場所のビルドを起動しても、配布版の登録を書き換えないため）。
    /// </summary>
    public static void RefreshPathIfMissing()
    {
        if (PackageInfo.IsPackaged) return; // MSIX 版はレジストリを使わない（書いても仮想化されて効かない）
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string current || current.Length == 0) return;
            if (System.IO.File.Exists(current.Trim('"'))) return;
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            key.SetValue(ValueName, $"\"{path}\"");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            ErrorLog.Write("Failed to update the auto-start path", ex);
        }
    }
}
