using System.IO;
using System.Text;
using PlainClip.Core.Settings;

namespace PlainClip.Services;

/// <summary>%LocalAppData%\PlainClip\error.log への記録。クリップボードの内容（履歴の本文）は決して書かない。</summary>
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
                Directory.CreateDirectory(SettingsStore.DefaultDirectory);
                var text = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').AppendLine(message);
                if (exception is not null) text.AppendLine(exception.ToString());
                File.AppendAllText(FilePath, text.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ログが書けなくても本体の動作は続ける。
        }
    }
}
