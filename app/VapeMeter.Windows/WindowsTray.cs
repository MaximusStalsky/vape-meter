using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

namespace VapeMeter.Windows;

internal sealed class WindowsTray
{
    private readonly MainWindow window;
    private readonly Func<bool> closeToTray;
    private WindowState restoreState = WindowState.Normal;

    public WindowsTray(Application app, IClassicDesktopStyleApplicationLifetime desktop, MainWindow window,
        EventWaitHandle activationSignal, Func<bool> closeToTray)
    {
        this.window = window;
        this.closeToTray = closeToTray;

        var open = new NativeMenuItem("Открыть Vape Meter");
        open.Click += (_, _) => Restore();
        var quit = new NativeMenuItem("Выход");
        quit.Click += (_, _) => desktop.Shutdown();
        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        using var asset = AssetLoader.Open(new Uri("avares://VapeMeter/Assets/VapeMeter-Tray.ico"));
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(asset),
            ToolTipText = "Vape Meter",
            Menu = menu,
            IsVisible = true
        };
        icon.Clicked += (_, _) => Restore();
        TrayIcon.SetIcons(app, new TrayIcons { icon });

        window.PropertyChanged += WindowPropertyChanged;
        window.Closing += WindowClosing;
        var activation = ThreadPool.RegisterWaitForSingleObject(activationSignal,
            (_, _) => Dispatcher.UIThread.Post(Restore), null, Timeout.Infinite, executeOnlyOnce: false);

        desktop.Exit += (_, _) =>
        {
            activation.Unregister(null);
            window.PropertyChanged -= WindowPropertyChanged;
            window.Closing -= WindowClosing;
            TrayIcon.SetIcons(app, new TrayIcons());
            icon.Dispose();
        };
    }

    private void WindowClosing(object? sender, WindowClosingEventArgs args)
    {
        if (args.CloseReason == WindowCloseReason.WindowClosing && closeToTray())
        {
            args.Cancel = true;
            Dispatcher.UIThread.Post(Hide);
        }
    }

    private void WindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != Window.WindowStateProperty)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    Hide();
                }
            });
        }
        else
        {
            restoreState = window.WindowState;
        }
    }

    private void Hide()
    {
        if (window.IsVisible)
        {
            window.Hide();
            window.ShowInTaskbar = false;
        }
    }

    private void Restore()
    {
        if (!window.IsVisible)
        {
            window.ShowInTaskbar = true;
            window.WindowState = restoreState;
            window.Show();
        }
        else if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = restoreState;
        }
        window.Activate();
    }
}
