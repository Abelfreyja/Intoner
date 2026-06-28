namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class UiConfiguration
{
    public required bool ShowSplashScreenOnStartup { get; set; }

    public static UiConfiguration CreateDefault()
        => new()
        {
            ShowSplashScreenOnStartup = true,
        };

    public UiConfiguration Copy()
        => new()
        {
            ShowSplashScreenOnStartup = ShowSplashScreenOnStartup,
        };
}

