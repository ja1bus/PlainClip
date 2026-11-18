using System.IO;
using System.Text;
using PlainClip.Core.Diagnostics;
using PlainClip.Core.Settings;

namespace PlainClip.Services;

/// <summary>
/// %LocalAppData%\PlainClip\error.log への記録。クリップボードの内容（履歴の本文）は決して書かない。
/// V2.3.0 から大きさに上限を設ける（512KB を超えそうになったら error.old.log へ移す。1 世代。<see cref="RollingLogFile"/>）。
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.DefaultDirectory, "error.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                // 例外の詳細を含む複数行も 1 件として渡す（世代の切り替えで分かれないように）。
                var entry = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(message);
                if (exception is not null) entry.Append("\r\n").Append(exception.ToString().TrimEnd());
                new RollingLogFile(FilePath).Append([entry.ToString()]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログが書けなくても本体の動作は続ける。
        }
    }
}
