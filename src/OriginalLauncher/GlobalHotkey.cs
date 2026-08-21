using System.Runtime.InteropServices;
using System.Windows.Input;

namespace OriginalLauncher;

/// <summary>
/// config.json で設定可能なグローバル起動キー。WH_KEYBOARD_LL でトリガーキーを監視し、
/// 必要な修飾キーが揃った状態での押下だけを検出して起動イベントを発火する。
/// 条件が揃った押下/離上は握りつぶし、他アプリに渡さない。
/// 修飾キーが揃っていない場合はそのトリガーキー自身の通常入力を妨げない。
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_CAPITAL = 0x14;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // GetKeyState は呼び出しスレッドのメッセージキューが最後に処理したメッセージ時点のスナップショット
    // であり、他プロセス/他ウィンドウ宛てに配送されたキーイベントを見逃すと古い状態のまま固着し得る。
    // 修飾キーのリアルタイム状態が必要なので GetAsyncKeyState を使う。
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly uint _triggerVk;
    private readonly ModifierKeys _requiredModifiers;

    // ネイティブ側からコールバックされ続けるため、GC に回収されないよう参照を保持する。
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookHandle;
    private bool _intercepting;
    private bool _disposed;

    public event Action? Triggered;

    public GlobalHotkey(HotkeyConfig config)
    {
        _triggerVk = ResolveVirtualKey(config.Key);
        _requiredModifiers = ParseModifiers(config.Modifiers);
        _proc = HookCallback;
    }

    public void Install()
    {
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to install the global hotkey keyboard hook.");
        }
    }

    private static uint ResolveVirtualKey(string keyName)
    {
        if (string.Equals(keyName, "CapsLock", StringComparison.OrdinalIgnoreCase))
        {
            return VK_CAPITAL;
        }

        if (Enum.TryParse<Key>(keyName, ignoreCase: true, out var key))
        {
            return (uint)KeyInterop.VirtualKeyFromKey(key);
        }

        throw new ArgumentException($"Unknown hotkey key name in config.json: '{keyName}'");
    }

    private static ModifierKeys ParseModifiers(IEnumerable<string> modifiers)
    {
        var result = ModifierKeys.None;
        foreach (var name in modifiers)
        {
            if (Enum.TryParse<ModifierKeys>(name, ignoreCase: true, out var modifier))
            {
                result |= modifier;
            }
        }

        return result;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == _triggerVk)
            {
                var message = (int)wParam;

                if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (_intercepting)
                    {
                        // 押しっぱなしによるオートリピート。継続して握りつぶす。
                        return (IntPtr)1;
                    }

                    if (CurrentModifiers() == _requiredModifiers)
                    {
                        _intercepting = true;
                        Triggered?.Invoke();
                        return (IntPtr)1;
                    }

                    // 必要な修飾キーが揃っていない場合は通常のキー入力として素通しする。
                }
                else if (message is WM_KEYUP or WM_SYSKEYUP)
                {
                    // 離上は握りつぶさず必ず素通しする。CapsLock 等のトグルキーで離上まで
                    // 握りつぶすと、OS 側が「押されたまま」と誤認して次回以降の押下が
                    // 正しい離上→再押下として扱われず、トグルが固着することがあるため。
                    _intercepting = false;
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var result = ModifierKeys.None;
        if (IsDown(VK_MENU)) result |= ModifierKeys.Alt;
        if (IsDown(VK_CONTROL)) result |= ModifierKeys.Control;
        if (IsDown(VK_SHIFT)) result |= ModifierKeys.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) result |= ModifierKeys.Windows;
        return result;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
