using System.Runtime.InteropServices;
using VRCTimeline.Services;

namespace VRCTimeline.Helpers;

/// <summary>
/// クリップボード操作の安全なラッパー。
///
/// WPF の <c>System.Windows.Clipboard.SetDataObject(data, true)</c> は内部で
/// <c>OleFlushClipboard</c> を呼び、クリップボード監視チェーン（Windows の
/// クリップボード履歴／クラウドクリップボードや常駐クリップボード管理ツール）へ
/// 同期通知する。チェーン内に応答の遅いプロセスがあると UI スレッドがブロックされて
/// フリーズし、最終的に CLIPBRD_E_CANT_OPEN(0x800401D0) の COMException で失敗する。
/// さらに WPF 内部にも 10 回 ×100ms の Thread.Sleep リトライがあり、固まりやすい。
///
/// そこで OLE を経由せず Win32 のクリップボード API を直接呼ぶ。OleFlushClipboard を
/// 介さないためフリーズしにくく、SetClipboardData で渡したメモリは OS が所有するため
/// アプリ終了後もコピー内容は残る。
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    // クリップボードが一瞬だけ他プロセスに握られている場合に備えた軽いリトライ。
    // OleFlushClipboard を経由しないので 1 回が即座に返り、合計でも体感できない短さに収める。
    private const int MaxOpenAttempts = 10;
    private const int OpenRetryDelayMs = 10;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>
    /// テキストをクリップボードにコピーする。失敗しても例外は呼び出し側に伝播させず false を返す。
    /// </summary>
    public static bool SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            if (!TryOpenClipboard()) return false;
            try
            {
                EmptyClipboard();
                return SetUnicodeText(text);
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError(ex);
            return false;
        }
    }

    private static bool TryOpenClipboard()
    {
        for (int attempt = 1; attempt <= MaxOpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (attempt < MaxOpenAttempts) System.Threading.Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }

    private static bool SetUnicodeText(string text)
    {
        // UTF-16(各2バイト) + null 終端1文字ぶん。
        var byteCount = (UIntPtr)((text.Length + 1) * 2);
        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, byteCount);
        if (hGlobal == IntPtr.Zero) return false;

        IntPtr target = GlobalLock(hGlobal);
        if (target == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }
        try
        {
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0); // null 終端
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
        {
            // 失敗時のみ解放する（成功時はメモリの所有権が OS に移る）。
            GlobalFree(hGlobal);
            return false;
        }
        return true;
    }
}
