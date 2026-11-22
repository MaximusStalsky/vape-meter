using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VapeMeter.Core;

namespace VapeMeter.Android;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        var russian = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = russian;
        CultureInfo.DefaultThreadCurrentUICulture = russian;
        CultureInfo.CurrentCulture = russian;
        CultureInfo.CurrentUICulture = russian;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activities)
        {
            var directory = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Закрытый каталог Android недоступен.");
            var store = new MeterStore(Path.Combine(directory, "android.sqlite"));
            var credentials = new AndroidCredentials(Path.Combine(directory, "connection.bin"));
            var connection = new TimewebConnection();

            RequestedThemeVariant = store.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            activities.MainViewFactory = () => new PhoneShell(store, credentials, connection);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
