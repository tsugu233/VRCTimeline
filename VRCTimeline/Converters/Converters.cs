using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VRCTimeline.Models;
using VRCTimeline.Services;

namespace VRCTimeline.Converters;

/// <summary>
/// ファイルパスから縮小サイズの <see cref="BitmapSource"/> に変換するコンバーター。
/// LRU 制御は <see cref="ThumbnailCache"/> に委譲し、本クラスは「キャッシュ参照 → なければデコード → キャッシュへ格納」
/// のフロー制御のみを担当する。Window Hide 時のキャッシュクリアは <see cref="ThumbnailCache.Clear"/> を直接呼ぶ。
/// </summary>
public class PathToThumbnailConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;

        if (ThumbnailCache.TryGet(path) is { } cached)
            return cached;

        try
        {
            var decoded = DecodeThumbnail(path);
            return ThumbnailCache.Put(path, decoded);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>
    /// ファイルから DecodePixelWidth=224 の Frozen な <see cref="BitmapSource"/> をデコードする。
    /// VRChat 印刷モード写真（2048×1440 ≈ 1.42:1、フッター枠付き）の場合は上寄せで 16:9 にクロップして返す。
    /// </summary>
    private static BitmapSource DecodeThumbnail(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.DecodePixelWidth = 224;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        var w = bitmap.PixelWidth;
        var h = bitmap.PixelHeight;
        if (w <= 0 || h <= 0) return bitmap;

        var ratio = (double)w / h;
        if (ratio < 1.40 || ratio > 1.45) return bitmap;

        var cropHeight = (int)Math.Round(w * 9.0 / 16.0);
        var yOffset = Math.Max(0, (int)Math.Round(h * 0.02));
        if (cropHeight <= 0 || yOffset + cropHeight > h) return bitmap;

        var cropped = new CroppedBitmap(bitmap, new Int32Rect(0, yOffset, w, cropHeight));
        cropped.Freeze();
        return cropped;
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
