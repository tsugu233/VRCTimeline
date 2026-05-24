using System.Windows.Media.Imaging;

namespace VRCTimeline.Services;

/// <summary>
/// 写真サムネイル用の LRU キャッシュ。
/// パス → Frozen な <see cref="BitmapSource"/> を保持し、容量上限を超えると最も古いエントリから破棄する。
/// 値はすべて Freeze 済みのためスレッド間で共有しても安全。<see cref="Clear"/> は Window Hide 等の
/// タイミングで呼び出すと全件破棄できる。
/// </summary>
internal static class ThumbnailCache
{
    /// <summary>
    /// 最大保持件数。DecodePixelWidth=224 の縮小版を想定し、1 枚あたり 100KB 前後 × 500 ≒ 50MB を上限とする。
    /// </summary>
    private const int Capacity = 500;

    private static readonly LinkedList<KeyValuePair<string, BitmapSource>> _lru = new();
    private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> _index = new();
    private static readonly object _lock = new();

    /// <summary>
    /// キャッシュにヒットすれば値を返し、該当ノードを最近使用としてリスト先頭へ昇格させる。
    /// ヒットしなければ null。
    /// </summary>
    public static BitmapSource? TryGet(string path)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(path, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Value;
            }
            return null;
        }
    }

    /// <summary>
    /// エントリを追加して最新位置に置く。容量超過時は末尾（最古）から削除する。
    /// 別スレッドが既に同じパスで追加済みの場合は、そちらを最新に昇格させて返す
    /// （呼び出し元はデコード後の値 / キャッシュ済みの値のどちらでも Frozen な BitmapSource として扱える）。
    /// </summary>
    public static BitmapSource Put(string path, BitmapSource bitmap)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(path, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Value;
            }

            var node = new LinkedListNode<KeyValuePair<string, BitmapSource>>(
                new KeyValuePair<string, BitmapSource>(path, bitmap));
            _lru.AddFirst(node);
            _index[path] = node;

            while (_index.Count > Capacity)
            {
                var oldest = _lru.Last;
                if (oldest is null) break;
                _lru.RemoveLast();
                _index.Remove(oldest.Value.Key);
            }

            return bitmap;
        }
    }

    /// <summary>キャッシュを全件破棄する。</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _index.Clear();
            _lru.Clear();
        }
    }
}
