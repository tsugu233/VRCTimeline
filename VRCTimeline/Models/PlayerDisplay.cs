using VRCTimeline.Helpers;

namespace VRCTimeline.Models;

/// <summary>
/// プレイヤーカード UI に表示するための表示用モデル。
/// DB エンティティ (PlayerSession) から変換して使用する。
/// </summary>
public class PlayerDisplay
{
    /// <summary>プレイヤーの表示名</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>VRChat ユーザーID。カードクリック時の検索キーとして使用</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>入室日時（再入場ありの場合は最初の入室時刻）</summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>退室日時（再入場ありの場合は最後の退室時刻、在室中なら null）</summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>
    /// 同一インスタンス内で複数回入退出した場合の各セッションの時刻範囲。
    /// 1 セッションのみの場合は省略可（JoinedAt/LeftAt を使用）。
    /// </summary>
    public List<(DateTime JoinedAt, DateTime? LeftAt)> Sessions { get; set; } = [];

    /// <summary>入室時刻の表示文字列</summary>
    public string JoinedAtText => JoinedAt.ToString(DateFormatHelper.TimeOnly);

    /// <summary>退室時刻の表示文字列</summary>
    public string LeftAtText => LeftAt?.ToString(DateFormatHelper.TimeOnly) ?? "退出不明";

    /// <summary>
    /// 入室〜退室の時間範囲表示（例: "21:00 ～ 23:30"）。
    /// 再入場により複数セッションがある場合は " | " で区切って全てを表示する
    /// （例: "21:00 ～ 22:00 | 22:30 ～ 23:30"）。
    /// </summary>
    public string TimeRange => Sessions.Count > 1
        ? string.Join("  |  ", Sessions.Select(s =>
            $"{s.JoinedAt.ToString(DateFormatHelper.TimeOnly)} ～ {(s.LeftAt?.ToString(DateFormatHelper.TimeOnly) ?? "退出不明")}"))
        : $"{JoinedAtText} ～ {LeftAtText}";
}
