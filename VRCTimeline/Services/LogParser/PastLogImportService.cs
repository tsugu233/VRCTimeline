using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Models;
using VRCTimeline.Services;

namespace VRCTimeline.Services.LogParser;

/// <summary>過去ログから解析された 1 プレイヤーの滞在セッション。</summary>
public sealed class ParsedPlayerSession
{
    public string DisplayName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
}

/// <summary>過去ログから解析された 1 回のワールド訪問。</summary>
public sealed class ParsedVisit
{
    public string WorldId { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// 退室時刻。
    /// ファイル末尾で閉じるため、解析後は必ず値が入る（未閉のまま DB に入れない）。
    /// </summary>
    public DateTime LeftAt { get; set; }

    public List<ParsedPlayerSession> Sessions { get; } = [];
}

/// <summary>過去ログから解析された通知受信。</summary>
public sealed class ParsedNotification
{
    public DateTime ReceivedAt { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string NotificationType { get; set; } = string.Empty;
    public ParsedVisit? Visit { get; set; }
}

/// <summary>過去ログから解析された動画再生。</summary>
public sealed class ParsedVideo
{
    public DateTime DetectedAt { get; set; }
    public string Url { get; set; } = string.Empty;
    public ParsedVisit? Visit { get; set; }
}

/// <summary>Parse の結果（DB 反映前の中間表現）。</summary>
public sealed class PastLogParseResult
{
    public List<ParsedVisit> Visits { get; } = [];
    public List<ParsedNotification> Notifications { get; } = [];
    public List<ParsedVideo> Videos { get; } = [];

    /// <summary>フォルダ内で見つかった output_log_*.txt の総数</summary>
    public int TotalFiles { get; set; }

    /// <summary>VRChat のログとして認識できなかった（タイムスタンプ行が皆無・読取エラー）ファイル数</summary>
    public int InvalidFiles { get; set; }
}

/// <summary>Apply の結果集計。</summary>
public sealed class PastLogImportSummary
{
    public int AddedVisits { get; set; }
    public int OverwrittenVisits { get; set; }
    public int SkippedVisits { get; set; }
    public int AddedNotifications { get; set; }
    public int AddedVideos { get; set; }
}

/// <summary>
/// 過去の VRChat ログファイル（退避したものを含む）を一括で DB に取り込むサービス。
/// リアルタイム監視 (<see cref="LogWatcher"/>) が動いていなかった期間の履歴を後から補完する。
///
/// 2 段階で動作する:
///   1. Parse   — フォルダ内の output_log_*.txt を解析し、訪問・セッション等の中間表現を作る（DB 未変更）
///   2. Apply   — 既存訪問（JoinedAt + WorldName 一致）との重複を解決しながら DB へ反映する
/// 呼び出し側は Parse と Apply の間で CountDuplicatesAsync を使い、上書き可否をユーザーに確認できる。
///
/// リアルタイム監視や訪問クローズ処理（未閉訪問の検出）と干渉しないよう、取り込む訪問の LeftAt は
/// 必ず設定する（各ファイルの末尾で最終ログ時刻により閉じる）。
/// VRChat 実行中の呼び出しは想定しない（呼び出し側でブロックする）。
/// </summary>
public class PastLogImportService
{
    /// <summary>これより古いタイムスタンプはデータ破損とみなして無視する（VRChat 正式リリース以前）</summary>
    private static readonly DateTime MinValidTimestamp = new(2016, 1, 1);

    /// <summary>
    /// 1 行の最大バイト数。
    /// 超えた行はログ以外のデータ（バイナリ等）とみなして破棄する。
    /// </summary>
    private const int MaxLineBytes = 1024 * 1024;

