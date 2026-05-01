using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Helpers;
using VRCTimeline.Models;
using VRCTimeline.Services;
using VRCTimeline.Services.LogParser;

namespace VRCTimeline.ViewModels;

/// <summary>
/// アクティビティ履歴画面の ViewModel。
/// ワールド訪問履歴の表示、プレイヤー検索（UserId ベース）、遭遇統計の集計を行う。
/// </summary>
public partial class ActivityHistoryViewModel : ObservableObject
{
    private readonly LoadingService _loading;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialog;
    private readonly SelfPlayerService _selfPlayer;

    /// <summary>プレイヤー検索の表示テキスト（UI のフィルター入力欄にバインド）</summary>
    [ObservableProperty]
    private string _searchPlayerName = string.Empty;

    /// <summary>ワールド名フィルターテキスト</summary>
    [ObservableProperty]
    private string _searchWorldName = string.Empty;

    /// <summary>表示期間の開始日（デフォルト: 30日前）</summary>
    [ObservableProperty]
    private DateTime _filterDateFrom = DateTime.Today.AddDays(-30);

    /// <summary>表示期間の終了日</summary>
    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today.AddDays(1);

    /// <summary>データ読み込み中フラグ</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>DataGrid で選択中のワールド訪問</summary>
    [ObservableProperty]
    private WorldVisitDisplay? _selectedVisit;

    /// <summary>プレイヤー検索時の遭遇統計を表示するかどうか</summary>
    [ObservableProperty]
    private bool _hasPlayerSummary;

    /// <summary>検索中プレイヤーの遭遇回数</summary>
    [ObservableProperty]
    private int _playerEncounterCount;

    /// <summary>検索中プレイヤーとの合計時間（"HH:MM" 形式）</summary>
    [ObservableProperty]
    private string _playerTotalTime = "";

    /// <summary>初回ロード完了フラグ（日付変更時の自動リロード制御用）</summary>
    private bool _initialized;

    /// <summary>特定のワールド訪問IDでフィルターする場合に使用（写真画面からの遷移用）</summary>
    private int? _filterVisitId;

    /// <summary>
    /// カードクリック時に設定されるユーザーID。
    /// テキスト入力ではなくカードクリックで検索した場合、UserId ベースで絞り込む。
    /// テキスト入力時は null にリセットされる。
    /// </summary>
    private string? _searchPlayerUserId;

    // ── プロパティ変更ハンドラ ──

    partial void OnFilterDateFromChanged(DateTime value)
    {
        if (_initialized)
            LoadHistoryCommand.Execute(null);
    }

    partial void OnFilterDateToChanged(DateTime value)
    {
        if (_initialized)
            LoadHistoryCommand.Execute(null);
    }

    /// <summary>テキスト入力変更時に UserId をクリアし、空文字なら自動リロード</summary>
    partial void OnSearchPlayerNameChanged(string value)
    {
        _searchPlayerUserId = null;
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadHistoryCommand.Execute(null);
    }

    partial void OnSearchWorldNameChanged(string value)
    {
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadHistoryCommand.Execute(null);
    }

    public ActivityHistoryViewModel(
        LoadingService loadingService,
        NavigationService navigationService,
        DialogService dialogService,
        SelfPlayerService selfPlayerService)
    {
        _loading = loadingService;
        _navigation = navigationService;
        _dialog = dialogService;
        _selfPlayer = selfPlayerService;
    }

    /// <summary>ワールド訪問履歴の一覧</summary>
    public ObservableCollection<WorldVisitDisplay> WorldVisits { get; } = [];

    /// <summary>選択中のワールド訪問に参加していたプレイヤーの一覧</summary>
    public ObservableCollection<PlayerDisplay> SelectedVisitPlayers { get; } = [];

    /// <summary>訪問が選択されているか</summary>
    public bool IsVisitSelected => SelectedVisit != null;

