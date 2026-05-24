using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VRCTimeline.Helpers;

/// <summary>
/// Win32 API への P/Invoke を集約する内部ヘルパー。
/// </summary>
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr handle, IntPtr min, IntPtr max);

    /// <summary>
    /// 現在プロセスのワーキングセットを Windows にトリム要求する。-1/-1 を渡すと
    /// 「可能な限り削減」を意味する。トレイ常駐アプリで Hide 直後にタスクマネージャ上の
    /// メモリ表示を下降させたい場合に使う。
    /// .NET の GC は managed heap を縮小しても OS の working set は自動では縮小しないため、
    /// この API 呼出が必要になる。失敗（権限不足等）は無視する。
    /// </summary>
    public static void TryTrimWorkingSet()
    {
        try
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            // best-effort
        }
    }
}
