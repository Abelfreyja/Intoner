using Intoner.Objects.Catalog;
using Intoner.Objects.Runtime;
using Intoner.Services.Configuration;

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
        if (location.Housing.CurrentArea == ObjectHousingArea.Outdoor && location.Housing.PlotBasis is null)
        {
            errorMessage = $"{formatName} exterior {operationName} requires an active outdoor housing plot.";
            return false;
        }

        ObjectHousingSize? currentSize = location.Housing.CurrentArea == ObjectHousingArea.Outdoor
            ? location.Housing.PlotBasis?.Size
            : location.Housing.CurrentSize;
        if (location.Housing.CurrentArea is not { } area
            || currentSize is not { } size)
        {
            errorMessage = $"{formatName} furniture {operationName} requires standing in an indoor housing territory or inside an outdoor housing plot.";
            return false;
        }

        context = new LayoutTransferContext(area, size, area == ObjectHousingArea.Outdoor ? location.Housing.PlotBasis : null);
        errorMessage = string.Empty;
        return true;
    }
}
