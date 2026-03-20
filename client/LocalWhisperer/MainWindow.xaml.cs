using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LocalWhisperer.Pages;

namespace LocalWhisperer;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(this)));
        appWindow.Resize(new Windows.Graphics.SizeInt32(580, 690));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tray-idle.ico"));

        // Select first item (Tilkobling)
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var tag = item.Tag as string;
        var pageType = tag switch
        {
            "connection" => typeof(ConnectionPage),
            "hotkey"     => typeof(HotkeyPage),
            "model"      => typeof(ModelPage),
            "audio"      => typeof(AudioPage),
            "display"    => typeof(DisplayPage),
            "corrections"=> typeof(CorrectionsPage),
            "general"    => typeof(GeneralPage),
            "about"      => typeof(AboutPage),
            _            => typeof(ConnectionPage),
        };

        ContentFrame.Navigate(pageType);
    }
}
