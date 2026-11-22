using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace VapeMeter.Windows;

internal static class WindowFrame
{
    private const int WindowCornerPreference = 33;
    private const int Round = 2;

    public static void EnableRoundedCorners(Window window)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            && window.TryGetPlatformHandle() is { } platform)
        {
            var preference = Round;
            _ = DwmSetWindowAttribute(platform.Handle, WindowCornerPreference, ref preference, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int length);
}
