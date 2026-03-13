using System.Runtime.InteropServices;

namespace LocalWhisperer.Helpers;

/// <summary>All P/Invoke declarations in one place.</summary>
internal static partial class NativeMethods
{
    // -------------------------------------------------------------------------
    // SendInput structures
    // -------------------------------------------------------------------------

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        // Union padding — MOUSEINPUT / HARDWAREINPUT are larger, pad to their size.
        private uint _padding1;
        private uint _padding2;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // -------------------------------------------------------------------------
    // Public helpers
    // -------------------------------------------------------------------------

    public static void SendUnicodeString(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        int idx = 0;
        foreach (char c in text)
        {
            inputs[idx++] = UnicodeKeyInput(c, keyUp: false);
            inputs[idx++] = UnicodeKeyInput(c, keyUp: true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendBackspaces(int count)
    {
        var inputs = new INPUT[count * 2];
        for (int i = 0; i < count; i++)
        {
            inputs[i * 2]     = VkKeyInput(VK_BACK, keyUp: false);
            inputs[i * 2 + 1] = VkKeyInput(VK_BACK, keyUp: true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendCtrlV()
    {
        INPUT[] inputs =
        [
            VkKeyInput(VK_CONTROL, keyUp: false),
            VkKeyInput(VK_V,       keyUp: false),
            VkKeyInput(VK_V,       keyUp: true),
            VkKeyInput(VK_CONTROL, keyUp: true),
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // -------------------------------------------------------------------------
    // Win32 Clipboard — works from any thread (WinRT Clipboard requires STA)
    // -------------------------------------------------------------------------

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    public static string? GetClipboardText()
    {
        if (!OpenClipboard(0)) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == 0) return null;
            var ptr = GlobalLock(h);
            if (ptr == 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static void SetClipboardText(string text)
    {
        // Allocate moveable global memory for the Unicode string (including null terminator)
        nuint bytes = (nuint)((text.Length + 1) * 2);
        var hMem = GlobalAlloc(GMEM_MOVEABLE, bytes);
        if (hMem == 0) return;

        var ptr = GlobalLock(hMem);
        if (ptr == 0) return;
        try { Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length); }
        finally { GlobalUnlock(hMem); }

        if (!OpenClipboard(0)) return;
        EmptyClipboard();
        SetClipboardData(CF_UNICODETEXT, hMem);
        // After SetClipboardData the OS owns hMem — do NOT free it
        CloseClipboard();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static INPUT UnicodeKeyInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = 0,
            wScan = c,
            dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
        },
    };

    private static INPUT VkKeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = vk,
            dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
        },
    };
}
