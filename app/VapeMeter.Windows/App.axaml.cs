using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VapeMeter.Core;
using VapeMeter.UI;

namespace VapeMeter.Windows;

public sealed partial class App : Application
{
    private static readonly Uri SyncEndpoint = new("https://vapemeter.stalsky.org/");
    internal static LaunchOptions Options { get; set; } = LaunchOptions.Default;
    internal static EventWaitHandle? ActivationSignal { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Directory.CreateDirectory(Options.DataDirectory);
            var store = new MeterStore(Path.Combine(Options.DataDirectory, "windows.sqlite"));
            var preferences = new WindowPreferences(Options.DataDirectory);
            RequestedThemeVariant = store.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

            Func<CancellationToken, Task<SyncStatus>>? synchronize = null;
            if (!Options.Offline && WindowsCredentials.GetSyncToken() is { } token)
            {
                var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                {
                    Timeout = TimeSpan.FromSeconds(12)
                };
                var client = new MeterSync(store, http, SyncEndpoint, token);
                synchronize = client.SyncAsync;
                desktop.Exit += (_, _) => http.Dispose();
            }

            var main = new MainView(store, synchronize, useHostBackdrop: true,
                getCloseToTray: () => preferences.CloseToTray, setCloseToTray: preferences.SetCloseToTray);
            var window = new MainWindow(main, main.ToggleTheme);
            window.SetLightThemeIcon(main.IsLightTheme);
            main.ThemeChanged += (_, _) => window.SetLightThemeIcon(main.IsLightTheme);
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            if (ActivationSignal is not null)
            {
                _ = new WindowsTray(this, desktop, window, ActivationSignal, () => preferences.CloseToTray);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
