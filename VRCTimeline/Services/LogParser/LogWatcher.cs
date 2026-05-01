using System.Globalization;
using System.IO;
using VRCTimeline.Models;

namespace VRCTimeline.Services.LogParser;

/// <summary>
/// 最新ログファイルから復元した現在のセッション状態。
/// アプリ起動時にリアルタイム監視の初期状態として使用する。
/// </summary>
public record CurrentSessionState(
    string? WorldName,
    string? InstanceId,
    List<string> CurrentPlayers
);

/// <summary>
/// VRChat のログファイルをリアルタイムに監視し、新しいイベントを検出して通知する。
/// 2秒間隔のポーリングでファイル末尾の追記を読み取り、LogEntry イベントを発行する。
/// </summary>
public class LogWatcher : IDisposable
{
    /// <summary>新規ログファイル検出用の FileSystemWatcher</summary>
    private FileSystemWatcher? _directoryWatcher;

    /// <summary>現在監視中のログファイルパス</summary>
    private string? _currentFilePath;

    /// <summary>前回読み取ったファイル位置（バイトオフセット）</summary>
    private long _lastPosition;

    /// <summary>2秒間隔のポーリングタイマー</summary>
    private Timer? _pollTimer;

    private readonly string _logDirectory;
    private readonly object _lock = new();

    /// <summary>ログ行が解析された際に発火するイベント</summary>
    public event Action<LogEntry>? LogEntryDetected;

    /// <summary>監視中かどうか</summary>
    public bool IsMonitoring { get; private set; }

    public LogWatcher(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    /// <summary>
    /// 最新ログファイルを解析し、現在のワールド・インスタンス・在室プレイヤーの状態を返す。
    /// ログ内の最後の入室イベント以降を再生して現在状態を復元する。
    /// </summary>
    public CurrentSessionState? ParseCurrentState()
    {
        var latestFile = FindLatestLogFile();
        if (latestFile == null) return null;

        string? worldName = null;
        string? instanceId = null;
        long lastRoomJoinPos = -1;

        // 1パス目: 最後の入室行の位置を特定
        using var stream = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var positions = new List<long>();
        long pos = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var roomMatch = LogPatterns.EnteringRoomRegex().Match(line);
            if (roomMatch.Success)
                lastRoomJoinPos = pos;
            pos++;
        }

        if (lastRoomJoinPos < 0) return null;

        // 2パス目: 最後の入室行以降の join/left を再生して在室プレイヤーを算出
        stream.Position = 0;
        using var reader2 = new StreamReader(stream);
        long currentLine = 0;
        var players = new HashSet<string>();

        while ((line = reader2.ReadLine()) != null)
        {
            if (currentLine >= lastRoomJoinPos)
            {
                var roomMatch = LogPatterns.EnteringRoomRegex().Match(line);
                if (roomMatch.Success)
                {
                    worldName = roomMatch.Groups[1].Value.Trim();
                    players.Clear();
                }

                var instMatch = LogPatterns.JoiningInstanceRegex().Match(line);
                if (instMatch.Success)
                    instanceId = instMatch.Groups[1].Value.Trim();

                var joinMatch = LogPatterns.PlayerJoinedRegex().Match(line);
                if (joinMatch.Success)
                    players.Add(LogPatterns.CleanPlayerName(joinMatch.Groups[1].Value.Trim()));

                var leftMatch = LogPatterns.PlayerLeftRegex().Match(line);
                if (leftMatch.Success)
                    players.Remove(LogPatterns.CleanPlayerName(leftMatch.Groups[1].Value.Trim()));
            }
            currentLine++;
        }

        if (worldName == null) return null;
        return new CurrentSessionState(worldName, instanceId, players.ToList());
    }

