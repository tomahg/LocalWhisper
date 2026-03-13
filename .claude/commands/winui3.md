---
description: WinUI 3 / Windows App SDK reference — gotchas, threading, hotkeys, system tray, XAML differences from WPF
---

# WinUI 3 / Windows App SDK Reference

## Threading Model

**Use `DispatcherQueue`, NOT `Dispatcher`:**

```csharp
// WPF (OLD) — DO NOT USE
Application.Current.Dispatcher.Invoke(() => { });

// WinUI 3 (CORRECT)
DispatcherQueue.TryEnqueue(() => {
    // UI updates here
});
```

Always store a reference to the DispatcherQueue on the UI thread:
```csharp
private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
```

---

## No WndProc — Global Hotkeys

WinUI 3 does NOT expose a traditional WndProc. **Do NOT use `RegisterHotKey` with the main window.**

**RECOMMENDED: Use `SetWindowsHookEx` with `WH_KEYBOARD_LL`**

This is cleaner — no hidden HWND needed. The hook runs on the thread that calls `SetWindowsHookEx`, which must have a message loop (WinUI 3 provides this automatically on the UI thread).

```csharp
_hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookDelegate, GetModuleHandle(null), 0);
```

Key rule: Store the delegate in a field to prevent GC:
```csharp
private HookProc _hookDelegate; // MUST be a field, not a local variable

public void Install()
{
    _hookDelegate = HookProc; // Store reference BEFORE calling SetWindowsHookEx
    _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookDelegate, GetModuleHandle(null), 0);
}
```

**CRITICAL:** Hook procedures must complete in **<1000ms** on Windows 10+. If the timeout is exceeded, Windows silently removes the hook. Never do slow work (I/O, inference calls) inside the hook — post to a channel/queue instead.

**If you do need a hidden HWND** (e.g., for `RegisterHotKey` which sends `WM_HOTKEY`):
```csharp
// Register a window class and create an invisible 0×0 window
// Use this HWND for RegisterHotKey; handle WM_HOTKEY in its WndProc
// WinUI 3 main window and this helper window are independent
```

---

## MSIX Packaged App

### Required capabilities in `Package.appxmanifest`:
```xml
<Capabilities>
  <Capability Name="microphone" />          <!-- NAudio mic capture -->
  <rescap:Capability Name="runFullTrust" /> <!-- Win32 API access -->
</Capabilities>
```

### .csproj settings:
```xml
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<TargetFramework>net9.0-windows10.0.22621.0</TargetFramework>
```

### Known MSIX restrictions:
- `SendInput` works fine in packaged apps
- `SetWindowsHookEx` with `WH_KEYBOARD_LL` works in packaged apps (WinUI 3)
- File I/O: use `ApplicationData.Current.LocalFolder` for app data
- Global hooks may be blocked in some enterprise environments (can't work around this)

---

## System Tray — H.NotifyIcon.WinUI

```xml
<PackageReference Include="H.NotifyIcon.WinUI" Version="2.*" />
```

### Gotchas:
1. **Initialize AFTER the window is loaded**, not in constructor
2. **Keep the NotifyIcon reference alive** — don't let it be garbage collected
3. **Use `DispatcherQueue.TryEnqueue()`** for any tray updates from background threads
4. **Icons can disappear on theme change** — test light + dark modes
5. **MSIX startup**: add a small delay before showing tray icon to avoid timing issues

```csharp
// In App.xaml.cs — initialize after window activation
_window.Activated += (s, e) => {
    if (!_trayInitialized) {
        InitializeTrayIcon();
        _trayInitialized = true;
    }
};
```

---

## XAML Differences from WPF

| Feature | WPF | WinUI 3 |
|---------|-----|---------|
| `x:Static` | Supported | **Not supported** — use bindings or resources |
| `Dispatcher` | `Application.Current.Dispatcher` | `DispatcherQueue` |
| `Trigger` / `DataTrigger` | Supported | Use `VisualStateManager` instead |
| Attached behaviors | XAML behaviors | Use `Microsoft.Xaml.Interactivity` |
| Style implicit targeting | Supported | Supported (same) |
| `ICommand` binding | Supported | Supported (same) |

### Resource lookup:
```xml
<!-- WinUI 3 NavigationView with settings page -->
<NavigationView x:Name="NavView"
                IsSettingsVisible="False">
    <NavigationView.MenuItems>
        <NavigationViewItem Content="Tilkobling" Tag="connection"/>
    </NavigationView.MenuItems>
</NavigationView>
```

---

## Common Async Patterns

```csharp
// Getting HWND for your WinUI 3 window
var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

// Activating window from background thread
_dispatcherQueue.TryEnqueue(() => {
    window.Activate();
});

// Changing tray icon
_dispatcherQueue.TryEnqueue(() => {
    _trayIcon.Icon = new BitmapImage(new Uri("ms-appx:///Assets/tray-listening.ico"));
});
```

---

## Checklist Before Implementing

- [ ] Hook delegate stored as field (GC prevention)
- [ ] All UI updates via `DispatcherQueue.TryEnqueue()`
- [ ] Hook proc completes in <1ms (post to channel, don't do work inline)
- [ ] `microphone` capability declared in manifest
- [ ] `WindowsAppSDKSelfContained=true` in .csproj
- [ ] Tray icon initialized after window activation event
- [ ] HWND obtained via `WinRT.Interop.WindowNative.GetWindowHandle()`
