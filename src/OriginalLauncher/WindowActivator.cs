using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OriginalLauncher;

/// <summary>
/// グローバルホットキー起動時など、自プロセスが直近の入力フォーカスを持っていない状況でも
/// 確実にウィンドウを前面化・フォーカスさせるためのヘルパー。
/// 単純な Window.Activate() は Windows のフォアグラウンドロックにより黙って失敗し、
/// WPF 内部では focus 済みに見えても実際の OS 入力フォーカスは奪えていない、ということが起こる。
///
/// Alt キーの疑似押下（SetForegroundWindow の「直近の入力を伴う」要件を満たす）と、
/// フォアグラウンドスレッドとの入力キュー共有（AttachThreadInput）を併用し、
/// <paramref name="whileAttached"/> でのフォーカス設定が確定するまでアタッチを維持する。
/// </summary>
public static class WindowActivator
{
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    public static void ForceActivate(Window window, Action whileAttached)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var foreground = GetForegroundWindow();
        var foregroundThreadId = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThreadId = GetCurrentThreadId();
        var attached = foreground != hwnd
            && foregroundThreadId != 0
            && foregroundThreadId != currentThreadId
            && AttachThreadInput(currentThreadId, foregroundThreadId, true);

        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            whileAttached();
        }
        finally
        {
            // 途中で例外が起きても Alt が押しっぱなしの状態で残らないよう必ず離す。
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }
}
