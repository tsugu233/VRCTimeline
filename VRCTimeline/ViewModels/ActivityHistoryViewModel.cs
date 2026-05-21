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
public partial class ActivityHistoryViewModel : ObservableObject, IDisposable
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

    /// <summary>表示期間の終了日（選択日を含む）</summary>
    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today;

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

    /// <summary>FilterDateTo を「今日」に追従させるかどうか（日付またぎ時の自動更新用）</summary>
    private bool _filterDateToFollowsToday = true;

    /// <summary>日付またぎを検知して FilterDateTo を更新するためのウォッチャー</summary>
    private readonly DayChangeWatcher _dayChangeWatcher;

    /// <summary>特定のワールド訪問IDでフィルターする場合に使用（写真画面からの遷移用）</summary>
    private int? _filterVisitId;

    /// <summary>
    /// カードクリック時に設定されるユーザーID。
    /// テキスト入力ではなくカードクリックで検索した場合、UserId ベースで絞り込む。
    /// テキスト入力時は null にリセットされる。
    /// </summary>
    private string? _searchPlayerUserId;

    /// <summary>
    /// 画面遷移時に SearchPlayerName / SearchWorldName をクリアする際、各 partial ハンドラ内の
    /// 自動リロード（LoadHistoryCommand.Execute）を一時的に抑止するためのフラグ。
    /// </summary>
    private bool _suppressFilterAutoReload;

    // ── プロパティ変更ハンドラ ──

    /// <summary>テキスト入力変更時に UserId をクリアし、空文字なら自動リロード</summary>
    partial void OnSearchPlayerNameChanged(string value)
    {
        _searchPlayerUserId = null;
        if (_suppressFilterAutoReload) return;
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadHistoryCommand.Execute(null);
    }

    partial void OnSearchWorldNameChanged(string value)
    {
        if (_suppressFilterAutoReload) return;
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadHistoryCommand.Execute(null);
    }

    /// <summary>ユーザーが終了日を変更した際、その値が「今日」かどうかを記録する</summary>
    partial void OnFilterDateToChanged(DateTime value)
    {
        _filterDateToFollowsToday = value.Date == DateTime.Today;
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
        _dayChangeWatcher = new DayChangeWatcher(() =>
        {
            if (_filterDateToFollowsToday) FilterDateTo = DateTime.Today;
        });

        // 曜日略称や "滞在中" 等のローカライズ文字列を含むプロパティを、
        // 再ロードなしで言語切替に追従させるために購読する。
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// 言語切替時に、WorldVisit カードの日時表示や選択中訪問の時間範囲を再評価させる。
    /// 一覧の再構築は走らせず、INotifyPropertyChanged 経由で UI 側の再フォーマットだけを促す。
    /// </summary>
    private void OnLanguageChanged()
    {
        foreach (var v in WorldVisits)
            v.RefreshLocalizedStrings();
        OnPropertyChanged(nameof(SelectedVisitTimeRange));
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
        // 「アクティビティを表示」ボタン遷移時はプレイヤー名／ワールド名フィルターが残ったままだと、
        // LoadHistoryAsync の後段で条件を満たさない訪問が除外されてしまい対象訪問が表示されない。
        // 各 partial ハンドラの自動リロード経路は抑止フラグで止め、後段の訪問フィルター読み込みに一本化する。
        _suppressFilterAutoReload = true;
        try
        {
            SearchPlayerName = string.Empty;
            SearchWorldName = string.Empty;
        }
        finally { _suppressFilterAutoReload = false; }
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
            var selfUserId = await _selfPlayer.GetSelfUserIdAsync();

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
                    query = query.Where(v => v.JoinedAt >= dateFrom && v.JoinedAt < dateTo.Date.AddDays(1));

                var allVisits = await query
                    .OrderByDescending(v => v.JoinedAt)
                    .ToListAsync();

                // ── プレイヤーフィルタ（UserId ベース） ──
                HashSet<string>? resolvedUserIds = null;

                if (!string.IsNullOrWhiteSpace(searchPlayerUserId))
                {
                    // カードクリック: UserId で絞り込みつつ、UserId が空のセッション
                    // (旧 activity log からインポートされた古いデータ等) は DisplayName で
                    // フォールバックマッチさせる。
                    var fallbackName = searchPlayer;
                    allVisits = allVisits.Where(v => v.PlayerSessions.Any(s =>
                        s.UserId == searchPlayerUserId
                        || (string.IsNullOrEmpty(s.UserId)
                            && !string.IsNullOrWhiteSpace(fallbackName)
                            && KanaHelper.ContainsKanaInsensitive(s.DisplayName, fallbackName))
                    )).ToList();
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
                // 同一インスタンス内の再入場は PlayerSession が複数行に分かれて記録されるため、
                // UserId（無ければ DisplayName）でグルーピングして 1 プレイヤー 1 カードに統合する。
                // 各セッションの時刻範囲は Sessions に保持し、TimeRange で " | " 区切り表示する。
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
                        .GroupBy(s => !string.IsNullOrEmpty(s.UserId) ? s.UserId : s.DisplayName)
                        .Select(g =>
                        {
                            var ordered = g.OrderBy(s => s.JoinedAt).ToList();
                            var stillIn = ordered.Any(s => s.LeftAt == null);
                            return new PlayerDisplay
                            {
                                DisplayName = ordered[0].DisplayName,
                                UserId = ordered[0].UserId,
                                JoinedAt = ordered[0].JoinedAt,
                                LeftAt = stillIn ? null : ordered.Max(s => s.LeftAt),
                                Sessions = ordered.Select(s => (s.JoinedAt, s.LeftAt)).ToList()
                            };
                        })
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
                        PlayerNames = sessions.Select(s => s.DisplayName).ToList(),
                        PlayerSessions = sessions
                    };
                }).ToList();

                // ── 遭遇統計の集計（UserId ベース） ──
                // 日付範囲・ワールド名・特定訪問フィルタには影響されないライフタイム統計として算出。
                // PlayerSessions を AsNoTracking で直接引くことで、先行クエリの date-filter された
                // Include による tracker 状態の影響（再入時に navigation が再評価されない等）を排除する。
                (bool Has, int Count, string TotalTime) summary = default;
                bool hasSearch = !string.IsNullOrWhiteSpace(searchPlayerUserId) || !string.IsNullOrWhiteSpace(searchPlayer);
                if (hasSearch)
                {
                    List<PlayerSession> matched;
                    if (!string.IsNullOrWhiteSpace(searchPlayerUserId))
                    {
                        // カードクリック: SQL で UserId 一致を直接引く
                        var targetUserId = searchPlayerUserId;
                        var byUserId = await db.PlayerSessions.AsNoTracking()
                            .Where(s => s.UserId == targetUserId)
                            .ToListAsync();

                        // UserId が空のセッション (旧 activity log インポート由来等) は DisplayName でフォールバック。
                        var fallbackName = searchPlayer?.Trim();
                        List<PlayerSession> byName = [];
                        if (!string.IsNullOrWhiteSpace(fallbackName))
                        {
                            var emptyIdSessions = await db.PlayerSessions.AsNoTracking()
                                .Where(s => s.UserId == "")
                                .ToListAsync();
                            byName = emptyIdSessions
                                .Where(s => KanaHelper.ContainsKanaInsensitive(s.DisplayName, fallbackName))
                                .ToList();
                        }
                        matched = byUserId.Concat(byName).ToList();
                    }
                    else
                    {
                        // テキスト入力: 全期間データから名前一致セッションの UserId を解決し、
                        // 同 UserId の全セッション（改名後含む）＋ UserId 空の名前一致をまとめてヒット対象にする。
                        // KanaHelper は SQL に翻訳できないため、全 PlayerSessions を一度ロードして in-memory で照合する。
                        var search = searchPlayer!.Trim();
                        var allSessions = await db.PlayerSessions.AsNoTracking().ToListAsync();
                        var summaryUserIds = allSessions
                            .Where(s => !string.IsNullOrEmpty(s.UserId)
                                        && KanaHelper.ContainsKanaInsensitive(s.DisplayName, search))
                            .Select(s => s.UserId)
                            .Distinct()
                            .ToHashSet();
                        matched = allSessions
                            .Where(s =>
                                (!string.IsNullOrEmpty(s.UserId) && summaryUserIds.Contains(s.UserId))
                                || (string.IsNullOrEmpty(s.UserId) && KanaHelper.ContainsKanaInsensitive(s.DisplayName, search)))
                            .ToList();
                    }

                    // 自分自身は「遭遇」ではないため集計から除外する。
                    // UserId が一致するか、UserId 空セッションで表示名が一致する場合をはじく。
                    matched = matched
                        .Where(s =>
                            !(!string.IsNullOrEmpty(selfUserId) && s.UserId == selfUserId)
                            && !(string.IsNullOrEmpty(s.UserId) && !string.IsNullOrEmpty(selfName) && s.DisplayName == selfName))
                        .ToList();

                    // 合計分: int.MaxValue 分（約 4084 年）を超える非現実値で OverflowException を起こさないようクランプ。
                    var totalMinutes = matched
                        .Where(s => s.LeftAt != null)
                        .Sum(s => (s.LeftAt!.Value - s.JoinedAt).TotalMinutes);
                    var ts = TimeSpan.FromMinutes(Math.Min(totalMinutes, (double)int.MaxValue));
                    // 同一インスタンスでの再入場は 1 遭遇として数えるため、訪問単位でユニーク化する。
                    var encounterCount = matched.Select(s => s.WorldVisitId).Distinct().Count();
                    summary = (
                        matched.Count > 0,
                        encounterCount,
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
        catch (Exception ex) { AppLogger.LogError(ex); }
        finally
        {
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
/// DataGrid に表示するワールド訪問の表示用モデル。
/// DB エンティティ (WorldVisit) から変換して使用する。
/// 言語切替で再フォーマットさせる必要があるプロパティ群があるため ObservableObject を継承する。
/// </summary>
public class WorldVisitDisplay : ObservableObject
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
        ? FormatTotalDuration(LeftAt.Value - JoinedAt)
        : LocalizationService.GetString("Str_StayingInWorld");

    /// <summary>
    /// 滞在時間を「総時間:分:秒」形式に整形する。
    /// 標準の "hh\:mm\:ss" は 24 時間で巻き戻るため、
    /// 25 時間以上の長時間滞在も正しく表示できるよう TotalHours を使用する。
    /// </summary>
    private static string FormatTotalDuration(TimeSpan d)
        => $"{(int)d.TotalHours:D2}:{d.Minutes:D2}:{d.Seconds:D2}";

    /// <summary>入室〜退室の時間範囲表示</summary>
    public string TimeRange => DateFormatHelper.FormatTimeRange(JoinedAt, LeftAt);

    /// <summary>同室プレイヤー数（自分を含む）</summary>
    public int PlayerCount { get; set; }

    /// <summary>同室プレイヤーの表示名リスト（DataGrid のピル表示用）</summary>
    public List<string> PlayerNames { get; set; } = [];

    /// <summary>同室プレイヤーのセッション詳細リスト（カード UI 用）</summary>
    public List<PlayerDisplay> PlayerSessions { get; set; } = [];

    /// <summary>
    /// 言語切替時に呼び出されるリフレッシュ。曜日略称や「滞在中」相当ローカライズ文字列を
    /// 含むプロパティの再評価を WPF バインディングに促す。
    /// </summary>
    public void RefreshLocalizedStrings()
    {
        OnPropertyChanged(nameof(JoinedAtDisplay));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(TimeRange));
    }
}
