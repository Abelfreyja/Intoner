namespace Intoner.Objects.Filesystem.Configuration;

internal enum ObjectWorkspaceMode
{
    Normal,
    Housing,
}

internal enum ObjectHousingSize
{
    Apartment,
    Small,
    Medium,
    Large,
}

internal enum ObjectHousingArea
{
    Indoor,
    Outdoor,
}

internal sealed class HousingModeConfiguration
{
    public required ObjectWorkspaceMode Mode { get; set; }
    public required ObjectHousingSize Size { get; set; }
    public required ObjectHousingArea Area { get; set; }

    public static HousingModeConfiguration CreateDefault()
        => new()
        {
            Mode = ObjectWorkspaceMode.Normal,
            Size = ObjectHousingSize.Small,
            Area = ObjectHousingArea.Indoor,
        };

    public HousingModeConfiguration Copy()
        => new()
        {
            Mode = Mode,
            Size = Size,
            Area = Area,
        };
}

