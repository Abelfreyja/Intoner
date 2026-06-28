using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.UI.TitleBar;

internal static class HousingTitleBarText
{
    public static string FormatContext(ObjectHousingModeState state)
        => $"{FormatSize(state.Size)} / {FormatArea(state)}";

    public static string FormatCompactContext(ObjectHousingModeState state)
        => $"{FormatCompactSize(state.Size)} / {FormatCompactArea(state)}";

    public static string FormatSize(ObjectHousingSize size)
        => size switch
        {
            ObjectHousingSize.Apartment => "Apartment",
            ObjectHousingSize.Small     => "Small",
            ObjectHousingSize.Medium    => "Medium",
            ObjectHousingSize.Large     => "Large",
            _                           => size.ToString(),
        };

    public static string FormatArea(ObjectHousingModeState state)
        => state.Size == ObjectHousingSize.Apartment
            ? "Interior"
            : state.Area switch
            {
                ObjectHousingArea.Indoor  => "Interior",
                ObjectHousingArea.Outdoor => "Exterior",
                _                         => state.Area.ToString(),
            };

    private static string FormatCompactSize(ObjectHousingSize size)
        => size switch
        {
            ObjectHousingSize.Apartment => "Apt",
            ObjectHousingSize.Small     => "S",
            ObjectHousingSize.Medium    => "M",
            ObjectHousingSize.Large     => "L",
            _                           => size.ToString(),
        };

    private static string FormatCompactArea(ObjectHousingModeState state)
        => state.Size == ObjectHousingSize.Apartment
            ? "In"
            : state.Area switch
            {
                ObjectHousingArea.Indoor  => "In",
                ObjectHousingArea.Outdoor => "Out",
                _                         => state.Area.ToString(),
            };
}

