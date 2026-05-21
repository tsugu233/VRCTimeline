using System.Diagnostics;

namespace VRCTimeline.Services;

/// <summary>
/// VRChat プロセスの起動・終了をポーリングで監視するサービス。
/// 状態が変化した際に VRChatStatusChanged イベントを発火する。
/// </summary>
public class VRChatProcessMonitor : IDisposable
{
    /// <summary>ポーリング用タイマー</summary>
    private Timer? _timer;

    /// <summary>前回チェック時の実行状態（変化検知用）</summary>
    private bool _wasRunning;

    /// <summary>VRChat の実行状態が変化したときに発火するイベント</summary>
    public event Action<bool>? VRChatStatusChanged;

    /// <summary>VRChat が現在実行中かどうか</summary>
    public bool IsVRChatRunning { get; private set; }

    /// <summary>指定間隔（秒）でプロセス監視を開始する</summary>
    public void Start(int intervalSeconds = 30)
    {
        _timer = new Timer(_ => CheckProcess(), null,
            TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
    }

    /// <summary>VRChat プロセスの存在を確認し、状態変化時にイベントを発火する</summary>
    private void CheckProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName("VRChat");
            try
            {
                IsVRChatRunning = processes.Length > 0;
                if (IsVRChatRunning != _wasRunning)
                {
                    _wasRunning = IsVRChatRunning;
                    VRChatStatusChanged?.Invoke(IsVRChatRunning);
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        catch
        {
            // プロセス列挙はアクセス権限や OS の一時状態で稀に失敗する。
            // ポーリングで再試行されるため、ここでは黙って次回ティックに委ねる。
        }
    }

    /// <summary>プロセス監視を停止する</summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>リソースを解放する</summary>
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
