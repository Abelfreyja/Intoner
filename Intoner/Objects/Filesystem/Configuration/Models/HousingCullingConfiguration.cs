namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class HousingCullingConfiguration
{
    public required bool DisableFurnitureDisplayCulling { get; set; }

    public static HousingCullingConfiguration CreateDefault()
        => new()
        {
            DisableFurnitureDisplayCulling = false,
        };

    public HousingCullingConfiguration Copy()
        => new()
        {
            DisableFurnitureDisplayCulling = DisableFurnitureDisplayCulling,
        };
}

