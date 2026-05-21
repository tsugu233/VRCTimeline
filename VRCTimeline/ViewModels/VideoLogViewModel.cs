using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Helpers;
using VRCTimeline.Services;

namespace VRCTimeline.ViewModels;

/// <summary>
/// 動画ログ画面の ViewModel。
/// VRChat ワールド内で再生された動画の履歴を表示し、タイトル・サムネイルを自動取得する。
/// </summary>
public partial class VideoLogViewModel : ObservableObject, IDisposable
{
    private readonly LoadingService _loading;

    /// <summary>動画情報取得サービス</summary>
    private readonly VideoInfoService _videoInfoService = new();

    /// <summary>初回ロード完了フラグ（フィルタクリア時の自動再読込トリガ用）</summary>
    private bool _initialized;

    /// <summary>FetchMissingInfoAsync の多重起動防止フラグ（Interlocked で操作）</summary>
    private int _fetchInFlight;

    /// <summary>FilterDateTo を「今日」に追従させるかどうか（日付またぎ時の自動更新用）</summary>
    private bool _filterDateToFollowsToday = true;

    /// <summary>日付またぎを検知して FilterDateTo を更新するためのウォッチャー</summary>
    private readonly DayChangeWatcher _dayChangeWatcher;

    /// <summary>表示期間の開始日</summary>
    [ObservableProperty]
    private DateTime _filterDateFrom = DateTime.Today.AddDays(-30);

    /// <summary>表示期間の終了日（選択日を含む）</summary>
    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today;

    /// <summary>ワールド名のフィルターテキスト</summary>
    [ObservableProperty]
    private string _searchWorldName = string.Empty;

    /// <summary>動画タイトルのフィルターテキスト</summary>
    [ObservableProperty]
    private string _searchVideoTitle = string.Empty;

    /// <summary>動画レコードの表示リスト</summary>
    public ObservableCollection<VideoDisplayItem> Videos { get; } = [];

    /// <summary>ワールド名フィルターがクリアされたら自動リロード</summary>
    partial void OnSearchWorldNameChanged(string value)
    {
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadVideosCommand.Execute(null);
    }

    /// <summary>動画タイトルフィルターがクリアされたら自動リロード</summary>
    partial void OnSearchVideoTitleChanged(string value)
    {
        if (string.IsNullOrEmpty(value) && _initialized)
            LoadVideosCommand.Execute(null);
    }

    /// <summary>ユーザーが終了日を変更した際、その値が「今日」かどうかを記録する</summary>
    partial void OnFilterDateToChanged(DateTime value)
    {
        _filterDateToFollowsToday = value.Date == DateTime.Today;
    }

