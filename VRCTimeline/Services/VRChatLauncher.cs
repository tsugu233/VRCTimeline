using System.Diagnostics;
using Microsoft.Win32;

namespace VRCTimeline.Services;

/// <summary>
/// VRChat のプロトコルリンク (vrchat://) を使用してインスタンスに参加するユーティリティ。
/// </summary>
public static class VRChatLauncher
{
    /// <summary>指定インスタンスIDの VRChat ワールドに参加する</summary>
    public static void LaunchInstance(string instanceId)
    {
        var url = $"vrchat://launch?id={instanceId}";

        // vrchat:// プロトコルが登録されているか確認
        if (IsVRChatProtocolRegistered())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        // プロトコル未登録の場合、launch.exe を直接起動
        var launchPath = FindVRChatLauncher();
        if (launchPath != null)
        {
            Process.Start(new ProcessStartInfo(launchPath, url) { UseShellExecute = false });
            return;
        }

        // 最終手段としてプロトコルで試行（OSのダイアログが出る可能性あり）
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>vrchat:// プロトコルがレジストリに登録されているか確認</summary>
    private static bool IsVRChatProtocolRegistered()
    {
        using var key = Registry.ClassesRoot.OpenSubKey(@"vrchat\shell\open\command");
        return key?.GetValue(null) != null;
    }

    /// <summary>VRChatフォルダ内の launch.exe のパスを検出</summary>
    private static string? FindVRChatLauncher()
    {
        var vrchatFolder = FindVRChatFolder();
        if (vrchatFolder == null) return null;

        var launchPath = Path.Combine(vrchatFolder, "launch.exe");
        return File.Exists(launchPath) ? launchPath : null;
    }

    /// <summary>VRChatインストールフォルダを検出</summary>
    private static string? FindVRChatFolder()
    {
        // Steam版のパスをレジストリから取得
        var steamPath = GetSteamVRChatPath();
        if (steamPath != null && Directory.Exists(steamPath))
            return steamPath;

        // 一般的なインストールパスを確認
        string[] commonPaths =
        [
            @"C:\Program Files (x86)\Steam\steamapps\common\VRChat",
            @"D:\Steam\steamapps\common\VRChat",
            @"D:\SteamLibrary\steamapps\common\VRChat",
            @"E:\SteamLibrary\steamapps\common\VRChat",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Steam\steamapps\common\VRChat")
        ];

        foreach (var path in commonPaths)
        {
            if (Directory.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>Steamのレジストリ情報からVRChatフォルダのパスを取得</summary>
    private static string? GetSteamVRChatPath()
    {
        // Steamのインストールパスを取得
        using var steamKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
        var steamPath = steamKey?.GetValue("InstallPath") as string;
        if (steamPath == null) return null;

        // メインのsteamappsを確認
        var mainPath = Path.Combine(steamPath, @"steamapps\common\VRChat");
        if (Directory.Exists(mainPath))
            return mainPath;

        // libraryfolders.vdf から追加ライブラリパスを読み取る
        var libraryFoldersPath = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath)) return null;

        try
        {
            var content = File.ReadAllText(libraryFoldersPath);
            // "path" "D:\\SteamLibrary" のような行を探す
            foreach (var line in content.Split('\n'))
            {
                if (!line.Contains("\"path\"")) continue;
                var parts = line.Split('"');
                if (parts.Length < 4) continue;
                var libPath = parts[3].Replace("\\\\", "\\");
                var vrchatFolder = Path.Combine(libPath, @"steamapps\common\VRChat");
                if (Directory.Exists(vrchatFolder))
                    return vrchatFolder;
            }
        }
        catch
        {
            // パース失敗は無視
        }

        return null;
    }
}
