using System.Windows;
using Microsoft.Data.Sqlite;

namespace VRCTimeline.Services;

/// <summary>
/// 例外ハンドラの統一窓口。ファイル出力は行わず、SqliteException 検出時のみ
/// セッション中 1 回だけ UI に通知する（連発によるスパムを抑止）。
/// </summary>
public static class AppLogger
{
    private static int _dbErrorNotified;

    public static void LogError(Exception ex)
    {
        if (ex is SqliteException && Interlocked.Exchange(ref _dbErrorNotified, 1) == 0)
            NotifyDbErrorOnce();
    }

    private static void NotifyDbErrorOnce()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var title = LocalizationService.GetString("Str_DbErrorTitle");
                    var template = LocalizationService.GetString("Str_DbErrorMessage");
                    var message = template.Replace("\\n", Environment.NewLine);
                    MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            }));
        }
        catch { }
    }
}