    /// <summary>
    /// フォルダ内の全 output_log_*.txt を解析し、中間表現を返す。
    /// DB は変更しない。
    /// VRChat のログとして認識できないファイルはスキップし、InvalidFiles に計上する。
    /// </summary>
    public PastLogParseResult Parse(string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new PastLogParseResult();
        if (!Directory.Exists(folder)) return result;

        var files = Directory.GetFiles(folder, "output_log_*.txt")
            .OrderBy(f => new FileInfo(f).CreationTime)
            .ToList();
        result.TotalFiles = files.Count;

        var maxValidTimestamp = DateTime.Now.AddDays(1);

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(files[i]);
            progress?.Report(string.Format(
                LocalizationService.GetString("Str_PastLogScanning"), i + 1, files.Count, fileName));

            try
            {
                if (!ParseFile(files[i], result, maxValidTimestamp, ct))
                    result.InvalidFiles++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.InvalidFiles++;
            }
        }

        DeduplicateParsedVisits(result);
        result.Visits.Sort((a, b) => a.JoinedAt.CompareTo(b.JoinedAt));
        return result;
    }

    /// <summary>
    /// 同一ログのコピーが別ファイル名で混在した場合等の、解析結果内の重複訪問
    /// （同一 JoinedAt + WorldName）を最初の 1 件に畳み込む。
    /// 落とした訪問を参照している通知・動画は残した訪問へ付け替える。
    /// </summary>
    private static void DeduplicateParsedVisits(PastLogParseResult result)
    {
        var byKey = new Dictionary<(DateTime, string), ParsedVisit>();
        var remap = new Dictionary<ParsedVisit, ParsedVisit>();
        var unique = new List<ParsedVisit>(result.Visits.Count);

        foreach (var v in result.Visits)
        {
            var key = (v.JoinedAt, v.WorldName);
            if (byKey.TryGetValue(key, out var kept))
            {
                remap[v] = kept;
            }
            else
            {
                byKey[key] = v;
                unique.Add(v);
            }
        }

        if (remap.Count == 0) return;

        result.Visits.Clear();
        result.Visits.AddRange(unique);
        foreach (var n in result.Notifications)
            if (n.Visit != null && remap.TryGetValue(n.Visit, out var kept)) n.Visit = kept;
        foreach (var v in result.Videos)
            if (v.Visit != null && remap.TryGetValue(v.Visit, out var kept)) v.Visit = kept;
    }

