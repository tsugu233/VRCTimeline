using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>写真スクロール領域のマウスホイールを高速化する（1.25倍）</summary>
    private void PhotoScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 1.25);
            e.Handled = true;
        }
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
    /// 指定写真のカードがスクロール領域に見えるようスクロールする。
    /// ItemsControl は仮想化していないので、ビジュアルツリーから DataContext が一致する
    /// FrameworkElement を探して BringIntoView() で呼び出す。
    /// </summary>
    private void OnScrollToPhotoRequested(PhotoDisplayItem photo)
    {
        Dispatcher.InvokeAsync(() =>
        {
            PhotoScrollViewer.UpdateLayout();
            var target = FindVisualByDataContext(PhotoScrollViewer, photo);
            target?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static FrameworkElement? FindVisualByDataContext(DependencyObject root, object dc)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && ReferenceEquals(fe.DataContext, dc))
                return fe;
            var result = FindVisualByDataContext(child, dc);
            if (result != null) return result;
        }
        return null;
    }
}
