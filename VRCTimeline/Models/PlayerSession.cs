namespace VRCTimeline.Models;

/// <summary>
/// ワールド訪問中に同室したプレイヤーの滞在記録。
/// 1人のプレイヤーが入室〜退室するまでを1セッションとして記録する。
/// </summary>
public class PlayerSession
{
    public int Id { get; set; }

    /// <summary>所属するワールド訪問の外部キー</summary>
    public int WorldVisitId { get; set; }

    /// <summary>プレイヤーの表示名（usr_xxx サフィックス除去済み）</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>VRChat ユーザーID（例: usr_xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx）。名前変更に依存しない一意識別子</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>入室日時</summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>退室日時。null の場合はまだ在室中またはログ欠損</summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>
    /// ユーザーが手動で追加した同席者かどうか。
    /// 「不明なワールド」写真の手動修正で当時のフレンドをタグ付けした際に true になる。
    /// 記憶ベースのため、遭遇統計（回数・合計時間）の集計からは除外する（プレイヤーカード表示には出す）。
    /// 既存データはすべて false（ログ由来）。
    /// </summary>
    public bool IsManual { get; set; }

    /// <summary>ナビゲーションプロパティ</summary>
    public WorldVisit WorldVisit { get; set; } = null!;
}
