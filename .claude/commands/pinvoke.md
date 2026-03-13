---
description: P/Invoke / Win32 interop reference for NorskTale — SendInput, keyboard hooks, correct struct definitions, LibraryImport vs DllImport
---

# P/Invoke / Win32 Interop Reference

All P/Invoke declarations go in `Helpers/NativeMethods.cs`. Use `LibraryImport` (modern, .NET 7+) over `DllImport`.

---

## LibraryImport vs DllImport

```csharp
// DllImport — legacy, still works
[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr GetForegroundWindow();

// LibraryImport — PREFERRED for .NET 7+
// Method must be: static + partial
[LibraryImport("user32.dll", SetLastError = true)]
public static partial IntPtr GetForegroundWindow();
```

**LibraryImport advantages:** source-generated (faster), AOT-compatible, compile-time safety.

---

## SendInput — Unicode Text Injection (æøå)

### Struct definitions:

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint Type;
    public INPUTUNION Data;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT Mouse;
    [FieldOffset(0)] public KEYBDINPUT Keyboard;
    [FieldOffset(0)] public HARDWAREINPUT Hardware;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort VirtualKeyCode;
    public ushort ScanCode;   // ← Unicode code point when KEYEVENTF_UNICODE
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int X, Y;
    public uint MouseData, Flags, Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT
{
    public uint Msg;
    public ushort ParamL, ParamH;
}

public const uint INPUT_KEYBOARD   = 1;
public const uint KEYEVENTF_UNICODE = 0x0004;
public const uint KEYEVENTF_KEYUP   = 0x0002;
public const uint VK_BACK           = 0x08;
public const uint VK_CONTROL        = 0x11;
public const ushort VK_V            = 0x56;

