using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRCTimeline.Models;

namespace VRCTimeline.Converters;

/// <summary>
/// ファイルパスから縮小サイズの BitmapSource に変換するコンバーター。
/// 同じパスに対する 2 回目以降の呼び出しはキャッシュから即時に Frozen な BitmapSource を返すため、
/// サムネイル一覧のスクロール・画面切替後の再表示で BitmapImage の再デコードを起こさない。
/// </summary>
public class PathToThumbnailConverter : IValueConverter
{
    /// <summary>
    /// パス → Frozen サムネイル(BitmapImage または CroppedBitmap)のキャッシュ。
    /// 値はすべて Freeze 済みなのでスレッド間で共有しても安全。
    /// DecodePixelWidth=224 の縮小版なので 1 枚あたり 100KB 前後。
    /// メモリ使用量を抑えるため容量上限 <see cref="CacheCapacity"/> 件の LRU として運用し、
    /// 上限超過時は最も古いエントリから順に破棄する。
    /// Window Hide 等のタイミングで <see cref="ClearCache"/> を呼び出すと全件破棄できる。
    /// </summary>
    private const int CacheCapacity = 500;

    private static readonly LinkedList<KeyValuePair<string, BitmapSource>> _lruList = new();
    private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> _cache = new();
    private static readonly object _lock = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;

        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var node))
            {
                // ヒット: 最近使用としてリスト先頭へ移動
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                return node.Value.Value;
            }
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 224;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            BitmapSource result = bitmap;

            // VRChat 印刷モード写真(2048×1440 ≈ 1.42:1)はフッター枠付き。
            // 黒い写真領域を囲むように、上寄せで 16:9 に切り出して返す。
            var w = bitmap.PixelWidth;
            var h = bitmap.PixelHeight;
            if (w > 0 && h > 0)
            {
                var ratio = (double)w / h;
                if (ratio >= 1.40 && ratio <= 1.45)
                {
                    var cropHeight = (int)Math.Round(w * 9.0 / 16.0);
                    var yOffset = Math.Max(0, (int)Math.Round(h * 0.02));
                    if (cropHeight > 0 && yOffset + cropHeight <= h)
                    {
                        var cropped = new CroppedBitmap(bitmap, new Int32Rect(0, yOffset, w, cropHeight));
                        cropped.Freeze();
                        result = cropped;
                    }
                }
            }

            lock (_lock)
            {
                // デコード中に別スレッドが同じパスを追加していた場合に備えて再チェック
                if (_cache.TryGetValue(path, out var existing))
                {
                    _lruList.Remove(existing);
                    _lruList.AddFirst(existing);
                    return existing.Value.Value;
                }

                var node = new LinkedListNode<KeyValuePair<string, BitmapSource>>(
                    new KeyValuePair<string, BitmapSource>(path, result));
                _lruList.AddFirst(node);
                _cache[path] = node;

                // 容量超過時は最も古いエントリ(末尾)を破棄
                while (_cache.Count > CacheCapacity)
                {
                    var oldest = _lruList.Last;
                    if (oldest is null) break;
                    _lruList.RemoveLast();
                    _cache.Remove(oldest.Value.Key);
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// サムネイルキャッシュを全件破棄する。Window Hide 等のタイミングで呼び出す想定。
    /// </summary>
    public static void ClearCache()
    {
        lock (_lock)
        {
            _cache.Clear();
            _lruList.Clear();
        }
    }
}

/// <summary>LogEntryType を色付き SolidColorBrush に変換するコンバーター</summary>
public class LogEntryTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is LogEntryType type
            ? type switch
            {
                LogEntryType.RoomJoin => new SolidColorBrush(Color.FromRgb(100, 181, 246)),
                LogEntryType.PlayerJoined => new SolidColorBrush(Color.FromRgb(129, 199, 132)),
                LogEntryType.PlayerLeft => new SolidColorBrush(Color.FromRgb(229, 115, 115)),
                _ => new SolidColorBrush(Colors.Gray)
            }
            : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool を反転するコンバーター</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>true → Collapsed、false → Visible に変換するコンバーター</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Hex カラー文字列を SolidColorBrush に変換するコンバーター</summary>
public class HexToSolidBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
            return new SolidColorBrush(Colors.Gray);
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            return new SolidColorBrush(color);
        }
        catch { return new SolidColorBrush(Colors.Gray); }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>2つの値の参照等価性を判定するマルチバインディングコンバーター</summary>
public class EqualityMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed、非 null → Visible に変換するコンバーター</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value != null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
