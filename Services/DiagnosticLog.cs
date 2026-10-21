using System.IO;
using PlainClip.Core.Diagnostics;
using PlainClip.Core.Settings;

namespace PlainClip.Services;

/// <summary>
/// サポート用の診断ログ（V2.1.0。PROJECT.md「診断ログ」）。
///  - 直近の記録は常にメモリ上だけに持つ（ファイルには書かない）。<see cref="Start"/> してから（設定画面の隠し操作。7 日で自動停止）だけ
///    diagnostic.log へ書く。記録を始めたときは、それまでのメモリ上の経過も先頭に書く（起動直後の様子も後から取れる）。
///  - <see cref="Add"/> はどのスレッドからでも呼べる（メモリへ足すだけ）。ファイルへは UI スレッドの <see cref="Flush"/> でまとめて書く。
///    フックのコールバックからは呼ばない。
///  - 記録を止めた後のログは、最後に書いてから 7 日で削除する（V2.2.0。<see cref="DiagnosticMode.ShouldDelete"/>）。設定画面からすぐ消すこともできる。
///  - 記録するのは時刻・件数・結果・エラー番号・版などだけ。クリップボードの本文・入力した文字・キーの並び・ウィンドウ名・パスは書かない。
///  - 保存先は実際のフォルダー。MSIX 版は %LOCALAPPDATA% への書き込みがパッケージのフォルダーへ移されるため、
///    エクスプローラーで開ける実際の場所（%LOCALAPPDATA%\Packages\&lt;ファミリー名&gt;\LocalCache\Local\PlainClip）へ直接書く。
/// </summary>
public static class DiagnosticLog
{
    public const string FileName = "diagnostic.log";

    private static readonly DiagnosticJournal Journal = new();
    private static readonly object FileGate = new();
    private static RollingLogFile? _file;
    private static bool _writeFailed;

    /// <summary>保存先のフォルダー（実際の場所）。</summary>
    public static string Folder { get; } = ResolveFolder();

    public static string FilePath => Path.Combine(Folder, FileName);

    /// <summary>大きさの上限で移した前のファイル（<see cref="RollingLogFile.OldPath"/>）。</summary>
    public static string OldFilePath => Path.ChangeExtension(FilePath, ".old.log");

    public static bool Recording => Journal.Recording;

    public static void Add(string message) => Journal.Add(DateTime.Now, message);

    /// <summary>ファイルへの記録を始める（見出しと、それまでのメモリ上の経過を先頭に書く）。</summary>
    public static void Start(IEnumerable<string> header)
    {
        _writeFailed = false;
        Journal.StartRecording(header);
        Flush();
    }

    public static void Stop(string reason)
    {
        Add("記録を停止: " + reason);
        Flush();
        Journal.StopRecording();
    }

    /// <summary>貯まった分をファイルへ書く（UI スレッドから定期的に呼ぶ）。</summary>
    public static void Flush()
    {
        var lines = Journal.TakePending();
        if (lines.Count == 0) return;
        try
        {
            lock (FileGate)
            {
                _file ??= new RollingLogFile(FilePath);
                _file.Append(lines);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 書けなくても本体の動作は続ける。続けて失敗しても error.log へは 1 回だけ書く。
            if (_writeFailed) return;
            _writeFailed = true;
            ErrorLog.Write($"診断ログを書けませんでした: {FilePath}", ex);
        }
    }

    /// <summary>残っているログ（今のファイル・前のファイル）を最後に書いた時刻（UTC）。どちらも無ければ null。</summary>
    public static DateTime? LastWriteUtc()
    {
        DateTime? latest = null;
        foreach (var path in new[] { FilePath, OldFilePath })
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && (latest is null || info.LastWriteTimeUtc > latest)) latest = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 調べられなければ無いものとして扱う（削除の判断・表示だけに使う）。
            }
        }
        return latest;
    }

    /// <summary>残っているログを削除する（記録中は何もしない）。消せなければ理由を返す（理由と保存先は error.log にも書く）。</summary>
    public static string? Delete()
    {
        if (Recording) return null;
        try
        {
            lock (FileGate)
            {
                foreach (var path in new[] { FilePath, OldFilePath })
                {
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"診断ログを削除できませんでした: {FilePath}", ex);
            return ex.Message;
        }
    }

    private static string ResolveFolder()
    {
        if (PackageInfo.FamilyName is { Length: > 0 } family)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", family, "LocalCache", "Local", "PlainClip");
        }
        return SettingsStore.DefaultDirectory;
    }
}
