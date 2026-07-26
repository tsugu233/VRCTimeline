using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Models;
using VRCTimeline.Services.LogParser;

namespace VRCTimeline.Services;

/// <summary>
/// VRChatActivityLogViewer の SQLite データベースからデータをインポートするサービス。
/// ワールド訪問履歴とプレイヤー遭遇データを本アプリの DB に変換して取り込む。
/// </summary>
public class ActivityLogImportService
{
    /// <summary>
    /// 指定された VRChatActivityLogViewer の DB ファイルからデータをインポートする。
    /// 重複する訪問（同一日時・ワールド名）はスキップされる。
    /// </summary>
    /// <param name="dbPath">インポート元の SQLite DB ファイルパス</param>
    /// <param name="progress">進捗メッセージの通知先</param>
    public async Task ImportAsync(string dbPath, IProgress<string>? progress = null)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("データベースファイルが見つかりません。", dbPath);

        progress?.Report("VRChatActivityLogViewerのデータを読み込み中...");

        // ── ソース DB からワールド入室・プレイヤー遭遇データを読み取り ──
        var worldJoins = new List<(DateTime Timestamp, string WorldId, string WorldName, string InstanceId)>();
        var playerMeets = new List<(DateTime Timestamp, string UserName)>();
        int malformedRows = 0;

        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            await conn.OpenAsync();

            // ワールド入室ログ（ActivityType = 0）
            using var joinCmd = conn.CreateCommand();
            joinCmd.CommandText = "SELECT Timestamp, WorldID, WorldName FROM ActivityLogs WHERE ActivityType = 0 AND WorldID IS NOT NULL ORDER BY Timestamp";
            using var joinReader = await joinCmd.ExecuteReaderAsync();
            while (await joinReader.ReadAsync())
            {
                try
                {
                    if (joinReader.IsDBNull(0) || joinReader.IsDBNull(1) || joinReader.IsDBNull(2))
                    {
                        malformedRows++;
                        continue;
                    }
                    if (!DateTime.TryParse(joinReader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    {
                        malformedRows++;
                        continue;
                    }
                    var worldIdFull = joinReader.GetString(1);
                    var worldName = joinReader.GetString(2);
                    var worldId = LogPatterns.ExtractWorldId(worldIdFull);
                    worldJoins.Add((ts, worldId, worldName, worldIdFull));
                }
                catch
                {
                    malformedRows++;
                }
            }
            joinReader.Close();

            // プレイヤー遭遇ログ（ActivityType = 1）
            using var playerCmd = conn.CreateCommand();
            playerCmd.CommandText = "SELECT Timestamp, UserName FROM ActivityLogs WHERE ActivityType = 1 AND UserName IS NOT NULL ORDER BY Timestamp";
            using var playerReader = await playerCmd.ExecuteReaderAsync();
            while (await playerReader.ReadAsync())
            {
                try
                {
                    if (playerReader.IsDBNull(0) || playerReader.IsDBNull(1))
                    {
                        malformedRows++;
                        continue;
                    }
                    if (!DateTime.TryParse(playerReader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    {
                        malformedRows++;
                        continue;
                    }
                    var userName = playerReader.GetString(1);
                    playerMeets.Add((ts, userName));
                }
                catch
                {
                    malformedRows++;
                }
            }
        }

        // 同一 Timestamp + ワールド名の重複入室行を 1 件に畳み込む。
        // ソース DB に稀に存在する重複レコードを残したまま処理すると、1 件目の LeftAt に
        // 2 件目の同時刻が入って滞在 0 秒の訪問になり、次の入室までのプレイヤー遭遇が
        // どの訪問にも紐づかず失われる（2 件目自体も重複チェックでスキップされる）。
        int duplicateJoins = 0;
        var dedupedJoins = new List<(DateTime Timestamp, string WorldId, string WorldName, string InstanceId)>(worldJoins.Count);
        foreach (var join in worldJoins)
        {
            if (dedupedJoins.Count > 0
                && dedupedJoins[^1].Timestamp == join.Timestamp
                && dedupedJoins[^1].WorldName == join.WorldName)
            {
                duplicateJoins++;
                continue;
            }
            dedupedJoins.Add(join);
        }
        worldJoins = dedupedJoins;

        progress?.Report($"ワールド訪問 {worldJoins.Count} 件、プレイヤー遭遇 {playerMeets.Count} 件を処理中...");

        // ── 本アプリの DB にインポート ──
        await using var db = new AppDbContext();
        int imported = 0;
        int skipped = 0;
        int failed = 0;
        int playerIdx = 0;

        for (int i = 0; i < worldJoins.Count; i++)
        {
            try
            {
                var join = worldJoins[i];
                var leftAt = i + 1 < worldJoins.Count ? worldJoins[i + 1].Timestamp : (DateTime?)null;

                // 重複チェック
                var exists = await db.WorldVisits.AnyAsync(v =>
                    v.JoinedAt == join.Timestamp && v.WorldName == join.WorldName);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                var visit = new WorldVisit
                {
                    WorldId = join.WorldId,
                    WorldName = join.WorldName,
                    InstanceId = join.InstanceId,
                    JoinedAt = join.Timestamp,
                    LeftAt = leftAt
                };
                db.WorldVisits.Add(visit);
                await db.SaveChangesAsync();

                // この訪問期間中のプレイヤー遭遇を紐づけ
                while (playerIdx < playerMeets.Count && playerMeets[playerIdx].Timestamp < join.Timestamp)
                    playerIdx++;

                var visitEnd = leftAt ?? DateTime.MaxValue;
                for (int j = playerIdx; j < playerMeets.Count && playerMeets[j].Timestamp < visitEnd; j++)
                {
                    db.PlayerSessions.Add(new PlayerSession
                    {
                        WorldVisitId = visit.Id,
                        DisplayName = playerMeets[j].UserName,
                        JoinedAt = playerMeets[j].Timestamp
                    });
                }

                await db.SaveChangesAsync();
                imported++;

                if (imported % 50 == 0)
                    progress?.Report($"インポート中... {imported}/{worldJoins.Count}");
            }
            catch
            {
                failed++;
                // EF の変更追跡をリセットして次行の処理を継続
                foreach (var entry in db.ChangeTracker.Entries().ToList())
                    entry.State = EntityState.Detached;
            }
        }

        var summary = $"完了: {imported} 件インポート、{skipped} 件スキップ（重複）";
        if (duplicateJoins > 0) summary += $"、{duplicateJoins} 件統合（重複入室行）";
        if (malformedRows > 0) summary += $"、{malformedRows} 件スキップ（不正な行）";
        if (failed > 0) summary += $"、{failed} 件失敗";
        progress?.Report(summary);
    }

}
