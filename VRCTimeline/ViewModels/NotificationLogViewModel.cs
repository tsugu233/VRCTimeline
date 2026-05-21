using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Helpers;
using VRCTimeline.Services;
using VRCTimeline.Services.LogParser;

namespace VRCTimeline.ViewModels;

/// <summary>
/// 通知ログ画面の ViewModel。
/// VRChat の通知（Invite、Request Invite、Boop）をフィルタリングして一覧表示する。
/// </summary>
public partial class NotificationLogViewModel : ObservableObject, IDisposable
{
    private readonly LoadingService _loading;

    /// <summary>初回ロード完了フラグ</summary>
    private bool _initialized;

    /// <summary>FilterDateTo を「今日」に追従させるかどうか（日付またぎ時の自動更新用）</summary>
    private bool _filterDateToFollowsToday = true;

    /// <summary>日付またぎを検知して FilterDateTo を更新するためのウォッチャー</summary>
    private readonly DayChangeWatcher _dayChangeWatcher;

    /// <summary>送信者名のフィルターテキスト</summary>
    [ObservableProperty]
    private string _searchPlayerName = string.Empty;

    /// <summary>通知種別フィルター（ローカライズされた "すべて" 相当文字列 or "Invite" など）</summary>
    [ObservableProperty]
    private string _selectedTypeFilter = string.Empty;

    /// <summary>表示期間の開始日</summary>
    [ObservableProperty]
    private DateTime _filterDateFrom = DateTime.Today.AddDays(-30);

    /// <summary>表示期間の終了日（選択日を含む）</summary>
    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today;

    /// <summary>通知レコードの表示リスト</summary>
    public ObservableCollection<NotificationDisplayItem> Notifications { get; } = [];

    /// <summary>通知種別フィルターの選択肢（言語変更時に再構築される）</summary>
    public ObservableCollection<string> TypeFilters { get; } = [];

    public NotificationLogViewModel(LoadingService loadingService)
    {
        _loading = loadingService;
        RebuildTypeFilters();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _dayChangeWatcher = new DayChangeWatcher(() =>
        {
            if (_filterDateToFollowsToday) FilterDateTo = DateTime.Today;
        });
    }

    /// <summary>ユーザーが終了日を変更した際、その値が「今日」かどうかを記録する</summary>
    partial void OnFilterDateToChanged(DateTime value)
    {
        _filterDateToFollowsToday = value.Date == DateTime.Today;
    }

    private void OnLanguageChanged()
    {
        var wasAll = IsAllFilter(SelectedTypeFilter);
        RebuildTypeFilters();
        if (wasAll)
            SelectedTypeFilter = TypeFilters[0];

        // ReceivedAtDisplay は曜日略称を含むカルチャ依存の文字列。
        // INotifyPropertyChanged 経由で UI に再評価させないと、検索などで一覧が再構築されるまで
        // 古いカルチャの曜日表記が残り続けてしまう。
        foreach (var n in Notifications)
            n.RefreshLocalizedStrings();
    }

    /// <summary>言語に合わせてフィルター選択肢を再構築する</summary>
    private void RebuildTypeFilters()
    {
        TypeFilters.Clear();
        TypeFilters.Add(LocalizationService.GetString("Filter_All"));
        TypeFilters.Add("Invite");
        TypeFilters.Add("Request Invite");
        TypeFilters.Add("Boop");
        if (string.IsNullOrEmpty(SelectedTypeFilter))
            SelectedTypeFilter = TypeFilters[0];
    }

    /// <summary>「すべて」相当の選択かどうかを判定する（言語に依存しない）</summary>
    private static bool IsAllFilter(string? filter)
        => string.IsNullOrEmpty(filter)
           || !new[] { "Invite", "Request Invite", "Boop" }.Contains(filter);

