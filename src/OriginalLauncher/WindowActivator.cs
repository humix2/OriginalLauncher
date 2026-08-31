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
/// SetForegroundWindow は「呼び出し元スレッドが直近に入力を受け取っている」ことを条件に
/// フォアグラウンドロックを回避できるため、そのための「直近の入力」を疑似的に作る。
/// これに Alt キーを使うと、Alt 単体の押下・解放は Windows 側で
/// システムメニュー/アクセスキー起動のジェスチャ（WM_SYSCOMMAND/SC_KEYMENU）として特別扱いされ、
/// 直後のキー入力が奪われたり、解除されるまで他ウィンドウの入力まで止まったりする不具合を招く
/// （Ctrl 等の他の修飾キーを併用しても、Alt の押下と解放の間に他のキー遷移が挟まらなければ
/// 同様に扱われるため回避になっていなかった）。
/// Shift キーはこの特別扱いの対象外（WM_SYSKEYDOWN/UP を生成しない）なので、代わりにこちらを使う。
/// さらに、SetForegroundWindow を呼ぶ前に完全に押下・解放を終わらせておくことで、
/// フォーカス変更処理の途中でキーが「押されっぱなし」のまま残るリスクも無くす。
///
/// フォアグラウンドスレッドとの入力キュー共有（AttachThreadInput）を併用し、
/// <paramref name="whileAttached"/> でのフォーカス設定が確定するまでアタッチを維持する。
/// </summary>
public static class WindowActivator
{
    private const byte VK_SHIFT = 0x10;
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

        // SetForegroundWindow の「直近の入力を伴う」要件を満たすためだけの疑似入力。
        // ここで完結させ、SetForegroundWindow の呼び出しやフォーカス設定に一切かぶせない。
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);

        try
        {
            whileAttached();
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }
}
