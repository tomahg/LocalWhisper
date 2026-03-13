using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using LocalWhisperer.Models;
using LocalWhisperer.Services;
using LocalWhisperer.ViewModels;

namespace LocalWhisperer;

public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<AppSettings>();
        services.AddSingleton<AudioCaptureService>();
        services.AddSingleton<WebSocketService>();
        services.AddSingleton<TextInjectionService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TranscriptionOrchestrator>();
        services.AddSingleton<MainViewModel>();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Activate();
    }
}
