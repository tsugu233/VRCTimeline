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
public partial class VideoLogViewModel : ObservableObject
{
    private readonly LoadingService _loading;

    /// <summary>動画情報取得サービス</summary>
    private readonly VideoInfoService _videoInfoService = new();

    /// <summary>初回ロード完了フラグ</summary>
    private bool _initialized;

    /// <summary>表示期間の開始日</summary>
    [ObservableProperty]
    private DateTime _filterDateFrom = DateTime.Today.AddDays(-30);

    /// <summary>表示期間の終了日</summary>
    [ObservableProperty]
    private DateTime _filterDateTo = DateTime.Today.AddDays(1);

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

    public VideoLogViewModel(LoadingService loadingService)
    {
        _loading = loadingService;
    }

    /// <summary>初回の動画ログ読み込み</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await LoadVideosAsync();
    }

    /// <summary>動画ログを DB から読み込み、ワールド名・タイトルでフィルタリングする</summary>
    [RelayCommand]
    private async Task LoadVideosAsync()
    {
        _loading.Show("動画ログを読み込み中...");
        try
        {
            await using var db = new AppDbContext();

            var allRecords = await db.VideoRecords
                .Include(v => v.WorldVisit)
                .Where(v => v.DetectedAt >= FilterDateFrom && v.DetectedAt <= FilterDateTo)
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
                    NeedsFetch = VideoInfoService.IsYouTubeUrl(r.Url) && r.Title == null
                });
            }
        }
        finally
        {
            _loading.Hide();
        }

        _ = FetchMissingInfoAsync();
    }

    /// <summary>
    /// タイトル・サムネイル未取得の動画情報をバックグラウンドで取得し、DB を更新する。
    /// 取得後に使われていないサムネイルキャッシュをクリーンアップする。
    /// </summary>
    private async Task FetchMissingInfoAsync()
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

            if (existing != null)
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

    /// <summary>指定 URL をデフォルトブラウザで開く</summary>
    [RelayCommand]
    private static void OpenUrl(string url)
    {
        if (!string.IsNullOrEmpty(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
}
