namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class LayoutConfiguration
{
    public required Guid? DefaultLayoutId { get; set; }

    public static LayoutConfiguration CreateDefault()
        => new()
        {
            DefaultLayoutId = null,
        };

    public LayoutConfiguration Copy()
        => new()
        {
            DefaultLayoutId = DefaultLayoutId,
        };
}

