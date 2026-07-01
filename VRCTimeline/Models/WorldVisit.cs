namespace VRCTimeline.Models;

/// <summary>
/// VRChat のワールド訪問を記録する DB エンティティ。
/// 入退室時刻と、その訪問中のプレイヤーセッション・写真を保持する。
/// </summary>
public class WorldVisit
{
    /// <summary>主キー</summary>
    public int Id { get; set; }

    /// <summary>VRChat ワールド ID（wrld_xxx 形式）</summary>
    public string WorldId { get; set; } = string.Empty;

    /// <summary>ワールド名</summary>
    public string WorldName { get; set; } = string.Empty;

    /// <summary>インスタンス ID（再参加用、wrld_xxx:12345~region 形式）</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>ワールドに入室した日時</summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>ワールドから退室した日時（滞在中は null）</summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>
    /// ユーザーが手動で作成した訪問かどうか。
    /// ログが消えて「不明なワールド」になった写真を手動修正する際に作成される訪問を識別する。
    /// 既存データはすべて false（ログ由来）。WorldId / InstanceId は空のため再参加ボタンは無効になる。
    /// </summary>
    public bool IsManual { get; set; }

    /// <summary>この訪問中に同室したプレイヤーのセッション一覧</summary>
    public List<PlayerSession> PlayerSessions { get; set; } = [];

    /// <summary>この訪問中に撮影された写真の一覧</summary>
    public List<PhotoRecord> Photos { get; set; } = [];
}
