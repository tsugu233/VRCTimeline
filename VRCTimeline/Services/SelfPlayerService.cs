using VRCTimeline.Services.LogParser;

namespace VRCTimeline.Services;

/// <summary>
/// ログファイルから自分自身のプレイヤー名とユーザーIDを特定するサービス。
/// 判定は次の優先順で行う:
///   1. ログイン行（"User Authenticated:" → 無ければ "post login checks for"）から表示名を取得し、
///      行に usr_xxx があればそれを、無ければ続く同名の OnPlayerJoined 行から UserId を取得する。
///   2. どのログファイルにもログイン行が無い場合は、1回の起動（= 1ログファイル）中の
///      全訪問に必ず入室している UserId を自分とみなす。自分の居ない訪問は存在しないため、
///      この共通集合が 1人に絞れればそれが自分。2人以上残る場合は誤判定を避けて採用しない。
/// 結果はキャッシュされ、アプリ起動中は再解析しない。
/// </summary>
public class SelfPlayerService
{
    private readonly SettingsService _settingsService;

    /// <summary>解析済みの自プレイヤー表示名</summary>
    private string? _cachedName;

    /// <summary>解析済みの自プレイヤーユーザーID</summary>
    private string? _cachedUserId;

    public SelfPlayerService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>自分のプレイヤー表示名を返す。未取得の場合はログファイルを解析する</summary>
    public async Task<string> GetSelfPlayerNameAsync()
    {
        if (!string.IsNullOrEmpty(_cachedName))
            return _cachedName;

        await Task.Run(FindSelfFromLog);
        return _cachedName ?? "";
    }

    /// <summary>自分の VRChat ユーザーIDを返す。未取得の場合はログファイルを解析する</summary>
    public async Task<string> GetSelfUserIdAsync()
    {
        if (_cachedUserId != null)
            return _cachedUserId;

        await Task.Run(FindSelfFromLog);
        return _cachedUserId ?? "";
    }

    /// <summary>1 ログファイルの解析結果</summary>
    /// <param name="LoginName">ログイン行から得た表示名。ログイン行が無ければ null</param>
    /// <param name="LoginUserId">ログイン行（または続く OnPlayerJoined）から得た UserId。不明なら空文字</param>
    /// <param name="PresentInAllVisits">全訪問に入室していた唯一のプレイヤー。1 人に絞れなければ null</param>
    private sealed record LogScanResult(
        string? LoginName,
        string LoginUserId,
        (string Name, string UserId)? PresentInAllVisits);

    /// <summary>
    /// ログファイルを新しい順に走査し、自プレイヤーの表示名とユーザーIDを特定する。
    /// ログイン行がいずれかのファイルにあればそれを優先し、どのファイルにも無い場合に限り
    /// 「全訪問に居るプレイヤー」で判定した結果のうち最も新しいファイルのものを採用する。
    /// </summary>
    private void FindSelfFromLog()
    {
        var logDir = _settingsService.Settings.VRChatLogDirectory;
        if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
            return;

        var logFiles = Directory.GetFiles(logDir, "output_log_*.txt")
            .OrderByDescending(f => new FileInfo(f).LastWriteTime);

        (string Name, string UserId)? presentInAllVisits = null;

        foreach (var logFile in logFiles)
        {
            try
            {
                var scan = ScanLogFile(logFile);
                if (scan.LoginName != null)
                {
                    _cachedName = scan.LoginName;
                    _cachedUserId = scan.LoginUserId;
                    return;
                }

                // ログイン行が無いファイルは全行を読み終えているため、追加の読み込みなしで得られる。
                // 新しいファイルの結果を優先する（起動直後でまだ訪問の無い最新ファイル等は null のまま次へ）。
                presentInAllVisits ??= scan.PresentInAllVisits;
            }
            catch { continue; }
        }

        if (presentInAllVisits is { } self)
        {
            _cachedName = self.Name;
            _cachedUserId = self.UserId;
        }
    }

    /// <summary>
    /// 1 ログファイルを先頭から読み、ログイン行による判定と「全訪問に居るプレイヤー」の集計を同時に行う。
    /// ログイン行から UserId まで確定した時点で読み込みを打ち切る。
    /// </summary>
    private static LogScanResult ScanLogFile(string logFile)
    {
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? loginName = null;

        // 「全訪問に居るプレイヤー」判定用
        HashSet<string>? common = null;            // これまでの全訪問に入室していた UserId の共通集合
        HashSet<string>? currentVisit = null;      // 現在の訪問で入室を確認した UserId
        var latestNames = new Dictionary<string, string>(); // UserId → このファイル内での最新の表示名

        // 入室者を 1 人も確認できなかった訪問（入室直後のクラッシュ等）は判定に使わない
        void CloseVisit()
        {
            if (currentVisit is { Count: > 0 })
            {
                if (common == null) common = currentVisit;
                else common.IntersectWith(currentVisit);
            }
            currentVisit = null;
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (loginName == null)
            {
                var loginMatch = LogPatterns.UserAuthenticatedRegex().Match(line);
                if (!loginMatch.Success)
                    loginMatch = LogPatterns.PostLoginChecksRegex().Match(line);
                if (loginMatch.Success)
                {
                    var rawLogin = loginMatch.Groups[1].Value.Trim();
                    loginName = LogPatterns.CleanPlayerName(rawLogin);
                    var loginUserId = LogPatterns.ExtractUserId(rawLogin);
                    if (!string.IsNullOrEmpty(loginUserId))
                        return new LogScanResult(loginName, loginUserId, null);
                    continue;
                }
            }

            if (LogPatterns.EnteringRoomRegex().IsMatch(line))
            {
                CloseVisit();
                currentVisit = [];
                continue;
            }

            var joinMatch = LogPatterns.PlayerJoinedRegex().Match(line);
            if (joinMatch.Success)
            {
                var rawName = joinMatch.Groups[1].Value.Trim();
                var cleanName = LogPatterns.CleanPlayerName(rawName);
                var userId = LogPatterns.ExtractUserId(rawName);

                // ログイン行に UserId が無かった場合: ログイン名と一致する最初の OnPlayerJoined から取得
                if (loginName != null && cleanName == loginName)
                    return new LogScanResult(loginName, userId, null);

                if (currentVisit != null && !string.IsNullOrEmpty(userId))
                {
                    currentVisit.Add(userId);
                    latestNames[userId] = cleanName;
                }
            }
        }

        // ログイン行はあったが自分の OnPlayerJoined が無い場合は表示名だけ返す
        if (loginName != null)
            return new LogScanResult(loginName, "", null);

        CloseVisit();
        if (common is { Count: 1 })
        {
            var selfUserId = common.First();
            return new LogScanResult(null, "", (latestNames[selfUserId], selfUserId));
        }
        return new LogScanResult(null, "", null);
    }
}
