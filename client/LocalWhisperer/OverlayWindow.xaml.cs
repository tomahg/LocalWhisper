using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace LocalWhisperer;

public sealed partial class OverlayWindow : Window
{
    private const int GWL_STYLE          = -16;
    private const int GWL_EXSTYLE       = -20;
    private const int WS_POPUP          = unchecked((int)0x80000000);
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")] private static extern int  GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern int  SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("gdi32.dll")]  private static extern nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);
    [DllImport("dwmapi.dll")] private static extern int  DwmSetWindowAttribute(nint hwnd, uint attr, ref int pvAttr, uint cbAttr);

    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int  DWMWCP_DONOTROUND              = 1;
    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;
    private string _lastResult = string.Empty;

    public OverlayWindow()
    {
        InitializeComponent();

        _hwnd      = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        // Bare popup — no title bar, no border, no chrome
        SetWindowLong(_hwnd, GWL_STYLE, WS_POPUP);

        // Always on top
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);

        // No activate + no taskbar button + click-through
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE,
            ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);

        // Prevent DWM from adding its own corner rounding on top of ours
        int noRound = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));

        PositionBottomRight(width: 360, height: 44);
    }

    // -------------------------------------------------------------------------
    // Public state transitions
    // -------------------------------------------------------------------------

    public void ShowListening()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetClickThrough(true);
            ListeningPanel.Visibility  = Visibility.Visible;
            ProcessingPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility     = Visibility.Collapsed;
            AudioLevelBar.Value        = 0;
            PositionBottomRight(width: 360, height: 44);
            _appWindow.Show();
        });
    }

    public void ShowProcessing()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetClickThrough(true);
            ListeningPanel.Visibility  = Visibility.Collapsed;
            ProcessingPanel.Visibility = Visibility.Visible;
            ResultPanel.Visibility     = Visibility.Collapsed;
            PositionBottomRight(width: 260, height: 44);
            _appWindow.Show();
        });
    }

    public void ShowResult(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lastResult = text;
            ResultText.Text = text;
            SetClickThrough(false); // buttons must be clickable
            ListeningPanel.Visibility  = Visibility.Collapsed;
            ProcessingPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility     = Visibility.Visible;
            PositionBottomRight(width: 560, height: 120);
            _appWindow.Show();
        });
    }

    public void UpdateAudioLevel(float level)
    {
        DispatcherQueue.TryEnqueue(() => AudioLevelBar.Value = level);
    }

    public void Hide()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lastResult = string.Empty;
            _appWindow.Hide();
        });
    }

    // -------------------------------------------------------------------------
    // Button handlers
    // -------------------------------------------------------------------------

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(_lastResult);
        _appWindow.Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _appWindow.Hide();
    }

    public void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SetClickThrough(bool clickThrough)
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (clickThrough)
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
        else
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
    }

    private void PositionBottomRight(int width, int height)
    {
        var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        const int margin = 16;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + workArea.Width  - width  - margin,
            workArea.Y + workArea.Height - height - margin,
            width, height));

        // Clip the window to a rounded rectangle — corners simply don't exist,
        // so there are no background-colour corner artifacts.
        uint dpi    = GetDpiForWindow(_hwnd);
        int  radius = (int)(8.0 * dpi / 96.0);
        // CreateRoundRectRgn takes the ellipse width/height (diameter), not radius
        SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2), true);
    }
}
