using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Models;
using VRCTimeline.Services;

namespace VRCTimeline.Services.LogParser;

/// <summary>
/// VRChat ログファイルのバッチスキャナー。
/// 過去のログファイルを順番に読み込み、ワールド訪問・プレイヤーセッション等を DB に保存する。
/// ProcessedLogFile テーブルで処理済み位置を記録し、差分スキャンに対応。
/// </summary>
public class LogScanner
{
    /// <summary>
    /// 指定ディレクトリ内の全ログファイルをスキャンし、未処理部分を解析して DB に保存する。
    /// </summary>
    public async Task ScanAllLogsAsync(string logDirectory, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(logDirectory)) return;

        var logFiles = Directory.GetFiles(logDirectory, "output_log_*.txt")
            .OrderBy(f => new FileInfo(f).CreationTime)
            .ToList();

        await using var db = new AppDbContext();
        await db.Database.EnsureCreatedAsync(ct);

        foreach (var logFile in logFiles)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(logFile);
            progress?.Report($"スキャン中: {fileName}");

            // 前回の処理済み位置を取得（なければ先頭から）
            var processed = await db.ProcessedLogFiles
                .FirstOrDefaultAsync(p => p.FileName == fileName, ct);

            long startPosition = processed?.LastPosition ?? 0;
            var fileLength = new FileInfo(logFile).Length;

            if (startPosition >= fileLength) continue;

            // ScanFileAsync は実際に消費したバイト位置（最後に処理した \n の直後）を返す。
            // VRChat がスキャン中に追記してもその分は次回再読されるので重複しない。
            long consumedPosition = await ScanFileAsync(db, logFile, startPosition, ct);

            // 処理済み位置を更新
            if (processed == null)
            {
                db.ProcessedLogFiles.Add(new ProcessedLogFile
                {
                    FileName = fileName,
                    LastPosition = consumedPosition,
                    ProcessedAt = DateTime.Now
                });
            }
            else
            {
                processed.LastPosition = consumedPosition;
                processed.ProcessedAt = DateTime.Now;
            }

            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// 1つのログファイルを指定位置からバイト単位で読み込み、行ごとに解析して DB に保存する。
    /// 末尾の不完全行（最後の \n より後ろのバイト）は処理せず、戻り値の position にも含めない。
    /// </summary>
    /// <returns>実際に消費したバイト位置（次回スキャンの開始位置）。</returns>
    private static async Task<long> ScanFileAsync(AppDbContext db, string filePath, long startPosition, CancellationToken ct)
    {
        // 前回から継続中の未閉ワールド訪問を取得
        var ctx = new ScanContext
        {
            CurrentVisit = await db.WorldVisits
                .Include(v => v.PlayerSessions)
                .Where(v => v.LeftAt == null)
                .OrderByDescending(v => v.JoinedAt)
                .FirstOrDefaultAsync(ct),
            LastVideoUrl = await db.VideoRecords
                .OrderByDescending(v => v.DetectedAt)
                .Select(v => v.Url)
                .FirstOrDefaultAsync(ct)
        };

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = startPosition;

        long consumedPosition = startPosition;
        var lineBytes = new List<byte>(1024);
        var readBuffer = new byte[8192];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            // この read チャンクの開始時点でのファイル位置
            long chunkStart = stream.Position - bytesRead;

            for (int i = 0; i < bytesRead; i++)
            {
                byte b = readBuffer[i];
                if (b == (byte)'\n')
                {
                    // 末尾の \r を除去
                    int len = lineBytes.Count;
                    if (len > 0 && lineBytes[len - 1] == (byte)'\r') len--;

                    string line = Encoding.UTF8.GetString(lineBytes.ToArray(), 0, len);
                    await ProcessLineAsync(db, ctx, line, ct);

                    consumedPosition = chunkStart + i + 1;
                    lineBytes.Clear();
                }
                else
                {
                    lineBytes.Add(b);
                }
            }
        }

        // 末尾の不完全行（lineBytes に残っているバイト）は処理せず、
        // consumedPosition も更新しない。次回スキャン時に \n が来たら処理される。

