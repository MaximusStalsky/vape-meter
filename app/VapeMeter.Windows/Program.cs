using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;

namespace VapeMeter.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = LaunchOptions.Default;
        try
        {
            options = LaunchOptions.Parse(args);
            var culture = CultureInfo.GetCultureInfo("ru-RU");
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            var profileHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                options.DataDirectory.ToUpperInvariant())));
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset,
                "Local\\VapeMeter.Show." + profileHash);
            using var instance = new Mutex(true, "Local\\VapeMeter." + profileHash, out var firstInstance);
            if (!firstInstance)
            {
                activation.Set();
                return 0;
            }

            App.Options = options;
            App.ActivationSignal = activation;
            return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error)
        {
            ShowStartupError(options.DataDirectory, error);
            return 1;
        }
    }

    private static void ShowStartupError(string dataDirectory, Exception error)
    {
        var log = Path.Combine(dataDirectory, "startup-error.log");
        var message = "Не удалось открыть Vape Meter.";
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(log, error.ToString());
            message += "\n\nПодробности сохранены в " + log;
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            message += "\n\nПроверьте доступ к папке данных.";
        }

        MessageBox(IntPtr.Zero, message, "Vape Meter", 0x10);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
