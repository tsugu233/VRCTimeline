namespace VRCTimeline.Models;

/// <summary>
/// VRChat で撮影されたスクリーンショットの DB エンティティ。
/// ファイルパスと撮影日時を保持し、ワールド訪問に紐づける。
/// </summary>
public class PhotoRecord
{
    /// <summary>主キー</summary>
    public int Id { get; set; }

    /// <summary>写真ファイルのフルパス</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>写真ファイル名</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>写真の撮影日時（ファイル名から解析）</summary>
    public DateTime TakenAt { get; set; }

    /// <summary>撮影時に滞在していたワールド訪問の外部キー（nullable）</summary>
    public int? WorldVisitId { get; set; }

    /// <summary>関連するワールド訪問（ナビゲーションプロパティ）</summary>
    public WorldVisit? WorldVisit { get; set; }

    /// <summary>
    /// ユーザーが手動でワールド訪問へ割り当てた写真か。false = 撮影時刻からの自動判定。
    /// 既存データはすべて false（起動時のスキーマ更新で NOT NULL DEFAULT 0 として追加される）。
    /// 「自動判定に戻す」を出すかどうかの判定と、写真カードの手動マーカー表示に使う。
    /// </summary>
    public bool IsManuallyAssigned { get; set; }
}
