using PlainClip.Core.Text;

namespace PlainClip.Core.History;

/// <summary>履歴の 1 件。本文は全文を保持し、一覧用の表示文字列は追加時に一度だけ作る。</summary>
public sealed class ClipItem
{
    public ClipItem(string text, ClipProtection protection = ClipProtection.None)
    {
        Text = text;
        Protection = protection;
        Preview = PreviewText.Create(text);
        LineCount = PreviewText.CountLines(text);
    }

    public string Text { get; }

    /// <summary>一覧表示用の 1 行（改行・タブを記号化し、長文は省略）。</summary>
    public string Preview { get; }

    public int LineCount { get; }

    /// <summary>
    /// コピー元の保護指定（V2.0.1。履歴に入るのはクラウド同期の禁止だけ）。貼り付け・コピーで書き直すときに同じ指定を付ける。
    /// 同じ本文が別の指定で再度コピーされたら、厳しい方へ強める（弱めない）。
    /// </summary>
    public ClipProtection Protection { get; internal set; }
}

/// <summary>
/// メモリ上だけの履歴（先頭 = 最新。コピーした順）。PlainClip から貼り付けても並べ替えない（V2.0.0。番号を固定するため）。
/// ディスクには保存しない（PROJECT.md「履歴仕様」「セキュリティ」）。
/// UI スレッドからだけ操作する前提で、スレッドセーフではない。
/// </summary>
public sealed class ClipHistory
{
    public const int DefaultMaxCount = 10;
    public const int MinMaxCount = 1;
    public const int MaxMaxCount = 50;

    private readonly List<ClipItem> _items = [];
    private int _maxCount = DefaultMaxCount;

    public event EventHandler? Changed;

    public IReadOnlyList<ClipItem> Items => _items;

    public int Count => _items.Count;

    public ClipItem? Latest => _items.Count > 0 ? _items[0] : null;

    /// <summary>同じ文字列を再度コピーしたとき、既存の同一履歴を消して先頭へ移すか。false でも「直前と同じ」は重ねない。</summary>
    public bool MergeDuplicates { get; set; } = true;

    public int MaxCount
    {
        get => _maxCount;
        set
        {
            _maxCount = Math.Clamp(value, MinMaxCount, MaxMaxCount);
            if (Trim()) OnChanged();
        }
    }

    /// <summary>
    /// 先頭に追加する。空文字は追加しない。追加（または先頭へ移動）したら true。
    /// 同じ本文の既存項目には、<paramref name="protection"/> を加える（保護指定を弱めない。V2.0.1）。
    /// </summary>
    public bool Add(string text, ClipProtection protection = ClipProtection.None)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (_items.Count > 0 && _items[0].Text == text)
        {
            _items[0].Protection |= protection; // 直前と同じ: 並びは変えない
            return false;
        }

        if (MergeDuplicates)
        {
            var existing = _items.FindIndex(i => i.Text == text);
            if (existing >= 0)
            {
                var item = _items[existing];
                item.Protection |= protection;
                _items.RemoveAt(existing);
                _items.Insert(0, item);
                OnChanged();
                return true;
            }
        }

        _items.Insert(0, new ClipItem(text, protection));
        Trim();
        OnChanged();
        return true;
    }

    /// <summary>番号（1 = 最新）の項目。範囲外は null（数字ショートカット）。</summary>
    public ClipItem? ItemAt(int number) => number >= 1 && number <= _items.Count ? _items[number - 1] : null;

    public bool Remove(ClipItem item)
    {
        if (!_items.Remove(item)) return false;
        OnChanged();
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        OnChanged();
    }

    private bool Trim()
    {
        if (_items.Count <= _maxCount) return false;
        _items.RemoveRange(_maxCount, _items.Count - _maxCount);
        return true;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
