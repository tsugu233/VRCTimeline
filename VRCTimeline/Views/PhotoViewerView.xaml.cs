using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using VRCTimeline.ViewModels;

namespace VRCTimeline.Views;

/// <summary>
/// アプリ内写真ビューアー。DialogHost に乗せて使用する UserControl。
/// 入力（キー・ホイール・ドラッグ）を扱い、ViewModel のズーム/パン状態と前後ナビを駆動する。
/// </summary>
public partial class PhotoViewerView : UserControl
{
    /// <summary>
    /// 利用可能な幅からダイアログ装飾分として差し引く余白(px)。
    /// DialogHost カードの Margin・Padding・シャドウ + 左右ウィンドウ境界を合算した想定値。
    /// </summary>
    private const double HorizontalChromeMargin = 190;

    /// <summary>
    /// 利用可能な高さから差し引く余白(px)。
    /// DialogHost はタイトルバー直下に置かれるため可視領域中央ではなく下寄りに表示される。
    /// そのぶん下端マージンを多めに確保する必要があり、水平方向より大きい値を取る。
    /// </summary>
    private const double VerticalChromeMargin = 260;

    /// <summary>
    /// 表示直後の DialogHost 自動クローズ抑止に使う遅延。ダブルクリック起動時の
    /// 2 回目クリック離散イベントが CloseOnClickAway を捉えて即閉じてしまうのを防ぐ。
    /// </summary>
    private static readonly TimeSpan ClickAwayEnableDelay = TimeSpan.FromMilliseconds(300);

    private Point _dragStart;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;
    private bool _isDragging;

    /// <summary>表示中のみ DialogHost.CloseOnClickAway を有効化するため、元の値を退避する</summary>
    private DialogHost? _hostingDialogHost;
    private bool _previousCloseOnClickAway;
    private DispatcherTimer? _clickAwayEnableTimer;

    /// <summary>ウィンドウサイズ変更に追従するため購読する</summary>
    private Window? _hostingWindow;

    public PhotoViewerView()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    private PhotoViewerViewModel? Vm => DataContext as PhotoViewerViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 親 DialogHost を取得して、ビューアー表示中はクリックアウェイで閉じれるようにする
        _hostingDialogHost = FindAncestor<DialogHost>(this);
        if (_hostingDialogHost != null)
        {
            _previousCloseOnClickAway = _hostingDialogHost.CloseOnClickAway;

            // ダブルクリック起動時、2回目のクリックの離散イベントが
            // 直後の CloseOnClickAway=true を捉えると一瞬で閉じてしまう。
            // 一定時間遅延させてから有効化する。
            _clickAwayEnableTimer = new DispatcherTimer
            {
                Interval = ClickAwayEnableDelay
            };
            _clickAwayEnableTimer.Tick += OnClickAwayEnableTick;
            _clickAwayEnableTimer.Start();
        }

        // ウィンドウのリサイズ・最大化に追従するためサイズを動的更新する
        _hostingWindow = Window.GetWindow(this);
        if (_hostingWindow != null)
        {
            _hostingWindow.SizeChanged += OnWindowSizeChanged;
            _hostingWindow.StateChanged += OnWindowStateChanged;
        }
        // レイアウト確定後に初期サイズを計算する。DialogHost のオープンアニメーション中は
        // ActualWidth/Height が不確定なため、LayoutUpdated でも一度発火させる。
        LayoutUpdated += OnLayoutUpdatedFirstTime;
        UpdateSize();

