using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

internal readonly record struct ObjectRuntimeLocationContext(
    ObjectLocationScope Scope,
    ObjectCreationContext CreationContext,
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
            HousingBlockId,
            BlockSource,
            SizeSource,
            HasCollisionScene);
}


