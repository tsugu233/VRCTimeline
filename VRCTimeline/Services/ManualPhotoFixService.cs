using Microsoft.EntityFrameworkCore;
using VRCTimeline.Data;
using VRCTimeline.Helpers;
using VRCTimeline.Models;

namespace VRCTimeline.Services;

/// <summary>
/// 「不明なワールド」（ログ欠損で訪問に紐づかない）写真を手動修正するための書込サービス。
/// 手動ワールド訪問の作成、写真の訪問への割り当て直し、手動同席者の追加/削除を担う。
/// 各メソッドは自前で AppDbContext を生成する（ActivityLogImportService 同様のステートレス設計）。
/// </summary>
public class ManualPhotoFixService
{
    /// <summary>候補訪問を検索する際、写真撮影時刻の前後にどれだけ余裕を持たせるか。</summary>
    private static readonly TimeSpan CandidateMargin = TimeSpan.FromHours(6);

    /// <summary>
    /// 手動ワールド訪問を作成して ID を返す。
    /// WorldId / InstanceId は空にするため、アクティビティ画面の再参加ボタンは自動的に無効になる。
    /// </summary>
    public async Task<int> CreateManualVisitAsync(string worldName, DateTime joinedAt, DateTime? leftAt)
    {
        await using var db = new AppDbContext();
        var visit = new WorldVisit
        {
            WorldName = string.IsNullOrWhiteSpace(worldName) ? "不明なワールド" : worldName.Trim(),
            WorldId = string.Empty,
            InstanceId = string.Empty,
            JoinedAt = joinedAt,
            LeftAt = leftAt,
            IsManual = true
        };
        db.WorldVisits.Add(visit);
        await db.SaveChangesAsync();
        return visit.Id;
    }

