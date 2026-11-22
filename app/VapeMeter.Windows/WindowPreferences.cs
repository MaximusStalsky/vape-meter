using System.Text.Json;

namespace VapeMeter.Windows;

internal sealed class WindowPreferences
{
    private readonly string path;

    public WindowPreferences(string dataDirectory)
    {
        path = Path.Combine(dataDirectory, "window-settings.json");
        try
        {
            CloseToTray = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))?.CloseToTray ?? false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable preferences give this window its default behavior.
        }
    }

    public bool CloseToTray { get; private set; }

    public void SetCloseToTray(bool value)
    {
        if (CloseToTray == value)
        {
            return;
        }

        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Settings(value)));
            File.Move(temporary, path, overwrite: true);
            CloseToTray = value;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record Settings(bool CloseToTray);
}
