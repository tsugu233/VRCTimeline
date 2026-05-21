using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace VRCTimeline.Services;

/// <summary>
/// 動画 URL からタイトルとサムネイルを取得するサービス。
/// noembed.com API を使用し、サムネイル画像をローカルにキャッシュする。
/// </summary>
public class VideoInfoService
{
    /// <summary>
    /// HTTP クライアント（アプリケーション全体で共有）。
    /// noembed.com 応答遅延が RateLimiter (Semaphore(1,1)) で直列化された後続フェッチを
    /// ブロックし続けるのを防ぐため、既定 100 秒ではなく短いタイムアウトを設定する。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>API レート制限用セマフォ（同時リクエスト数: 1）</summary>
    private static readonly SemaphoreSlim RateLimiter = new(1, 1);

    /// <summary>サムネイルキャッシュの保存ディレクトリ</summary>
    public static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCTimeline", "cache", "thumbnails");

    /// <summary>指定 URL が YouTube の動画かどうかを判定する</summary>
    public static bool IsYouTubeUrl(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// nextnex 等のラッパー URL を実際の YouTube URL にアンラップする。
    /// クエリパラメータ "url" の値が YouTube URL であればそれを返し、
    /// それ以外は入力をそのまま返す。
    /// 例: https://nextnex.com/?url=https://www.youtube.com/watch?v=xxx
    ///     → https://www.youtube.com/watch?v=xxx
    /// </summary>
    public static string UnwrapVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (string.IsNullOrEmpty(uri.Query)) return url;

        try
        {
            var inner = HttpUtility.ParseQueryString(uri.Query)["url"];
            if (!string.IsNullOrEmpty(inner) && IsYouTubeUrl(inner))
                return inner;
        }
        catch
        {
            // クエリ解析に失敗した場合は元の URL をそのまま使う（保存をブロックしない）
        }
        return url;
    }

    /// <summary>
    /// 動画 URL からタイトルとサムネイルを取得する。
    /// サムネイルはローカルにキャッシュされ、そのパスを返す。
    /// レート制限のため、リクエスト間に 600ms の遅延を挿入する。
    /// </summary>
    public async Task<(string? Title, string? ThumbnailPath)> FetchInfoAsync(string url)
    {
        await RateLimiter.WaitAsync();
        try
        {
            await Task.Delay(600);

            var response = await Http.GetStringAsync(
                $"https://noembed.com/embed?url={Uri.EscapeDataString(url)}");

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var thumbUrl = root.TryGetProperty("thumbnail_url", out var th) ? th.GetString() : null;

            string? localPath = null;
            if (thumbUrl != null)
            {
                Directory.CreateDirectory(CacheDir);
                var fileName = GetCacheFileName(url);
                localPath = Path.Combine(CacheDir, fileName);
                if (!File.Exists(localPath))
                {
                    var imageBytes = await Http.GetByteArrayAsync(thumbUrl);
                    await File.WriteAllBytesAsync(localPath, imageBytes);
                }
            }

            return (title, localPath);
        }
        catch (Exception ex)
        {
            // noembed.com 応答エラー・タイムアウト・JSON パース失敗等。
            // 呼び出し元には null を返し続け、診断のため AppLogger に記録する。
            AppLogger.LogError(ex);
            return (null, null);
        }
        finally
        {
            RateLimiter.Release();
        }
    }

    /// <summary>URL の SHA256 ハッシュから一意なキャッシュファイル名を生成する</summary>
    private static string GetCacheFileName(string url)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        return $"{hash}.jpg";
    }

    /// <summary>使用されていないサムネイルキャッシュファイルを削除する</summary>
    public static void CleanupThumbnails(HashSet<string> pathsToKeep)
    {
        if (!Directory.Exists(CacheDir)) return;
        foreach (var file in Directory.EnumerateFiles(CacheDir))
        {
            if (!pathsToKeep.Contains(file))
            {
                try { File.Delete(file); } catch { /* キャッシュ削除はベストエフォート（次回起動時に再試行） */ }
            }
        }
    }

    /// <summary>サムネイルキャッシュフォルダごと削除する</summary>
    public static void ClearCache()
    {
        if (Directory.Exists(CacheDir))
            Directory.Delete(CacheDir, true);
    }

    /// <summary>サムネイルキャッシュの合計サイズ（バイト）を返す</summary>
    public static long GetCacheSizeBytes()
    {
        if (!Directory.Exists(CacheDir)) return 0;
        return Directory.EnumerateFiles(CacheDir).Sum(f => new FileInfo(f).Length);
    }
}