[LibraryImport("user32.dll", SetLastError = true)]
public static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
```

### Sending Unicode text (æøå safe):

```csharp
public static void SendUnicodeText(string text)
{
    var inputs = new INPUT[text.Length * 2];
    for (int i = 0; i < text.Length; i++)
    {
        ushort ch = (ushort)text[i];
        inputs[i * 2] = new INPUT {
            Type = INPUT_KEYBOARD,
            Data = new INPUTUNION {
                Keyboard = new KEYBDINPUT {
                    VirtualKeyCode = 0,   // Must be 0 for KEYEVENTF_UNICODE
                    ScanCode = ch,        // Unicode code point here
                    Flags = KEYEVENTF_UNICODE
                }
            }
        };
        inputs[i * 2 + 1] = inputs[i * 2];
        inputs[i * 2 + 1].Data.Keyboard.Flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
    }
    SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
```

### Sending backspaces (for partial text replacement):

```csharp
public static void SendBackspaces(int count)
{
    if (count <= 0) return;
    var inputs = new INPUT[count * 2];
    for (int i = 0; i < count; i++)
    {
        inputs[i * 2] = new INPUT {
            Type = INPUT_KEYBOARD,
            Data = new INPUTUNION {
                Keyboard = new KEYBDINPUT { VirtualKeyCode = VK_BACK, Flags = 0 }
            }
        };
        inputs[i * 2 + 1] = inputs[i * 2];
        inputs[i * 2 + 1].Data.Keyboard.Flags = KEYEVENTF_KEYUP;
    }
    SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
```

### Clipboard + Ctrl+V fallback (for long texts):

```csharp
public static async Task PasteViaClipboard(string text)
{
    // Save existing clipboard
    string? previous = null;
    if (Clipboard.GetContent() is DataPackageView view &&
        view.Contains(StandardDataFormats.Text))
        previous = await view.GetTextAsync();

    // Set new content
    var package = new DataPackage();
    package.SetText(text);
    Clipboard.SetContent(package);

    // Send Ctrl+V
    var inputs = new INPUT[4];
    // Ctrl down
    inputs[0] = MakeKeyInput(VK_CONTROL, 0);
    // V down
    inputs[1] = MakeKeyInput(VK_V, 0);
    // V up
    inputs[2] = MakeKeyInput(VK_V, KEYEVENTF_KEYUP);
    // Ctrl up
    inputs[3] = MakeKeyInput(VK_CONTROL, KEYEVENTF_KEYUP);
    SendInput(4, inputs, Marshal.SizeOf<INPUT>());

    // Restore clipboard after paste completes
    await Task.Delay(200);
    if (previous != null) {
        var restore = new DataPackage();
        restore.SetText(previous);
        Clipboard.SetContent(restore);
    }
}
```

---

## SetWindowsHookEx — Global Keyboard Hook

```csharp
public const int WH_KEYBOARD_LL = 13;
public const int HC_ACTION = 0;
public const int WM_KEYDOWN = 0x0100;
public const int WM_KEYUP   = 0x0101;
public const int WM_SYSKEYDOWN = 0x0104;

[StructLayout(LayoutKind.Sequential)]
public struct KBDLLHOOKSTRUCT
{
    public uint VKCode;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public IntPtr DwExtraInfo;
}

public const uint LLKHF_INJECTED = 0x10;   // Key was injected (e.g. by SendInput)
public const uint LLKHF_UP       = 0x80;   // Key is up
public const uint LLKHF_ALTDOWN  = 0x20;   // Alt is held

public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

[LibraryImport("user32.dll", SetLastError = true)]
public static partial IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

[LibraryImport("user32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
public static partial bool UnhookWindowsHookEx(IntPtr hhk);

[LibraryImport("user32.dll")]
public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

[LibraryImport("kernel32.dll")]
public static partial IntPtr GetModuleHandle(IntPtr lpModuleName); // null = current module
```

### Usage pattern:

```csharp
private HookProc _hookDelegate; // FIELD — prevents GC
private IntPtr _hookHandle;

public void Install()
{
    _hookDelegate = HookCallback;  // store before calling!
    _hookHandle = NativeMethods.SetWindowsHookEx(
        WH_KEYBOARD_LL, _hookDelegate, NativeMethods.GetModuleHandle(IntPtr.Zero), 0);
}

private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
{
    if (nCode >= HC_ACTION)
    {
        var ks = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        bool isInjected = (ks.Flags & LLKHF_INJECTED) != 0;
        bool isKeyDown = (uint)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;

        // IMPORTANT: Skip injected keys (from our own SendInput calls)
        if (!isInjected)
        {
            if (isKeyDown && ks.VKCode == /* your hotkey VK */ 0xBF /* Ctrl+Shift+Space etc */)
            {
                // Post to channel — do NOT do work here!
                _channel.Writer.TryWrite(new KeyEvent(ks.VKCode, isKeyDown));
            }
        }
    }
    // ALWAYS call next hook — never swallow keys unless intentionally blocking
    return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
}

public void Uninstall()
{
    if (_hookHandle != IntPtr.Zero)
    {
        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }
}
```

### ⚠️ Critical rules for hook callbacks:
1. **Complete in <1000ms** — Windows silently removes slow hooks
2. **Never do I/O, network calls, or inference inside the hook**
3. **Filter out injected keys** (`LLKHF_INJECTED`) to avoid feedback loops from your own `SendInput`
4. **Always call `CallNextHookEx`** — don't swallow keys unless blocking is intentional
5. **Call `SetWindowsHookEx` from the UI thread** (which has a message loop)

---

## GetForegroundWindow

```csharp
[LibraryImport("user32.dll")]
public static partial IntPtr GetForegroundWindow();

[LibraryImport("user32.dll")]
public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
```

---

## Marshalling Quick Reference

| Scenario | Correct attribute |
|----------|-------------------|
| String parameter (Win32 Unicode) | `StringMarshalling = StringMarshalling.Utf16` |
| Bool return value | `[return: MarshalAs(UnmanagedType.Bool)]` |
| Check Win32 error | `SetLastError = true` → `Marshal.GetLastWin32Error()` |
| Win32 struct | `[StructLayout(LayoutKind.Sequential)]` |
| Union struct | `[StructLayout(LayoutKind.Explicit)]` + `[FieldOffset(0)]` |
| C calling convention | `[UnmanagedCallConv(CallConvs = new[]{typeof(CallConvCdecl)})]` |

**Always call `Marshal.GetLastWin32Error()` immediately after the P/Invoke call** — other managed code can reset the error.
