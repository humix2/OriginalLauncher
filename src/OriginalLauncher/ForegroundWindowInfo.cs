using System.Runtime.InteropServices;

namespace OriginalLauncher;

/// <summary>
/// フォアグラウンドウィンドウが、乗っているモニタ全域を覆う「フルスクリーン」状態かどうかを判定する。
/// ゲームなど排他/ボーダレスフルスクリーンのアプリがフォアグラウンドのとき、ホットキーを抑制するために使う。
/// </summary>
internal static class ForegroundWindowInfo
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// フォアグラウンドウィンドウの矩形が、それが乗っているモニタの矩形を覆っていれば true を返す。
    /// デスクトップ（Shell ウィンドウ）がフォアグラウンドの場合や、取得に失敗した場合は false。
    /// </summary>
    public static bool IsForegroundWindowFullscreen()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero || hWnd == GetShellWindow())
        {
            return false;
        }

        if (!GetWindowRect(hWnd, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        return windowRect.Left <= info.rcMonitor.Left
            && windowRect.Top <= info.rcMonitor.Top
            && windowRect.Right >= info.rcMonitor.Right
            && windowRect.Bottom >= info.rcMonitor.Bottom;
    }
}
