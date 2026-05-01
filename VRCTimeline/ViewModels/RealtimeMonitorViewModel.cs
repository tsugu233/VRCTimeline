using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Models;
using VRCTimeline.Services;
using VRCTimeline.Services.LogParser;

namespace VRCTimeline.ViewModels;

/// <summary>
/// リアルタイム監視画面の ViewModel。
/// VRChat 実行中のワールド・プレイヤー状態をリアルタイムに追跡し、DB へ記録する。
/// </summary>
public partial class RealtimeMonitorViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly SelfPlayerService _selfPlayer;

    /// <summary>ログファイルのリアルタイム監視インスタンス</summary>
    private LogWatcher? _logWatcher;

    /// <summary>現在滞在中のワールド訪問ID（DB の主キー）</summary>
    private int? _currentWorldVisitId;

    /// <summary>動画 URL の重複検出用</summary>
    private string? _lastVideoUrl;

    /// <summary>自分の表示名（プレイヤー一覧から除外するため）</summary>
    private string _selfPlayerName = "";

    /// <summary>自分のユーザーID</summary>
    private string _selfPlayerUserId = "";

    /// <summary>現在のワールド名（未接続時は "未接続"）</summary>
    [ObservableProperty]
    private string _currentWorldName = "未接続";

    /// <summary>現在のインスタンスID</summary>
    [ObservableProperty]
    private string _currentInstanceId = string.Empty;

    /// <summary>現在の同室プレイヤー数</summary>
    [ObservableProperty]
    private int _playerCount;

    /// <summary>監視中フラグ</summary>
    [ObservableProperty]
    private bool _isMonitoring;

    /// <summary>ステータスバー表示テキスト</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>リアルタイムログの表示リスト（最新が先頭、最大500件）</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    /// <summary>現在同室しているプレイヤーの表示名リスト</summary>
    public ObservableCollection<string> CurrentPlayers { get; } = [];

    public RealtimeMonitorViewModel(SettingsService settingsService, SelfPlayerService selfPlayerService)
    {
        _settingsService = settingsService;
        _selfPlayer = selfPlayerService;
    }

    /// <summary>
    /// リアルタイム監視を開始する。
    /// ログファイルから現在状態を復元し、以降のイベントを購読して DB に記録する。
    /// </summary>
    public async void StartMonitoring()
    {
        if (IsMonitoring) return;

        _selfPlayerName = await _selfPlayer.GetSelfPlayerNameAsync();
        _selfPlayerUserId = await _selfPlayer.GetSelfUserIdAsync();

        _logWatcher = new LogWatcher(_settingsService.Settings.VRChatLogDirectory);

        // 現在のセッション状態を復元（ワールド名・プレイヤーリスト）
        var state = _logWatcher.ParseCurrentState();
        if (state != null)
        {
            CurrentWorldName = state.WorldName ?? "未接続";
            CurrentInstanceId = state.InstanceId ?? string.Empty;
            CurrentPlayers.Clear();
            foreach (var player in state.CurrentPlayers)
                CurrentPlayers.Add(player);
            PlayerCount = CurrentPlayers.Count;
        }

        // 未閉のワールド訪問IDを取得（前回異常終了時の継続用）
        try
        {
            await using var db = new AppDbContext();
            var visit = await db.WorldVisits
                .Where(v => v.LeftAt == null)
                .OrderByDescending(v => v.JoinedAt)
                .FirstOrDefaultAsync();
            _currentWorldVisitId = visit?.Id;
        }
        catch { }

        _logWatcher.LogEntryDetected += OnLogEntryDetected;
        _logWatcher.Start();
        IsMonitoring = true;
    }

    /// <summary>リアルタイム監視を停止する</summary>
    public void StopMonitoring()
    {
        _logWatcher?.Stop();
        _logWatcher?.Dispose();
        _logWatcher = null;
        IsMonitoring = false;
    }

    /// <summary>LogWatcher からのイベントを UI スレッドにディスパッチする</summary>
    private void OnLogEntryDetected(LogEntry entry)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => ProcessLogEntry(entry));
    }

    /// <summary>
    /// VRChat 終了時の後処理（訪問・セッションの閉じ処理、UI リセット、終了ログの追加）。
    /// プロセス監視側から VRChat 終了が検知された際に呼び出す。
    /// </summary>
    public async void HandleVRChatExited()
    {
        StopMonitoring();
        await CloseCurrentWorldVisitAsync();
        CurrentWorldName = "未接続";
        CurrentInstanceId = string.Empty;
        CurrentPlayers.Clear();
        PlayerCount = 0;
        LogEntries.Insert(0, new LogEntry
        {
            Timestamp = DateTime.Now,
            Type = LogEntryType.Info,
            Message = "VRChatが終了しました"
        });
    }

    /// <summary>現在のワールド訪問と未閉セッションを閉じる</summary>
    private async Task CloseCurrentWorldVisitAsync()
    {
        if (_currentWorldVisitId == null) return;
        try
        {
            await using var db = new AppDbContext();
            var visit = await db.WorldVisits
                .Include(v => v.PlayerSessions)
                .FirstOrDefaultAsync(v => v.Id == _currentWorldVisitId.Value);
            if (visit != null && visit.LeftAt == null)
            {
                visit.LeftAt = DateTime.Now;
                foreach (var s in visit.PlayerSessions.Where(s => s.LeftAt == null))
                    s.LeftAt = DateTime.Now;
                await db.SaveChangesAsync();
            }
            _currentWorldVisitId = null;
        }
        catch { }
    }

    /// <summary>
    /// 解析されたログイベントを種別ごとに処理する。
    /// UI の更新と DB への保存を行う。
    /// </summary>
    private async void ProcessLogEntry(LogEntry entry)
    {
        switch (entry.Type)
        {
            // ── ワールド入室 / インスタンス接続 ──
            case LogEntryType.RoomJoin:
                if (entry.WorldName != null)
                {
                    CurrentWorldName = entry.WorldName;
                    CurrentPlayers.Clear();
                    PlayerCount = 0;
                    _lastVideoUrl = null;
                    await SaveWorldVisitAsync(entry);
                }
                if (entry.InstanceId != null)
                {
                    CurrentInstanceId = entry.InstanceId;
                    await UpdateInstanceIdAsync(entry.InstanceId);
                }
                break;

            // ── プレイヤー入室 ──
            case LogEntryType.PlayerJoined:
                if (entry.PlayerName != null)
                {
                    if (entry.PlayerName == _selfPlayerName)
                        entry.Message = $"{CurrentWorldName} に入室しました";
                    if (!CurrentPlayers.Contains(entry.PlayerName))
                    {
                        CurrentPlayers.Add(entry.PlayerName);
                        PlayerCount = CurrentPlayers.Count;
                    }
                    await SavePlayerJoinAsync(entry);
                }
                break;

            // ── プレイヤー退室 ──
            case LogEntryType.PlayerLeft:
                if (entry.PlayerName != null)
                {
                    if (entry.PlayerName == _selfPlayerName)
                        entry.Message = $"{CurrentWorldName} から退室しました";
                    CurrentPlayers.Remove(entry.PlayerName);
                    PlayerCount = CurrentPlayers.Count;
                    await SavePlayerLeftAsync(entry);
                }
                break;

            case LogEntryType.Notification:
                await SaveNotificationAsync(entry);
                break;

            case LogEntryType.VideoUrl:
                await SaveVideoAsync(entry);
                break;
        }

        // ログ一覧に追加（ワールド入室・動画は別 UI で表示するため除外）
        if (entry.Type is not (LogEntryType.RoomJoin or LogEntryType.VideoUrl))
        {
            LogEntries.Insert(0, entry);
            if (LogEntries.Count > 500)
                LogEntries.RemoveAt(LogEntries.Count - 1);
        }
    }

    /// <summary>新しいワールド訪問を DB に保存し、前の訪問を閉じる</summary>
    private async Task SaveWorldVisitAsync(LogEntry entry)
    {
        try
        {
            await using var db = new AppDbContext();
            var lastVisit = await db.WorldVisits
                .Include(v => v.PlayerSessions)
                .Where(v => v.LeftAt == null)
                .OrderByDescending(v => v.JoinedAt)
                .FirstOrDefaultAsync();

            if (lastVisit != null)
            {
                lastVisit.LeftAt = entry.Timestamp;
                foreach (var s in lastVisit.PlayerSessions.Where(s => s.LeftAt == null))
                    s.LeftAt = entry.Timestamp;
            }

            var visit = new WorldVisit
            {
                WorldName = entry.WorldName!,
                JoinedAt = entry.Timestamp
            };
            db.WorldVisits.Add(visit);
            await db.SaveChangesAsync();
            _currentWorldVisitId = visit.Id;
        }
        catch { }
    }

    /// <summary>現在のワールド訪問にインスタンスIDとワールドIDを設定する</summary>
    private async Task UpdateInstanceIdAsync(string instanceId)
    {
        if (_currentWorldVisitId == null) return;
        try
        {
            await using var db = new AppDbContext();
            var visit = await db.WorldVisits.FindAsync(_currentWorldVisitId);
            if (visit != null)
            {
                visit.InstanceId = instanceId;
                visit.WorldId = LogPatterns.ExtractWorldId(instanceId);
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    /// <summary>プレイヤー入室セッションを DB に保存する（UserId 付き）</summary>
    private async Task SavePlayerJoinAsync(LogEntry entry)
    {
        if (_currentWorldVisitId == null) return;
        try
        {
            await using var db = new AppDbContext();
            db.PlayerSessions.Add(new PlayerSession
            {
                WorldVisitId = _currentWorldVisitId.Value,
                DisplayName = entry.PlayerName!,
                UserId = entry.PlayerUserId ?? "",
                JoinedAt = entry.Timestamp
            });
            await db.SaveChangesAsync();
        }
        catch { }
    }

    /// <summary>
    /// プレイヤー退室時にセッションの LeftAt を設定する。
    /// UserId が利用可能な場合は UserId で照合し、なければ表示名で照合する。
    /// </summary>
    private async Task SavePlayerLeftAsync(LogEntry entry)
    {
        if (_currentWorldVisitId == null) return;
        try
        {
            await using var db = new AppDbContext();
            var query = db.PlayerSessions
                .Where(s => s.WorldVisitId == _currentWorldVisitId.Value && s.LeftAt == null);

            if (!string.IsNullOrEmpty(entry.PlayerUserId))
                query = query.Where(s => s.UserId == entry.PlayerUserId);
            else
                query = query.Where(s => s.DisplayName == entry.PlayerName);

            var session = await query
                .OrderByDescending(s => s.JoinedAt)
                .FirstOrDefaultAsync();

            if (session != null)
            {
                session.LeftAt = entry.Timestamp;
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    /// <summary>通知レコードを DB に保存する</summary>
    private async Task SaveNotificationAsync(LogEntry entry)
    {
        if (_currentWorldVisitId == null && entry.NotificationType == null) return;
        try
        {
            await using var db = new AppDbContext();
            db.NotificationRecords.Add(new NotificationRecord
            {
                ReceivedAt = entry.Timestamp,
                SenderName = entry.PlayerName ?? "",
                NotificationType = entry.NotificationType ?? "",
                WorldVisitId = _currentWorldVisitId
            });
            await db.SaveChangesAsync();
        }
        catch { }
    }

    /// <summary>動画再生レコードを DB に保存する（同一 URL の重複は排除）</summary>
    private async Task SaveVideoAsync(LogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.VideoUrl)) return;
        if (entry.VideoUrl == _lastVideoUrl) return;
        _lastVideoUrl = entry.VideoUrl;
        try
        {
            await using var db = new AppDbContext();
            var exists = await db.VideoRecords.AnyAsync(
                v => v.Url == entry.VideoUrl && v.DetectedAt == entry.Timestamp);
            if (!exists)
            {
                db.VideoRecords.Add(new VideoRecord
                {
                    DetectedAt = entry.Timestamp,
                    Url = entry.VideoUrl,
                    WorldVisitId = _currentWorldVisitId
                });
                await db.SaveChangesAsync();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}