    /// <summary>
    /// リアルタイム監視を開始する。
    /// 最新ログファイルの末尾位置を記録し、以降の追記をポーリングで検出する。
    /// </summary>
    public void Start()
    {
        if (IsMonitoring) return;
        IsMonitoring = true;

        var latestFile = FindLatestLogFile();
        if (latestFile != null)
        {
            _currentFilePath = latestFile;
            _lastPosition = new FileInfo(latestFile).Length;
        }

        // 新しいログファイルの作成を監視（VRChat 再起動時）
        if (Directory.Exists(_logDirectory))
        {
            _directoryWatcher = new FileSystemWatcher(_logDirectory)
            {
                Filter = "output_log_*.txt",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _directoryWatcher.Created += OnNewFileCreated;
            _directoryWatcher.EnableRaisingEvents = true;
        }

        _pollTimer = new Timer(ReadNewContent, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    /// <summary>リアルタイム監視を停止し、リソースを解放する</summary>
    public void Stop()
    {
        IsMonitoring = false;
        _directoryWatcher?.Dispose();
        _directoryWatcher = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    /// <summary>ログディレクトリ内の最新ログファイルを返す</summary>
    private string? FindLatestLogFile()
    {
        if (!Directory.Exists(_logDirectory)) return null;
        return Directory.GetFiles(_logDirectory, "output_log_*.txt")
            .OrderByDescending(f => new FileInfo(f).LastWriteTime)
            .FirstOrDefault();
    }

    /// <summary>新しいログファイルが作成された際に監視対象を切り替える</summary>
    private void OnNewFileCreated(object sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            _currentFilePath = e.FullPath;
            _lastPosition = 0;
        }
    }

    /// <summary>
    /// ポーリングタイマーのコールバック。
    /// ログファイルの追記分を読み取り、各行を解析してイベントを発行する。
    /// </summary>
    private void ReadNewContent(object? state)
    {
        if (!IsMonitoring) return;

        string? filePath;
        long position;

        lock (_lock)
        {
            filePath = _currentFilePath;
            position = _lastPosition;
        }

        if (filePath == null || !File.Exists(filePath)) return;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= position) return;

            stream.Position = position;
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var entry = ParseLine(line);
                if (entry != null)
                    LogEntryDetected?.Invoke(entry);
            }

            lock (_lock)
            {
                _lastPosition = stream.Position;
            }
        }
        catch (IOException)
        {
            // VRChat がファイルをロック中。次回のポーリングでリトライ
        }
    }

    /// <summary>
    /// ログ行1行を解析し、対応する LogEntry を返す。
    /// 認識できない行の場合は null を返す。
    /// </summary>
    internal static LogEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var timestampMatch = LogPatterns.TimestampRegex().Match(line);
        if (!timestampMatch.Success) return null;

        if (!DateTime.TryParseExact(timestampMatch.Groups[1].Value,
            LogPatterns.TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp))
            return null;

        // ── ワールド入室 ──
        var roomMatch = LogPatterns.EnteringRoomRegex().Match(line);
        if (roomMatch.Success)
        {
            var worldName = roomMatch.Groups[1].Value.Trim();
            return new LogEntry
            {
                Timestamp = timestamp,
                Type = LogEntryType.RoomJoin,
                WorldName = worldName,
                Message = $"ワールドに入室: {worldName}"
            };
        }

        // ── インスタンス接続 ──
        var instanceMatch = LogPatterns.JoiningInstanceRegex().Match(line);
        if (instanceMatch.Success)
        {
            return new LogEntry
            {
                Timestamp = timestamp,
                Type = LogEntryType.RoomJoin,
                InstanceId = instanceMatch.Groups[1].Value.Trim(),
                Message = "インスタンスに接続"
            };
        }

        // ── プレイヤー入室 ──
        var joinMatch = LogPatterns.PlayerJoinedRegex().Match(line);
        if (joinMatch.Success)
        {
            var rawName = joinMatch.Groups[1].Value.Trim();
            var playerName = LogPatterns.CleanPlayerName(rawName);
            return new LogEntry
            {
                Timestamp = timestamp,
                Type = LogEntryType.PlayerJoined,
                PlayerName = playerName,
                PlayerUserId = LogPatterns.ExtractUserId(rawName),
                Message = $"{playerName} が入室しました"
            };
        }

        // ── プレイヤー退室 ──
        var leftMatch = LogPatterns.PlayerLeftRegex().Match(line);
        if (leftMatch.Success)
        {
            var rawName = leftMatch.Groups[1].Value.Trim();
            var playerName = LogPatterns.CleanPlayerName(rawName);
            return new LogEntry
            {
                Timestamp = timestamp,
                Type = LogEntryType.PlayerLeft,
                PlayerName = playerName,
                PlayerUserId = LogPatterns.ExtractUserId(rawName),
                Message = $"{playerName} が退室しました"
            };
        }

        // ── 通知受信 ──
        var notifMatch = LogPatterns.NotificationRegex().Match(line);
        if (notifMatch.Success)
        {
            var sender = LogPatterns.CleanPlayerName(notifMatch.Groups[1].Value.Trim());
            var notifType = notifMatch.Groups[2].Value.Trim();
            var displayType = notifType switch
            {
                "invite" => "Invite",
                "requestInvite" => "Request Invite",
                "boop" => "Boop",
                _ => notifType
            };
            return new LogEntry
            {
                Timestamp = timestamp,
                Type = LogEntryType.Notification,
                PlayerName = sender,
                NotificationType = notifType,
                Message = $"{sender} から {displayType} を受信"
            };
        }

        // ── 動画再生検出 ──
        var videoMatch = LogPatterns.VideoPlaybackRegex().Match(line);
        if (videoMatch.Success)
        {
            var url = videoMatch.Groups[1].Value.Trim();
            return new LogEntry
            {
                Timestamp = timestamp,
                Type = LogEntryType.VideoUrl,
                VideoUrl = url,
                Message = $"動画再生: {url}"
            };
        }

        return null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
