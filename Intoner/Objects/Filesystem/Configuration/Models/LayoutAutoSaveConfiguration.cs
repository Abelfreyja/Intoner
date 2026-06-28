namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class LayoutAutoSaveConfiguration
{
    public const int MinimumIntervalSeconds = 15;
    public const int MaximumIntervalSeconds = 1800;
    public const int DefaultIntervalSeconds = 60;

    public required bool Enabled { get; set; }
    public required int IntervalSeconds { get; set; }

    public static LayoutAutoSaveConfiguration CreateDefault()
        => new()
        {
            Enabled = true,
            IntervalSeconds = DefaultIntervalSeconds,
        };

    public LayoutAutoSaveConfiguration Copy()
        => new()
        {
            Enabled = Enabled,
            IntervalSeconds = IntervalSeconds,
        };

    public static int ClampIntervalSeconds(int value)
        => Math.Clamp(value, MinimumIntervalSeconds, MaximumIntervalSeconds);
}

