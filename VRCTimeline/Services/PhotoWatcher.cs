using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Models;

namespace VRCTimeline.Services;

/// <summary>
/// VRChat 写真フォルダをリアルタイムに監視し、新しいスクリーンショットを自動で DB に登録する。
/// 起動時に未登録写真のバッチスキャンも行う。
/// </summary>
public class PhotoWatcher : IDisposable
{
    private readonly SettingsService _settingsService;
    private FileSystemWatcher? _watcher;

    /// <summary>写真ファイルの同時処理を防ぐロック</summary>
    private readonly SemaphoreSlim _processLock = new(1, 1);

    /// <summary>
    /// Dispose 通知用 CTS。Task.Delay(3000) の待機中や _processLock.WaitAsync 待機中に
    /// Dispose が走ると、後続の WaitAsync / Release が ObjectDisposedException を投げ、
    /// async void ハンドラ (OnFileCreated/OnFileRenamed) 経由でアプリをクラッシュさせる。
    /// CTS でキャンセルを観測してクリーンに抜けることでこれを防ぐ。
    /// </summary>
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>新しい写真が DB に登録された際の通知データ</summary>
    public record PhotoAddedInfo(
        string FilePath, string FileName, DateTime TakenAt,
        int? WorldVisitId, string? WorldName, DateTime? WorldJoinedAt);

    /// <summary>新しい写真が登録された際に発火するイベント</summary>
    public event Action<PhotoAddedInfo>? PhotoAdded;

    public PhotoWatcher(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// 写真フォルダの監視を開始する。
    /// FileSystemWatcher でファイル作成/リネームを検出し、起動時に未登録写真をバッチスキャンする。
    /// </summary>
    public void Start()
    {
        var dir = _settingsService.Settings.PhotoDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
        };
        _watcher.Created += OnFileCreated;
        _watcher.Renamed += OnFileRenamed;
        _watcher.EnableRaisingEvents = true;

        _ = ScanMissedPhotosAsync(dir);
    }

    /// <summary>
    /// 監視を停止してから新しい設定のディレクトリで再開する。
    /// 写真フォルダの変更時に呼び出す。
    /// </summary>
    public void Restart()
    {
        _watcher?.Dispose();
        _watcher = null;
        Start();
    }

    private async void OnFileCreated(object sender, FileSystemEventArgs e) => await TryProcessFile(e.FullPath);
    private async void OnFileRenamed(object sender, RenamedEventArgs e) => await TryProcessFile(e.FullPath);

    /// <summary>
    /// 新しい写真ファイルを処理し、DB に登録してイベントを発行する。
    /// VRChat の書き込み完了を待つため3秒の遅延あり。
    /// </summary>
    private async Task TryProcessFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (!fileName.StartsWith("VRChat_", StringComparison.OrdinalIgnoreCase)) return;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) return;

        // 待機系は CTS を観測してキャンセル可能にする。Dispose との競合で例外が出るのを防ぐ。
        try
        {
            await Task.Delay(3000, _disposeCts.Token);
            await _processLock.WaitAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }

        try
        {
            var timestamp = PhotoScanner.ParsePhotoTimestamp(fileName);
            if (timestamp == null) return;

            await using var db = new AppDbContext();

            // 重複チェック
            if (await db.PhotoRecords.AnyAsync(p => p.FilePath == filePath))
                return;

            // 撮影時刻に対応するワールド訪問を検索
            var worldVisit = await db.WorldVisits
                .Where(v => v.JoinedAt <= timestamp && (v.LeftAt == null || v.LeftAt >= timestamp))
                .OrderByDescending(v => v.JoinedAt)
                .Select(v => new { v.Id, v.WorldName, v.JoinedAt })
                .FirstOrDefaultAsync();

            var record = new PhotoRecord
            {
                FilePath = filePath,
                FileName = fileName,
                TakenAt = timestamp.Value,
                WorldVisitId = worldVisit?.Id
            };

            db.PhotoRecords.Add(record);
            await db.SaveChangesAsync();

            PhotoAdded?.Invoke(new PhotoAddedInfo(
                filePath, fileName, timestamp.Value,
                worldVisit?.Id, worldVisit?.WorldName, worldVisit?.JoinedAt));
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
        finally
        {
            try { _processLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>起動時に未登録の写真をバッチスキャンする（3秒遅延後）</summary>
    private async Task ScanMissedPhotosAsync(string dir)
    {
        try
        {
            await Task.Delay(3000);
            var scanner = new PhotoScanner();
            await scanner.ScanAsync(dir);
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();

        // 進行中ハンドラに先にキャンセル通知してから semaphore を破棄する。
        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }

        _processLock.Dispose();
        _disposeCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
