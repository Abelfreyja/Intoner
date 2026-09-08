using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Configuration;

namespace Intoner.Objects.Runtime;

internal readonly record struct ObjectRuntimeLocationContext(
    SceneLocationScope Scope,
    SceneCreationContext CreationContext,
    ObjectTerritoryMetadata Territory,
    ObjectHousingRuntimeContext Housing)
{
    public bool IsValid
        => Scope.IsValid;
}

internal readonly record struct ObjectHousingPlotContext(
    ObjectHousingDistrict District,
    int Plot);

internal readonly record struct ObjectHousingRuntimeContext(
    ObjectHousingArea? CurrentArea,
    ObjectHousingSize? CurrentSize,
    byte? HousingBlockId,
    HousingPlacementBlockSource BlockSource,
    HousingPlacementSizeSource SizeSource,
    ObjectHousingPlotContext? Plot,
    ObjectHousingPlotBasis? PlotBasis,
    bool HasCollisionScene)
{
    public bool HasCurrentArea
        => CurrentArea.HasValue;

    public bool HasCurrentSize
        => CurrentSize.HasValue;

    public bool HasHousingBlock
        => HousingBlockId.HasValue;

    public HousingPlacementContext ToPlacementContext(ObjectHousingModeState targetState)
        => new(
            targetState.Area,
            targetState.Size,
            CurrentArea,
            CurrentSize,
            PlotBasis,
            HousingBlockId,
            BlockSource,
            SizeSource,
            HasCollisionScene);
}