    /// <summary>再参加ボタンが有効か（インスタンスIDがある場合のみ）</summary>
    public bool CanRejoinSelected => SelectedVisit != null && !string.IsNullOrEmpty(SelectedVisit.InstanceId);

    /// <summary>選択中のワールド名</summary>
    public string SelectedVisitWorldName => SelectedVisit?.WorldName ?? "";

    /// <summary>選択中の訪問の滞在時間範囲</summary>
    public string SelectedVisitTimeRange => SelectedVisit?.TimeRange ?? "";

    /// <summary>初回の履歴読み込み</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await LoadHistoryAsync();
    }

    /// <summary>データ変更後の再読み込み</summary>
    public async Task ReloadAsync()
    {
        _initialized = true;
        await LoadHistoryAsync();
    }

    /// <summary>特定のワールド訪問IDで絞り込み表示する（写真画面からの遷移用）</summary>
    public async Task FilterByVisitId(int visitId)
    {
        _initialized = true;
        _filterVisitId = visitId;
        await LoadHistoryAsync();
        SelectedVisit = WorldVisits.FirstOrDefault(v => v.Id == visitId);
    }

    /// <summary>訪問選択時にプレイヤー一覧を更新する</summary>
    partial void OnSelectedVisitChanged(WorldVisitDisplay? value)
    {
        SelectedVisitPlayers.Clear();
        if (value != null)
        {
            foreach (var p in value.PlayerSessions)
                SelectedVisitPlayers.Add(p);
        }
        OnPropertyChanged(nameof(IsVisitSelected));
        OnPropertyChanged(nameof(CanRejoinSelected));
        OnPropertyChanged(nameof(SelectedVisitWorldName));
        OnPropertyChanged(nameof(SelectedVisitTimeRange));
    }

    /// <summary>
    /// ワールド訪問履歴を DB から読み込み、フィルタリング・遭遇統計の集計を行う。
    /// プレイヤー検索は UserId ベース: カードクリック時は直接 UserId で、
    /// テキスト入力時は表示名→UserId 解決→同一ユーザーの全セッションを含める。
    /// </summary>
    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        IsLoading = true;
        _loading.Show("アクティビティを読み込み中...");
        try
        {
            // UI スレッドの値をキャプチャ（バックグラウンド処理用）
            var dateFrom = FilterDateFrom;
            var dateTo = FilterDateTo;
            var searchPlayer = SearchPlayerName;
            var searchWorld = SearchWorldName;
            var searchPlayerUserId = _searchPlayerUserId;
            var filterVisitId = _filterVisitId;
            _filterVisitId = null;

            var selfName = await _selfPlayer.GetSelfPlayerNameAsync();

            var result = await Task.Run(async () =>
            {
                await using var db = new AppDbContext();

                var query = db.WorldVisits
                    .Include(v => v.PlayerSessions)
                    .AsQueryable();

                // 日付範囲 or 特定訪問でフィルター
                if (filterVisitId.HasValue)
                    query = query.Where(v => v.Id == filterVisitId.Value);
                else
                    query = query.Where(v => v.JoinedAt >= dateFrom && v.JoinedAt <= dateTo);

                var allVisits = await query
                    .OrderByDescending(v => v.JoinedAt)
                    .ToListAsync();

                // ── プレイヤーフィルタ（UserId ベース） ──
                HashSet<string>? resolvedUserIds = null;

                if (!string.IsNullOrWhiteSpace(searchPlayerUserId))
                {
                    // カードクリック: UserId で直接フィルター
                    allVisits = allVisits.Where(v => v.PlayerSessions.Any(s =>
                        s.UserId == searchPlayerUserId)).ToList();
                    resolvedUserIds = [searchPlayerUserId];
                }
                else if (!string.IsNullOrWhiteSpace(searchPlayer))
                {
                    // テキスト入力: 表示名にマッチするセッションの UserId を収集し、
                    // そのユーザーの全セッション（名前変更後含む）でフィルター
                    var search = searchPlayer.Trim();
                    resolvedUserIds = allVisits
                        .SelectMany(v => v.PlayerSessions)
                        .Where(s => KanaHelper.ContainsKanaInsensitive(s.DisplayName, search)
                                    && !string.IsNullOrEmpty(s.UserId))
                        .Select(s => s.UserId)
                        .Distinct()
                        .ToHashSet();

                    allVisits = allVisits.Where(v => v.PlayerSessions.Any(s =>
                        (resolvedUserIds.Count > 0 && !string.IsNullOrEmpty(s.UserId) && resolvedUserIds.Contains(s.UserId))
                        || KanaHelper.ContainsKanaInsensitive(s.DisplayName, search)
                    )).ToList();
                }

                // ── ワールド名フィルタ ──
                if (!string.IsNullOrWhiteSpace(searchWorld))
                {
                    var search = searchWorld.Trim();
                    allVisits = allVisits.Where(v =>
                        KanaHelper.ContainsKanaInsensitive(v.WorldName, search)).ToList();
                }

                var visits = allVisits.Take(200).ToList();

                // ── 表示用データに変換 ──
                var displayItems = visits.Select(v =>
                {
                    var sessions = v.PlayerSessions
                        .Select(s => new PlayerDisplay
                        {
                            DisplayName = LogPatterns.CleanPlayerName(s.DisplayName),
                            UserId = s.UserId,
                            JoinedAt = s.JoinedAt,
                            LeftAt = s.LeftAt
                        })
                        .Where(s => s.DisplayName != selfName)
                        .OrderBy(s => s.JoinedAt)
                        .ToList();

                    return new WorldVisitDisplay
                    {
                        Id = v.Id,
                        WorldName = v.WorldName,
                        InstanceId = v.InstanceId,
                        JoinedAt = v.JoinedAt,
                        LeftAt = v.LeftAt,
                        PlayerCount = sessions.Count + 1,
                        PlayerNames = sessions.Select(s => s.DisplayName).Distinct().ToList(),
                        PlayerSessions = sessions
                    };
                }).ToList();

                // ── 遭遇統計の集計（UserId ベース） ──
                (bool Has, int Count, string TotalTime) summary = default;
                bool hasSearch = !string.IsNullOrWhiteSpace(searchPlayerUserId) || !string.IsNullOrWhiteSpace(searchPlayer);
                if (hasSearch)
                {
                    List<PlayerSession> matched;
                    if (resolvedUserIds is { Count: > 0 })
                    {
                        matched = visits
                            .SelectMany(v => v.PlayerSessions)
                            .Where(s => !string.IsNullOrEmpty(s.UserId) && resolvedUserIds.Contains(s.UserId))
                            .ToList();
                    }
                    else
                    {
                        var search = searchPlayer!.Trim();
                        matched = visits
                            .SelectMany(v => v.PlayerSessions)
                            .Where(s => KanaHelper.ContainsKanaInsensitive(s.DisplayName, search))
                            .ToList();
                    }

                    var ts = TimeSpan.FromMinutes(
                        (int)matched.Sum(s =>
                            s.LeftAt != null ? (s.LeftAt.Value - s.JoinedAt).TotalMinutes : 0));
                    summary = (
                        matched.Count > 0,
                        matched.Count,
                        $"{(int)ts.TotalHours}:{ts.Minutes:D2}"
                    );
                }

                return (displayItems, summary, hasSearch);
            });

            // ── UI 更新 ──
            var previousSelectedId = SelectedVisit?.Id;
            WorldVisits.Clear();
            SelectedVisit = null;

            foreach (var item in result.displayItems)
                WorldVisits.Add(item);

            // 選択状態を復元
            if (previousSelectedId.HasValue)
                SelectedVisit = WorldVisits.FirstOrDefault(v => v.Id == previousSelectedId.Value);

            if (result.hasSearch)
            {
                HasPlayerSummary = result.summary.Has;
                PlayerEncounterCount = result.summary.Count;
                PlayerTotalTime = result.summary.TotalTime;
            }
            else
            {
                HasPlayerSummary = false;
            }
        }
        finally
        {
            IsLoading = false;
            _loading.Hide();
        }
    }

    /// <summary>
    /// プレイヤーカードクリック時の検索コマンド。
    /// 表示名を検索欄に表示しつつ、UserId ベースでフィルタリングする。
    /// OnSearchPlayerNameChanged で一度クリアされた _searchPlayerUserId を再設定する。
    /// </summary>
    [RelayCommand]
    private async Task SearchByPlayer(PlayerDisplay player)
    {
        SearchPlayerName = player.DisplayName;
        _searchPlayerUserId = !string.IsNullOrEmpty(player.UserId) ? player.UserId : null;
        await LoadHistoryAsync();
    }

    /// <summary>選択中のワールドに再参加する（VRChat のプロトコルリンクを起動）</summary>
    [RelayCommand]
    private async Task RejoinSelectedInstanceAsync()
    {
        if (SelectedVisit == null || string.IsNullOrEmpty(SelectedVisit.InstanceId)) return;
        if (await _dialog.ShowConfirmAsync(string.Format(LocalizationService.GetString("Confirm_Rejoin"), SelectedVisit.WorldName)))
            VRChatLauncher.LaunchInstance(SelectedVisit.InstanceId);
    }

    /// <summary>選択中のワールド訪問の写真を表示する画面に遷移する</summary>
    [RelayCommand]
    private async Task ShowPhotosForVisit()
    {
        if (SelectedVisit == null) return;

        await using var db = new AppDbContext();
        var hasPhotos = await db.PhotoRecords.AnyAsync(p => p.WorldVisitId == SelectedVisit.Id);
        if (!hasPhotos)
        {
            await _dialog.ShowInfoAsync(LocalizationService.GetString("Info_NoPhotosForVisit"));
            return;
        }

        _navigation.ShowPhotosForVisit(SelectedVisit.Id);
    }

    /// <summary>訪問詳細パネルを閉じる</summary>
    [RelayCommand]
    private void CloseVisitDetail()
    {
        SelectedVisit = null;
    }
}

