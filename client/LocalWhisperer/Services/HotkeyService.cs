using System.Runtime.InteropServices;

namespace LocalWhisperer.Services;

/// <summary>
/// Global keyboard hook using WH_KEYBOARD_LL (SetWindowsHookEx).
/// Supports both toggle mode and hold-to-talk mode.
/// WinUI 3 has no WndProc, so RegisterHotKey is not used.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;

    public event Action? HotkeyDown;
    public event Action? HotkeyUp;
    public event Action? EscapePressed;

    private nint _hookHandle;
    private readonly LowLevelKeyboardProc _hookProc;
    private int _watchedVk;

    public HotkeyService()
    {
        // Keep a strong reference to the delegate so the GC doesn't collect it.
        _hookProc = HookCallback;
    }

    public void Register(int virtualKey)
    {
        _watchedVk = virtualKey;

        if (_hookHandle != 0) return;

        using var module = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(module.ModuleName), 0);
    }

    public void Update(int newVirtualKey)
    {
        _watchedVk = newVirtualKey;
        _keyIsDown = false;
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
        if (nCode >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            if (vk == _watchedVk)
            {
                if (wParam == WM_KEYDOWN && !_keyIsDown)
                {
                    _keyIsDown = true;
                    HotkeyDown?.Invoke();
                }
                else if (wParam == WM_KEYUP)
                {
                    _keyIsDown = false;
                    HotkeyUp?.Invoke();
                }
            }
            else if (vk == VK_ESCAPE && wParam == WM_KEYDOWN)
            {
                EscapePressed?.Invoke();
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

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
