namespace VRCTimeline.Models;

/// <summary>
/// アプリケーション設定を保持するモデル。
/// JSON ファイルにシリアライズ/デシリアライズされる。
/// </summary>
public class AppSettings
{
    /// <summary>VRChat ログフォルダのパス</summary>
    public string VRChatLogDirectory { get; set; } = string.Empty;

    /// <summary>VRChat 写真フォルダのパス</summary>
    public string PhotoDirectory { get; set; } = string.Empty;

    /// <summary>Windows 起動時にアプリを自動起動するか</summary>
    public bool LaunchOnStartup { get; set; } = true;

    /// <summary>起動時にウィンドウを最小化するか</summary>
    public bool MinimizeOnStartup { get; set; } = true;

    /// <summary>VRChat プロセスの起動を検知してウィンドウを自動表示するか</summary>
    public bool AutoDetectVRChat { get; set; }

    /// <summary>ダークモードが有効か</summary>
    public bool IsDarkMode { get; set; } = true;

    /// <summary>UI アクセントカラーの Hex 値</summary>
    public string AccentColorHex { get; set; } = "#79A1CB";

    /// <summary>ボタンテキストカラーの Hex 値</summary>
    public string ButtonTextColorHex { get; set; } = "#262626";

    /// <summary>UI 言語コード ("ja" / "en" / "ko")。空文字の場合は初回起動時にシステム設定から自動検出する</summary>
    public string Language { get; set; } = string.Empty;
}
