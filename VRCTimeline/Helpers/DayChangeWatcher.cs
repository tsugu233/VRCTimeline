using System.Windows.Threading;

namespace VRCTimeline.Helpers;

/// <summary>
/// 日付がまたいだタイミングでコールバックを呼び出す軽量タイマー。
/// 常駐アプリで「今日」依存の値（例: 期間フィルタの終了日）を最新化するために使用する。
/// IDisposable を実装しており、所有者 (通常は ViewModel) の Dispose で停止できる。
/// </summary>
public sealed class DayChangeWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private DateTime _lastDate;

    public DayChangeWatcher(Action onDayChanged)
    {
        _lastDate = DateTime.Today;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) =>
        {
            var today = DateTime.Today;
            if (today == _lastDate) return;
            _lastDate = today;
            onDayChanged();
        };
        _timer.Start();
    }

    /// <summary>タイマーを停止する。複数回呼んでも安全。</summary>
    public void Dispose()
    {
        _timer.Stop();
    }
}
