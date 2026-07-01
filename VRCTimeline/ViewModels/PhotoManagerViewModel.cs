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
    private readonly ManualPhotoFixService _manualFix;

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

    /// <summary>
    /// ワールド訪問ごとにグループ化された写真の一覧。
    /// OnPhotoAdded での「既存グループの検索」など内部状態管理に使用する。
    /// 表示用バインディングは <see cref="Photos"/> + XAML 側 CollectionViewSource のグループ化で行う。
    /// </summary>
    public ObservableCollection<PhotoGroupDisplay> PhotoGroups { get; } = [];

    /// <summary>
    /// 全写真の平坦リスト。View 側で CollectionViewSource により <see cref="PhotoDisplayItem.Group"/> でグループ化し、
    /// VirtualizingWrapPanel で表示する。各 item は属する <see cref="PhotoGroupDisplay"/> への参照を持つ。
    /// グループの並び順は、最初に出現するアイテムの位置で決まる（CollectionView の仕様）ため、
    /// 「最新グループを先頭」を保つには新規グループの最初の写真を Photos の先頭に挿入する。
    /// </summary>
    public ObservableCollection<PhotoDisplayItem> Photos { get; } = [];

    /// <summary>選択中の写真に関連するプレイヤーの一覧</summary>
    public ObservableCollection<PlayerDisplay> SelectedPhotoPlayers { get; } = [];

    /// <summary>
    /// 複数選択（拡張選択）された写真の一覧。MultiSelectBehavior 経由で ListBox.SelectedItems をミラーする。
    /// 「不明なワールド」写真の一括修正コマンドの対象になる。
    /// </summary>
    public ObservableCollection<PhotoDisplayItem> SelectedPhotos { get; } = [];

    /// <summary>
    /// 選択中に「不明なワールド」写真が1件以上あるか。
    /// 一括修正コマンドバーは、修正が実際に適用できるこの場合のみ表示する
    /// （通常の閲覧目的の単一選択ではバーを出さない）。
    /// </summary>
    public bool HasFixableSelection => SelectedPhotos.Any(p => p.WorldVisitId == null);

    /// <summary>「N 枚選択中」表示文字列</summary>
    public string MultiSelectStatus =>
        string.Format(LocalizationService.GetString("Photo_MultiSelectStatus"), SelectedPhotos.Count);

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
        SelfPlayerService selfPlayerService,
        ManualPhotoFixService manualFixService)
    {
        _settingsService = settingsService;
        _loading = loadingService;
        _navigation = navigationService;
        _dialog = dialogService;
        _selfPlayer = selfPlayerService;
        _manualFix = manualFixService;

        // 初期表示期間を設定値（既定14日）から決定する。終了日は今日のまま。
        FilterDateFrom = DateTime.Today.AddDays(-_settingsService.Settings.DefaultFilterDays);

        // 複数選択（一括修正用）の変更を CanExecute・件数表示へ反映する
        SelectedPhotos.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFixableSelection));
            OnPropertyChanged(nameof(MultiSelectStatus));
            FixSelectedUnknownPhotosCommand.NotifyCanExecuteChanged();
        };

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
                item.Group = group;

                // Photos（平坦リスト）にも反映: 既存グループ内の先頭位置へ挿入することで
                // CollectionView 上の within-group 順序が「最新が先頭」を保つ。
                var firstIdx = IndexOfFirstInGroup(group);
                if (firstIdx >= 0)
                    Photos.Insert(firstIdx, item);
                else
                    Photos.Insert(0, item); // 念のためのフォールバック
            }
            else
            {
                var newGroup = new PhotoGroupDisplay
                {
                    WorldName = info.WorldName ?? "不明なワールド",
                    JoinedAt = info.WorldJoinedAt ?? info.TakenAt,
                    WorldVisitId = info.WorldVisitId,
                    Photos = new ObservableCollection<PhotoDisplayItem> { item }
                };
                item.Group = newGroup;
                PhotoGroups.Insert(0, newGroup);

                // 新グループの最初の写真を Photos 先頭に入れることで、CollectionView のグループ順が
                // 「新しいグループほど先頭」を保つ（CollectionView は最初に出現した順で並ぶ）。
                Photos.Insert(0, item);
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
                .Select(s => new { s.DisplayName, s.UserId, s.JoinedAt, s.LeftAt, s.IsManual })
                .ToListAsync();
            selected.Players = players
                .Select(p => new PlayerDisplay
                {
                    DisplayName = LogPatterns.CleanPlayerName(p.DisplayName),
                    UserId = p.UserId,
                    JoinedAt = p.JoinedAt,
                    LeftAt = p.LeftAt,
                    IsManual = p.IsManual
                })
                .Where(p => p.DisplayName != selfName)
                .GroupBy(p => p.DisplayName)
                .Select(g => { var f = g.First(); f.IsManual = g.All(x => x.IsManual); return f; })
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

                // 手動同席者 (IsManual) を含む訪問ID集合。グループの編集可否マーキングに使う。
                var manualVisitIds = (await db.PlayerSessions.AsNoTracking()
                    .Where(s => s.IsManual)
                    .Select(s => s.WorldVisitId)
                    .Distinct()
                    .ToListAsync()).ToHashSet();

                // ── ワールド訪問ごとにグループ化 ──
                // 各 PhotoDisplayItem に属するグループへの参照(Group)を設定する。
                // この参照は View 側で CollectionViewSource のグループ化キーに使われ、
                // VirtualizingWrapPanel + GroupStyle による仮想化表示で同一グループ判定の根拠になる。
                var groups = photos.Count == 0 ? [] : photos
                    .GroupBy(p => p.WorldVisitId ?? -p.Id)
                    .Select(g =>
                    {
                        var first = g.First();
                        var groupDisplay = new PhotoGroupDisplay
                        {
                            WorldName = first.WorldVisit?.WorldName ?? "不明なワールド",
                            JoinedAt = first.WorldVisit?.JoinedAt ?? first.TakenAt,
                            LeftAt = first.WorldVisit?.LeftAt,
                            WorldVisitId = first.WorldVisitId,
                            IsManual = first.WorldVisit?.IsManual ?? false,
                            HasManualPlayers = first.WorldVisitId.HasValue
                                && manualVisitIds.Contains(first.WorldVisitId.Value),
                        };
                        var items = new ObservableCollection<PhotoDisplayItem>(
                            g.OrderByDescending(p => p.TakenAt).Select(p => new PhotoDisplayItem
                            {
                                FilePath = p.FilePath,
                                FileName = p.FileName,
                                TakenAt = p.TakenAt,
                                WorldName = p.WorldVisit?.WorldName,
                                WorldJoinedAt = p.WorldVisit?.JoinedAt,
                                WorldLeftAt = p.WorldVisit?.LeftAt,
                                WorldVisitId = p.WorldVisitId,
                                InstanceId = p.WorldVisit?.InstanceId ?? string.Empty,
                                Group = groupDisplay
                            }));
                        groupDisplay.Photos = items;
                        return groupDisplay;
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
                        .Select(s => new { s.DisplayName, s.UserId, s.JoinedAt, s.LeftAt, s.IsManual })
                        .ToListAsync();
                    visitPlayers = rawPlayers
                        .Select(p => new PlayerDisplay
                        {
                            DisplayName = LogPatterns.CleanPlayerName(p.DisplayName),
                            UserId = p.UserId,
                            JoinedAt = p.JoinedAt,
                            LeftAt = p.LeftAt,
                            IsManual = p.IsManual
                        })
                        .Where(p => p.DisplayName != selfName)
                        .GroupBy(p => p.DisplayName)
                        .Select(g => { var f = g.First(); f.IsManual = g.All(x => x.IsManual); return f; })
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
                        // 手動タグ付けの同席者 (IsManual) は記憶ベースのため遭遇統計から除外する。
                        var byUserId = await db.PlayerSessions.AsNoTracking()
                            .Where(s => s.UserId == targetUserId && !s.IsManual)
                            .ToListAsync();

                        // UserId が空のセッション (旧 activity log インポート由来等) は DisplayName でフォールバック。
                        var fallbackName = playerFilter!.Trim();
                        var emptyIdSessions = await db.PlayerSessions.AsNoTracking()
                            .Where(s => s.UserId == "" && !s.IsManual)
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
                        // 手動タグ付けの同席者 (IsManual) は遭遇統計から除外する。
                        var allSessions = await db.PlayerSessions.AsNoTracking()
                            .Where(s => !s.IsManual)
                            .ToListAsync();
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
            Photos.Clear();
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
            // VirtualizingWrapPanel 導入により可視範囲外のカードは生成されないため、
            // 一括追加でも UI スレッドの詰まりは大幅に減るが、進捗表示と UI 応答性のために
            // 従来同様の Yield ロジックを維持する。Photos（平坦リスト）と PhotoGroups の両方に追加する。
            const int yieldEveryPhotos = 50;
            int photosSinceYield = 0;
            int processedPhotos = 0;
            int totalPhotos = result.photos.Count;
            foreach (var g in result.groups)
            {
                PhotoGroups.Add(g);
                foreach (var p in g.Photos)
                    Photos.Add(p);
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

    /// <summary>
    /// 指定グループに属する最初の写真の <see cref="Photos"/> 内インデックスを返す。
    /// 見つからなければ -1。OnPhotoAdded で既存グループの先頭に新着写真を差し込むために使う。
    /// 線形探索だが、PhotoWatcher の通知頻度（数秒〜数分に 1 度）と
    /// 想定表示枚数（数百〜数千）から実用上問題ない。
    /// </summary>
    private int IndexOfFirstInGroup(PhotoGroupDisplay group)
    {
        for (int i = 0; i < Photos.Count; i++)
        {
            if (ReferenceEquals(Photos[i].Group, group))
                return i;
        }
        return -1;
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

    /// <summary>複数選択中の「不明なワールド」写真を一括で手動修正する（コマンドバーから）。</summary>
    [RelayCommand(CanExecute = nameof(CanFixSelectedUnknownPhotos))]
    private async Task FixSelectedUnknownPhotosAsync()
    {
        var targets = SelectedPhotos.Where(p => p.WorldVisitId == null).ToList();
        await FixUnknownPhotosAsync(targets);
    }

    /// <summary>選択中に1枚でも「不明なワールド」写真があれば一括修正可能。</summary>
    private bool CanFixSelectedUnknownPhotos() => SelectedPhotos.Any(p => p.WorldVisitId == null);

    /// <summary>
    /// グループヘッダーの編集（鉛筆）ボタンの振り分け。
    /// 「不明」グループは初回修正フロー、手動訪問／手動フレンドを持つ実訪問は再編集フローへ。
    /// </summary>
    [RelayCommand]
    private async Task FixGroupAsync(PhotoGroupDisplay? group)
    {
        if (group == null) return;
        if (group.WorldVisitId == null)
        {
            var targets = group.Photos.Where(p => p.WorldVisitId == null).ToList();
            await FixUnknownPhotosAsync(targets);
        }
        else
        {
            await EditVisitAsync(group);
        }
    }

    /// <summary>
    /// 手動訪問／手動フレンドを持つ実訪問を再編集する。
    /// 手動訪問: 名前変更・手動フレンド編集・修正の取り消し。実訪問: 手動フレンドの追加/削除のみ。
    /// ログ由来のデータ（実ワールド名・実プレイヤー）は一切変更しない。
    /// </summary>
    private async Task EditVisitAsync(PhotoGroupDisplay group)
    {
        if (group.WorldVisitId is not int visitId) return;

        var selfUserId = await _selfPlayer.GetSelfUserIdAsync();
        var selfName = await _selfPlayer.GetSelfPlayerNameAsync();
        var knownPlayers = await _manualFix.GetKnownPlayersAsync(selfUserId, selfName);
        var existing = await _manualFix.GetManualPlayerSessionsAsync(visitId);
        var existingFriends = existing
            .Select(e => new TaggedFriend(LogPatterns.CleanPlayerName(e.DisplayName), e.UserId, e.Id))
            .ToList();

        var mode = group.IsManual ? FixDialogMode.EditManualVisit : FixDialogMode.EditRealVisitFriends;
        var dialogVm = new FixUnknownPhotoDialogViewModel(
            mode, group.Photos.Count, group.WorldName, [], knownPlayers, existingFriends);
        var dialogView = new FixUnknownPhotoDialog { DataContext = dialogVm };
        await DialogHost.Show(dialogView, "RootDialogHost");

        var anchorPath = group.Photos.OrderByDescending(p => p.TakenAt).FirstOrDefault()?.FilePath;

        // 修正の取り消し（手動訪問のみ）: 写真を「不明」に戻し、手動訪問と手動フレンドを削除する。
        if (dialogVm.UndoRequested)
        {
            _loading.Show(LocalizationService.GetString("Photo_FixUnknown_Saving"));
            try { await _manualFix.UndoManualVisitAsync(visitId); }
            catch (Exception ex) { AppLogger.LogError(ex); }
            finally { _loading.Hide(); }
            await ReloadAndScrollToAsync(anchorPath);
            return;
        }

        if (!dialogVm.Confirmed) return;

        _loading.Show(LocalizationService.GetString("Photo_FixUnknown_Saving"));
        try
        {
            // ワールド名変更（手動訪問のみ）。
            if (dialogVm.AllowRename)
                await _manualFix.RenameVisitAsync(visitId, dialogVm.WorldName);

            // 手動フレンドの差分適用: 既存から外されたものを削除、新規追加分を追加。
            var originalIds = existing.Select(e => e.Id).ToHashSet();
            var keptIds = dialogVm.TaggedFriends
                .Where(t => t.SessionId.HasValue)
                .Select(t => t.SessionId!.Value)
                .ToHashSet();

            foreach (var removedId in originalIds.Where(id => !keptIds.Contains(id)))
                await _manualFix.RemoveManualPlayerSessionAsync(removedId);

            foreach (var added in dialogVm.TaggedFriends.Where(t => !t.SessionId.HasValue))
                await _manualFix.AddManualPlayerSessionAsync(
                    visitId, added.DisplayName, added.UserId, group.JoinedAt, group.LeftAt);
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
        finally { _loading.Hide(); }

        await ReloadAndScrollToAsync(anchorPath);
    }

    /// <summary>
    /// 編集後の共通後処理。選択をクリアして再読み込みし、編集対象の写真までスクロール位置を復元する
    /// （一覧が先頭へ戻らないようにする）。
    /// </summary>
    private async Task ReloadAndScrollToAsync(string? anchorPath)
    {
        SelectedPhotos.Clear();
        await ReloadAsync();
        if (anchorPath == null) return;
        var anchor = PhotoGroups.SelectMany(g => g.Photos).FirstOrDefault(p => p.FilePath == anchorPath);
        if (anchor != null)
        {
            SelectedPhoto = anchor;
            ScrollToPhotoRequested?.Invoke(anchor);
        }
    }

    /// <summary>
    /// 「不明なワールド」写真群の手動修正ダイアログを開き、結果に応じて
    /// 既存訪問への割り当て or 手動訪問作成＋写真割り当て＋手動同席者追加を行う。
    /// 割り当て後は孤立写真群が1つの訪問グループに統合されるため再読み込みする。
    /// </summary>
    private async Task FixUnknownPhotosAsync(IReadOnlyList<PhotoDisplayItem> targets)
    {
        if (targets.Count == 0)
        {
            await _dialog.ShowInfoAsync(LocalizationService.GetString("Photo_FixUnknown_NoTargets"));
            return;
        }

        var fromTime = targets.Min(p => p.TakenAt);
        var toTime = targets.Max(p => p.TakenAt);

        var selfUserId = await _selfPlayer.GetSelfUserIdAsync();
        var selfName = await _selfPlayer.GetSelfPlayerNameAsync();

        var candidates = await _manualFix.GetCandidateVisitsAsync(fromTime, toTime);
        var knownPlayers = await _manualFix.GetKnownPlayersAsync(selfUserId, selfName);

        var dialogVm = new FixUnknownPhotoDialogViewModel(
            FixDialogMode.CreateOrAssign, targets.Count, null, candidates, knownPlayers, []);
        var dialogView = new FixUnknownPhotoDialog { DataContext = dialogVm };
        await DialogHost.Show(dialogView, "RootDialogHost");

        if (!dialogVm.Confirmed) return;

        _loading.Show(LocalizationService.GetString("Photo_FixUnknown_Saving"));
        try
        {
            int visitId;
            DateTime visitJoinedAt;
            DateTime? visitLeftAt;

            if (dialogVm.UseExistingVisit && dialogVm.SelectedCandidate != null)
            {
                visitId = dialogVm.SelectedCandidate.Id;
                visitJoinedAt = dialogVm.SelectedCandidate.JoinedAt;
                visitLeftAt = dialogVm.SelectedCandidate.LeftAt;
            }
            else
            {
                // 手動訪問は写真の撮影時刻範囲を入退室時刻として作成する。
                visitJoinedAt = fromTime;
                visitLeftAt = toTime;
                visitId = await _manualFix.CreateManualVisitAsync(dialogVm.WorldName, fromTime, toTime);
            }

            var filePaths = targets.Select(p => p.FilePath).ToList();
            await _manualFix.AssignPhotosToVisitAsync(filePaths, visitId);

            // 手動同席者は対象訪問の時間範囲を入退室時刻として付与する（統計には含めない）。
            foreach (var friend in dialogVm.TaggedFriends)
                await _manualFix.AddManualPlayerSessionAsync(
                    visitId, friend.DisplayName, friend.UserId, visitJoinedAt, visitLeftAt);
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex);
        }
        finally
        {
            _loading.Hide();
        }

        // 編集後に一覧が先頭へ戻らないよう、修正対象（最新の撮影時刻）の写真までスクロール位置を復元する。
        var anchorPath = targets.OrderByDescending(p => p.TakenAt).First().FilePath;
        await ReloadAndScrollToAsync(anchorPath);
    }

    /// <summary>
    /// Window 非表示時に表示用リソースを破棄する。
    /// Singleton VM そのものは生存し続けるため、再表示時には UserControl_Loaded → InitializeAsync で再ロードできるよう
    /// 初期化フラグをリセットする。DB 接続・PhotoWatcher 購読・LocalizationService 購読・DayChangeWatcher は触らない
    /// （これらは VM ライフタイム全体で必要なため）。
    /// _maintenanceDone は意図的に維持: セッション中の DB メンテナンスは 1 回で十分。
    /// </summary>
    public void ReleaseUiResources()
    {
        PhotoGroups.Clear();
        Photos.Clear();
        SelectedPhoto = null;
        SelectedPhotos.Clear();
        SelectedPhotoPlayers.Clear();
        HasNoPhotos = false;
        StatusText = string.Empty;
        DateRangeText = string.Empty;
        HasPlayerSummary = false;
        PlayerEncounterCount = 0;
        PlayerTotalTime = string.Empty;
        _currentVisitPlayers = [];
        _currentVisitWorldName = "";
        _currentVisitJoinedAt = null;
        _currentVisitLeftAt = null;
        _filterWorldVisitId = null;
        _searchPlayerUserId = null;
        _photoMinDate = null;
        _photoMaxDate = null;
        _initialized = false;
        _isInitialLoad = true;
    }

    // ── 右クリックメニュー（写真の元ワールドへの再参加 / URL 操作） ──

    /// <summary>ワールド紹介ページ URL をクリップボードにコピーする（再訪用）</summary>
    [RelayCommand]
    private void CopyWorldUrl(PhotoDisplayItem? photo)
    {
        if (photo == null || !photo.HasWorldId) return;
        ClipboardHelper.SetText(VRChatLauncher.WorldPageUrl(photo.InstanceId));
    }

    /// <summary>ワールド紹介ページを既定ブラウザで開く（再訪用）</summary>
    [RelayCommand]
    private void OpenWorldInBrowser(PhotoDisplayItem? photo)
    {
        if (photo == null || !photo.HasWorldId) return;
        VRChatLauncher.OpenInBrowser(VRChatLauncher.WorldPageUrl(photo.InstanceId));
    }

    /// <summary>インスタンス起動ページ URL をクリップボードにコピーする（InviteMe での移動用）</summary>
    [RelayCommand]
    private void CopyInstanceUrl(PhotoDisplayItem? photo)
    {
        if (photo == null || !photo.HasInstanceId) return;
        ClipboardHelper.SetText(VRChatLauncher.InstanceLaunchUrl(photo.InstanceId));
    }

    /// <summary>インスタンス起動ページを既定ブラウザで開く（InviteMe での移動用）</summary>
    [RelayCommand]
    private void OpenInstanceInBrowser(PhotoDisplayItem? photo)
    {
        if (photo == null || !photo.HasInstanceId) return;
        VRChatLauncher.OpenInBrowser(VRChatLauncher.InstanceLaunchUrl(photo.InstanceId));
    }

    /// <summary>ワールド名（テキスト）をクリップボードにコピーする</summary>
    [RelayCommand]
    private void CopyWorldName(PhotoDisplayItem? photo)
    {
        if (photo == null || !photo.HasWorldName) return;
        ClipboardHelper.SetText(photo.WorldName!);
    }

    /// <summary>右クリックした写真のインスタンスに再参加する（確認ダイアログ付き）</summary>
    [RelayCommand]
    private async Task RejoinInstanceAsync(PhotoDisplayItem? photo)
    {
        if (photo == null || !photo.HasInstanceId) return;
        if (await _dialog.ShowConfirmAsync(string.Format(LocalizationService.GetString("Confirm_Rejoin"), photo.WorldName)))
            VRChatLauncher.LaunchInstance(photo.InstanceId);
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

    /// <summary>
    /// ワールド訪問に紐づかない「不明なワールド」グループかどうか。
    /// </summary>
    public bool IsUnknownWorld => WorldVisitId == null;

    /// <summary>このグループの訪問がユーザー作成の手動訪問かどうか。</summary>
    public bool IsManual { get; set; }

    /// <summary>このグループの訪問が手動同席者（IsManual セッション）を1人以上含むか。</summary>
    public bool HasManualPlayers { get; set; }

    /// <summary>
    /// グループヘッダーの編集（鉛筆）ボタンを表示すべきか。
    /// 「不明」グループ（初回修正）、手動訪問（名前/フレンド編集・取り消し）、
    /// 手動フレンドを持つ実訪問（手動フレンドの編集）のいずれか＝自分が編集したデータを後から直せる。
    /// </summary>
    public bool IsEditable => IsUnknownWorld || IsManual || HasManualPlayers;

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

    /// <summary>対応するワールド訪問のインスタンスID（再参加・URL生成用）</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>インスタンスIDから抽出したワールドID（再訪・URL生成用）</summary>
    public string WorldId => LogPatterns.ExtractWorldId(InstanceId);

    /// <summary>再参加・インスタンスURL操作が可能か（インスタンスIDを持つ場合のみ）</summary>
    public bool HasInstanceId => !string.IsNullOrEmpty(InstanceId);

    /// <summary>有効なワールドIDを持つか（不明ワールド・旧データは InstanceId が空のため false）</summary>
    public bool HasWorldId => WorldId.StartsWith("wrld_");

    /// <summary>ワールド名を持つか（ワールド名コピーの可否判定用）</summary>
    public bool HasWorldName => !string.IsNullOrEmpty(WorldName);

    /// <summary>この写真に関連するプレイヤーリスト（遅延読み込み・キャッシュ）</summary>
    public List<PlayerDisplay> Players { get; set; } = [];

    /// <summary>
    /// この写真が属するグループ（ワールド訪問単位）への参照。
    /// View 側で CollectionViewSource のグループ化キーとして用いるため、
    /// 同一ワールド訪問の全写真は同一の <see cref="PhotoGroupDisplay"/> インスタンスを共有する。
    /// </summary>
    public PhotoGroupDisplay? Group { get; set; }

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
