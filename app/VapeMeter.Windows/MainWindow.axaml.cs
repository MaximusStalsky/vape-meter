using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VapeMeter.Windows;

public sealed partial class MainWindow : Window
{
    private static readonly Geometry MaximizeIcon = Geometry.Parse("F0 M0,0 H10 V10 H0 Z M1.2,1.2 H8.8 V8.8 H1.2 Z");
    private static readonly Geometry RestoreIcon = Geometry.Parse("F0 M0,2.5 H7.6 V10 H0 Z M1.1,3.6 H6.5 V8.9 H1.1 Z M2.5,0 H10 V7.5 H8.4 V1.1 H2.5 Z");
    private static readonly Geometry MoonIcon = Geometry.Parse("M 11.9,1.05 A 7,7 0 1 0 15,13.3 6.25,6.25 0 0 1 11.9,1.05 Z");
    private static readonly Geometry SunIcon = Geometry.Parse("M7,0 H9 V3 H7 Z M7,13 H9 V16 H7 Z M0,7 H3 V9 H0 Z M13,7 H16 V9 H13 Z M2,1 L4.8,3.8 L3.8,4.8 L1,2 Z M15,2 L12.2,4.8 L11.2,3.8 L14,1 Z M2,15 L4.8,12.2 L3.8,11.2 L1,14 Z M15,14 L12.2,11.2 L11.2,12.2 L14,15 Z M8,4 A4,4 0 1 0 8,12 A4,4 0 1 0 8,4 Z");
    private readonly Action? toggleTheme;

    public MainWindow()
    {
        InitializeComponent();
        RefreshWindowControls();
        Opened += (_, _) => WindowFrame.EnableRoundedCorners(this);
    }

    public MainWindow(Control content, Action toggleTheme) : this()
    {
        this.toggleTheme = toggleTheme;
        Workspace.Content = content;
    }

    public void SetLightThemeIcon(bool light)
    {
        ShellBackdrop.IsVisible = !light;
        ThemeGlyph.Data = light ? SunIcon : MoonIcon;
        AutomationProperties.SetName(ThemeButton, light ? "Тёмная тема" : "Светлая тема");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && MaximizeGlyph is not null)
        {
            RefreshWindowControls();
        }
    }

    private void RefreshWindowControls()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Data = maximized ? RestoreIcon : MaximizeIcon;
        AutomationProperties.SetName(MaximizeButton, maximized ? "Восстановить" : "Развернуть");
        ResizeRegions.IsVisible = WindowState == WindowState.Normal;
    }

    private void ToggleTheme(object? sender, RoutedEventArgs e) => toggleTheme?.Invoke();

    private void Minimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
}