        await db.SaveChangesAsync(ct);
        return consumedPosition;
    }

    /// <summary>
    /// スキャン中に持ち回る状態（現在のワールド訪問・直前の動画 URL・行カウンタ）。
    /// </summary>
    private sealed class ScanContext
    {
        public WorldVisit? CurrentVisit;
        public string? LastVideoUrl;
        public int LineCount;
    }

    /// <summary>
    /// 1 行を解析し、対応するイベントを DB に追加する。
    /// </summary>
    private static async Task ProcessLineAsync(AppDbContext db, ScanContext ctx, string line, CancellationToken ct)
    {
        // タイムスタンプのない行はスキップ
        var timestampMatch = LogPatterns.TimestampRegex().Match(line);
        if (!timestampMatch.Success) return;

        if (!DateTime.TryParseExact(timestampMatch.Groups[1].Value,
            LogPatterns.TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var timestamp))
            return;

        // ── ワールド入室 ──
        var roomMatch = LogPatterns.EnteringRoomRegex().Match(line);
        if (roomMatch.Success)
        {
            // 前のワールド訪問を閉じる
            if (ctx.CurrentVisit is { LeftAt: null })
            {
                ctx.CurrentVisit.LeftAt = timestamp;
                foreach (var s in ctx.CurrentVisit.PlayerSessions.Where(s => s.LeftAt == null))
                    s.LeftAt = timestamp;
            }

            ctx.CurrentVisit = new WorldVisit
            {
                WorldName = roomMatch.Groups[1].Value.Trim(),
                JoinedAt = timestamp
            };
            db.WorldVisits.Add(ctx.CurrentVisit);
            ctx.LastVideoUrl = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        // ── インスタンス接続（ワールドID・インスタンスID の補完） ──
        var instanceMatch = LogPatterns.JoiningInstanceRegex().Match(line);
        if (instanceMatch.Success && ctx.CurrentVisit != null)
        {
            var fullId = instanceMatch.Groups[1].Value.Trim();
            ctx.CurrentVisit.InstanceId = fullId;
            ctx.CurrentVisit.WorldId = LogPatterns.ExtractWorldId(fullId);
            await db.SaveChangesAsync(ct);
            return;
        }

        // ── プレイヤー入室 ──
        var joinMatch = LogPatterns.PlayerJoinedRegex().Match(line);
        if (joinMatch.Success && ctx.CurrentVisit != null)
        {
            var rawName = joinMatch.Groups[1].Value.Trim();
            ctx.CurrentVisit.PlayerSessions.Add(new PlayerSession
            {
                DisplayName = LogPatterns.CleanPlayerName(rawName),
                UserId = LogPatterns.ExtractUserId(rawName),
                JoinedAt = timestamp
            });
            ctx.LineCount++;
            if (ctx.LineCount % 50 == 0)
                await db.SaveChangesAsync(ct);
            return;
        }

        // ── プレイヤー退室（UserId 優先でセッションを照合） ──
        var leftMatch = LogPatterns.PlayerLeftRegex().Match(line);
        if (leftMatch.Success && ctx.CurrentVisit != null)
        {
            var rawName = leftMatch.Groups[1].Value.Trim();
            var userId = LogPatterns.ExtractUserId(rawName);
            var playerName = LogPatterns.CleanPlayerName(rawName);

            var session = ctx.CurrentVisit.PlayerSessions
                .Where(s => (!string.IsNullOrEmpty(userId) ? s.UserId == userId : s.DisplayName == playerName) && s.LeftAt == null)
                .OrderByDescending(s => s.JoinedAt)
                .FirstOrDefault();

            if (session != null)
                session.LeftAt = timestamp;

            ctx.LineCount++;
            if (ctx.LineCount % 50 == 0)
                await db.SaveChangesAsync(ct);
            return;
        }

        // ── 通知受信 ──
        var notifMatch = LogPatterns.NotificationRegex().Match(line);
        if (notifMatch.Success)
        {
            var sender = LogPatterns.CleanPlayerName(notifMatch.Groups[1].Value.Trim());
            var notifType = notifMatch.Groups[2].Value.Trim();
            if (notifType is "invite" or "requestInvite" or "boop")
            {
                db.NotificationRecords.Add(new NotificationRecord
                {
                    ReceivedAt = timestamp,
                    SenderName = sender,
                    NotificationType = notifType,
                    WorldVisitId = ctx.CurrentVisit?.Id
                });
                ctx.LineCount++;
                if (ctx.LineCount % 50 == 0)
                    await db.SaveChangesAsync(ct);
            }
            return;
        }

        // ── 動画再生検出 ──
        var videoMatch = LogPatterns.VideoPlaybackRegex().Match(line);
        if (videoMatch.Success)
        {
            var url = VideoInfoService.UnwrapVideoUrl(videoMatch.Groups[1].Value.Trim());
            if (url != ctx.LastVideoUrl)
            {
                var exists = await db.VideoRecords.AnyAsync(v => v.Url == url && v.DetectedAt == timestamp, ct);
                if (!exists)
                {
                    db.VideoRecords.Add(new VideoRecord
                    {
                        DetectedAt = timestamp,
                        Url = url,
                        WorldVisitId = ctx.CurrentVisit?.Id
                    });
                    ctx.LineCount++;
                    if (ctx.LineCount % 50 == 0)
                        await db.SaveChangesAsync(ct);
                }
                ctx.LastVideoUrl = url;
            }
        }
    }
}
