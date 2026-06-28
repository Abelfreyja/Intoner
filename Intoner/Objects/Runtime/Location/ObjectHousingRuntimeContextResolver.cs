using Dalamud.Plugin.Services;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

internal sealed class ObjectHousingRuntimeContextResolver(
    IDataManager gameData,
    NativePlacementQuery nativePlacementQuery)
{
    public ObjectHousingRuntimeContext Resolve(uint territoryId)
    {
        NativeHousingPlacementState nativeState = nativePlacementQuery.ResolveCurrentHousingState();
        ObjectHousingPlotContext? plot = TryResolveOutdoorPlot(
            territoryId,
            nativeState.Block,
            out ObjectHousingPlotContext resolvedPlot)
            ? resolvedPlot
            : null;
        HousingPlacementSizeResult size = ResolveHousingSize(
            territoryId,
            nativeState.CurrentArea,
            nativeState.Block,
            plot);

        return new ObjectHousingRuntimeContext(
            nativeState.CurrentArea,
            size.Size,
            nativeState.Block.Id,
            nativeState.Block.Source,
            size.Source,
            plot,
            nativeState.HasCollisionScene);
    }

    private HousingPlacementSizeResult ResolveHousingSize(
        uint territoryId,
        ObjectHousingArea? currentArea,
        HousingPlacementBlock block,
        ObjectHousingPlotContext? plot)
        => currentArea switch
        {
            ObjectHousingArea.Indoor  => ResolveIndoorSize(territoryId),
            ObjectHousingArea.Outdoor => ResolveOutdoorSize(block, plot),
            _                         => HousingPlacementSizeResult.Unavailable,
        };

    private HousingPlacementSizeResult ResolveIndoorSize(uint territoryId)
        => ObjectHousingTerritoryUtility.TryResolveIndoorHousingSize(gameData, territoryId, out ObjectHousingSize currentSize)
            ? new HousingPlacementSizeResult(currentSize, HousingPlacementSizeSource.IndoorTerritory)
            : HousingPlacementSizeResult.Unavailable;

    private static HousingPlacementSizeResult ResolveOutdoorSize(
        HousingPlacementBlock block,
        ObjectHousingPlotContext? plot)
    {
        HousingPlacementSizeSource source = block.Source switch
        {
            HousingPlacementBlockSource.PlayerMapRange => HousingPlacementSizeSource.MapRangeBlock,
            HousingPlacementBlockSource.CurrentPlot    => HousingPlacementSizeSource.CurrentPlot,
            _                                          => HousingPlacementSizeSource.None,
        };
        if (plot is not { } resolvedPlot
            || source == HousingPlacementSizeSource.None)
        {
            return HousingPlacementSizeResult.Unavailable;
        }

        return new HousingPlacementSizeResult(
            ObjectHousingAddress.GetSize(resolvedPlot.District, resolvedPlot.Plot),
            source);
    }

    private static bool TryResolveOutdoorPlot(
        uint territoryId,
        HousingPlacementBlock block,
        out ObjectHousingPlotContext plotContext)
    {
        plotContext = default;
        if (territoryId == 0
            || block.Id is not { } blockId
            || !TryConvertNativePlotIndex(blockId, out int plot)
            || !ObjectHousingAddress.TryResolveDistrict(territoryId, out ObjectHousingDistrict district))
        {
            return false;
        }

        plotContext = new ObjectHousingPlotContext(district, plot);
        return true;
    }

    private static bool TryConvertNativePlotIndex(int nativePlotIndex, out int plot)
    {
        plot = 0;
        if (nativePlotIndex is < 0 or >= 60)
        {
            return false;
        }

        plot = nativePlotIndex + 1;
        return true;
    }
}

