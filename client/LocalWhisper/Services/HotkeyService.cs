using System.Runtime.InteropServices;

namespace LocalWhisper.Services;

/// <summary>
/// Global keyboard hook using WH_KEYBOARD_LL (SetWindowsHookEx).
/// Supports both toggle mode and hold-to-talk mode.
/// WinUI 3 has no WndProc, so RegisterHotKey is not used.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL  = 13;
    private const int WM_KEYDOWN      = 0x0100;
    private const int WM_KEYUP        = 0x0101;
    private const int WM_SYSKEYDOWN   = 0x0104;
    private const int WM_SYSKEYUP     = 0x0105;

    // Left/right specific VK codes (what WH_KEYBOARD_LL actually reports)
    private const int VK_LSHIFT   = 0xA0;
    private const int VK_RSHIFT   = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU    = 0xA4;
    private const int VK_RMENU    = 0xA5;
    private const int VK_LWIN     = 0x5B;
    private const int VK_RWIN     = 0x5C;

    public event Action? HotkeyDown;
    public event Action? HotkeyUp;
    public event Action? EscapePressed;

    private nint _hookHandle;
    private readonly LowLevelKeyboardProc _hookProc;
    private int _watchedVk;
    private int _watchedModifiers; // bitmask: 1=Ctrl, 2=Shift, 4=Alt, 8=Win
    private volatile bool _suspended;

    // Real-time modifier tracking inside the hook
    // Bitmask: 1=Ctrl, 2=Shift, 4=Alt, 8=Win
    private volatile bool _ctrlDown;
    private volatile bool _shiftDown;
    private volatile bool _altDown;
    private volatile bool _winDown;

    public HotkeyService()
    {
        // Keep a strong reference to the delegate so the GC doesn't collect it.
        _hookProc = HookCallback;
    }

    public void Register(int virtualKey, int modifiers = 0)
    {
        _watchedVk        = virtualKey;
        _watchedModifiers = modifiers;

        if (_hookHandle != 0) return;

        using var module = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(module.ModuleName), 0);
    }

    public void Update(int newVirtualKey, int modifiers = 0)
    {
        _watchedVk        = newVirtualKey;
        _watchedModifiers = modifiers;
        _keyIsDown        = false;
    }

    public void Unregister()
    {
        if (_hookHandle == 0) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = 0;
    }

    private const int VK_ESCAPE = 0x1B;

    // Suppress key-repeat: WH_KEYBOARD_LL fires for every repeat while held
    private bool _keyIsDown;

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && !_suspended)
        {
            var vk     = Marshal.ReadInt32(lParam);
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

            // Track modifier state (WH_KEYBOARD_LL reports specific left/right VK codes)
            if      (vk is VK_LCONTROL or VK_RCONTROL) _ctrlDown  = isDown;
            else if (vk is VK_LSHIFT   or VK_RSHIFT)   _shiftDown = isDown;
            else if (vk is VK_LMENU    or VK_RMENU)    _altDown   = isDown;
            else if (vk is VK_LWIN     or VK_RWIN)     _winDown   = isDown;

            if (_watchedVk == 0)
            {
                // Modifier-only hotkey — fire when modifier state transitions to exact match
                bool matches = ModifiersExactlyMatch();
                if (isDown && matches && !_keyIsDown)
                {
                    _keyIsDown = true;
                    HotkeyDown?.Invoke();
                }
                else if (isUp && _keyIsDown && !matches)
                {
                    _keyIsDown = false;
                    HotkeyUp?.Invoke();
                }
            }
            else if (vk == _watchedVk)
            {
                if (isDown && !_keyIsDown && ModifiersExactlyMatch())
                {
                    _keyIsDown = true;
                    HotkeyDown?.Invoke();
                }
                else if (isUp)
                {
                    _keyIsDown = false;
                    HotkeyUp?.Invoke();
                }
            }

            if (vk == VK_ESCAPE && isDown)
                EscapePressed?.Invoke();
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>Temporarily disables hotkey firing (e.g. while the user is capturing a new hotkey).</summary>
    public void Suspend() => _suspended = true;
    public void Resume()  => _suspended = false;

    private bool ModifiersExactlyMatch() =>
        ((_watchedModifiers & 1) != 0) == _ctrlDown  &&
        ((_watchedModifiers & 2) != 0) == _shiftDown &&
        ((_watchedModifiers & 4) != 0) == _altDown   &&
        ((_watchedModifiers & 8) != 0) == _winDown;

    public void Dispose() => Unregister();

    // -------------------------------------------------------------------------
    // P/Invoke
    // -------------------------------------------------------------------------

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        nint hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
