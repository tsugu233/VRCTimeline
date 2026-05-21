using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VRCTimeline.ViewModels;
using VRCTimeline.Views;

namespace VRCTimeline;

/// <summary>
/// メインウィンドウ。
/// DWM API を使用してタイトルバーをダークテーマに合わせてカスタマイズする。
/// また、サイドメニュー切替時の View 再生成を避けるため、各サブ View のインスタンスを
/// キャッシュして使い回す仕組みを持つ。
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    /// <summary>
    /// サブ View インスタンスのキャッシュ。Key は対応する ViewModel インスタンス(Singleton)。
    /// 初回切替時に生成し、以降は使い回す。
    /// </summary>
    private readonly Dictionary<object, FrameworkElement> _viewCache = new();
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// MainViewModel.CurrentViewModel の変更を購読し、サブ View の切替を駆動する。
    /// DataContext は App.xaml.cs:125-127 のコンストラクタで設定されているため Loaded 時点で取得可能。
    /// </summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        _vm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        ApplyContent(vm.CurrentViewModel);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentViewModel) && _vm != null)
            ApplyContent(_vm.CurrentViewModel);
    }

    /// <summary>
    /// 指定 ViewModel に対応する View インスタンスを ContentRoot に設定する。
    /// 初回はインスタンスを生成してキャッシュへ、2 回目以降はキャッシュから取り出す。
    /// View を再利用するためスクロール位置・サムネイル BitmapImage 等のビジュアル状態が保持される。
    /// </summary>
    private void ApplyContent(object? viewModel)
    {
        if (viewModel == null)
        {
            ContentRoot.Content = null;
            return;
        }

        if (!_viewCache.TryGetValue(viewModel, out var view))
        {
            view = viewModel switch
            {
                RealtimeMonitorViewModel    => new RealtimeMonitorView(),
                ActivityHistoryViewModel    => new ActivityHistoryView(),
                PhotoManagerViewModel       => new PhotoManagerView(),
                NotificationLogViewModel    => new NotificationLogView(),
                VideoLogViewModel           => new VideoLogView(),
                SettingsViewModel           => new SettingsView(),
                _ => throw new InvalidOperationException(
                    $"No view mapping for ViewModel type {viewModel.GetType().FullName}")
            };
            view.DataContext = viewModel;
            _viewCache[viewModel] = view;
        }
        ContentRoot.Content = view;
    }

    /// <summary>ウィンドウハンドル取得後にタイトルバーの色を適用する</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarColor();
    }

    /// <summary>DWM API でタイトルバーの背景色・テキスト色をテーマに合わせて設定する</summary>
    private void ApplyTitleBarColor()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;

        var hwnd = source.Handle;

        int darkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        if (TryFindResource("MaterialDesignPaper") is SolidColorBrush bg)
        {
            int bgRef = bg.Color.R | (bg.Color.G << 8) | (bg.Color.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref bgRef, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref bgRef, sizeof(int));
        }

        if (TryFindResource("MaterialDesignBody") is SolidColorBrush fg)
        {
            int fgRef = fg.Color.R | (fg.Color.G << 8) | (fg.Color.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref fgRef, sizeof(int));
        }
    }
}
