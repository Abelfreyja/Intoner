using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;

namespace Intoner.Objects.Runtime;

internal static class HousingFurnitureAreaPolicy
{
    public static bool AllowsArea(HousingFurnitureMetadata metadata, ObjectHousingArea area)
        => area switch
        {
            ObjectHousingArea.Indoor  => metadata.IsIndoor,
            ObjectHousingArea.Outdoor => metadata.IsOutdoor,
            _                         => false,
        };

    public static string FormatArea(ObjectHousingArea? area)
        => area switch
        {
            ObjectHousingArea.Indoor  => "indoor",
            ObjectHousingArea.Outdoor => "outdoor",
            _                         => "unknown",
        };

    public static string FormatArea(HousingFurnitureMetadata metadata)
        => metadata.Area switch
        {
            HousingFurnitureArea.Indoor  => "indoor",
            HousingFurnitureArea.Outdoor => "outdoor",
            _                            => "unknown",
        };

    public static string FormatSize(ObjectHousingSize? size)
        => size switch
        {
            ObjectHousingSize.Apartment => "apartment",
            ObjectHousingSize.Small     => "small",
            ObjectHousingSize.Medium    => "medium",
            ObjectHousingSize.Large     => "large",
            _                           => "unknown",
        };
}

