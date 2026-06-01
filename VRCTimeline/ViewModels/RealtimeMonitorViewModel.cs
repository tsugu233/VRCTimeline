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

    /// <summary>
    /// ProcessLogEntry の直列化用セマフォ。
    /// LogWatcher から流れ込むイベントは Dispatcher.InvokeAsync で UI スレッドにキューされるが、
    /// ProcessLogEntry が async void のため、ある呼び出しが await 中に次の呼び出しが割り込む。
    /// その結果 SaveWorldVisitAsync が _currentWorldVisitId を設定する前に後続の
    /// PlayerJoined / JoiningInstance / Notification 等が走り、_currentWorldVisitId == null
    /// ガードで黙って drop されてしまう（InstanceId 欠落・初期プレイヤー取りこぼし・前ワールドへの誤紐付け）。
    /// セマフォで 1 件ずつ完了を待たせ、await の前後で状態が一貫するようにする。
    /// HandleVRChatExited も同じセマフォを取り、終了処理と通常イベント処理が交互に走って
    /// _currentWorldVisitId が null 化された後に新規 WorldVisit が作られて未閉のまま残る
    /// （次回起動時にゾンビ訪問として復元される）競合を防ぐ。
    /// </summary>
    private readonly SemaphoreSlim _processSemaphore = new(1, 1);

    /// <summary>
    /// Dispose 通知用 CTS。Dispose 時にキャンセルすることで、_processSemaphore.WaitAsync 待ちの
    /// 進行中ハンドラを OperationCanceledException で抜けさせる。これがないと
    /// _processSemaphore.Dispose() 後の WaitAsync / Release が ObjectDisposedException を投げ、
    /// async void 経由で未捕捉例外としてアプリをクラッシュさせる可能性がある。
    /// </summary>
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>
    /// StartMonitoring の直列化用セマフォ。
    /// VRChat の高速 ON/OFF トグルで OnVRChatStatusChanged から StartMonitoring が連続発火されると、
    /// IsMonitoring = true は2つの await の後に書き込まれるため、関数冒頭のチェックだけでは並走を防げない。
    /// その結果、複数の LogWatcher（FileSystemWatcher + Timer）が生成され、最後に代入された 1 つだけが
    /// _logWatcher に残り、他はリークしつつ LogEntryDetected を二重発火する競合が発生する。
    /// セマフォで開始処理全体を直列化し、待機後にも IsMonitoring を再チェックして二重ガードする。
    /// </summary>
    private readonly SemaphoreSlim _startStopSemaphore = new(1, 1);

    /// <summary>自分の表示名（プレイヤー一覧から除外するため）</summary>
    private string _selfPlayerName = "";

    /// <summary>自分のユーザーID</summary>
    private string _selfPlayerUserId = "";

    /// <summary>現在のワールド名（未接続時はローカライズされた "未接続" 相当文字列）</summary>
    [ObservableProperty]
    private string _currentWorldName = string.Empty;

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
        CurrentWorldName = LocalizationService.GetString("Str_NotConnected");
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        if (_logWatcher == null)
            CurrentWorldName = LocalizationService.GetString("Str_NotConnected");
    }

    /// <summary>
    /// リアルタイム監視を開始する。
    /// ログファイルから現在状態を復元し、以降のイベントを購読して DB に記録する。
    /// 同時呼び出しはセマフォで直列化され、IsMonitoring の二重チェックで重複起動を防ぐ。
    /// 重い <see cref="LogWatcher.ParseCurrentState"/> はバックグラウンドスレッドで実行する
    /// （長時間プレイで 100MB を超えるログを 2 パス走査するため、UI スレッドで動かすと数秒〜十数秒固まる）。
    /// </summary>
    public async Task StartMonitoring()
    {
        if (IsMonitoring) return;

        try
        {
            await _startStopSemaphore.WaitAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }

        try
        {
            // 待機中に他スレッドが先行起動した／Dispose 済みのケースを排除
            if (IsMonitoring) return;

            _selfPlayerName = await _selfPlayer.GetSelfPlayerNameAsync();
            _selfPlayerUserId = await _selfPlayer.GetSelfUserIdAsync();

            // 半端な初期化状態でフィールドが見えないように、購読・Start 完了後に _logWatcher へ代入する
            var watcher = new LogWatcher(_settingsService.Settings.VRChatLogDirectory);

            // 現在のセッション状態を復元（ワールド名・プレイヤーリスト）。
            // ParseCurrentState は最大数百MBのファイルを2回走査するため UI スレッドでは動かさない。
            var state = await Task.Run(() => watcher.ParseCurrentState());
            if (state != null)
            {
                CurrentWorldName = state.WorldName ?? LocalizationService.GetString("Str_NotConnected");
                CurrentInstanceId = state.InstanceId ?? string.Empty;
                CurrentPlayers.Clear();
                foreach (var player in state.CurrentPlayers)
                    CurrentPlayers.Add(player.DisplayName);
                PlayerCount = CurrentPlayers.Count;
            }

            // 起動時の DB 整合化:
            //   - 未閉訪問が無い、または現在のログ状態と一致しないなら、現セッションを新規 WorldVisit として登録する。
            //   - 一致するならその ID を引き継いで以後のイベントを紐付ける。
            //
            // 「前回 VRChat 起動中にアプリだけ終了 → 別ログで別ワールドに居る状態でアプリ再起動」というシナリオでは、
            // LogWatcher はファイル末尾から監視を始めるため "Entering Room" 行が二度発火せず、
            // 現ワールドが永遠に DB に書かれず、過去ワールドへ誤紐付けされる不具合があった。
            // ここで明示的に新規 WorldVisit を作ることでその欠落を埋める。
            await ReconcileCurrentVisitAsync(state);

            watcher.LogEntryDetected += OnLogEntryDetected;
            watcher.NewLogSessionStarted += OnNewLogSessionStarted;
            watcher.Start();
            _logWatcher = watcher;
            IsMonitoring = true;
        }
        finally
        {
            try { _startStopSemaphore.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Window 非表示時の表示用リソース解放（現状は意図的に no-op）。
    /// リアルタイム監視画面は Window 非表示中も View・VM 状態とも保持し続ける方針のため、
    /// LogEntries・CurrentPlayers・CurrentWorldName いずれもクリアしない。
    /// LogWatcher / DB 書込パイプラインは元々止めない（VRChat ログ消去対策）。
    /// メソッドは呼び出し側の統一目的で残す。
    /// </summary>
    public void ReleaseUiResources()
    {
        // intentionally empty
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
    /// 新ログファイル検知（VRChat 再起動）を UI スレッドにディスパッチする。
    /// ProcessLogEntry と同じ Dispatcher キュー経由かつ同じセマフォで直列化されるため、
    /// 新ファイルの最初の room join 解析より先に旧訪問のクローズが完了する。
    /// </summary>
    private void OnNewLogSessionStarted(DateTime? previousSessionEnd)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => HandleNewLogSession(previousSessionEnd));
    }

    /// <summary>
    /// VRChat 再起動を検知した際、前セッションで開いたままの訪問を閉じる。
    /// プロセス監視がスリープ跨ぎ等で終了遷移を取りこぼし、HandleVRChatExited が
    /// 呼ばれなかった場合でも、ここで旧訪問を確実に閉じて幻の長時間滞在を防ぐ。
    /// </summary>
    private async void HandleNewLogSession(DateTime? previousSessionEnd)
    {
        try
        {
            await _processSemaphore.WaitAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }

        try
        {
            await CloseCurrentSessionForRestartAsync(previousSessionEnd);
        }
        finally
        {
            try { _processSemaphore.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// VRChat 終了時の後処理（訪問・セッションの閉じ処理、UI リセット、終了ログの追加）。
    /// プロセス監視側から VRChat 終了が検知された際に呼び出す。
    /// </summary>
    public async void HandleVRChatExited()
    {
        StopMonitoring();

        // 進行中の ProcessLogEntry を完走させてから終了処理に入る。
        // これがないと SaveWorldVisitAsync が新しい WorldVisit を作って _currentWorldVisitId を
        // 上書きするレースで、CloseCurrentWorldVisitAsync が古い ID を閉じ → 新 ID は閉じられず
        // LeftAt = null のゾンビ訪問が DB に残る。
        try
        {
            await _processSemaphore.WaitAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }

        try
        {
            await CloseCurrentWorldVisitAsync();
            CurrentWorldName = LocalizationService.GetString("Str_NotConnected");
            CurrentInstanceId = string.Empty;
            CurrentPlayers.Clear();
            PlayerCount = 0;
            LogEntries.Insert(0, new LogEntry
            {
                Timestamp = DateTime.Now,
                Type = LogEntryType.Info,
                Message = LocalizationService.GetString("Log_VRChatExited")
            });
        }
        finally
        {
            try { _processSemaphore.Release(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// 起動時に、ログから復元した現在状態と DB の訪問を突き合わせて整合化する。
    ///
    /// 判定ルール:
    ///   ・state == null（VRChat 未接続 or ログ未生成）→ 未閉訪問があれば閉じて DB を綺麗にする。
    ///   ・現状態と同じ入室（WorldName + JoinedAt）の訪問が既にある → その訪問を継続する。
    ///     クローズ済みでも再開（LeftAt を null に戻す）して ID を引き継ぐ。これは VRChat プロセスの
    ///     誤検知（スリープ復帰等で一瞬「終了」と判定 → 直後に再検知）で HandleVRChatExited が
    ///     現在の訪問を閉じてしまい、再開時に同一 JoinedAt の訪問が二重作成される問題への対策。
    ///   ・上記が無く、別ワールドの未閉訪問が残っている → ゾンビなので閉じる。LeftAt は確定できないため
    ///     JoinedAt を入れて 0 秒記録扱いとし、長期間「滞在中」表示が残るのを防ぐ。
    ///   ・引き継げる訪問が無い & state が現ワールドを示す → 新規 WorldVisit を作って
    ///     初期プレイヤーを PlayerSession として書き込む。
    /// </summary>
    private async Task ReconcileCurrentVisitAsync(CurrentSessionState? state)
    {
        try
        {
            await using var db = new AppDbContext();

            if (state == null || state.WorldName == null)
            {
                // ログから現状態を取れなかった: 未閉のゾンビ訪問だけ閉じて終わる
                var zombie = await db.WorldVisits
                    .Include(v => v.PlayerSessions)
                    .Where(v => v.LeftAt == null)
                    .OrderByDescending(v => v.JoinedAt)
                    .FirstOrDefaultAsync();
                if (zombie != null)
                {
                    zombie.LeftAt = zombie.JoinedAt;
                    foreach (var s in zombie.PlayerSessions.Where(s => s.LeftAt == null))
                        s.LeftAt = zombie.JoinedAt;
                    await db.SaveChangesAsync();
                }
                _currentWorldVisitId = null;
                return;
            }

            // 現状態と同じ入室（WorldName + JoinedAt）の訪問が既にあるか。
            // JoinedAt は "Entering Room" 行のログ秒精度タイムスタンプで 1 入室につき一意なので、
            // WorldName と合わせれば「同じ訪問」を LeftAt の有無に関わらず確実に同定できる。
            var sameVisit = await db.WorldVisits
                .Include(v => v.PlayerSessions)
                .Where(v => v.WorldName == state.WorldName && v.JoinedAt == state.JoinedAt)
                .OrderByDescending(v => v.Id)
                .FirstOrDefaultAsync();

            if (sameVisit != null)
            {
                // InstanceId が未取得なら現状態で補完する
                if (string.IsNullOrEmpty(sameVisit.InstanceId) && !string.IsNullOrEmpty(state.InstanceId))
                {
                    sameVisit.InstanceId = state.InstanceId;
                    sameVisit.WorldId = LogPatterns.ExtractWorldId(state.InstanceId);
                }

                // 誤検知終了で閉じられていた場合は再開する。
                // 今も在室しているプレイヤー（state.CurrentPlayers）のセッションも一緒に再開し、
                // ブリップ時刻で「退室」したまま残らないようにする。
                if (sameVisit.LeftAt != null)
                {
                    sameVisit.LeftAt = null;
                    foreach (var p in state.CurrentPlayers)
                    {
                        var s = sameVisit.PlayerSessions
                            .Where(x => !string.IsNullOrEmpty(p.UserId) ? x.UserId == p.UserId : x.DisplayName == p.DisplayName)
                            .OrderByDescending(x => x.JoinedAt)
                            .FirstOrDefault();
                        if (s is { LeftAt: not null })
                            s.LeftAt = null;
                    }
                }

                await db.SaveChangesAsync();
                _currentWorldVisitId = sameVisit.Id;
                return;
            }

            // 別ワールドの未閉訪問が残っていれば閉じる（ゾンビ）
            var orphan = await db.WorldVisits
                .Include(v => v.PlayerSessions)
                .Where(v => v.LeftAt == null)
                .OrderByDescending(v => v.JoinedAt)
                .FirstOrDefaultAsync();
            if (orphan != null)
            {
                orphan.LeftAt = orphan.JoinedAt;
                foreach (var s in orphan.PlayerSessions.Where(s => s.LeftAt == null))
                    s.LeftAt = orphan.JoinedAt;
                await db.SaveChangesAsync();
            }

            // 現在のワールドを新規訪問として登録する
            var instanceId = state.InstanceId ?? string.Empty;
            var visit = new WorldVisit
            {
                WorldName = state.WorldName,
                InstanceId = instanceId,
                WorldId = string.IsNullOrEmpty(instanceId) ? string.Empty : LogPatterns.ExtractWorldId(instanceId),
                JoinedAt = state.JoinedAt
            };
            db.WorldVisits.Add(visit);
            await db.SaveChangesAsync();

            foreach (var p in state.CurrentPlayers)
            {
                db.PlayerSessions.Add(new PlayerSession
                {
                    WorldVisitId = visit.Id,
                    DisplayName = p.DisplayName,
                    UserId = p.UserId,
                    JoinedAt = p.JoinedAt
                });
            }
            await db.SaveChangesAsync();

            _currentWorldVisitId = visit.Id;
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
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
        catch (Exception ex) { AppLogger.LogError(ex); }
    }

    /// <summary>
    /// VRChat 再起動検知時に、前セッションの未クローズ訪問を閉じる。
    /// LeftAt は旧ログファイルの最終ログ時刻（previousSessionEnd）を使い、取得できなければ
    /// JoinedAt を入れて 0 秒記録扱いとする。JoinedAt より前にならないようガードする。
    /// </summary>
    private async Task CloseCurrentSessionForRestartAsync(DateTime? previousSessionEnd)
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
                var closeAt = previousSessionEnd ?? visit.JoinedAt;
                if (closeAt < visit.JoinedAt) closeAt = visit.JoinedAt;
                visit.LeftAt = closeAt;
                foreach (var s in visit.PlayerSessions.Where(s => s.LeftAt == null))
                    s.LeftAt = closeAt;
                await db.SaveChangesAsync();
            }
            _currentWorldVisitId = null;
        }
        catch (Exception ex) { AppLogger.LogError(ex); }
    }

    /// <summary>
    /// 解析されたログイベントを種別ごとに処理する。
    /// UI の更新と DB への保存を行う。
    /// </summary>
    private async void ProcessLogEntry(LogEntry entry)
    {
        try
        {
            await _processSemaphore.WaitAsync(_disposeCts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }

        try
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
                            entry.Message = string.Format(LocalizationService.GetString("Log_SelfJoined"), CurrentWorldName);
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
                            entry.Message = string.Format(LocalizationService.GetString("Log_SelfLeft"), CurrentWorldName);
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
        finally
        {
            try { _processSemaphore.Release(); } catch (ObjectDisposedException) { }
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
        catch (Exception ex) { AppLogger.LogError(ex); }
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
        catch (Exception ex) { AppLogger.LogError(ex); }
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
        catch (Exception ex) { AppLogger.LogError(ex); }
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
        catch (Exception ex) { AppLogger.LogError(ex); }
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
        catch (Exception ex) { AppLogger.LogError(ex); }
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
        catch (Exception ex) { AppLogger.LogError(ex); }
    }

    public void Dispose()
    {
        StopMonitoring();
        LocalizationService.LanguageChanged -= OnLanguageChanged;

        // 進行中ハンドラに先にキャンセルを通知してから semaphore を破棄する。
        // これがないと WaitAsync 中のハンドラが ObjectDisposedException を投げ、
        // async void 経由で未捕捉となりアプリがクラッシュする可能性がある。
        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }

        _processSemaphore.Dispose();
        _startStopSemaphore.Dispose();
        _disposeCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
