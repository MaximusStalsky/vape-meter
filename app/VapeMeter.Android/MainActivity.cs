using global::Android.App;
using global::Android.Content.PM;
using global::Android.Runtime;
using global::Android.Views;
using Avalonia.Android;

namespace VapeMeter.Android;

[Application]
public sealed class AndroidApplication : AvaloniaAndroidApplication<App>
{
    public AndroidApplication(IntPtr javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
    {
    }
}

[Activity(
    Label = "Vape Meter",
    Theme = "@style/VapeMeterTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden
        | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnResume()
    {
        base.OnResume();
        if (Content is PhoneShell shell)
        {
            shell.SetSystemWindow(Window);
            shell.SetSyncActive(true);
        }
    }

    protected override void OnPause()
    {
        (Content as PhoneShell)?.SetSyncActive(false);
        base.OnPause();
    }
}