        Focus();
        Keyboard.Focus(this);
    }

    private void OnClickAwayEnableTick(object? sender, EventArgs e)
    {
        if (_clickAwayEnableTimer != null)
        {
            _clickAwayEnableTimer.Stop();
            _clickAwayEnableTimer.Tick -= OnClickAwayEnableTick;
            _clickAwayEnableTimer = null;
        }
        if (_hostingDialogHost != null)
            _hostingDialogHost.CloseOnClickAway = true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_clickAwayEnableTimer != null)
        {
            _clickAwayEnableTimer.Stop();
            _clickAwayEnableTimer.Tick -= OnClickAwayEnableTick;
            _clickAwayEnableTimer = null;
        }

        // 退避しておいた CloseOnClickAway を元に戻す（他のダイアログに影響させない）
        if (_hostingDialogHost != null)
        {
            _hostingDialogHost.CloseOnClickAway = _previousCloseOnClickAway;
            _hostingDialogHost = null;
        }

        if (_hostingWindow != null)
        {
            _hostingWindow.SizeChanged -= OnWindowSizeChanged;
            _hostingWindow.StateChanged -= OnWindowStateChanged;
            _hostingWindow = null;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSize();
    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateSize();

    /// <summary>
    /// レイアウト初回確定時に一度だけ UpdateSize を呼び出す。DialogHost のオープンアニメーション中は
    /// 各所のサイズが 0 や中間値になることがあるため、レイアウト完了を待ってから計算する。
    /// </summary>
    private void OnLayoutUpdatedFirstTime(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdatedFirstTime;
        UpdateSize();
    }

    /// <summary>
    /// ビューアーのサイズを画面の見える領域に確実に収まるように更新する。
    /// SystemParameters.WorkArea（タスクバー除外の可視領域）を絶対上限とし、
    /// 高さ・幅ともに余裕を持って小さく取ることで、DialogHost カードのマージン・パディング・
    /// シャドウや WPF の最大化時挙動による下端のはみ出しを完全に防ぐ。
    /// </summary>
    private void UpdateSize()
    {
        if (_hostingWindow == null) return;

        var workArea = SystemParameters.WorkArea;

        // 最大化時は WorkArea（タスクバー除外）を、それ以外はウィンドウ実サイズと WorkArea の小さい方を使う。
        // これにより WPF が最大化時にスクリーン外へはみ出す挙動や、ユーザーがウィンドウを画面より
        // 大きくドラッグした場合でも、ダイアログは必ず可視領域内に収まる。
        var availW = _hostingWindow.WindowState == WindowState.Maximized
            ? workArea.Width
            : Math.Min(workArea.Width, _hostingWindow.ActualWidth);
        var availH = _hostingWindow.WindowState == WindowState.Maximized
            ? workArea.Height
            : Math.Min(workArea.Height, _hostingWindow.ActualHeight);

        // ダイアログ装飾分を差し引いてカードを画面内に確実に収める。
        // 詳細な値の根拠は HorizontalChromeMargin / VerticalChromeMargin の定義コメント参照。
        var targetW = availW - HorizontalChromeMargin;
        var targetH = availH - VerticalChromeMargin;

        Width = Math.Min(availW, Math.Max(MinWidth, targetW));
        Height = Math.Min(availH, Math.Max(MinHeight, targetH));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;

        switch (e.Key)
        {
            case Key.Left:
                if (Vm.PreviousCommand.CanExecute(null))
                    Vm.PreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                if (Vm.NextCommand.CanExecute(null))
                    Vm.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                if (DialogHost.CloseDialogCommand.CanExecute(null, this))
                    DialogHost.CloseDialogCommand.Execute(null, this);
                e.Handled = true;
                break;
        }
    }

    /// <summary>マウスホイールで、カーソル位置を基点にズーム</summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm == null) return;

        var oldZoom = Vm.Zoom;
        var factor = e.Delta > 0 ? 1.2 : 1.0 / 1.2;
        var newZoom = Math.Clamp(oldZoom * factor, PhotoViewerViewModel.MinZoom, PhotoViewerViewModel.MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.0001) return;

        // ホスト中心からマウス位置までのオフセット。Image は RenderTransformOrigin=(0.5,0.5)
        // で中心スケーリングしているため、マウス位置を基点に保つには
        // OffsetX/Y を「ズーム前のマウス相対位置」と「ズーム後のマウス相対位置」の差で補正する。
        var mouse = e.GetPosition(ImageHost);
        var centerX = ImageHost.ActualWidth / 2.0;
        var centerY = ImageHost.ActualHeight / 2.0;
        var dx = mouse.X - centerX;
        var dy = mouse.Y - centerY;
        var scale = newZoom / oldZoom;

        Vm.Zoom = newZoom;
        Vm.OffsetX = (Vm.OffsetX - dx) * scale + dx;
        Vm.OffsetY = (Vm.OffsetY - dy) * scale + dy;

        // 等倍に戻ったらパン位置もリセット
        if (Math.Abs(newZoom - PhotoViewerViewModel.MinZoom) < 0.0001)
        {
            Vm.OffsetX = 0;
            Vm.OffsetY = 0;
        }

        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null || Vm.Zoom <= PhotoViewerViewModel.MinZoom) return;

        _isDragging = true;
        _dragStart = e.GetPosition(ImageHost);
        _dragStartOffsetX = Vm.OffsetX;
        _dragStartOffsetY = Vm.OffsetY;
        ImageHost.CaptureMouse();
        ImageHost.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || Vm == null) return;
        var pos = e.GetPosition(ImageHost);
        Vm.OffsetX = _dragStartOffsetX + (pos.X - _dragStart.X);
        Vm.OffsetY = _dragStartOffsetY + (pos.Y - _dragStart.Y);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ImageHost.ReleaseMouseCapture();
        ImageHost.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
