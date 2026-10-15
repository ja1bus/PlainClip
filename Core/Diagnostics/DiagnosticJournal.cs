using System.Text;

namespace PlainClip.Core.Diagnostics;

/// <summary>
/// サポート用の診断ログを記録する期間（V2.1.0。PROJECT.md「診断ログ」）。普段は記録しない。
/// 設定画面のバージョン表示を Ctrl+Shift+クリックして有効にし、7 日たつと自動で止まる（止め忘れても残り続けない）。
/// </summary>
public static class DiagnosticMode
{
    public static readonly TimeSpan Duration = TimeSpan.FromDays(7);

    public static DateTime NewUntil(DateTime nowUtc) => nowUtc + Duration;

    /// <summary>記録中か。期限が過ぎた・期限が不自然に先（設定ファイルの手書き・時計の変更）なら記録しない。</summary>
    public static bool IsActive(DateTime? untilUtc, DateTime nowUtc) =>
        untilUtc is { } until && nowUtc < until && until - nowUtc <= Duration + TimeSpan.FromDays(1);
}

/// <summary>
/// 診断の記録（スレッドセーフ）。直近の記録は常にメモリ上だけに持ち（ファイルには書かない）、
/// 記録中（<see cref="DiagnosticMode"/>）だけファイルへ書く分を貯める。ファイルへの書き込みは呼び出し側が UI スレッドでまとめて行う
/// （フックのスレッド・コールバックではファイルに触れない）。
/// 記録する内容は、件数・時刻・結果・エラー番号などに限る。クリップボードの本文・入力した文字・キーの並び・ウィンドウ名は渡さない。
/// </summary>
public sealed class DiagnosticJournal
{
    /// <summary>メモリ上に残す直近の行数（記録を有効にしたとき、それまでの経過としてファイルの先頭に書く）。</summary>
    public const int MemoryLines = 200;

    /// <summary>1 行の上限（長い例外のメッセージなどを切る）。</summary>
    public const int MaxLineLength = 300;

    /// <summary>ファイルへ書く前に貯める上限（書き込みが続けて失敗しても増え続けないように）。</summary>
    public const int MaxPendingLines = 2000;

    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly List<string> _pending = [];
    private bool _recording;

    public bool Recording
    {
        get
        {
            lock (_gate) return _recording;
        }
    }

    public void Add(DateTime now, string message)
    {
        var line = now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + Clean(message);
        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > MemoryLines) _recent.Dequeue();
            if (_recording && _pending.Count < MaxPendingLines) _pending.Add(line);
        }
    }

    /// <summary>ファイルへの記録を始める。見出しと、それまでのメモリ上の記録を最初に書く分として貯める。</summary>
    public void StartRecording(IEnumerable<string> header)
    {
        lock (_gate)
        {
            _recording = true;
            _pending.Clear();
            _pending.AddRange(header.Select(Clean));
            if (_recent.Count > 0)
            {
                _pending.Add("--- 記録を始める前の経過（メモリ上の直近の記録） ---");
                _pending.AddRange(_recent);
                _pending.Add("--- ここから記録中 ---");
            }
        }
    }

    public void StopRecording()
    {
        lock (_gate)
        {
            _recording = false;
            _pending.Clear();
        }
    }

    /// <summary>ファイルへ書く分を取り出す（取り出した分は消える）。</summary>
    public IReadOnlyList<string> TakePending()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return [];
            var lines = _pending.ToArray();
            _pending.Clear();
            return lines;
        }
    }

    public IReadOnlyList<string> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    /// <summary>1 行にそろえて長さを抑える（改行で記録の形が崩れないように）。</summary>
    private static string Clean(string message)
    {
        var s = message.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= MaxLineLength ? s : s[..MaxLineLength] + "…";
    }
}

/// <summary>
/// 大きさに上限のあるログファイル。上限を超えそうになったら今のファイルを *.old.log に移して新しく始める
/// （残るのは 2 つまで。合計で上限の 2 倍まで）。
/// </summary>
public sealed class RollingLogFile
{
    public const long DefaultMaxBytes = 512 * 1024;

    public RollingLogFile(string path, long maxBytes = DefaultMaxBytes)
    {
        FilePath = path;
        MaxBytes = maxBytes;
    }

    public string FilePath { get; }

    public long MaxBytes { get; }

    public string OldPath => Path.ChangeExtension(FilePath, ".old.log");

    /// <summary>追記する。失敗時は例外（IOException / UnauthorizedAccessException）を呼び出し側へ返す。</summary>
    public void Append(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        var encoding = new UTF8Encoding(false);
        // 1 回で上限を超える分は、新しい行を優先して残す。
        var kept = new List<string>();
        long bytes = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var size = encoding.GetByteCount(lines[i]) + 2;
            if (bytes + size > MaxBytes) break;
            bytes += size;
            kept.Add(lines[i]);
        }
        kept.Reverse();
        if (kept.Count == 0) return;

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var info = new FileInfo(FilePath);
        if (info.Exists && info.Length + bytes > MaxBytes) File.Move(FilePath, OldPath, overwrite: true);
        File.AppendAllText(FilePath, string.Join("\r\n", kept) + "\r\n", encoding);
    }
}
