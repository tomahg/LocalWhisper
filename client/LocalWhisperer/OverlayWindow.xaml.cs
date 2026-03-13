using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace LocalWhisperer;

/// <summary>
/// Small always-on-top transparent overlay that shows live transcription.
/// Positioned at the bottom-right of the primary screen.
/// Click-through so it never steals focus from the target app.
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED    = 0x00080000;

    [DllImport("user32.dll")] private static extern int  GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
    [DllImport("dwmapi.dll")] private static extern int  DwmExtendFrameIntoClientArea(nint hwnd, ref Margins pMarInset);

    private struct Margins { public int Left, Right, Top, Bottom; }

    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;

    public OverlayWindow()
    {
        InitializeComponent();

        _hwnd      = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        // Remove title bar
        _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable        = false;
            p.IsMaximizable      = false;
            p.IsMinimizable      = false;
            p.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }

        // Always on top
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);

        // No activate + no taskbar button + click-through + layered (for transparency)
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE,
            ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED);

        // Extend DWM frame across the entire client area — makes the window
        // background truly transparent so rounded corners have no artifacts.
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width    = 560;
        var height   = 60;
        var margin   = 16;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + workArea.Width  - width  - margin,
            workArea.Y + workArea.Height - height - margin,
            width, height));
    }

    public void ShowListening()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TranscriptionText.Text = "Lytter...";
            RecordingDot.Opacity = 1.0;
            PositionBottomRight();
            _appWindow.Show();
        });
    }

    public void ShowText(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TranscriptionText.Text = text;
            RecordingDot.Opacity = 1.0;
            PositionBottomRight(); // re-anchor in case resolution changed
            _appWindow.Show();
        });
    }

    public void Hide()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TranscriptionText.Text = string.Empty;
            _appWindow.Hide();
        });
    }
}
