using Dalamud.Plugin.Services;
using Intoner.Services.Configuration;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

internal sealed class ObjectHousingRuntimeContextResolver(
    IDataManager gameData,
    NativePlacementQuery nativePlacementQuery)
{
    public ObjectHousingRuntimeContext Resolve(uint territoryId)
        => Resolve(territoryId, nativePlacementQuery.ResolveCurrentHousingState());

    public ObjectHousingRuntimeContext Resolve(uint territoryId, NativeHousingPlacementState nativeState)
    {
        ObjectHousingPlotContext? plot = nativeState.CurrentArea == ObjectHousingArea.Outdoor
            && TryResolveOutdoorPlot(
                territoryId,
                nativeState.CurrentPlotIndex,
                out ObjectHousingPlotContext resolvedPlot)
            ? resolvedPlot
            : null;
        ObjectHousingPlotBasis? plotBasis = plot is { } basisPlot
            && nativeState.CurrentPlotIndex is { } blockId
            && ObjectHousingPlotBasisTable.TryResolve(basisPlot.District, basisPlot.Plot, blockId, out ObjectHousingPlotBasis resolvedBasis)
            ? resolvedBasis
            : null;
        HousingPlacementSizeResult size = ResolveHousingSize(
            territoryId,
            nativeState.CurrentArea,
            nativeState.Block);

        return new ObjectHousingRuntimeContext(
            nativeState.CurrentArea,
            size.Size,
            nativeState.Block.Id,
            nativeState.Block.Source,
            size.Source,
            plot,
            plotBasis,
            nativeState.HasCollisionScene);
    }

    private HousingPlacementSizeResult ResolveHousingSize(
        uint territoryId,
        ObjectHousingArea? currentArea,
        HousingPlacementBlock block)
        => currentArea switch
        {
            ObjectHousingArea.Indoor  => ResolveIndoorSize(territoryId),
            ObjectHousingArea.Outdoor => ResolveOutdoorSize(territoryId, block),
            _                         => HousingPlacementSizeResult.Unavailable,
        };

    private HousingPlacementSizeResult ResolveIndoorSize(uint territoryId)
        => ObjectHousingTerritoryUtility.TryResolveIndoorHousingSize(gameData, territoryId, out ObjectHousingSize currentSize)
            ? new HousingPlacementSizeResult(currentSize, HousingPlacementSizeSource.IndoorTerritory)
            : HousingPlacementSizeResult.Unavailable;

    private static HousingPlacementSizeResult ResolveOutdoorSize(
        uint territoryId,
        HousingPlacementBlock block)
    {
        HousingPlacementSizeSource source = block.Source switch
        {
            HousingPlacementBlockSource.PlayerMapRange => HousingPlacementSizeSource.MapRangeBlock,
            HousingPlacementBlockSource.CurrentPlot    => HousingPlacementSizeSource.CurrentPlot,
            _                                          => HousingPlacementSizeSource.None,
        };
        if (source == HousingPlacementSizeSource.None
            || !TryResolveOutdoorPlot(territoryId, block.Id, out ObjectHousingPlotContext resolvedPlot))
        {
            return HousingPlacementSizeResult.Unavailable;
        }

        return new HousingPlacementSizeResult(
            ObjectHousingAddress.GetSize(resolvedPlot.District, resolvedPlot.Plot),
            source);
    }

    private static bool TryResolveOutdoorPlot(
        uint territoryId,
        byte? nativePlotIndex,
        out ObjectHousingPlotContext plotContext)
    {
        plotContext = default;
        if (territoryId == 0
            || nativePlotIndex is not { } blockId
            || !ObjectHousingPlotIndexUtility.TryConvertNativePlotIndex(blockId, out int plot)
            || !ObjectHousingAddress.TryResolveDistrict(territoryId, out ObjectHousingDistrict district))
        {
            return false;
        }

        plotContext = new ObjectHousingPlotContext(district, plot);
        return true;
    }
}

