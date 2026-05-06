using System.Text.Json;
using VRCTimeline.Models;

namespace VRCTimeline.Services;

/// <summary>
/// アプリケーション設定の読み込み・保存を管理するサービス。
/// 設定は AppData フォルダ内の JSON ファイルに永続化される。
/// </summary>
public class SettingsService
{
    /// <summary>アプリケーション設定ファイルの保存ディレクトリ</summary>
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCTimeline");

    /// <summary>設定 JSON ファイルのフルパス</summary>
    private static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");

    /// <summary>JSON シリアライズオプション（整形出力有効）</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>並行 SaveAsync をシリアル化するためのロック</summary>
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>現在の設定値</summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// 直近の LoadAsync で設定ファイルの破損を検知し、バックアップにリネームしたかを示す。
    /// 起動側で一度だけユーザに通知するために参照される。
    /// </summary>
    public bool LoadCorruptionDetected { get; private set; }

    /// <summary>破損検知時に作成したバックアップファイルのフルパス</summary>
    public string? CorruptionBackupPath { get; private set; }

    /// <summary>設定保存時に発火するイベント</summary>
    public event Action? SettingsChanged;

    /// <summary>設定ファイルを読み込む。未設定のパスにはデフォルト値を補填する。</summary>
    public async Task LoadAsync()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(SettingsPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new JsonException("Settings deserialized to null");
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            {
                // JSON パース失敗 → 破損とみなしてバックアップを作成し、デフォルトで起動
                CorruptionBackupPath = TryBackupCorruptFile();
                LoadCorruptionDetected = true;
                Settings = new AppSettings();
            }
            // IOException 等は意図的に再スローしない: 一時的な競合の可能性があり、
            // 上書き保存で正常設定を破壊するリスクがあるためデフォルトに落とす。
            catch (IOException)
            {
                Settings = new AppSettings();
            }
            catch (UnauthorizedAccessException)
            {
                Settings = new AppSettings();
            }
        }

        if (string.IsNullOrEmpty(Settings.VRChatLogDirectory))
            Settings.VRChatLogDirectory = GetDefaultLogDirectory();

        if (string.IsNullOrEmpty(Settings.PhotoDirectory))
            Settings.PhotoDirectory = GetDefaultPhotoDirectory();
    }

    /// <summary>
    /// 破損した設定ファイルを settings.json.corrupt-{yyyyMMddHHmmss}.bak としてコピーする。
    /// 失敗しても呼び出し側の起動を妨げないよう例外は飲み込む。
    /// </summary>
    private static string? TryBackupCorruptFile()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var dest = SettingsPath + $".corrupt-{stamp}.bak";
            File.Copy(SettingsPath, dest, overwrite: true);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 現在の設定を JSON ファイルに保存し、変更イベントを発火する。
    /// SemaphoreSlim で同時呼び出しをシリアル化し、temp ファイル + Move でアトミックに置換する。
    /// </summary>
    public async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(AppDataDir);

            // Serialize は同期で完了させ、ファイル I/O 中に Settings の変更が反映されないようスナップショットを確定する。
            var json = JsonSerializer.Serialize(Settings, JsonOptions);

            var tmp = SettingsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }

        SettingsChanged?.Invoke();
    }

    /// <summary>VRChat ログフォルダのデフォルトパスを返す</summary>
    public static string GetDefaultLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "VRChat", "VRChat");
    }

    /// <summary>VRChat 写真フォルダのデフォルトパスを返す</summary>
    public static string GetDefaultPhotoDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "VRChat");
    }
}