    /// <summary>初回の通知ログ読み込み</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await LoadNotificationsAsync();
    }

    /// <summary>通知ログを DB から読み込み、日付・種別・送信者名でフィルタリングする</summary>
    [RelayCommand]
    private async Task LoadNotificationsAsync()
    {
        _loading.Show("通知ログを読み込み中...");
        try
        {
            await LoadNotificationsCoreAsync();
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
        finally
        {
            _loading.Hide();
        }
    }

    /// <summary>LoadNotificationsAsync の本体。例外は呼び出し側の catch で AppLogger に記録される。</summary>
    private async Task LoadNotificationsCoreAsync()
    {
        await using var db = new AppDbContext();

        var query = db.NotificationRecords
            .Include(n => n.WorldVisit)
            .Where(n => n.ReceivedAt >= FilterDateFrom && n.ReceivedAt < FilterDateTo.Date.AddDays(1));

        // 種別フィルター（"すべて" 相当かどうかは言語に依存しない IsAllFilter で判定）
        if (!IsAllFilter(SelectedTypeFilter))
        {
            var typeKey = SelectedTypeFilter switch
            {
                "Invite" => "invite",
                "Request Invite" => "requestInvite",
                "Boop" => "boop",
                _ => ""
            };
            if (!string.IsNullOrEmpty(typeKey))
                query = query.Where(n => n.NotificationType == typeKey);
        }

        var allRecords = await query
            .OrderByDescending(n => n.ReceivedAt)
            .ToListAsync();

        // 送信者名フィルター（かな文字差異を無視）
        if (!string.IsNullOrWhiteSpace(SearchPlayerName))
        {
            var search = SearchPlayerName.Trim();
            allRecords = allRecords.Where(n =>
                KanaHelper.ContainsKanaInsensitive(n.SenderName, search)).ToList();
        }

        var records = allRecords.Take(500).ToList();

        Notifications.Clear();
        foreach (var r in records)
        {
            Notifications.Add(new NotificationDisplayItem
            {
                ReceivedAt = r.ReceivedAt,
                SenderName = LogPatterns.CleanPlayerName(r.SenderName),
                NotificationType = r.NotificationType switch
                {
                    "invite" => "Invite",
                    "requestInvite" => "Request Invite",
                    "boop" => "Boop",
                    _ => r.NotificationType
                },
                WorldName = r.WorldVisit?.WorldName
            });
        }
    }

    /// <summary>プレイヤー名で検索する（カード UI クリック時に使用）</summary>
    [RelayCommand]
    private async Task SearchByPlayer(string playerName)
    {
        SearchPlayerName = playerName;
        await LoadNotificationsAsync();
    }

    /// <summary>
    /// Window 非表示時に表示用リソースを破棄する。
    /// ViewModel の Singleton インスタンスは破棄されないため、再表示時には Loaded イベント経由で
    /// 再ロードされるよう初期化フラグをリセットする。
    /// DB アクセス・タイマー・静的イベント購読には触れず、UI 表示用コレクションのクリアのみ行う。
    /// TypeFilters は言語切替時にも再構築される選択肢のため、ここではクリアしない。
    /// </summary>
    public void ReleaseUiResources()
    {
        Notifications.Clear();
        _initialized = false;
    }

    /// <summary>
    /// 静的イベントの購読解除と DayChangeWatcher のタイマー停止を行う。
    /// 現状は Singleton 登録なのでアプリ終了時に DI コンテナから呼ばれる。
    /// </summary>
    public void Dispose()
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        _dayChangeWatcher.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 通知ログ画面の表示用モデル。
/// DB エンティティ (NotificationRecord) から変換して使用する。
/// 言語切替で再フォーマットさせる必要がある日時表示プロパティのため、
/// INotifyPropertyChanged を持つ ObservableObject を継承している。
/// </summary>
public class NotificationDisplayItem : ObservableObject
{
    /// <summary>通知受信日時</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>送信者の表示名</summary>
    public string SenderName { get; set; } = string.Empty;

    /// <summary>通知種別の表示テキスト</summary>
    public string NotificationType { get; set; } = string.Empty;

    /// <summary>通知受信時のワールド名（nullable）</summary>
    public string? WorldName { get; set; }

    /// <summary>受信日時の表示文字列（曜日・秒付き）</summary>
    public string ReceivedAtDisplay => ReceivedAt.ToString(DateFormatHelper.DateWithDayAndSeconds, DateFormatHelper.GetCurrentCulture());

    /// <summary>通知種別に対応する MaterialDesign アイコン名</summary>
    public string TypeIcon => NotificationType switch
    {
        "Invite" => "EmailOpen",
        "Request Invite" => "EmailSend",
        "Boop" => "HandWave",
        _ => "Bell"
    };

    /// <summary>
    /// 言語切替時に呼び出されるリフレッシュ。曜日略称を含むカルチャ依存プロパティの
    /// 再評価を WPF バインディングに促す。
    /// </summary>
    public void RefreshLocalizedStrings()
    {
        OnPropertyChanged(nameof(ReceivedAtDisplay));
    }
}
