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

    /// <summary>前回チェック時の確定実行状態（変化検知用）</summary>
    private bool _wasRunning;

    /// <summary>連続して「不在」を観測した回数（誤検知デバウンス用）</summary>
    private int _absentTicks;

    /// <summary>
    /// この回数連続で不在を観測するまで「終了」と確定しない。
    /// スリープ復帰やプロセス列挙の一時的失敗で 1 ティックだけ 0 件になる誤検知を吸収する。
    /// 誤った終了発火は HandleVRChatExited による訪問クローズ → 再検知時の再開を招き、
    /// 長時間セッションが分断・二重記録される原因になるため。
    /// </summary>
    private const int AbsentTicksThreshold = 2;

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
                bool running = processes.Length > 0;

                if (running)
                {
                    // 起動検知は即時反映（速やかに監視を開始したいため）
                    _absentTicks = 0;
                    if (!_wasRunning)
                    {
                        _wasRunning = true;
                        IsVRChatRunning = true;
                        VRChatStatusChanged?.Invoke(true);
                    }
                }
                else if (_wasRunning)
                {
                    // 終了検知はデバウンス: 連続 AbsentTicksThreshold 回不在を確認してから確定する
                    _absentTicks++;
                    if (_absentTicks >= AbsentTicksThreshold)
                    {
                        _wasRunning = false;
                        IsVRChatRunning = false;
                        VRChatStatusChanged?.Invoke(false);
                    }
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
            // 状態は変えず（誤った終了発火を防ぐ）、次回ティックに委ねる。
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