/// <summary>
/// DataGrid に表示するワールド訪問の表示用モデル。
/// DB エンティティ (WorldVisit) から変換して使用する。
/// </summary>
public class WorldVisitDisplay
{
    public int Id { get; set; }

    /// <summary>ワールド名</summary>
    public string WorldName { get; set; } = string.Empty;

    /// <summary>インスタンスID（再参加用）</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>入室日時</summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>退室日時</summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>日付・曜日・時刻を含む表示文字列</summary>
    public string JoinedAtDisplay => DateFormatHelper.FormatDateWithDayAndTime(JoinedAt);

    /// <summary>滞在時間の表示文字列（未退室の場合はローカライズされた「滞在中」相当文字列）</summary>
    public string Duration => LeftAt.HasValue
        ? (LeftAt.Value - JoinedAt).ToString(@"hh\:mm\:ss")
        : LocalizationService.GetString("Str_StayingInWorld");

    /// <summary>入室〜退室の時間範囲表示</summary>
    public string TimeRange => DateFormatHelper.FormatTimeRange(JoinedAt, LeftAt);

    /// <summary>同室プレイヤー数（自分を含む）</summary>
    public int PlayerCount { get; set; }

    /// <summary>同室プレイヤーの表示名リスト（DataGrid のピル表示用）</summary>
    public List<string> PlayerNames { get; set; } = [];

    /// <summary>同室プレイヤーのセッション詳細リスト（カード UI 用）</summary>
    public List<PlayerDisplay> PlayerSessions { get; set; } = [];
}
