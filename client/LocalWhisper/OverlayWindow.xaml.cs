using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LocalWhisper;

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

    private static readonly HashSet<string> AllowedExtensions =
        [".wav", ".mp3", ".m4a", ".ogg", ".flac", ".webm", ".wma", ".aac"];

    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;
    private readonly Models.AppSettings _settings;
    private string _lastResult = string.Empty;
    private DispatcherTimer? _processingTimer;
    private DateTime _processingStart;

    /// <summary>Raised when the user drops or picks an audio file.</summary>
    public event Action<string>? FileSelected;

    public OverlayWindow()
    {
        InitializeComponent();

        _hwnd      = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _settings  = App.Services.GetRequiredService<Models.AppSettings>();

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

        PositionOverlay(width: 360, height: 44);
    }

    // -------------------------------------------------------------------------
    // Public state transitions
    // -------------------------------------------------------------------------

    public void ShowListening()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopProcessingTimer();
            SetClickThrough(false);  // allow drag-drop onto listening panel
            SetNoActivate(true);
            ListeningPanel.Visibility      = Visibility.Visible;
            ListeningTextPanel.Visibility  = Visibility.Collapsed;
            ProcessingPanel.Visibility     = Visibility.Collapsed;
            ResultPanel.Visibility         = Visibility.Collapsed;
            AudioLevelBarClip.Rect         = new Windows.Foundation.Rect(0, 0, 0, 6);
            PositionOverlay(width: 360, height: 44);
            _appWindow.Show();
        });
    }

    public void ShowListeningWithText(string text)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopProcessingTimer();
            SetClickThrough(false); // Kopier button must be clickable
            SetNoActivate(true);

            SetTextWithLineBreaks(AccumulatedText, text);
            ListeningPanel.Visibility      = Visibility.Collapsed;
            ListeningTextPanel.Visibility  = Visibility.Visible;
            ProcessingPanel.Visibility     = Visibility.Collapsed;
            ResultPanel.Visibility         = Visibility.Collapsed;

            // Measure content to size window
            const int width = 500;
            const int maxHeight = 400;
            const int headerHeight = 44; // listening indicator row
            const int padding = 28;      // border padding + margins
            var contentGrid = (Microsoft.UI.Xaml.Controls.Grid)ListeningTextPanel.Child;
            contentGrid.Measure(new Windows.Foundation.Size(width - 28, double.PositiveInfinity));
            int height = Math.Clamp((int)contentGrid.DesiredSize.Height + padding, headerHeight + 20, maxHeight);

            PositionOverlay(width, height);
            _appWindow.Show();
        });
    }

    public void ShowProcessing()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetClickThrough(true);
            SetNoActivate(true);

            _processingStart = DateTime.UtcNow;
            ProcessingTimer.Text = "";
            StopProcessingTimer();
            _processingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _processingTimer.Tick += ProcessingTimer_Tick;
            _processingTimer.Start();

            ListeningPanel.Visibility      = Visibility.Collapsed;
            ListeningTextPanel.Visibility  = Visibility.Collapsed;
            ProcessingPanel.Visibility     = Visibility.Visible;
            ResultPanel.Visibility         = Visibility.Collapsed;
            PositionOverlay(width: 260, height: 44);
            _appWindow.Show();
        });
    }

    private void StopProcessingTimer()
    {
        if (_processingTimer is not null)
        {
            _processingTimer.Stop();
            _processingTimer.Tick -= ProcessingTimer_Tick;
        }
    }

    private void ProcessingTimer_Tick(object? sender, object e)
    {
        var elapsed = DateTime.UtcNow - _processingStart;
        ProcessingTimer.Text = elapsed.TotalSeconds < 60
            ? $"{(int)elapsed.TotalSeconds} sek"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
    }

    public void ShowResult(string text, bool showCopy = true,
        int audioDurationMs = 0, int processingTimeMs = 0)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopProcessingTimer();
            _lastResult = text;
            SetTextWithLineBreaks(ResultText, text);
            CopyButton.Visibility = showCopy ? Visibility.Visible : Visibility.Collapsed;
            StatsText.Text = FormatStats(audioDurationMs, processingTimeMs);
            SetClickThrough(false); // buttons must be clickable
            SetNoActivate(true);

            ListeningPanel.Visibility      = Visibility.Collapsed;
            ListeningTextPanel.Visibility  = Visibility.Collapsed;
            ProcessingPanel.Visibility     = Visibility.Collapsed;
            ResultPanel.Visibility         = Visibility.Visible;

            // Measure the actual content to size the window tightly
            const int width = 560;
            const int maxHeight = 400;
            const int padding = 48; // border padding + button row + margins
            ResultContent.Measure(new Windows.Foundation.Size(width - 28, double.PositiveInfinity));
            int height = Math.Clamp((int)ResultContent.DesiredSize.Height + padding, 100, maxHeight);

            PositionOverlay(width, height);
            _appWindow.Show();
        });
    }

    /// <summary>
    /// Amplification applied to raw RMS for the level bar display only.
    /// Adjust this constant to change bar sensitivity without affecting silence detection.
    /// </summary>
    private const float LevelDisplayGain = 8f;

    public void UpdateAudioLevel(float level)
    {
        var threshold = (float)_settings.SilenceLevelThreshold;
        var displayLevel = level < threshold ? 0f : Math.Min(1f, level * LevelDisplayGain);
        DispatcherQueue.TryEnqueue(() =>
        {
            AudioLevelBarClip.Rect     = new Windows.Foundation.Rect(0, 0, AudioLevelBarContainer.ActualWidth * displayLevel, 6);
            AudioLevelBarTextClip.Rect = new Windows.Foundation.Rect(0, 0, AudioLevelBarTextContainer.ActualWidth * displayLevel, 6);
        });
    }

    public void RepositionIfVisible()
    {
        if (!_appWindow.IsVisible) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var size = _appWindow.Size;
            PositionOverlay(size.Width, size.Height);
        });
    }

    public void Hide()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StopProcessingTimer();
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

    private void AccumulatedCopyButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(AccumulatedText.Text);
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
    // File drop / picker handlers
    // -------------------------------------------------------------------------

    private void ListeningPanel_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            ListeningPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xCC, 0x2A, 0x3A, 0x4E));
            ListeningContent.Visibility = Visibility.Collapsed;
            DropHint.Visibility = Visibility.Visible;
        }
    }

    private void ListeningPanel_DragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        ListeningPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E));
        ListeningContent.Visibility = Visibility.Visible;
        DropHint.Visibility = Visibility.Collapsed;
    }

    private async void ListeningPanel_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        // Reset visuals immediately
        ListeningPanel_DragLeave(sender, e);

        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;

        if (items[0] is StorageFile file && AllowedExtensions.Contains(Path.GetExtension(file.Path).ToLowerInvariant()))
            FileSelected?.Invoke(file.Path);
    }

    private void ResultPanel_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            ResultPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xCC, 0x2A, 0x3A, 0x4E));
            ResultContent.Visibility = Visibility.Collapsed;
            ResultDropHint.Visibility = Visibility.Visible;
        }
    }

    private void ResultPanel_DragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        ResultPanel.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E));
        ResultContent.Visibility = Visibility.Visible;
        ResultDropHint.Visibility = Visibility.Collapsed;
    }

    private async void ResultPanel_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        ResultPanel_DragLeave(sender, e);

        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0) return;

        if (items[0] is StorageFile file && AllowedExtensions.Contains(Path.GetExtension(file.Path).ToLowerInvariant()))
            FileSelected?.Invoke(file.Path);
    }

    public async void OpenFilePicker()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, _hwnd);
            foreach (var ext in AllowedExtensions)
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                FileSelected?.Invoke(file.Path);
        });
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void SetTextWithLineBreaks(Microsoft.UI.Xaml.Controls.TextBlock tb, string text)
    {
        tb.Inlines.Clear();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = lines[i] });
        }
    }


    private static string FormatStats(int audioDurationMs, int processingTimeMs)
    {
        if (audioDurationMs <= 0 && processingTimeMs <= 0) return "";

        static string FormatDuration(int ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes} min {ts.Seconds} sek"
                : $"{(int)ts.TotalSeconds} sek";
        }

        if (audioDurationMs > 0 && processingTimeMs > 0)
            return $"{FormatDuration(audioDurationMs)} behandlet på {FormatDuration(processingTimeMs)}";
        if (processingTimeMs > 0)
            return $"Behandlet på {FormatDuration(processingTimeMs)}";
        return "";
    }

    private void SetNoActivate(bool noActivate)
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (noActivate)
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
        else
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
    }

    private void SetClickThrough(bool clickThrough)
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (clickThrough)
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
        else
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT);
    }

    private void PositionOverlay(int width, int height)
    {
        var workArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        const int margin = 16;

        var position = _settings.OverlayPosition;
        bool isTop = position is Models.OverlayPosition.TopLeft
                              or Models.OverlayPosition.TopCenter
                              or Models.OverlayPosition.TopRight;

        int x = position switch
        {
            Models.OverlayPosition.BottomLeft  or Models.OverlayPosition.TopLeft   => workArea.X + margin,
            Models.OverlayPosition.BottomCenter or Models.OverlayPosition.TopCenter => workArea.X + (workArea.Width - width) / 2,
            _                                                                        => workArea.X + workArea.Width - width - margin
        };
        int y = isTop
            ? workArea.Y + margin
            : workArea.Y + workArea.Height - height - margin;

        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

        // Clip the window to a rounded rectangle — corners simply don't exist,
        // so there are no background-colour corner artifacts.
        uint dpi    = GetDpiForWindow(_hwnd);
        int  radius = (int)(8.0 * dpi / 96.0);
        // CreateRoundRectRgn takes the ellipse width/height (diameter), not radius
        SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, width, height, radius * 2, radius * 2), true);
    }
}
