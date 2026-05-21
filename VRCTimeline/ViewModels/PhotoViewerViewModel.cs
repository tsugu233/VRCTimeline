using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VRCTimeline.Services;

namespace VRCTimeline.ViewModels;

/// <summary>
/// アプリ内写真ビューアーの ViewModel。
/// PhotoManagerViewModel が起動時点のフィルター結果（平坦化した PhotoDisplayItem の並び）と
/// 開始インデックスを渡し、ここで前後ナビ・ズーム/パン状態を保持する。
/// ビューアーを閉じたとき、最後に表示していた写真は CurrentPhoto で参照できる。
/// </summary>
public partial class PhotoViewerViewModel : ObservableObject
{
    /// <summary>ズーム倍率の下限（等倍）</summary>
    public const double MinZoom = 1.0;

    /// <summary>ズーム倍率の上限</summary>
    public const double MaxZoom = 8.0;

    private readonly IReadOnlyList<PhotoDisplayItem> _photos;

    [ObservableProperty]
    private int _currentIndex;

    [ObservableProperty]
    private PhotoDisplayItem? _currentPhoto;

    [ObservableProperty]
    private BitmapSource? _currentImageSource;

    [ObservableProperty]
    private double _zoom = 1.0;

    [ObservableProperty]
    private double _offsetX;

    [ObservableProperty]
    private double _offsetY;

    public PhotoViewerViewModel(IReadOnlyList<PhotoDisplayItem> photos, int startIndex)
    {
        _photos = photos;
        _currentIndex = Math.Clamp(startIndex, 0, Math.Max(0, photos.Count - 1));
        LoadCurrent();
    }

    /// <summary>前の写真が存在するか</summary>
    public bool HasPrevious => CurrentIndex > 0;

    /// <summary>次の写真が存在するか</summary>
    public bool HasNext => CurrentIndex < _photos.Count - 1;

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void Previous()
    {
        if (!HasPrevious) return;
        CurrentIndex--;
        LoadCurrent();
    }

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next()
    {
        if (!HasNext) return;
        CurrentIndex++;
        LoadCurrent();
    }

    /// <summary>現在インデックスの写真をロードし、ズーム/パンをリセットする</summary>
    private void LoadCurrent()
    {
        if (_photos.Count == 0)
        {
            CurrentPhoto = null;
            CurrentImageSource = null;
            return;
        }

        CurrentPhoto = _photos[CurrentIndex];
        CurrentImageSource = LoadFullImage(CurrentPhoto.FilePath);
        ResetView();

        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
    }

    /// <summary>ズーム倍率とパン位置を初期状態に戻す</summary>
    public void ResetView()
    {
        Zoom = 1.0;
        OffsetX = 0;
        OffsetY = 0;
    }

    /// <summary>
    /// 画像をフル解像度で読み込む。
    /// FileStream 経由 + OnLoad + Freeze で、
    /// ファイルロックを残さず・スレッド越しに渡せる凍結 BitmapImage を返す。
    /// 破損ファイルや読み取り権限不足は AppLogger に記録し、null を返してビューアー側でスキップさせる。
    /// </summary>
    private static BitmapSource? LoadFullImage(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex);
            return null;
        }
    }
}