    /// <summary>
    /// 指定したファイルパスの写真群を、指定のワールド訪問へ割り当てる。
    /// FilePath をキーにするのは、表示モデル（PhotoDisplayItem）が DB の主キーではなく FilePath を保持し、
    /// かつ FilePath が一意インデックスだから。割り当て後はもう孤立写真ではないため、
    /// RelinkOrphanPhotosAsync の自動再リンク対象外になる（上書きされない）。
    /// </summary>
    public async Task AssignPhotosToVisitAsync(IReadOnlyList<string> filePaths, int worldVisitId)
    {
        if (filePaths.Count == 0) return;
        await using var db = new AppDbContext();
        var pathSet = filePaths.ToHashSet();
        var photos = await db.PhotoRecords
            .Where(p => pathSet.Contains(p.FilePath))
            .ToListAsync();
        if (photos.Count == 0) return;
        foreach (var photo in photos)
            photo.WorldVisitId = worldVisitId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 手動の同席者セッションを訪問に追加して ID を返す。
    /// userId は空でも可（DB に存在しないフリー入力フレンド）。IsManual=true で統計から除外される。
    /// </summary>
    public async Task<int> AddManualPlayerSessionAsync(
        int worldVisitId, string displayName, string userId, DateTime joinedAt, DateTime? leftAt)
    {
        await using var db = new AppDbContext();
        var session = new PlayerSession
        {
            WorldVisitId = worldVisitId,
            DisplayName = displayName.Trim(),
            UserId = userId ?? string.Empty,
            JoinedAt = joinedAt,
            LeftAt = leftAt,
            IsManual = true
        };
        db.PlayerSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    /// <summary>
    /// 手動同席者セッションを削除する。IsManual のセッションのみ対象とし、
    /// ログ由来のセッションを誤って削除しないようガードする。
    /// </summary>
    public async Task RemoveManualPlayerSessionAsync(int sessionId)
    {
        await using var db = new AppDbContext();
        var session = await db.PlayerSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.IsManual);
        if (session == null) return;
        db.PlayerSessions.Remove(session);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 手動訪問のワールド名を変更する。IsManual の訪問のみ対象（ログ由来の実訪問名は変更不可）。
    /// 空名は「不明なワールド」に補完する。
    /// </summary>
    public async Task RenameVisitAsync(int visitId, string worldName)
    {
        await using var db = new AppDbContext();
        var visit = await db.WorldVisits.FirstOrDefaultAsync(v => v.Id == visitId && v.IsManual);
        if (visit == null) return;
        visit.WorldName = string.IsNullOrWhiteSpace(worldName) ? "不明なワールド" : worldName.Trim();
        await db.SaveChangesAsync();
    }

    /// <summary>指定訪問に紐づく手動同席者セッション (IsManual) の一覧を返す（編集ダイアログのプリロード用）。</summary>
    public async Task<List<ManualSession>> GetManualPlayerSessionsAsync(int visitId)
    {
        await using var db = new AppDbContext();
        return await db.PlayerSessions.AsNoTracking()
            .Where(s => s.WorldVisitId == visitId && s.IsManual)
            .OrderBy(s => s.Id)
            .Select(s => new ManualSession(s.Id, s.DisplayName, s.UserId))
            .ToListAsync();
    }

    /// <summary>
    /// 手動訪問の修正を取り消す。IsManual の訪問のみ対象。
    /// その訪問の写真を「不明」（WorldVisitId=null）に戻し、手動同席者セッションを削除し、訪問自体を削除する。
    /// 写真レコード・ファイルは削除しない。次回ロードで RelinkOrphanPhotos が実訪問へ自動再マッチし得る。
    /// </summary>
    public async Task UndoManualVisitAsync(int visitId)
    {
        await using var db = new AppDbContext();
        var visit = await db.WorldVisits
            .Include(v => v.Photos)
            .Include(v => v.PlayerSessions)
            .FirstOrDefaultAsync(v => v.Id == visitId && v.IsManual);
        if (visit == null) return;

        foreach (var photo in visit.Photos)
            photo.WorldVisitId = null;
        db.PlayerSessions.RemoveRange(visit.PlayerSessions);
        db.WorldVisits.Remove(visit);
        await db.SaveChangesAsync();
    }

    /// <summary>手動同席者セッションを1件以上含む訪問の ID 集合を返す（編集可否のマーキング用）。</summary>
    public async Task<HashSet<int>> GetVisitIdsWithManualSessionsAsync()
    {
        await using var db = new AppDbContext();
        var ids = await db.PlayerSessions.AsNoTracking()
            .Where(s => s.IsManual)
            .Select(s => s.WorldVisitId)
            .Distinct()
            .ToListAsync();
        return ids.ToHashSet();
    }

    /// <summary>
    /// 同席者タグ付けのオートコンプリート候補として、既知プレイヤーの (表示名, UserId) を重複なしで返す。
    /// 自分自身は除外する。UserId が空のものは表示名で一意化する。
    /// </summary>
    public async Task<List<KnownPlayer>> GetKnownPlayersAsync(string? selfUserId, string? selfName)
    {
        await using var db = new AppDbContext();
        var sessions = await db.PlayerSessions.AsNoTracking()
            .Select(s => new { s.DisplayName, s.UserId })
            .ToListAsync();

        return sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.DisplayName))
            .Where(s => !(!string.IsNullOrEmpty(selfUserId) && s.UserId == selfUserId)
                        && !(!string.IsNullOrEmpty(selfName) && s.DisplayName == selfName))
            // 表示名でまとめて1人1件にする。UserId 付き・UserId 空（旧インポート由来）の両方が
            // 存在しても重複表示しないよう、表示名キーで集約し UserId 付きを優先採用する。
            .GroupBy(s => s.DisplayName)
            .Select(g => new KnownPlayer(
                g.Key,
                g.Select(x => x.UserId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? string.Empty))
            .OrderBy(p => p.DisplayName)
            .ToList();
    }

    /// <summary>
    /// 写真の撮影時刻範囲の近傍にあるワールド訪問を候補として返す（前後に余裕を持たせる）。
    /// 厳密な入退室ウィンドウに収まる訪問は RelinkOrphanPhotos が既に自動紐づけしているため、
    /// ここでは「近いがウィンドウ外」のケース（撮影時刻のズレ等）を拾えるよう ±数時間に広げる。
    /// </summary>
    public async Task<List<CandidateVisit>> GetCandidateVisitsAsync(DateTime fromTime, DateTime toTime)
    {
        await using var db = new AppDbContext();
        var lower = fromTime - CandidateMargin;
        var upper = toTime + CandidateMargin;
        var visits = await db.WorldVisits.AsNoTracking()
            .Where(v => v.JoinedAt <= upper && (v.LeftAt == null || v.LeftAt >= lower))
            .OrderByDescending(v => v.JoinedAt)
            .Take(50)
            .Select(v => new CandidateVisit(v.Id, v.WorldName, v.JoinedAt, v.LeftAt))
            .ToListAsync();
        return visits;
    }
}

/// <summary>同席者タグ付けのオートコンプリート候補。</summary>
public record KnownPlayer(string DisplayName, string UserId);

/// <summary>既存の手動同席者セッション（編集ダイアログのプリロード用）。</summary>
public record ManualSession(int Id, string DisplayName, string UserId);

/// <summary>写真を割り当てる既存訪問の候補。</summary>
public record CandidateVisit(int Id, string WorldName, DateTime JoinedAt, DateTime? LeftAt)
{
    /// <summary>コンボボックス表示用（現在のカルチャで都度フォーマット）。</summary>
    public string Display => $"{WorldName}  ({DateFormatHelper.FormatDateWithDayAndTime(JoinedAt)})";
}
