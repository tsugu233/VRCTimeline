using Microsoft.Win32;

namespace VRCTimeline.Services;

/// <summary>
/// Windows のスタートアップ登録（HKCU\...\Run）を管理する静的サービス。
/// バージョンアップで exe のパスが変わってもアプリ起動時に自動で追従する。
/// </summary>
public static class StartupRegistryService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "VRCTimeline";
    private const string StartupArg = "--startup";

    /// <summary>
    /// 設定値に従ってレジストリを同期する。
    /// 有効時は現在の exe パスと一致しない場合のみ書き換え、
    /// タスクマネージャーで無効化されている状態（StartupApproved\Run）も解除する。
    /// </summary>
    public static void Sync(bool launchOnStartup)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (launchOnStartup)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;

                var expected = $"\"{exePath}\" {StartupArg}";
                var current = key.GetValue(ValueName) as string;

                if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, expected);
                }

                ClearStartupApprovedDisabledFlag();
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // レジストリアクセス失敗時は何もしない（権限/UAC等）
        }
    }

    /// <summary>
    /// タスクマネージャーの「スタートアップ」タブで無効化された状態を解除する。
    /// StartupApproved\Run の値を削除すると、Windows は既定で有効として扱う。
    /// </summary>
    private static void ClearStartupApprovedDisabledFlag()
    {
        try
        {
            using var approvedKey = Registry.CurrentUser.OpenSubKey(
                StartupApprovedRunKeyPath, writable: true);
            if (approvedKey == null) return;

            var value = approvedKey.GetValue(ValueName);
            if (value is byte[] bytes && bytes.Length > 0 && (bytes[0] & 0x01) != 0)
            {
                // 先頭バイトの bit0 が立っていると「無効」状態。値ごと削除して有効に戻す。
                approvedKey.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 失敗は無視
        }
    }
}