    public VideoLogViewModel(LoadingService loadingService)
    {
        _loading = loadingService;
        _dayChangeWatcher = new DayChangeWatcher(() =>
        {
            if (_filterDateToFollowsToday) FilterDateTo = DateTime.Today;
        });

        // DetectedAtDisplay などの曜日略称付きプロパティを、再ロードなしで
        // 言語切替に追従させるために購読する。
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// 言語切替時に、表示中の動画アイテムの日時表示を再評価させる。
    /// DB 再ロードは行わず INPC 経由で UI 側の再フォーマットだけを促す。
    /// </summary>
    private void OnLanguageChanged()
    {
        foreach (var v in Videos)
            v.RefreshLocalizedStrings();
    }

    /// <summary>
    /// 画面表示時の動画ログ読み込み。
    /// 本アプリは長時間起動が前提なので、初回だけでなく画面遷移のたびに呼ばれて
    /// 最新の DB 内容とサムネ取得状態を反映する。
    /// LoadVideosCommand は [RelayCommand] により並列実行が抑止されるため、
    /// タブ高速切り替えや連打で重複起動されても安全。
    /// </summary>
    public async Task InitializeAsync()
    {
        _initialized = true;
        if (LoadVideosCommand.CanExecute(null))
            await LoadVideosCommand.ExecuteAsync(null);
    }

    /// <summary>動画ログを DB から読み込み、ワールド名・タイトルでフィルタリングする</summary>
    [RelayCommand]
    private async Task LoadVideosAsync()
    {
        _loading.Show("動画ログを読み込み中...");
        bool loadSucceeded = false;
        try
        {
            await using var db = new AppDbContext();

            var allRecords = await db.VideoRecords
                .Include(v => v.WorldVisit)
                .Where(v => v.DetectedAt >= FilterDateFrom && v.DetectedAt < FilterDateTo.Date.AddDays(1))
                .OrderByDescending(v => v.DetectedAt)
                .ToListAsync();

            if (!string.IsNullOrWhiteSpace(SearchWorldName))
            {
                var search = SearchWorldName.Trim();
                allRecords = allRecords.Where(v => v.WorldVisit != null &&
                    KanaHelper.ContainsKanaInsensitive(v.WorldVisit.WorldName, search)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(SearchVideoTitle))
            {
                var search = SearchVideoTitle.Trim();
                allRecords = allRecords.Where(v => v.Title != null &&
                    KanaHelper.ContainsKanaInsensitive(v.Title, search)).ToList();
            }

            var records = allRecords.Take(50).ToList();

            Videos.Clear();
            foreach (var r in records)
            {
                Videos.Add(new VideoDisplayItem
                {
                    Id = r.Id,
                    DetectedAt = r.DetectedAt,
                    Url = r.Url,
                    Title = r.Title,
                    ThumbnailPath = r.ThumbnailPath != null && File.Exists(r.ThumbnailPath) ? r.ThumbnailPath : null,
                    WorldName = r.WorldVisit?.WorldName,
                    IsYouTube = VideoInfoService.IsYouTubeUrl(r.Url),
                    // CleanupThumbnails で過去のキャッシュファイルが削除されているケースを救うため、
                    // ThumbnailPath が DB にあってもファイル不在なら再フェッチ対象とする。
                    NeedsFetch = VideoInfoService.IsYouTubeUrl(r.Url) &&
                                 (r.Title == null ||
                                  (r.ThumbnailPath != null && !File.Exists(r.ThumbnailPath)))
                });
            }
            loadSucceeded = true;
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
        finally
        {
            _loading.Hide();
        }

        // 取得失敗（スキーマ drift 等）のときに未取得サムネを再フェッチしても二重失敗するだけなのでスキップ
        if (loadSucceeded)
            _ = FetchMissingInfoAsync();
    }

    /// <summary>
    /// タイトル・サムネイル未取得の動画情報をバックグラウンドで取得し、DB を更新する。
    /// 取得後に使われていないサムネイルキャッシュをクリーンアップする。
    /// fire-and-forget で呼び出されるため、未捕捉例外がアプリを落とさないよう全体を try/catch で囲む
    /// （DB スキーマ drift・SqliteException 等の診断のため AppLogger に流す）。
    /// 画面遷移・検索連打で LoadVideosAsync が短時間に複数回成功すると本処理が並列に積まれ、
    /// noembed への重複リクエスト・DB の競合書き込みが起きるため Interlocked で 1 件に絞る。
    /// 既に走っている場合は新規呼び出しを破棄する（実行中のものが処理を継続）。
    /// </summary>
    private async Task FetchMissingInfoAsync()
    {
        if (Interlocked.CompareExchange(ref _fetchInFlight, 1, 0) != 0) return;
        try
        {
            await using var db = new AppDbContext();

            var toFetch = Videos.Where(v => v.NeedsFetch).ToList();
            foreach (var item in toFetch)
            {
                // 同一 URL で既に取得済みのレコードがあればそれを流用
                var existing = await db.VideoRecords
                    .Where(v => v.Url == item.Url && v.Title != null)
                    .FirstOrDefaultAsync();

                string? title;
                string? thumbPath;

                // existing の ThumbnailPath が DB に残っていても、CleanupThumbnails で
                // 実ファイルが削除されているとそのまま流用すると壊れたパスを引き継いでしまう。
                // ファイル不在ならフレッシュフェッチに回す。
                if (existing != null
                    && (existing.ThumbnailPath == null || File.Exists(existing.ThumbnailPath)))
                {
                    title = existing.Title;
                    thumbPath = existing.ThumbnailPath;
                }
                else
                {
                    (title, thumbPath) = await _videoInfoService.FetchInfoAsync(item.Url);
                }

                if (title == null && thumbPath == null) continue;

                item.Title = title;
                item.ThumbnailPath = thumbPath;
                item.NeedsFetch = false;

                var record = await db.VideoRecords.FindAsync(item.Id);
                if (record != null)
                {
                    record.Title = title;
                    record.ThumbnailPath = thumbPath;
                    if (VideoInfoService.IsYouTubeUrl(item.Url))
                        record.ThumbnailUrl = item.Url;
                    await db.SaveChangesAsync();
                }
            }

            // 最近の50件に使われているサムネイル以外を削除
            var recentPaths = await db.VideoRecords
                .OrderByDescending(v => v.DetectedAt)
                .Take(50)
                .Where(v => v.ThumbnailPath != null)
                .Select(v => v.ThumbnailPath!)
                .ToListAsync();
            VideoInfoService.CleanupThumbnails(
                new HashSet<string>(recentPaths, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
        finally
        {
            Interlocked.Exchange(ref _fetchInFlight, 0);
        }
    }

    /// <summary>指定 URL をデフォルトブラウザで開く</summary>
    [RelayCommand]
    private static void OpenUrl(string url)
    {
        if (!string.IsNullOrEmpty(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// Window 非表示時に表示用リソースを破棄する。
    /// ViewModel の Singleton インスタンスは破棄されないため、再表示時には Loaded イベント経由で
    /// 再ロードされるよう初期化フラグをリセットする。
    /// DB アクセス・タイマー・静的イベント購読・バックグラウンドのフェッチ状態には触れず、
    /// UI 表示用コレクション（サムネイル画像を保持する VideoDisplayItem 群）のクリアのみ行う。
    /// </summary>
    public void ReleaseUiResources()
    {
        Videos.Clear();
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
/// 動画ログ画面の表示用モデル。
/// DB エンティティ (VideoRecord) から変換して使用する。
/// タイトルとサムネイルは動的に取得・更新されるため ObservableObject を継承する。
/// </summary>
public partial class VideoDisplayItem : ObservableObject
{
    /// <summary>DB レコードの主キー</summary>
    public int Id { get; set; }

    /// <summary>動画が検出された日時</summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>動画の URL</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>動画タイトル（API から非同期取得、UI に変更通知する）</summary>
    [ObservableProperty]
    private string? _title;

    /// <summary>サムネイル画像のローカルキャッシュパス（UI に変更通知する）</summary>
    [ObservableProperty]
    private string? _thumbnailPath;

    /// <summary>検出時のワールド名</summary>
    public string? WorldName { get; set; }

    /// <summary>YouTube の動画かどうか</summary>
    public bool IsYouTube { get; set; }

    /// <summary>タイトル・サムネイルの取得が必要かどうか</summary>
    public bool NeedsFetch { get; set; }

    /// <summary>検出日時の表示文字列（曜日・秒付き）</summary>
    public string DetectedAtDisplay => DetectedAt.ToString(DateFormatHelper.DateWithDayAndSeconds, DateFormatHelper.GetCurrentCulture());

    /// <summary>サムネイルが利用可能かどうか</summary>
    public bool HasThumbnail => ThumbnailPath != null;

    /// <summary>サムネイルパス変更時に HasThumbnail の変更も通知する</summary>
    partial void OnThumbnailPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
    }

    /// <summary>言語切替時に呼び出されるリフレッシュ。曜日略称を含むプロパティの再評価を促す。</summary>
    public void RefreshLocalizedStrings()
    {
        OnPropertyChanged(nameof(DetectedAtDisplay));
    }
}
