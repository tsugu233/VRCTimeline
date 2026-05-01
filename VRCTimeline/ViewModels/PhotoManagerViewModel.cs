using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
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
/// 写真管理画面の ViewModel。
/// VRChat スクリーンショットをワールド訪問ごとにグループ化して表示し、
/// プレイヤー・ワールド名でフィルタリングする。
/// PhotoWatcher からのリアルタイム通知にも対応する。
/// </summary>
public partial class PhotoManagerViewModel : ObservableObject
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

    /// <summary>特定ワールド訪問IDでフィルタリングする場合に使用（アクティビティ画面からの遷移用）</summary>
    private int? _filterWorldVisitId;

    /// <summary>訪問フィルター時のワールド名（写真選択前のデフォルト表示用）</summary>
    private string _currentVisitWorldName = "";

    /// <summary>訪問フィルター時の滞在時間範囲（写真選択前のデフォルト表示用）</summary>
    private string _currentVisitTimeRange = "";

    /// <summary>訪問フィルター時のプレイヤーリスト（写真選択前のデフォルト表示用）</summary>
    private List<PlayerDisplay> _currentVisitPlayers = [];

    /// <summary>表示中写真の最小撮影日時（言語変更時の再フォーマット用）</summary>
    private DateTime? _photoMinDate;

    /// <summary>表示中写真の最大撮影日時（言語変更時の再フォーマット用）</summary>
    private DateTime? _photoMaxDate;

    /// <summary>表示期間の開始日（デフォルト: 30日前）</summary>
    [ObservableProperty]
    private DateTime _filterDateFrom = DateTime.Today.AddDays(-30);

    /// <summary>表示期間の終了日</summary>
    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today.AddDays(1);

    /// <summary>プレイヤー名フィルターテキスト</summary>
    [ObservableProperty]
    private string? _playerFilter;

    /// <summary>ワールド名フィルターテキスト</summary>
    [ObservableProperty]
    private string? _worldFilter;

    /// <summary>データ読み込み中フラグ</summary>
    [ObservableProperty]
    private bool _isLoading;

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

    /// <summary>選択写真 or 訪問フィルターの滞在時間範囲</summary>
    public string SelectedPhotoTimeRange => SelectedPhoto?.WorldTimeRange ?? _currentVisitTimeRange;

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
    }

    /// <summary>言語切替時に表示テキストを現在のカルチャで再生成する</summary>
    private void OnLanguageChanged()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            UpdateStatus();
            UpdateDateRangeText();
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
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadPhotosCommand.Execute(null);
    }

    partial void OnWorldFilterChanged(string? value)
    {
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadPhotosCommand.Execute(null);
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
        await LoadPhotosAsync();
    }

    /// <summary>特定ワールド訪問の写真のみを表示する（アクティビティ画面からの遷移用）</summary>
    public async Task FilterByWorldVisitId(int worldVisitId)
    {
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
        if (SelectedPhoto == null)
        {
            // 写真未選択時は訪問フィルターのプレイヤーリストを表示
            foreach (var p in _currentVisitPlayers)
                SelectedPhotoPlayers.Add(p);
            return;
        }

        // 遅延読み込み: 写真に紐づくプレイヤーをまだ取得していない場合
        if (SelectedPhoto.WorldVisitId.HasValue && SelectedPhoto.Players.Count == 0)
        {
            var selfName = await _selfPlayer.GetSelfPlayerNameAsync();

            await using var db = new AppDbContext();
            var players = await db.PlayerSessions
                .Where(s => s.WorldVisitId == SelectedPhoto.WorldVisitId.Value)
                .Select(s => new { s.DisplayName, s.JoinedAt, s.LeftAt })
                .ToListAsync();
            SelectedPhoto.Players = players
                .Select(p => new PlayerDisplay
                {
                    DisplayName = LogPatterns.CleanPlayerName(p.DisplayName),
                    JoinedAt = p.JoinedAt,
                    LeftAt = p.LeftAt
                })
                .Where(p => p.DisplayName != selfName)
                .GroupBy(p => p.DisplayName)
                .Select(g => g.First())
                .OrderBy(p => p.JoinedAt)
                .ToList();
        }

        foreach (var p in SelectedPhoto.Players)
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
        IsLoading = true;
        _loading.Show("写真を読み込み中...");
        try
        {
            var dateFrom = FilterDateFrom;
            var dateTo = FilterDateTo;
            var worldFilter = WorldFilter;
            var playerFilter = PlayerFilter;
            var filterVisitId = _filterWorldVisitId;
            _filterWorldVisitId = null;

            var selfName = await _selfPlayer.GetSelfPlayerNameAsync();

            var result = await Task.Run(async () =>
            {
                await using var db = new AppDbContext();

                // DB メンテナンス: 孤立写真の紐づけ修復・存在しないファイルの削除
                await RelinkOrphanPhotosAsync(db);
                await RemoveMissingPhotosAsync(db);

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
                    query = query.Where(p => p.TakenAt >= dateFrom && p.TakenAt < dateTo);
                }

                var photos = await query
                    .OrderByDescending(p => p.TakenAt)
                    .ToListAsync();

                // ── フィルタリング ──
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
                        var matchingVisitIds = await db.PlayerSessions
                            .Where(s => visitIds.Contains(s.WorldVisitId))
                            .ToListAsync();
                        var filteredVisitIds = matchingVisitIds
                            .Where(s => KanaHelper.ContainsKanaInsensitive(s.DisplayName, playerFilter))
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
                string visitWorldName = "";
                string visitTimeRange = "";
                List<PlayerDisplay> visitPlayers = [];

                if (filterByVisit && photos.Count > 0)
                {
                    var visit = await db.WorldVisits.FindAsync(visitId);
                    if (visit != null)
                    {
                        visitWorldName = visit.WorldName;
                        visitTimeRange = DateFormatHelper.FormatTimeRange(visit.JoinedAt, visit.LeftAt);
                    }

                    var rawPlayers = await db.PlayerSessions
                        .Where(s => s.WorldVisitId == visitId)
                        .Select(s => new { s.DisplayName, s.JoinedAt, s.LeftAt })
                        .ToListAsync();
                    visitPlayers = rawPlayers
                        .Select(p => new PlayerDisplay
                        {
                            DisplayName = LogPatterns.CleanPlayerName(p.DisplayName),
                            JoinedAt = p.JoinedAt,
                            LeftAt = p.LeftAt
                        })
                        .Where(p => p.DisplayName != selfName)
                        .GroupBy(p => p.DisplayName)
                        .Select(g => g.First())
                        .OrderBy(p => p.JoinedAt)
                        .ToList();
                }

                return (photos, groups, filterByVisit, visitWorldName, visitTimeRange, visitPlayers);
            });

            // ── UI 更新 ──
            PhotoGroups.Clear();
            SelectedPhoto = null;
            _currentVisitPlayers = [];
            _currentVisitWorldName = "";
            _currentVisitTimeRange = "";

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

            foreach (var g in result.groups)
                PhotoGroups.Add(g);

            UpdateStatus();

            _photoMinDate = result.photos.Min(p => p.TakenAt);
            _photoMaxDate = result.photos.Max(p => p.TakenAt);
            UpdateDateRangeText();

            // 訪問フィルター時はプレイヤー情報を設定
            if (result.filterByVisit)
            {
                _currentVisitWorldName = result.visitWorldName;
                _currentVisitTimeRange = result.visitTimeRange;
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
            IsLoading = false;
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

    /// <summary>選択中の写真を既定のアプリで開く</summary>
    [RelayCommand]
    private void OpenSelectedPhoto()
    {
        if (SelectedPhoto != null && File.Exists(SelectedPhoto.FilePath))
            Process.Start(new ProcessStartInfo(SelectedPhoto.FilePath) { UseShellExecute = true });
    }

    /// <summary>指定した写真を既定のアプリで開く</summary>
    [RelayCommand]
    private static void OpenPhotoFile(PhotoDisplayItem? photo)
    {
        if (photo != null && File.Exists(photo.FilePath))
            Process.Start(new ProcessStartInfo(photo.FilePath) { UseShellExecute = true });
    }

    /// <summary>選択中の写真のフォルダをエクスプローラーで開く</summary>
    [RelayCommand]
    private void OpenPhotoFolder()
    {
        if (SelectedPhoto == null || !File.Exists(SelectedPhoto.FilePath)) return;
        Process.Start("explorer.exe", $"/select,\"{SelectedPhoto.FilePath}\"");
    }

    /// <summary>プレイヤーカードクリック時にそのプレイヤーで写真をフィルタリングする</summary>
    [RelayCommand]
    private async Task SearchByPlayer(string playerName)
    {
        // フィルター前の状態を保存（フィルター後に復元するため）
        var savedPlayers = SelectedPhotoPlayers.ToList();
        var savedWorldName = SelectedPhotoWorldName;
        var savedTimeRange = SelectedPhotoTimeRange;
        var savedFilePath = SelectedPhoto?.FilePath;

        PlayerFilter = playerName;
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
        _currentVisitTimeRange = savedTimeRange;

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
}

/// <summary>ワールド訪問単位でグループ化された写真の表示モデル</summary>
public class PhotoGroupDisplay
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
}

/// <summary>写真1枚の表示用モデル</summary>
public class PhotoDisplayItem
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
}
