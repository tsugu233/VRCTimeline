using System.Text.RegularExpressions;

namespace VRCTimeline.Services.LogParser;

/// <summary>
/// VRChat ログファイル (output_log_*.txt) の解析に使用する正規表現パターンと
/// プレイヤー名・ワールドID の抽出ユーティリティ。
/// </summary>
public static partial class LogPatterns
{
    /// <summary>ログ行先頭のタイムスタンプ書式</summary>
    public const string TimestampFormat = "yyyy.MM.dd HH:mm:ss";

    /// <summary>ログ行先頭のタイムスタンプを抽出（例: "2026.04.30 21:00:00"）</summary>
    [GeneratedRegex(@"^(\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})")]
    public static partial Regex TimestampRegex();

    /// <summary>ワールド入室イベント。グループ1 = ワールド名</summary>
    [GeneratedRegex(@"\[Behaviour\] Entering Room: (.+)$")]
    public static partial Regex EnteringRoomRegex();

    /// <summary>インスタンス接続イベント。グループ1 = "wrld_xxx:nonce" 形式のフルID</summary>
    [GeneratedRegex(@"\[Behaviour\] Joining (wrld_[a-f0-9\-]+:.+)$")]
    public static partial Regex JoiningInstanceRegex();

    /// <summary>ユーザー認証行。グループ1 = 認証済みプレイヤー名</summary>
    [GeneratedRegex(@"User Authenticated: (.+)$")]
    public static partial Regex UserAuthenticatedRegex();

    /// <summary>
    /// ログイン後チェック行
    /// 例: "[Authentication - …] Preforming post login checks for 表示名 (usr_xxx)"
    /// グループ1 = "表示名 (usr_xxx)" 形式の生データ
    /// </summary>
    [GeneratedRegex(@"post login checks for (.+)$")]
    public static partial Regex PostLoginChecksRegex();

    /// <summary>プレイヤー入室イベント。グループ1 = "表示名 (usr_xxx)" 形式の生データ</summary>
    [GeneratedRegex(@"\[Behaviour\] OnPlayerJoined (.+)$")]
    public static partial Regex PlayerJoinedRegex();

    /// <summary>プレイヤー退室イベント。グループ1 = "表示名 (usr_xxx)" 形式の生データ</summary>
    [GeneratedRegex(@"\[Behaviour\] OnPlayerLeft (.+)$")]
    public static partial Regex PlayerLeftRegex();

    /// <summary>プレイヤー名末尾の "(usr_xxx)" サフィックス全体にマッチ（除去用）</summary>
    [GeneratedRegex(@"\s+\(usr_[0-9a-f\-]+\)$")]
    private static partial Regex UserSuffixRegex();

    /// <summary>プレイヤー名末尾の "(usr_xxx)" から usr_xxx 部分のみをキャプチャ</summary>
    [GeneratedRegex(@"\((usr_[0-9a-f\-]+)\)$")]
    private static partial Regex UserIdRegex();

    /// <summary>
    /// 生のプレイヤー名から "(usr_xxx)" サフィックスを除去して表示名を返す。
    /// 例: "PlayerName (usr_abc-123)" → "PlayerName"
    /// </summary>
    public static string CleanPlayerName(string name)
    {
        return UserSuffixRegex().Replace(name, "");
    }

    /// <summary>
    /// 生のプレイヤー名から VRChat ユーザーID を抽出する。
    /// 例: "PlayerName (usr_abc-123)" → "usr_abc-123"
    /// 見つからない場合は空文字を返す。
    /// </summary>
    public static string ExtractUserId(string rawName)
    {
        var match = UserIdRegex().Match(rawName);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>通知受信イベント。グループ1 = 送信者名, グループ2 = 通知種別</summary>
    [GeneratedRegex(@"Received Notification:.*?from username:([^,]+).*?type:(\w+)")]
    public static partial Regex NotificationRegex();

    /// <summary>動画再生検出イベント。グループ1 = 動画URL</summary>
    [GeneratedRegex(@"\[Video Playback\].*?Attempting to resolve URL '(.+?)'")]
    public static partial Regex VideoPlaybackRegex();

    /// <summary>
    /// フルインスタンスID ("wrld_xxx:nonce") からワールドID部分のみを抽出する。
    /// 例: "wrld_abc:12345~friends" → "wrld_abc"
    /// </summary>
    public static string ExtractWorldId(string fullInstanceId)
    {
        var colonIndex = fullInstanceId.IndexOf(':');
        return colonIndex > 0 ? fullInstanceId[..colonIndex] : fullInstanceId;
    }
}
