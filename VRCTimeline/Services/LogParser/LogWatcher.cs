using System.Globalization;
using System.IO;
using System.Text;
using VRCTimeline.Models;
using VRCTimeline.Services;

namespace VRCTimeline.Services.LogParser;

/// <summary>
/// 現在のセッション状態に含まれる初期プレイヤー情報。
/// アプリ起動時に「既に同室していたプレイヤー」を PlayerSession として DB に書き戻すために、
/// 表示名・UserId・入室時刻を保持する。
/// </summary>
public record CurrentSessionPlayer(string DisplayName, string UserId, DateTime JoinedAt);

/// <summary>
/// 最新ログファイルから復元した現在のセッション状態。
/// アプリ起動時にリアルタイム監視の初期状態として使用する。
/// </summary>
/// <param name="WorldName">現在のワールド名</param>
/// <param name="InstanceId">インスタンス ID（"wrld_xxx:nonce" 形式、未取得時は null）</param>
/// <param name="JoinedAt">"Entering Room" 行の VRChat ログタイムスタンプ。
/// 既存の未閉 WorldVisit と「同じ訪問か別の訪問か」を判別するキーになる。</param>
/// <param name="CurrentPlayers">最後の入室以降に観測された在室プレイヤー</param>
public record CurrentSessionState(
    string? WorldName,
    string? InstanceId,
    DateTime JoinedAt,
    List<CurrentSessionPlayer> CurrentPlayers
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

    /// <summary>前回読み取ったファイル位置（最後に処理した \n の直後のバイトオフセット）</summary>
    private long _lastPosition;

    /// <summary>2秒間隔のポーリングタイマー</summary>
    private Timer? _pollTimer;

    /// <summary>ReadNewContent の再入ガード（0=idle, 1=実行中）。Timer のコールバック重複対策。</summary>
    private int _readInProgress;

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
    /// バイト単位で読み込み、完全な行（\n 終端）のみを処理対象とする。
    /// 末尾の不完全行は無視され、後続の <see cref="LogWatcher"/> ポーリングで完成後に処理される。
    /// 各 room join で状態をリセットしながら単一パスで走査する。
    /// </summary>
    public CurrentSessionState? ParseCurrentState()
    {
        // ファイルが VRChat にロックされている／検出と open の間で削除・差し替えされる可能性があるため、
        // FileStream 構築や Read を含む全体を IO 例外でガードする。失敗時は null を返し、
        // 呼び出し側は「状態復元なし」として既存パスで処理する。
        try
        {
            var latestFile = FindLatestLogFile();
            if (latestFile == null) return null;

            long endPosition = FindPositionAfterLastNewline(latestFile);
            if (endPosition == 0) return null;

            string? worldName = null;
            string? instanceId = null;
            DateTime worldJoinedAt = default;
            // 表示名 → (UserId, JoinedAt) の順序保持マップ。OnPlayerLeft で削除しつつ
            // 最初に入室した時刻を保つため、Dictionary ではなく自前で順序管理する。
            var players = new Dictionary<string, (string UserId, DateTime JoinedAt)>();

            using var stream = new FileStream(latestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var lineBuffer = new List<byte>(1024);
            var readBuffer = new byte[8192];
            long readSoFar = 0;

            while (readSoFar < endPosition)
            {
                int toRead = (int)Math.Min(readBuffer.Length, endPosition - readSoFar);
                int n = stream.Read(readBuffer, 0, toRead);
                if (n == 0) break;
                readSoFar += n;

                for (int i = 0; i < n; i++)
                {
                    byte b = readBuffer[i];
                    if (b != (byte)'\n')
                    {
                        lineBuffer.Add(b);
                        continue;
                    }

                    int len = lineBuffer.Count;
                    if (len > 0 && lineBuffer[len - 1] == (byte)'\r') len--;
                    string line = Encoding.UTF8.GetString(lineBuffer.ToArray(), 0, len);
                    lineBuffer.Clear();

                    // 全イベントのタイムスタンプを取得するため最初にパースする。
                    // タイムスタンプ無し行は VRChat の補足出力（スタックトレース等）なのでスキップ。
                    var tsMatch = LogPatterns.TimestampRegex().Match(line);
                    if (!tsMatch.Success) continue;
                    if (!DateTime.TryParseExact(tsMatch.Groups[1].Value,
                        LogPatterns.TimestampFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var ts)) continue;

                    // room join で状態をリセット（最後の入室以降の状態だけが残る）
                    var roomMatch = LogPatterns.EnteringRoomRegex().Match(line);
                    if (roomMatch.Success)
                    {
                        worldName = roomMatch.Groups[1].Value.Trim();
                        instanceId = null;
                        worldJoinedAt = ts;
                        players.Clear();
                        continue;
                    }

                    var instMatch = LogPatterns.JoiningInstanceRegex().Match(line);
                    if (instMatch.Success)
                    {
                        instanceId = instMatch.Groups[1].Value.Trim();
                        continue;
                    }

                    var joinMatch = LogPatterns.PlayerJoinedRegex().Match(line);
                    if (joinMatch.Success)
                    {
                        var raw = joinMatch.Groups[1].Value.Trim();
                        var name = LogPatterns.CleanPlayerName(raw);
                        // 同一表示名で複数回入退室した場合、最後の入室を採用（最新セッションを表現）。
                        players[name] = (LogPatterns.ExtractUserId(raw), ts);
                        continue;
                    }

                    var leftMatch = LogPatterns.PlayerLeftRegex().Match(line);
                    if (leftMatch.Success)
                    {
                        var name = LogPatterns.CleanPlayerName(leftMatch.Groups[1].Value.Trim());
                        players.Remove(name);
                    }
                }
            }

            if (worldName == null) return null;
            var initialPlayers = players
                .Select(kv => new CurrentSessionPlayer(kv.Key, kv.Value.UserId, kv.Value.JoinedAt))
                .ToList();
            return new CurrentSessionState(worldName, instanceId, worldJoinedAt, initialPlayers);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
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
            // 末尾が不完全行の場合は最後の \n の直後から監視を始める。
            // partial line は VRChat が \n を書き込んだ次のポーリングで完全な行として処理される。
            _lastPosition = FindPositionAfterLastNewline(latestFile);
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

    /// <summary>
    /// ファイル末尾から逆方向に走査し、最後の \n の直後のバイト位置を返す。
    /// \n が一つも無い／空ファイルの場合は 0。
    /// 末尾の不完全行を踏み越えて位置設定するのを防ぐために使用する。
    /// </summary>
    private static long FindPositionAfterLastNewline(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long fileLength = stream.Length;
        if (fileLength == 0) return 0;

        const int chunkSize = 4096;
        byte[] buffer = new byte[chunkSize];
        long pos = fileLength;

        while (pos > 0)
        {
            long readStart = Math.Max(0, pos - chunkSize);
            int readLen = (int)(pos - readStart);
            stream.Position = readStart;

            int totalRead = 0;
            while (totalRead < readLen)
            {
                int n = stream.Read(buffer, totalRead, readLen - totalRead);
                if (n == 0) break;
                totalRead += n;
            }

            for (int i = totalRead - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                    return readStart + i + 1;
            }
            pos = readStart;
        }
        return 0;
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
    /// ログファイルの追記分をバイト単位で読み取り、\n で区切られた完全な行のみ解析・発行する。
    /// _lastPosition は「最後に処理した \n の直後」までしか進めず、末尾の不完全バイトは
    /// 次回ポーリング時に最初から再読み込みされる（バッファは各ポーリングでローカル）。
    /// これにより、VRChat の書き込み途中で読み取りが走っても行が分断されず、欠落を防ぐ。
    /// 前回の tick が完了する前に Timer が再発火した場合は早期リターンし、同じバイト範囲を
    /// 重複処理して LogEntryDetected を二重発火するのを防ぐ。
    /// </summary>
    private void ReadNewContent(object? state)
    {
        if (!IsMonitoring) return;

        // System.Threading.Timer はコールバックを直列化しないため、前 tick の処理が
        // 2 秒以内に終わらないと並行実行されうる。同じ _lastPosition から同じ範囲を
        // 二重に読んで LogEntryDetected を多重発火させないため、再入ガードでスキップする。
        // スキップしても追記分は次の tick で読まれるので欠落は起きない。
        if (Interlocked.CompareExchange(ref _readInProgress, 1, 0) != 0) return;

        try
        {
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

                long consumedPosition = position;
                var lineBuffer = new List<byte>(1024);
                var readBuffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    long chunkStart = stream.Position - bytesRead;

                    for (int i = 0; i < bytesRead; i++)
                    {
                        byte b = readBuffer[i];
                        if (b == (byte)'\n')
                        {
                            int len = lineBuffer.Count;
                            if (len > 0 && lineBuffer[len - 1] == (byte)'\r') len--;

                            string line = Encoding.UTF8.GetString(lineBuffer.ToArray(), 0, len);
                            var entry = ParseLine(line);
                            if (entry != null)
                                LogEntryDetected?.Invoke(entry);

                            consumedPosition = chunkStart + i + 1;
                            lineBuffer.Clear();
                        }
                        else
                        {
                            lineBuffer.Add(b);
                        }
                    }
                }

                lock (_lock)
                {
                    // 読み取り中にログファイルが切り替わっていなければ位置を確定する。
                    // 切替時は OnNewFileCreated が _lastPosition = 0 にリセット済みなので上書きしない。
                    if (filePath == _currentFilePath)
                        _lastPosition = consumedPosition;
                }
            }
            catch (IOException)
            {
                // VRChat がファイルをロック中。次回のポーリングでリトライ
            }
        }
        finally
        {
            Interlocked.Exchange(ref _readInProgress, 0);
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
                Message = LocalizationService.GetString("Log_WorldJoined") + worldName
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
                Message = LocalizationService.GetString("Log_InstanceConnected")
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
                Message = string.Format(LocalizationService.GetString("Log_PlayerJoined"), playerName)
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
                Message = string.Format(LocalizationService.GetString("Log_PlayerLeft"), playerName)
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
                Message = string.Format(LocalizationService.GetString("Log_Notification"), sender, displayType)
            };
        }

        // ── 動画再生検出 ──
        var videoMatch = LogPatterns.VideoPlaybackRegex().Match(line);
        if (videoMatch.Success)
        {
            var url = VideoInfoService.UnwrapVideoUrl(videoMatch.Groups[1].Value.Trim());
            return new LogEntry
            {
                Timestamp = timestamp,
                Type = LogEntryType.VideoUrl,
                VideoUrl = url,
                Message = LocalizationService.GetString("Log_VideoPlayback") + url
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