    /// <summary>
    /// 1 ファイルを解析して result に追記する。
    /// 戻り値は「VRChat のログとして認識できたか」（タイムスタンプ付きの行が 1 行でもあったか）。
    /// 過去ログは追記が終わっているため、末尾の改行なし行も 1 行として処理する。
    /// ファイル末尾で開いたままの訪問・セッションは最終ログ時刻で閉じる。
    /// </summary>
    private static bool ParseFile(string filePath, PastLogParseResult result, DateTime maxValidTimestamp, CancellationToken ct)
    {
        ParsedVisit? currentVisit = null;
        string? lastVideoUrl = null;
        DateTime? lastTimestamp = null;
        bool sawTimestamp = false;

        // 現在の訪問と未閉セッションを指定時刻で閉じる（JoinedAt より前にはしない）
        void CloseCurrentVisit(DateTime at)
        {
            if (currentVisit == null) return;
            var closeAt = at < currentVisit.JoinedAt ? currentVisit.JoinedAt : at;
            currentVisit.LeftAt = closeAt;
            foreach (var s in currentVisit.Sessions.Where(s => s.LeftAt == null))
                s.LeftAt = closeAt;
            currentVisit = null;
        }

        void ProcessLine(string line)
        {
            var tsMatch = LogPatterns.TimestampRegex().Match(line);
            if (!tsMatch.Success) return;
            if (!DateTime.TryParseExact(tsMatch.Groups[1].Value, LogPatterns.TimestampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                return;
            // 実在しえない日時はファイル破損・偽装データとみなして無視
            if (ts < MinValidTimestamp || ts > maxValidTimestamp) return;

            sawTimestamp = true;
            lastTimestamp = ts;

            var roomMatch = LogPatterns.EnteringRoomRegex().Match(line);
            if (roomMatch.Success)
            {
                CloseCurrentVisit(ts);
                currentVisit = new ParsedVisit
                {
                    WorldName = roomMatch.Groups[1].Value.Trim(),
                    JoinedAt = ts,
                    LeftAt = ts
                };
                result.Visits.Add(currentVisit);
                lastVideoUrl = null;
                return;
            }

            var instanceMatch = LogPatterns.JoiningInstanceRegex().Match(line);
            if (instanceMatch.Success && currentVisit != null)
            {
                var fullId = instanceMatch.Groups[1].Value.Trim();
                currentVisit.InstanceId = fullId;
                currentVisit.WorldId = LogPatterns.ExtractWorldId(fullId);
                return;
            }

            var joinMatch = LogPatterns.PlayerJoinedRegex().Match(line);
            if (joinMatch.Success && currentVisit != null)
            {
                var rawName = joinMatch.Groups[1].Value.Trim();
                currentVisit.Sessions.Add(new ParsedPlayerSession
                {
                    DisplayName = LogPatterns.CleanPlayerName(rawName),
                    UserId = LogPatterns.ExtractUserId(rawName),
                    JoinedAt = ts
                });
                return;
            }

            var leftMatch = LogPatterns.PlayerLeftRegex().Match(line);
            if (leftMatch.Success && currentVisit != null)
            {
                var rawName = leftMatch.Groups[1].Value.Trim();
                var userId = LogPatterns.ExtractUserId(rawName);
                var playerName = LogPatterns.CleanPlayerName(rawName);
                var session = currentVisit.Sessions
                    .Where(s => (!string.IsNullOrEmpty(userId) ? s.UserId == userId : s.DisplayName == playerName)
                                && s.LeftAt == null)
                    .OrderByDescending(s => s.JoinedAt)
                    .FirstOrDefault();
                if (session != null)
                    session.LeftAt = ts;
                return;
            }

            var notifMatch = LogPatterns.NotificationRegex().Match(line);
            if (notifMatch.Success)
            {
                var sender = LogPatterns.CleanPlayerName(notifMatch.Groups[1].Value.Trim());
                var notifType = notifMatch.Groups[2].Value.Trim();
                if (notifType is "invite" or "requestInvite" or "boop")
                {
                    result.Notifications.Add(new ParsedNotification
                    {
                        ReceivedAt = ts,
                        SenderName = sender,
                        NotificationType = notifType,
                        Visit = currentVisit
                    });
                }
                return;
            }

            var videoMatch = LogPatterns.VideoPlaybackRegex().Match(line);
            if (videoMatch.Success)
            {
                var url = VideoInfoService.UnwrapVideoUrl(videoMatch.Groups[1].Value.Trim());
                if (url != lastVideoUrl)
                {
                    result.Videos.Add(new ParsedVideo
                    {
                        DetectedAt = ts,
                        Url = url,
                        Visit = currentVisit
                    });
                    lastVideoUrl = url;
                }
            }
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var lineBytes = new List<byte>(1024);
        var readBuffer = new byte[64 * 1024];
        bool skippingOverlongLine = false;
        int bytesRead;

        while ((bytesRead = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            for (int i = 0; i < bytesRead; i++)
            {
                byte b = readBuffer[i];
                if (b == (byte)'\n')
                {
                    if (!skippingOverlongLine)
                    {
                        int len = lineBytes.Count;
                        if (len > 0 && lineBytes[len - 1] == (byte)'\r') len--;
                        ProcessLine(Encoding.UTF8.GetString(lineBytes.ToArray(), 0, len));
                    }
                    lineBytes.Clear();
                    skippingOverlongLine = false;
                }
                else if (!skippingOverlongLine)
                {
                    if (lineBytes.Count >= MaxLineBytes)
                    {
                        // 異常に長い行はログではないデータとみなし、次の改行まで読み捨てる
                        lineBytes.Clear();
                        skippingOverlongLine = true;
                    }
                    else
                    {
                        lineBytes.Add(b);
                    }
                }
            }
        }

        // 末尾の改行なし行（過去ログでは完成済みの行）を処理する
        if (!skippingOverlongLine && lineBytes.Count > 0)
        {
            int len = lineBytes.Count;
            if (len > 0 && lineBytes[len - 1] == (byte)'\r') len--;
            ProcessLine(Encoding.UTF8.GetString(lineBytes.ToArray(), 0, len));
        }

        // ファイル末尾 = そのセッションの終端。
        // 最終ログ時刻で訪問を閉じる。
        if (currentVisit != null)
            CloseCurrentVisit(lastTimestamp ?? currentVisit.JoinedAt);

        return sawTimestamp;
    }

    /// <summary>
    /// 解析結果のうち、既存の訪問（JoinedAt + WorldName 一致）と重複する件数を返す。
    /// 呼び出し側はこの値で上書き確認ダイアログを出すか判断する。
    /// </summary>
    public async Task<int> CountDuplicatesAsync(PastLogParseResult result, CancellationToken ct = default)
    {
        if (result.Visits.Count == 0) return 0;
        var existing = await LoadExistingVisitIndexAsync(result, ct);
        return result.Visits.Count(v => existing.ContainsKey((v.JoinedAt, v.WorldName)));
    }

    /// <summary>
    /// 解析結果を DB へ反映する。
    /// 既存と重複する訪問は overwriteDuplicates に応じて上書き（Id 保持・非手動セッションの再作成）
    /// またはスキップする。
    /// 通知・動画は内容一致（時刻＋内容）で重複を除外して追加する。
    /// </summary>
    public async Task<PastLogImportSummary> ApplyAsync(
        PastLogParseResult result, bool overwriteDuplicates,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var summary = new PastLogImportSummary();
        await using var db = new AppDbContext();

        var existing = await LoadExistingVisitIndexAsync(result, ct, db);

        // 通知・動画の紐付け先を解決するため、ParsedVisit → DB 上の WorldVisit.Id を記録する
        var visitDbIds = new Dictionary<ParsedVisit, int>();

        int processed = 0;
        foreach (var v in result.Visits)
        {
            ct.ThrowIfCancellationRequested();

            if (existing.TryGetValue((v.JoinedAt, v.WorldName), out var existingId))
            {
                if (overwriteDuplicates)
                {
                    var dbVisit = await db.WorldVisits
                        .Include(x => x.PlayerSessions)
                        .FirstAsync(x => x.Id == existingId, ct);

                    if (!string.IsNullOrEmpty(v.InstanceId))
                    {
                        dbVisit.InstanceId = v.InstanceId;
                        dbVisit.WorldId = v.WorldId;
                    }
                    dbVisit.LeftAt = v.LeftAt;
                    // ログという実測データで裏付けられたので手動フラグは下ろす
                    dbVisit.IsManual = false;

                    // 手動タグ付け（IsManual）のセッションは記憶ベースの独自情報なので残し、
                    // ログ由来セッションのみログの内容で作り直す
                    foreach (var s in dbVisit.PlayerSessions.Where(s => !s.IsManual).ToList())
                        db.PlayerSessions.Remove(s);
                    foreach (var ps in v.Sessions)
                        dbVisit.PlayerSessions.Add(ToDbSession(ps));

                    await db.SaveChangesAsync(ct);
                    summary.OverwrittenVisits++;
                }
                else
                {
                    summary.SkippedVisits++;
                }
                visitDbIds[v] = existingId;
            }
            else
            {
                var dbVisit = new WorldVisit
                {
                    WorldId = v.WorldId,
                    WorldName = v.WorldName,
                    InstanceId = v.InstanceId,
                    JoinedAt = v.JoinedAt,
                    LeftAt = v.LeftAt
                };
                foreach (var ps in v.Sessions)
                    dbVisit.PlayerSessions.Add(ToDbSession(ps));

                db.WorldVisits.Add(dbVisit);
                await db.SaveChangesAsync(ct);
                summary.AddedVisits++;
                visitDbIds[v] = dbVisit.Id;
            }

            processed++;
            if (processed % 20 == 0)
            {
                progress?.Report(string.Format(
                    LocalizationService.GetString("Str_PastLogApplying"), processed, result.Visits.Count));
            }
        }

        await ApplyNotificationsAsync(db, result, visitDbIds, summary, ct);
        await ApplyVideosAsync(db, result, visitDbIds, summary, ct);

        return summary;
    }

    private static PlayerSession ToDbSession(ParsedPlayerSession ps) => new()
    {
        DisplayName = ps.DisplayName,
        UserId = ps.UserId,
        JoinedAt = ps.JoinedAt,
        LeftAt = ps.LeftAt
    };

    /// <summary>
    /// 解析結果の期間内にある既存訪問の (JoinedAt, WorldName) → Id 索引を作る。
    /// 同一キーの既存行が複数ある場合は最初の 1 件を採用する。
    /// </summary>
    private static async Task<Dictionary<(DateTime, string), int>> LoadExistingVisitIndexAsync(
        PastLogParseResult result, CancellationToken ct, AppDbContext? sharedDb = null)
    {
        var index = new Dictionary<(DateTime, string), int>();
        if (result.Visits.Count == 0) return index;

        var min = result.Visits.Min(v => v.JoinedAt);
        var max = result.Visits.Max(v => v.JoinedAt);

        var db = sharedDb ?? new AppDbContext();
        try
        {
            var rows = await db.WorldVisits
                .Where(v => v.JoinedAt >= min && v.JoinedAt <= max)
                .Select(v => new { v.Id, v.JoinedAt, v.WorldName })
                .ToListAsync(ct);
            foreach (var r in rows)
                index.TryAdd((r.JoinedAt, r.WorldName), r.Id);
        }
        finally
        {
            if (sharedDb == null) await db.DisposeAsync();
        }
        return index;
    }

    /// <summary>通知を内容一致（受信時刻＋送信者＋種別）で重複除外しつつ追加する。</summary>
    private static async Task ApplyNotificationsAsync(
        AppDbContext db, PastLogParseResult result,
        Dictionary<ParsedVisit, int> visitDbIds, PastLogImportSummary summary, CancellationToken ct)
    {
        if (result.Notifications.Count == 0) return;

        var min = result.Notifications.Min(n => n.ReceivedAt);
        var max = result.Notifications.Max(n => n.ReceivedAt);
        var seen = (await db.NotificationRecords
                .Where(n => n.ReceivedAt >= min && n.ReceivedAt <= max)
                .Select(n => new { n.ReceivedAt, n.SenderName, n.NotificationType })
                .ToListAsync(ct))
            .Select(n => (n.ReceivedAt, n.SenderName, n.NotificationType))
            .ToHashSet();

        foreach (var n in result.Notifications)
        {
            // Add が false = 既存 DB か今回の取り込み内で登録済み
            if (!seen.Add((n.ReceivedAt, n.SenderName, n.NotificationType))) continue;

            db.NotificationRecords.Add(new NotificationRecord
            {
                ReceivedAt = n.ReceivedAt,
                SenderName = n.SenderName,
                NotificationType = n.NotificationType,
                WorldVisitId = n.Visit != null && visitDbIds.TryGetValue(n.Visit, out var id) ? id : null
            });
            summary.AddedNotifications++;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>動画再生を内容一致（検出時刻＋URL）で重複除外しつつ追加する。</summary>
    private static async Task ApplyVideosAsync(
        AppDbContext db, PastLogParseResult result,
        Dictionary<ParsedVisit, int> visitDbIds, PastLogImportSummary summary, CancellationToken ct)
    {
        if (result.Videos.Count == 0) return;

        var min = result.Videos.Min(v => v.DetectedAt);
        var max = result.Videos.Max(v => v.DetectedAt);
        var seen = (await db.VideoRecords
                .Where(v => v.DetectedAt >= min && v.DetectedAt <= max)
                .Select(v => new { v.DetectedAt, v.Url })
                .ToListAsync(ct))
            .Select(v => (v.DetectedAt, v.Url))
            .ToHashSet();

        foreach (var v in result.Videos)
        {
            if (!seen.Add((v.DetectedAt, v.Url))) continue;

            db.VideoRecords.Add(new VideoRecord
            {
                DetectedAt = v.DetectedAt,
                Url = v.Url,
                WorldVisitId = v.Visit != null && visitDbIds.TryGetValue(v.Visit, out var id) ? id : null
            });
            summary.AddedVideos++;
        }
        await db.SaveChangesAsync(ct);
    }
}
