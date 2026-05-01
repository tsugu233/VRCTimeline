using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using VRCTimeline.Services;

namespace VRCTimeline.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel。
/// ナビゲーション制御、VRChat プロセス監視、PhotoWatcher の起動を担当する。
/// 各画面の ViewModel を保持し、ナビゲーションインデックスに応じて切り替える。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    /// <summary>VRChat プロセスの起動・終了を監視するモニター</summary>
    private readonly VRChatProcessMonitor _processMonitor;

    /// <summary>写真フォルダのリアルタイム監視サービス</summary>
    private readonly PhotoWatcher _photoWatcher;

    private readonly NavigationService _navigation;

    /// <summary>グローバルローディング UI のサービス</summary>
    public LoadingService Loading { get; }

    // ── 各画面の ViewModel ──
    public RealtimeMonitorViewModel RealtimeMonitorVm { get; }
    public ActivityHistoryViewModel ActivityHistoryVm { get; }
    public PhotoManagerViewModel PhotoManagerVm { get; }
    public NotificationLogViewModel NotificationLogVm { get; }
    public VideoLogViewModel VideoLogVm { get; }
    public SettingsViewModel SettingsVm { get; }

    /// <summary>現在表示中の画面 ViewModel</summary>
    [ObservableProperty]
    private object? _currentViewModel;

    /// <summary>左側ナビゲーションの選択インデックス</summary>
    [ObservableProperty]
    private int _selectedNavIndex;

    /// <summary>VRChat が実行中かどうか</summary>
    [ObservableProperty]
    private bool _isVRChatRunning;

    public MainViewModel(
        SettingsService settingsService,
        LoadingService loadingService,
        VRChatProcessMonitor processMonitor,
        PhotoWatcher photoWatcher,
        NavigationService navigationService,
        RealtimeMonitorViewModel realtimeVm,
        ActivityHistoryViewModel activityVm,
        PhotoManagerViewModel photoVm,
        NotificationLogViewModel notificationVm,
        VideoLogViewModel videoVm,
        SettingsViewModel settingsVm)
    {
        _settingsService = settingsService;
        _processMonitor = processMonitor;
        _photoWatcher = photoWatcher;
        _navigation = navigationService;

        Loading = loadingService;
        RealtimeMonitorVm = realtimeVm;
        ActivityHistoryVm = activityVm;
        PhotoManagerVm = photoVm;
        NotificationLogVm = notificationVm;
        VideoLogVm = videoVm;
        SettingsVm = settingsVm;

        CurrentViewModel = RealtimeMonitorVm;

        // VRChat プロセス監視（3秒間隔）と写真監視を開始
        _processMonitor.VRChatStatusChanged += OnVRChatStatusChanged;
        _processMonitor.Start(3);
        _photoWatcher.Start();

        // 画面間ナビゲーションイベントの購読
        _navigation.ShowPhotosRequested += OnShowPhotosRequested;
        _navigation.ShowActivityRequested += OnShowActivityRequested;
        _navigation.DataImported += OnDataImported;
    }

    /// <summary>ナビゲーションインデックスに応じて表示する ViewModel を切り替える</summary>
    partial void OnSelectedNavIndexChanged(int value)
    {
        CurrentViewModel = value switch
        {
            0 => RealtimeMonitorVm,
            1 => ActivityHistoryVm,
            2 => PhotoManagerVm,
            3 => NotificationLogVm,
            4 => VideoLogVm,
            5 => (object)SettingsVm,
            _ => RealtimeMonitorVm
        };
    }

    /// <summary>写真表示リクエスト時に写真画面へ遷移する</summary>
    private async void OnShowPhotosRequested(int? worldVisitId)
    {
        if (worldVisitId.HasValue)
            await PhotoManagerVm.FilterByWorldVisitId(worldVisitId.Value);
        SelectedNavIndex = 2;
    }

    /// <summary>アクティビティ表示リクエスト時に履歴画面へ遷移する</summary>
    private async void OnShowActivityRequested(int? worldVisitId)
    {
        SelectedNavIndex = 1;
        if (worldVisitId.HasValue)
            await ActivityHistoryVm.FilterByVisitId(worldVisitId.Value);
        else
            await ActivityHistoryVm.LoadHistoryCommand.ExecuteAsync(null);
    }

    /// <summary>データインポート完了時に各画面をリロードする</summary>
    private async void OnDataImported()
    {
        await ActivityHistoryVm.ReloadAsync();
        await PhotoManagerVm.ReloadAsync();
    }

    /// <summary>
    /// VRChat プロセスの起動・終了を検知し、リアルタイム監視の開始/停止を制御する。
    /// AutoDetectVRChat 設定が有効な場合、VRChat 起動時にウィンドウを自動表示する。
    /// </summary>
    private void OnVRChatStatusChanged(bool isRunning)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsVRChatRunning = isRunning;
            if (isRunning)
            {
                RealtimeMonitorVm.StartMonitoring();
                if (_settingsService.Settings.AutoDetectVRChat)
                {
                    var mainWin = Application.Current.MainWindow;
                    if (mainWin != null)
                    {
                        mainWin.Show();
                        mainWin.WindowState = WindowState.Normal;
                        mainWin.Activate();
                    }
                }
            }
            else
            {
                RealtimeMonitorVm.HandleVRChatExited();
            }
        });
    }
}
