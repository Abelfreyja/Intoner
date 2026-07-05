using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.Api;

internal sealed record LayoutTransferContext(
    ObjectHousingArea Area,
    ObjectHousingSize Size,
    ObjectHousingPlotBasis? PlotBasis)
{
    public HousingFurnitureArea FurnitureArea
        => Area == ObjectHousingArea.Outdoor
            ? HousingFurnitureArea.Outdoor
            : HousingFurnitureArea.Indoor;

    public string AreaLabel
        => HousingFurnitureAreaPolicy.FormatArea(Area);

    public string OppositeAreaLabel
        => HousingFurnitureAreaPolicy.FormatArea(OppositeArea);

    public string ScopeLabel
        => $"{HouseSize} {AreaLabel}";

    public string HouseSize
        => Size switch
        {
            ObjectHousingSize.Apartment => "Apartment",
            ObjectHousingSize.Small     => "Small",
            ObjectHousingSize.Medium    => "Medium",
            ObjectHousingSize.Large     => "Large",
            _                           => string.Empty,
        };

    private ObjectHousingArea OppositeArea
        => Area == ObjectHousingArea.Outdoor
            ? ObjectHousingArea.Indoor
            : ObjectHousingArea.Outdoor;

    public static bool TryResolve(
        ObjectRuntimeLocationContext location,
        string formatName,
        string operationName,
        out LayoutTransferContext context,
        out string errorMessage)
    {
        context = null!;
        if (location.Housing.CurrentArea is not { } area
            || location.Housing.CurrentSize is not { } size)
        {
            errorMessage = $"{formatName} furniture {operationName} requires standing in an indoor housing territory or inside an outdoor housing plot.";
            return false;
        }

        if (area == ObjectHousingArea.Outdoor && location.Housing.PlotBasis is null)
        {
            errorMessage = $"{formatName} exterior {operationName} requires standing inside the target outdoor housing plot.";
            return false;
        }

        context = new LayoutTransferContext(area, size, location.Housing.PlotBasis);
        errorMessage = string.Empty;
        return true;
    }
}
