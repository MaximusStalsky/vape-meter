namespace VapeMeter.Windows;

internal sealed record LaunchOptions(string DataDirectory, bool Offline)
{
    public static LaunchOptions Default => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VapeMeter"),
        false);

    public static LaunchOptions Parse(string[] args)
    {
        var options = Default;
        for (var i = 0; i < args.Length; i++)
        {
            options = args[i] switch
            {
                "--offline" => options with { Offline = true },
                "--data-dir" when i + 1 < args.Length =>
                    options with { DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[++i])) },
                _ => throw new ArgumentException("Unknown or incomplete launch option.")
            };
        }

        return options;
    }
}
