using Intoner.Objects.Filesystem.Configuration;

namespace Intoner.Objects.Runtime;

internal enum HousingPlacementBlockSource
{
    None,
    CurrentPlot,
    PlayerMapRange,
}

internal enum HousingPlacementSizeSource
{
    None,
    IndoorTerritory,
    CurrentPlot,
    MapRangeBlock,
}

internal readonly record struct HousingPlacementBlock(byte? Id, HousingPlacementBlockSource Source)
{
    public static HousingPlacementBlock Unavailable { get; } = new(null, HousingPlacementBlockSource.None);
}

internal readonly record struct HousingPlacementSizeResult(ObjectHousingSize? Size, HousingPlacementSizeSource Source)
{
    public static HousingPlacementSizeResult Unavailable { get; } = new(null, HousingPlacementSizeSource.None);
}

internal readonly record struct HousingPlacementContext(
    ObjectHousingArea TargetArea,
    ObjectHousingSize TargetSize,
    ObjectHousingArea? CurrentArea,
    ObjectHousingSize? CurrentSize,
    byte? HousingBlockId,
    HousingPlacementBlockSource BlockSource,
    HousingPlacementSizeSource SizeSource,
    bool HasCollisionScene)
{
    public bool HasCurrentArea
        => CurrentArea.HasValue;

    public bool HasCurrentSize
        => CurrentSize.HasValue;

    public bool HasHousingBlock
        => HousingBlockId.HasValue;

    public bool HasAreaMismatch
        => CurrentArea.HasValue && CurrentArea.Value != TargetArea;

    public bool HasSizeMismatch
        => CurrentSize.HasValue && CurrentSize.Value != TargetSize;

    public bool CanCheckContainment
        => HasCollisionScene && HasCurrentArea && HasCurrentSize && HasHousingBlock && !HasAreaMismatch && !HasSizeMismatch;

    public string TargetAreaName
        => HousingFurnitureAreaPolicy.FormatArea(TargetArea);

    public string CurrentAreaName
        => HousingFurnitureAreaPolicy.FormatArea(CurrentArea);

    public string TargetSizeName
        => HousingFurnitureAreaPolicy.FormatSize(TargetSize);

    public string CurrentSizeName
        => HousingFurnitureAreaPolicy.FormatSize(CurrentSize);
}

