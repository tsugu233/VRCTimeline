using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VRCTimeline.ViewModels;

namespace VRCTimeline.Views;

/// <summary>写真管理画面のコードビハインド</summary>
public partial class PhotoManagerView : UserControl
{
    private PhotoManagerViewModel? _subscribedVm;

    public PhotoManagerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    /// <summary>画面表示時に ViewModel の初期データ読み込みを実行する</summary>
    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PhotoManagerViewModel vm)
            await vm.InitializeAsync();
    }

    /// <summary>プレイヤー一覧パネルのリサイズ操作を処理する</summary>
    private void PlayerResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var newHeight = PlayerScrollViewer.Height + e.VerticalChange;
        PlayerScrollViewer.Height = Math.Clamp(newHeight, 75, 500);
    }

    /// <summary>
    /// 仮想化 ListBox + CanContentScroll=True のホイールスクロール。
    /// VirtualizingWrapPanel の VerticalOffset の単位は実装次第で「行」「アイテム」「ピクセル」のいずれかに
    /// なりうるため、固定値ではなく <see cref="ScrollViewer.ViewportHeight"/> の比率でスクロールする。
    /// 1 ティックあたり「表示中の約 1/3」をスクロールする体感で、単位に依存せず一定の動き量を確保する。
    /// </summary>
    private void PhotoListBox_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var sv = FindScrollViewer(lb);
        if (sv == null) return;

        // ViewportHeight が 0 になり得る初期化直後のフォールバックとして妥当な値を持っておく。
        var amount = sv.ViewportHeight > 0 ? sv.ViewportHeight * 0.3 : 2.0;
        var newOffset = e.Delta > 0
            ? sv.VerticalOffset - amount
            : sv.VerticalOffset + amount;
        sv.ScrollToVerticalOffset(newOffset);
        e.Handled = true;
    }

    /// <summary>子の中から最初に見つかった ScrollViewer を返す</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.ScrollToPhotoRequested -= OnScrollToPhotoRequested;

        _subscribedVm = e.NewValue as PhotoManagerViewModel;

        if (_subscribedVm != null)
            _subscribedVm.ScrollToPhotoRequested += OnScrollToPhotoRequested;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToPhotoRequested -= OnScrollToPhotoRequested;
            _subscribedVm = null;
        }
    }

    /// <summary>
    /// 指定写真までスクロールする。ListBox.ScrollIntoView は仮想化されたコンテナでも
    /// 該当アイテムを実体化してスクロールしてくれる。
    /// </summary>
    private void OnScrollToPhotoRequested(PhotoDisplayItem photo)
    {
        Dispatcher.InvokeAsync(() =>
        {
            PhotoListBox.UpdateLayout();
            PhotoListBox.ScrollIntoView(photo);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

}
