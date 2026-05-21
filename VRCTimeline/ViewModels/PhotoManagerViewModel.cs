using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Helpers;
using VRCTimeline.Models;
using VRCTimeline.Services;
using VRCTimeline.Services.LogParser;
using VRCTimeline.Views;

namespace VRCTimeline.ViewModels;

/// <summary>
/// 写真管理画面の ViewModel。
/// VRChat スクリーンショットをワールド訪問ごとにグループ化して表示し、
/// プレイヤー・ワールド名でフィルタリングする。
/// PhotoWatcher からのリアルタイム通知にも対応する。
/// </summary>
public partial class PhotoManagerViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly LoadingService _loading;
    private readonly NavigationService _navigation;
    private readonly DialogService _dialog;
    private readonly SelfPlayerService _selfPlayer;

    /// <summary>初回ロード完了フラグ</summary>
    private bool _initialized;

    /// <summary>初回ロード時の取得件数制限フラグ</summary>
    private bool _isInitialLoad;

    /// <summary>
    /// DB メンテナンス（孤立写真の紐づけ修復・存在しないファイルのレコード削除）を
    /// このセッションで既に実施したかどうか。
    /// PhotoRecord 全行ロード + File.Exists の重コストをフィルタ変更ごとに繰り返さないため、
    /// 起動後 1 回（および ReloadAsync 経由のデータ再構築時）だけ走らせる。
    /// </summary>
    private bool _maintenanceDone;

    /// <summary>FilterDateTo を「今日」に追従させるかどうか（日付またぎ時の自動更新用）</summary>
    private bool _filterDateToFollowsToday = true;

    /// <summary>日付またぎを検知して FilterDateTo を更新するためのウォッチャー</summary>
    private readonly DayChangeWatcher _dayChangeWatcher;

    /// <summary>特定ワールド訪問IDでフィルタリングする場合に使用（アクティビティ画面からの遷移用）</summary>
    private int? _filterWorldVisitId;

    /// <summary>訪問フィルター時のワールド名（写真選択前のデフォルト表示用）</summary>
    private string _currentVisitWorldName = "";

    /// <summary>
    /// 訪問フィルター時のワールド入室日時（写真選択前のデフォルト表示用）。
    /// フォーマット済み文字列ではなく DateTime のまま保持することで、
    /// 言語切替時に再フォーマットしても曜日略称等が現在のカルチャで表示できる。
    /// </summary>
    private DateTime? _currentVisitJoinedAt;

    /// <summary>訪問フィルター時のワールド退室日時（同上の理由で DateTime のまま保持）</summary>
    private DateTime? _currentVisitLeftAt;

    /// <summary>訪問フィルター時のプレイヤーリスト（写真選択前のデフォルト表示用）</summary>
    private List<PlayerDisplay> _currentVisitPlayers = [];

    /// <summary>表示中写真の最小撮影日時（言語変更時の再フォーマット用）</summary>
    private DateTime? _photoMinDate;

    /// <summary>表示中写真の最大撮影日時（言語変更時の再フォーマット用）</summary>
    private DateTime? _photoMaxDate;

    /// <summary>
    /// プレイヤーカードクリック時に設定されるユーザーID。
    /// テキスト入力ではなくカードクリックで検索した場合、UserId ベースで遭遇統計を集計する。
    /// テキスト入力時は null にリセットされる。
    /// </summary>
    private string? _searchPlayerUserId;

    /// <summary>
    /// 画面遷移時に PlayerFilter / WorldFilter をクリアする際、各 partial ハンドラ内の
    /// 自動リロード（LoadPhotosCommand.Execute）を一時的に抑止するためのフラグ。
    /// </summary>
    private bool _suppressFilterAutoReload;

    /// <summary>表示期間の開始日（デフォルト: 30日前）</summary>
    [ObservableProperty]
    private DateTime _filterDateFrom = DateTime.Today.AddDays(-30);

    /// <summary>表示期間の終了日（選択日を含む）</summary>
    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today;

    /// <summary>プレイヤー名フィルターテキスト</summary>
    [ObservableProperty]
    private string? _playerFilter;

    /// <summary>ワールド名フィルターテキスト</summary>
    [ObservableProperty]
    private string? _worldFilter;

    /// <summary>ステータスバーに表示するテキスト</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>写真が1枚もない場合のプレースホルダー表示フラグ</summary>
    [ObservableProperty]
    private bool _hasNoPhotos;

    /// <summary>現在選択中の写真</summary>
    [ObservableProperty]
    private PhotoDisplayItem? _selectedPhoto;

    /// <summary>表示中写真の日付範囲テキスト</summary>
    [ObservableProperty]
    private string _dateRangeText = string.Empty;

    /// <summary>プレイヤー検索時の遭遇統計を表示するかどうか</summary>
    [ObservableProperty]
    private bool _hasPlayerSummary;

    /// <summary>検索中プレイヤーの遭遇回数</summary>
    [ObservableProperty]
    private int _playerEncounterCount;

    /// <summary>検索中プレイヤーとの合計時間（"HH:MM" 形式）</summary>
    [ObservableProperty]
    private string _playerTotalTime = "";

    /// <summary>ワールド訪問ごとにグループ化された写真の一覧</summary>
    public ObservableCollection<PhotoGroupDisplay> PhotoGroups { get; } = [];

    /// <summary>選択中の写真に関連するプレイヤーの一覧</summary>
    public ObservableCollection<PlayerDisplay> SelectedPhotoPlayers { get; } = [];

    /// <summary>写真が選択されているか</summary>
    public bool IsPhotoSelected => SelectedPhoto != null;

    /// <summary>プレイヤーカードを表示すべきコンテキストがあるか</summary>
    public bool HasPlayerContext => SelectedPhoto != null || _currentVisitPlayers.Count > 0;

    /// <summary>選択写真 or 訪問フィルターのワールド名</summary>
    public string SelectedPhotoWorldName => SelectedPhoto?.WorldName ?? _currentVisitWorldName;

    /// <summary>
    /// 選択写真 or 訪問フィルターの滞在時間範囲。
    /// 都度フォーマットするため、言語切替で OnPropertyChanged を発火すれば現在のカルチャで再表示される。
    /// </summary>
    public string SelectedPhotoTimeRange => SelectedPhoto?.WorldTimeRange ?? CurrentVisitTimeRangeFormatted;

    /// <summary>
    /// 訪問フィルターコンテキストの時間範囲を現在のカルチャで整形する。
    /// JoinedAt が null の場合（写真フィルター解除中）は空文字を返す。
    /// </summary>
    private string CurrentVisitTimeRangeFormatted =>
        _currentVisitJoinedAt.HasValue
            ? DateFormatHelper.FormatTimeRange(_currentVisitJoinedAt.Value, _currentVisitLeftAt)
            : string.Empty;

    public PhotoManagerViewModel(
        SettingsService settingsService,
        PhotoWatcher photoWatcher,
        LoadingService loadingService,
        NavigationService navigationService,
        DialogService dialogService,
        SelfPlayerService selfPlayerService)
    {
        _settingsService = settingsService;
        _loading = loadingService;
        _navigation = navigationService;
        _dialog = dialogService;
        _selfPlayer = selfPlayerService;

        // PhotoWatcher からのリアルタイム通知を購読
        photoWatcher.PhotoAdded += OnPhotoAdded;

        // 言語切替時にステータステキスト・日付範囲表示を再ローカライズする
        LocalizationService.LanguageChanged += OnLanguageChanged;

        _dayChangeWatcher = new DayChangeWatcher(() =>
        {
            if (_filterDateToFollowsToday) FilterDateTo = DateTime.Today;
        });
    }

    /// <summary>
    /// 言語切替時に表示テキストを現在のカルチャで再生成する。
    /// ViewModel 側プロパティ（ステータス、日付範囲テキスト、選択中時間範囲）の更新に加えて、
    /// 各 PhotoGroup/PhotoDisplayItem の曜日付き表示プロパティも INPC 経由で再評価させる。
    /// 再ロードは行わないので DB アクセスは発生しない。
    /// </summary>
    private void OnLanguageChanged()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            UpdateStatus();
            UpdateDateRangeText();

            foreach (var group in PhotoGroups)
            {
                group.RefreshLocalizedStrings();
                foreach (var photo in group.Photos)
                    photo.RefreshLocalizedStrings();
            }

            // 訪問コンテキストの時間範囲は CurrentVisitTimeRangeFormatted 経由で都度フォーマットされるため、
            // 選択写真側ではなくこちらの再評価通知だけで現在のカルチャに追従する。
            OnPropertyChanged(nameof(SelectedPhotoTimeRange));
        });
    }

    /// <summary>保持している日付範囲を現在のカルチャで再フォーマットする</summary>
    private void UpdateDateRangeText()
    {
        if (_photoMinDate.HasValue && _photoMaxDate.HasValue)
        {
            var culture = DateFormatHelper.GetCurrentCulture();
            DateRangeText = $"{_photoMinDate.Value.ToString(DateFormatHelper.DateWithDay, culture)} ～ {_photoMaxDate.Value.ToString(DateFormatHelper.DateWithDay, culture)}";
        }
        else
        {
            DateRangeText = string.Empty;
        }
    }

    // ── フィルターテキスト変更ハンドラ（クリア時に自動リロード） ──

    partial void OnPlayerFilterChanged(string? value)
    {
        _searchPlayerUserId = null;
        if (_suppressFilterAutoReload) return;
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadPhotosCommand.Execute(null);
    }

    partial void OnWorldFilterChanged(string? value)
    {
        if (_suppressFilterAutoReload) return;
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadPhotosCommand.Execute(null);
    }

    /// <summary>ユーザーが終了日を変更した際、その値が「今日」かどうかを記録する</summary>
    partial void OnFilterDateToChanged(DateTime value)
    {
        _filterDateToFollowsToday = value.Date == DateTime.Today;
    }

    /// <summary>PhotoWatcher から新しい写真が追加された際にリアルタイムで一覧に反映する</summary>
    private void OnPhotoAdded(PhotoWatcher.PhotoAddedInfo info)
    {
        if (!_initialized || !File.Exists(info.FilePath)) return;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var item = new PhotoDisplayItem
            {
                FilePath = info.FilePath,
                FileName = info.FileName,
                TakenAt = info.TakenAt,
                WorldName = info.WorldName,
                WorldJoinedAt = info.WorldJoinedAt,
                WorldVisitId = info.WorldVisitId
            };

            // 同じワールド訪問のグループがあれば追加、なければ新規グループ作成
            var group = PhotoGroups.FirstOrDefault(g => g.WorldVisitId == info.WorldVisitId);
            if (group != null)
            {
                group.Photos.Insert(0, item);
            }
            else
            {
                PhotoGroups.Insert(0, new PhotoGroupDisplay
                {
                    WorldName = info.WorldName ?? "不明なワールド",
                    JoinedAt = info.WorldJoinedAt ?? info.TakenAt,
                    WorldVisitId = info.WorldVisitId,
                    Photos = new ObservableCollection<PhotoDisplayItem> { item }
                });
            }

            HasNoPhotos = false;
            UpdateStatus();
        });
    }

    /// <summary>初回の写真読み込み</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        _isInitialLoad = true;
        await LoadPhotosAsync();
    }

    /// <summary>データ変更後の再読み込み</summary>
    public async Task ReloadAsync()
    {
        _initialized = true;
        // データインポート等で DB が変化したのでメンテナンスを再実行する
        _maintenanceDone = false;
        await LoadPhotosAsync();
    }

    /// <summary>特定ワールド訪問の写真のみを表示する（アクティビティ画面からの遷移用）</summary>
    public async Task FilterByWorldVisitId(int worldVisitId)
    {
        // 「写真を表示」ボタン遷移時はプレイヤー名／ワールド名フィルターが残ったままだと、
        // 後続でフィルター無効化したいケースで意図したデータが見えなくなる。
        // 各 partial ハンドラの自動リロード経路は抑止フラグで止め、後段の訪問フィルター読み込みに一本化する。
        _suppressFilterAutoReload = true;
        try
        {
            PlayerFilter = null;
            WorldFilter = null;
        }
        finally { _suppressFilterAutoReload = false; }
        _filterWorldVisitId = worldVisitId;
        _initialized = true;
        await LoadPhotosAsync();
    }

    /// <summary>写真選択時にプレイヤーカード一覧を更新する</summary>
    partial void OnSelectedPhotoChanged(PhotoDisplayItem? value)
    {
        OnPropertyChanged(nameof(IsPhotoSelected));
        OnPropertyChanged(nameof(HasPlayerContext));
        OnPropertyChanged(nameof(SelectedPhotoWorldName));
        OnPropertyChanged(nameof(SelectedPhotoTimeRange));
        UpdatePlayerCards();
    }

    /// <summary>
    /// 選択写真に対応するプレイヤーカードを読み込む。
    /// 未読み込みの場合は DB から取得してキャッシュする。
    /// </summary>
    private async void UpdatePlayerCards()
    {
        SelectedPhotoPlayers.Clear();

        // await 前に対象写真を捕捉。await 中に選択が変わっても捕捉した写真に対して処理を完結させる。
        var selected = SelectedPhoto;
        if (selected == null)
        {
            // 写真未選択時は訪問フィルターのプレイヤーリストを表示
            foreach (var p in _currentVisitPlayers)
                SelectedPhotoPlayers.Add(p);
            return;
        }

        // 遅延読み込み: 写真に紐づくプレイヤーをまだ取得していない場合
        if (selected.WorldVisitId.HasValue && selected.Players.Count == 0)
        {
            var selfName = await _selfPlayer.GetSelfPlayerNameAsync();

            await using var db = new AppDbContext();
            var players = await db.PlayerSessions
                .Where(s => s.WorldVisitId == selected.WorldVisitId.Value)
                .Select(s => new { s.DisplayName, s.UserId, s.JoinedAt, s.LeftAt })
                .ToListAsync();
            selected.Players = players
                .Select(p => new PlayerDisplay
                {
                    DisplayName = LogPatterns.CleanPlayerName(p.DisplayName),
                    UserId = p.UserId,
                    JoinedAt = p.JoinedAt,
                    LeftAt = p.LeftAt
                })
                .Where(p => p.DisplayName != selfName)
                .GroupBy(p => p.DisplayName)
                .Select(g => g.First())
                .OrderBy(p => p.JoinedAt)
                .ToList();
        }

        // await 中に選択が変わっていた場合は古い結果で UI を上書きしない
        if (!ReferenceEquals(SelectedPhoto, selected)) return;

        foreach (var p in selected.Players)
            SelectedPhotoPlayers.Add(p);
    }

    /// <summary>
    /// 写真を DB から読み込み、フィルタリング・グループ化して一覧に表示する。
    /// 孤立写真のワールド訪問紐づけ修復、存在しない写真の削除も行う。
    /// </summary>
    [RelayCommand]
    private async Task LoadPhotosAsync()
    {
        StatusText = "読み込み中...";
        _loading.Show("写真を読み込み中...");
        try
        {
            var dateFrom = FilterDateFrom;
            var dateTo = FilterDateTo;
            var worldFilter = WorldFilter;
            var playerFilter = PlayerFilter;
            var searchPlayerUserId = _searchPlayerUserId;
            var filterVisitId = _filterWorldVisitId;
            _filterWorldVisitId = null;

            var selfName = await _selfPlayer.GetSelfPlayerNameAsync();
            var selfUserId = await _selfPlayer.GetSelfUserIdAsync();

            var runMaintenance = !_maintenanceDone;
            _maintenanceDone = true;

            var result = await Task.Run(async () =>
            {
                await using var db = new AppDbContext();

                // DB メンテナンス: 孤立写真の紐づけ修復・存在しないファイルの削除。
                // 全行ロード + File.Exists で大量写真ライブラリではコスト大なので、
                // セッション初回 (もしくはデータ再構築直後) のみに限定する。
                if (runMaintenance)
                {
                    await RelinkOrphanPhotosAsync(db);
                    await RemoveMissingPhotosAsync(db);
                }

                var query = db.PhotoRecords
                    .Include(p => p.WorldVisit)
                    .AsQueryable();

                bool filterByVisit = filterVisitId.HasValue;
                var visitId = filterVisitId ?? 0;
                if (filterByVisit)
                {
                    query = query.Where(p => p.WorldVisitId == visitId);
                }
                else
                {
                    query = query.Where(p => p.TakenAt >= dateFrom && p.TakenAt < dateTo.Date.AddDays(1));
                }

                var photos = await query
                    .OrderByDescending(p => p.TakenAt)
                    .ToListAsync();

                // ── フィルタリング ──
                // プレイヤー検索は UserId ベースで同一人物（改名歴あり）も同じヒットとして扱う。
                // カードクリック: searchPlayerUserId 直接利用。
                // テキスト入力: 表示名にマッチするセッションの UserId を解決し、同 UserId の
                // 全セッション（別の表示名で記録されたものも含む）をヒット対象にする。
                // この resolvedUserIds は遭遇統計の集計でも再利用する。
                HashSet<string>? resolvedUserIds = null;
                if (!filterByVisit)
                {
                    if (!string.IsNullOrWhiteSpace(worldFilter))
                        photos = photos.Where(p => p.WorldVisit != null &&
                            KanaHelper.ContainsKanaInsensitive(p.WorldVisit.WorldName, worldFilter)).ToList();

                    if (!string.IsNullOrWhiteSpace(playerFilter))
                    {
                        var visitIds = photos
                            .Where(p => p.WorldVisitId.HasValue)
                            .Select(p => p.WorldVisitId!.Value)
                            .Distinct()
                            .ToList();
                        var sessionsInScope = await db.PlayerSessions
                            .Where(s => visitIds.Contains(s.WorldVisitId))
                            .ToListAsync();

                        if (!string.IsNullOrEmpty(searchPlayerUserId))
                        {
                            // カードクリック: UserId 直接照合
                            resolvedUserIds = [searchPlayerUserId];
                        }
                        else
                        {
                            // テキスト入力: 表示名一致セッションの UserId を解決
                            resolvedUserIds = sessionsInScope
                                .Where(s => KanaHelper.ContainsKanaInsensitive(s.DisplayName, playerFilter)
                                            && !string.IsNullOrEmpty(s.UserId))
                                .Select(s => s.UserId)
                                .Distinct()
                                .ToHashSet();
                        }

                        // カードクリック時 (searchPlayerUserId あり) でも、UserId が空のセッション
                        // (旧 activity log インポート由来等) は DisplayName でフォールバックマッチさせる。
                        var filteredVisitIds = sessionsInScope
                            .Where(s =>
                                (resolvedUserIds.Count > 0 && !string.IsNullOrEmpty(s.UserId) && resolvedUserIds.Contains(s.UserId))
                                || (string.IsNullOrEmpty(s.UserId) && KanaHelper.ContainsKanaInsensitive(s.DisplayName, playerFilter)))
                            .Select(s => s.WorldVisitId)
                            .ToHashSet();
                        photos = photos.Where(p => p.WorldVisitId != null &&
                            filteredVisitIds.Contains(p.WorldVisitId.Value)).ToList();
                    }
                }

                // 初回ロード時は件数制限
                if (_isInitialLoad)
                    photos = photos.Take(150).ToList();
                _isInitialLoad = false;

                // ── ワールド訪問ごとにグループ化 ──
                var groups = photos.Count == 0 ? [] : photos
                    .GroupBy(p => p.WorldVisitId ?? -p.Id)
                    .Select(g =>
                    {
                        var first = g.First();
                        return new PhotoGroupDisplay
                        {
                            WorldName = first.WorldVisit?.WorldName ?? "不明なワールド",
                            JoinedAt = first.WorldVisit?.JoinedAt ?? first.TakenAt,
                            LeftAt = first.WorldVisit?.LeftAt,
                            WorldVisitId = first.WorldVisitId,
                            Photos = new ObservableCollection<PhotoDisplayItem>(
                                g.OrderByDescending(p => p.TakenAt).Select(p => new PhotoDisplayItem
                                {
                                    FilePath = p.FilePath,
                                    FileName = p.FileName,
                                    TakenAt = p.TakenAt,
                                    WorldName = p.WorldVisit?.WorldName,
                                    WorldJoinedAt = p.WorldVisit?.JoinedAt,
                                    WorldLeftAt = p.WorldVisit?.LeftAt,
                                    WorldVisitId = p.WorldVisitId
                                }))
                        };
                    })
                    .OrderByDescending(g => g.JoinedAt)
                    .ToList();

                // ── 訪問フィルター時のプレイヤー情報取得 ──
                // 時間範囲は文字列ではなく DateTime のまま返し、UI 側で都度フォーマットすることで
                // 言語切替時の曜日略称の再評価を可能にする。
                string visitWorldName = "";
                DateTime? visitJoinedAt = null;
                DateTime? visitLeftAt = null;
                List<PlayerDisplay> visitPlayers = [];

                if (filterByVisit && photos.Count > 0)
                {
                    var visit = await db.WorldVisits.FindAsync(visitId);
                    if (visit != null)
                    {
                        visitWorldName = visit.WorldName;
                        visitJoinedAt = visit.JoinedAt;
                        visitLeftAt = visit.LeftAt;
                    }

                    var rawPlayers = await db.PlayerSessions
                        .Where(s => s.WorldVisitId == visitId)
                        .Select(s => new { s.DisplayName, s.UserId, s.JoinedAt, s.LeftAt })
                        .ToListAsync();
                    visitPlayers = rawPlayers
                        .Select(p => new PlayerDisplay
                        {
                            DisplayName = LogPatterns.CleanPlayerName(p.DisplayName),
                            UserId = p.UserId,
                            JoinedAt = p.JoinedAt,
                            LeftAt = p.LeftAt
                        })
                        .Where(p => p.DisplayName != selfName)
                        .GroupBy(p => p.DisplayName)
                        .Select(g => g.First())
                        .OrderBy(p => p.JoinedAt)
                        .ToList();
                }

                // ── 遭遇統計の集計（全期間ライフタイム統計） ──
                // 表示中の写真や訪問フィルタには影響されず、PlayerSessions 全体を対象に計算する。
                // PlayerSessions を AsNoTracking で直接引いて先行クエリの tracker 状態の影響を排除する。
                (bool Has, int Count, string TotalTime) summary = default;
                bool hasPlayerSearch = !string.IsNullOrWhiteSpace(playerFilter);
                if (hasPlayerSearch)
                {
                    List<PlayerSession> matched;
                    if (!string.IsNullOrEmpty(searchPlayerUserId))
                    {
                        // カードクリック: SQL で UserId 一致を直接引く
                        var targetUserId = searchPlayerUserId;
                        var byUserId = await db.PlayerSessions.AsNoTracking()
                            .Where(s => s.UserId == targetUserId)
                            .ToListAsync();

                        // UserId が空のセッション (旧 activity log インポート由来等) は DisplayName でフォールバック。
                        var fallbackName = playerFilter!.Trim();
                        var emptyIdSessions = await db.PlayerSessions.AsNoTracking()
                            .Where(s => s.UserId == "")
                            .ToListAsync();
                        var byName = emptyIdSessions
                            .Where(s => KanaHelper.ContainsKanaInsensitive(s.DisplayName, fallbackName))
                            .ToList();
                        matched = byUserId.Concat(byName).ToList();
                    }
                    else
                    {
                        // テキスト入力: 全期間データから名前一致セッションの UserId を解決し、
                        // 同 UserId の全セッション（改名後含む）＋ UserId 空の名前一致をまとめてヒット対象にする。
                        var search = playerFilter!.Trim();
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

                return (photos, groups, filterByVisit, visitWorldName, visitJoinedAt, visitLeftAt, visitPlayers, summary, hasPlayerSearch);
            });

            // ── UI 更新 ──
            PhotoGroups.Clear();
            SelectedPhoto = null;
            _currentVisitPlayers = [];
            _currentVisitWorldName = "";
            _currentVisitJoinedAt = null;
            _currentVisitLeftAt = null;

            // 遭遇統計の表示反映
            if (result.hasPlayerSearch)
            {
                HasPlayerSummary = result.summary.Has;
                PlayerEncounterCount = result.summary.Count;
                PlayerTotalTime = result.summary.TotalTime;
            }
            else
            {
                HasPlayerSummary = false;
            }

            if (result.photos.Count == 0)
            {
                HasNoPhotos = true;
                StatusText = string.Empty;
                _photoMinDate = null;
                _photoMaxDate = null;
                DateRangeText = string.Empty;
                return;
            }
            HasNoPhotos = false;

            // 非仮想化 ItemsControl + WrapPanel のため、Add ごとに全写真分の Card ビジュアルが
            // 同期生成され UI スレッドが詰まる。一括追加するとローディングオーバーレイの
            // IsIndeterminate スピナーが UI スレッド駆動のため止まって見えるので、
            // 一定枚数ごとに Dispatcher へ制御を返し描画フレームを進めさせる。
            // 併せて LoadingService のサブメッセージで "処理済み / 全体" の進捗を表示する。
            const int yieldEveryPhotos = 50;
            int photosSinceYield = 0;
            int processedPhotos = 0;
            int totalPhotos = result.photos.Count;
            foreach (var g in result.groups)
            {
                PhotoGroups.Add(g);
                processedPhotos += g.Photos.Count;
                photosSinceYield += g.Photos.Count;
                if (photosSinceYield >= yieldEveryPhotos)
                {
                    photosSinceYield = 0;
                    _loading.UpdateSubMessage($"{processedPhotos} / {totalPhotos}");
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }

            UpdateStatus();

            _photoMinDate = result.photos.Min(p => p.TakenAt);
            _photoMaxDate = result.photos.Max(p => p.TakenAt);
            UpdateDateRangeText();

            // 訪問フィルター時はプレイヤー情報を設定
            if (result.filterByVisit)
            {
                _currentVisitWorldName = result.visitWorldName;
                _currentVisitJoinedAt = result.visitJoinedAt;
                _currentVisitLeftAt = result.visitLeftAt;
                _currentVisitPlayers = result.visitPlayers;

                foreach (var p in _currentVisitPlayers)
                    SelectedPhotoPlayers.Add(p);

                OnPropertyChanged(nameof(HasPlayerContext));
                OnPropertyChanged(nameof(SelectedPhotoWorldName));
                OnPropertyChanged(nameof(SelectedPhotoTimeRange));
            }
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.GetString("Str_ErrorPrefix") + ex.Message;
        }
        finally
        {
            _loading.Hide();
        }
    }

    /// <summary>表示中の写真枚数をステータスバーに反映する</summary>
    private void UpdateStatus()
    {
        var count = PhotoGroups.Sum(g => g.Photos.Count);
        StatusText = string.Format(LocalizationService.GetString("Str_PhotoCount"), count);
    }

    /// <summary>ファイルが存在しなくなった写真レコードを DB から削除する</summary>
    private static async Task RemoveMissingPhotosAsync(AppDbContext db)
    {
        var allPhotos = await db.PhotoRecords.ToListAsync();
        var missing = allPhotos.Where(p => !File.Exists(p.FilePath)).ToList();
        if (missing.Count == 0) return;
        db.PhotoRecords.RemoveRange(missing);
        await db.SaveChangesAsync();
    }

    /// <summary>ワールド訪問に紐づいていない写真を撮影時刻からマッチングして紐づける</summary>
    private static async Task RelinkOrphanPhotosAsync(AppDbContext db)
    {
        var orphans = await db.PhotoRecords
            .Where(p => p.WorldVisitId == null)
            .ToListAsync();

        if (orphans.Count == 0) return;

        var visits = await db.WorldVisits
            .OrderBy(v => v.JoinedAt)
            .Select(v => new { v.Id, v.JoinedAt, v.LeftAt })
            .ToListAsync();

        if (visits.Count == 0) return;

        bool changed = false;
        foreach (var photo in orphans)
        {
            var visitId = WorldVisitMatcher.FindWorldVisitId(
                visits, photo.TakenAt,
                v => v.JoinedAt, v => v.LeftAt, v => v.Id);

            if (visitId.HasValue)
            {
                photo.WorldVisitId = visitId.Value;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    /// <summary>写真を選択する</summary>
    [RelayCommand]
    private void SelectPhoto(PhotoDisplayItem? photo)
    {
        if (photo == null) return;
        SelectedPhoto = photo;
    }

    /// <summary>選択中の写真をアプリ内ビューアーで開く</summary>
    [RelayCommand]
    private async Task OpenSelectedPhotoAsync()
    {
        await OpenPhotoViewerAsync(SelectedPhoto);
    }

    /// <summary>指定した写真をアプリ内ビューアーで開く</summary>
    [RelayCommand]
    private async Task OpenPhotoFileAsync(PhotoDisplayItem? photo)
    {
        await OpenPhotoViewerAsync(photo);
    }

    /// <summary>
    /// ビューアーを閉じた直後など、View 側で特定の写真までスクロールさせたいときに発火するイベント。
    /// View はこれを購読し、対応するカードを BringIntoView() でスクロール領域に表示する。
    /// </summary>
    public event Action<PhotoDisplayItem>? ScrollToPhotoRequested;

    /// <summary>
    /// アプリ内写真ビューアーを DialogHost で開く。
    /// 起動時点の PhotoGroups を平坦化したリストを渡し、前後ナビは現在のフィルター結果内で行う。
    /// 閉じた時点で表示していた写真は SelectedPhoto に反映し、スクロール位置も追従させる。
    /// </summary>
    private async Task OpenPhotoViewerAsync(PhotoDisplayItem? photo)
    {
        if (photo == null || !File.Exists(photo.FilePath))
            return;

        try
        {
            var photos = PhotoGroups.SelectMany(g => g.Photos).ToList();
            var index = photos.FindIndex(p => p.FilePath == photo.FilePath);
            if (index < 0) return;

            var vm = new PhotoViewerViewModel(photos, index);
            var view = new PhotoViewerView { DataContext = vm };

            await DialogHost.Show(view, "RootDialogHost");

            // ビューアーで最後に見ていた写真をメイン画面で選択状態にしてスクロール反映
            if (vm.CurrentPhoto != null)
            {
                SelectedPhoto = vm.CurrentPhoto;
                ScrollToPhotoRequested?.Invoke(vm.CurrentPhoto);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex);
            await _dialog.ShowInfoAsync(
                LocalizationService.GetString("OpenError_ExternalAppFailed"));
        }
    }

    /// <summary>選択中の写真のフォルダをエクスプローラーで開く</summary>
    [RelayCommand]
    private void OpenPhotoFolder()
    {
        if (SelectedPhoto == null || !File.Exists(SelectedPhoto.FilePath)) return;
        Process.Start("explorer.exe", $"/select,\"{SelectedPhoto.FilePath}\"");
    }

    /// <summary>
    /// プレイヤーカードクリック時にそのプレイヤーで写真をフィルタリングする。
    /// 表示名で写真を絞り込みつつ、UserId は遭遇統計の集計に利用する。
    /// OnPlayerFilterChanged で一度クリアされた _searchPlayerUserId を再設定する。
    /// </summary>
    [RelayCommand]
    private async Task SearchByPlayer(PlayerDisplay player)
    {
        // フィルター前の状態を保存（フィルター後に復元するため）。
        // 時間範囲はフォーマット済み文字列ではなく DateTime を保持することで、
        // 復元後も SelectedPhotoTimeRange は現在のカルチャで動的にフォーマットされる。
        var savedPlayers = SelectedPhotoPlayers.ToList();
        var savedWorldName = SelectedPhotoWorldName;
        var savedFilePath = SelectedPhoto?.FilePath;
        var savedJoinedAt = SelectedPhoto?.WorldJoinedAt ?? _currentVisitJoinedAt;
        var savedLeftAt = SelectedPhoto?.WorldLeftAt ?? _currentVisitLeftAt;

        PlayerFilter = player.DisplayName;
        _searchPlayerUserId = !string.IsNullOrEmpty(player.UserId) ? player.UserId : null;
        await LoadPhotosAsync();

        // 以前選択していた写真を再選択
        if (savedFilePath != null)
        {
            SelectedPhoto = PhotoGroups
                .SelectMany(g => g.Photos)
                .FirstOrDefault(p => p.FilePath == savedFilePath);
        }

        // プレイヤーカード・ワールド情報を復元
        _currentVisitPlayers = savedPlayers;
        _currentVisitWorldName = savedWorldName;
        _currentVisitJoinedAt = savedJoinedAt;
        _currentVisitLeftAt = savedLeftAt;

        SelectedPhotoPlayers.Clear();
        foreach (var p in savedPlayers)
            SelectedPhotoPlayers.Add(p);

        OnPropertyChanged(nameof(HasPlayerContext));
        OnPropertyChanged(nameof(SelectedPhotoWorldName));
        OnPropertyChanged(nameof(SelectedPhotoTimeRange));
    }

    /// <summary>選択写真のワールド訪問をアクティビティ履歴画面で表示する</summary>
    [RelayCommand]
    private async Task ShowActivityForVisit()
    {
        if (SelectedPhoto?.WorldVisitId == null) return;

        await using var db = new AppDbContext();
        var visit = await db.WorldVisits.FindAsync(SelectedPhoto.WorldVisitId.Value);
        if (visit == null)
        {
            await _dialog.ShowInfoAsync(LocalizationService.GetString("Info_NoActivityForPhoto"));
            return;
        }

        _navigation.ShowActivityForVisit(SelectedPhoto.WorldVisitId);
    }

    /// <summary>写真詳細パネルを閉じる</summary>
    [RelayCommand]
    private void ClosePhotoDetail()
    {
        SelectedPhoto = null;
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
/// ワールド訪問単位でグループ化された写真の表示モデル。
/// 言語切替で曜日付きの滞在時間範囲を再フォーマットさせる必要があるため ObservableObject を継承する。
/// </summary>
public class PhotoGroupDisplay : ObservableObject
{
    /// <summary>ワールド名</summary>
    public string WorldName { get; set; } = string.Empty;

    /// <summary>ワールド入室日時</summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>ワールド退室日時</summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>対応するワールド訪問のID</summary>
    public int? WorldVisitId { get; set; }

    /// <summary>このグループに属する写真の一覧</summary>
    public ObservableCollection<PhotoDisplayItem> Photos { get; set; } = [];

    /// <summary>グループヘッダーに表示するワールド名</summary>
    public string HeaderDisplay => WorldName;

    /// <summary>グループヘッダーに表示する滞在時間範囲</summary>
    public string HeaderTimeRange => DateFormatHelper.FormatTimeRange(JoinedAt, LeftAt);

    /// <summary>言語切替時に呼び出されるリフレッシュ。曜日略称や「滞在中」表示の再評価を促す。</summary>
    public void RefreshLocalizedStrings()
    {
        OnPropertyChanged(nameof(HeaderTimeRange));
    }
}

/// <summary>
/// 写真1枚の表示用モデル。
/// 言語切替で曜日付き表示プロパティを再評価させるため ObservableObject を継承する。
/// </summary>
public class PhotoDisplayItem : ObservableObject
{
    /// <summary>写真ファイルのフルパス</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>ファイル名</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>撮影日時（ファイル名から解析）</summary>
    public DateTime TakenAt { get; set; }

    /// <summary>撮影時のワールド名</summary>
    public string? WorldName { get; set; }

    /// <summary>対応するワールド訪問の入室日時</summary>
    public DateTime? WorldJoinedAt { get; set; }

    /// <summary>対応するワールド訪問の退室日時</summary>
    public DateTime? WorldLeftAt { get; set; }

    /// <summary>対応するワールド訪問のID</summary>
    public int? WorldVisitId { get; set; }

    /// <summary>この写真に関連するプレイヤーリスト（遅延読み込み・キャッシュ）</summary>
    public List<PlayerDisplay> Players { get; set; } = [];

    /// <summary>撮影日時の表示文字列</summary>
    public string TakenAtDisplay => DateFormatHelper.FormatDateWithDayAndTime(TakenAt);

    /// <summary>ワールド訪問の滞在時間範囲</summary>
    public string WorldTimeRange =>
        WorldJoinedAt == null ? "" :
        DateFormatHelper.FormatTimeRange(WorldJoinedAt.Value, WorldLeftAt);

    /// <summary>言語切替時に呼び出されるリフレッシュ。曜日略称や「滞在中」表示の再評価を促す。</summary>
    public void RefreshLocalizedStrings()
    {
        OnPropertyChanged(nameof(TakenAtDisplay));
        OnPropertyChanged(nameof(WorldTimeRange));
    }
}
