namespace Intoner.Objects.Runtime;

internal enum PlacementValidationStatus
{
    Unknown,
    Valid,
    Invalid,
}

internal enum PlacementIssueCode
{
    None,
    BoundsUnavailable,
    InvalidHousingObjectKind,
    InvalidFurnitureModel,
    MissingFurnitureMetadata,
    FurnitureAreaMismatch,
    FurnitureLimitExceeded,
    MissingPlacementSurface,
    InvalidPlacementSurface,
    NotAlignedToSurface,
    MissingFloorClearance,
    WallBoundsUnavailable,
    WallSurfaceUnavailable,
    WallFloatingFromSurface,
    WallNormalMismatch,
    HousingAreaUnavailable,
    HousingMapAreaMismatch,
    HousingSizeUnavailable,
    HousingSizeMismatch,
    OutsideHousingArea,
    MissingAquariumFootprint,
    FootprintConflict,
    MissingAttachmentParent,
    AttachmentCycle,
    ParentPlacementInvalid,
    ParentPlacementUnknown,
    OutsideAttachmentParentSurface,
    NotEvaluated,
}

internal enum PlacementFixKind
{
    SnapToSurface,
    MoveToPlayerPlacement,
    ClearAttachmentParent,
}

internal readonly record struct PlacementValidationIssue(
    PlacementIssueCode Code,
    string Message);

internal readonly record struct PlacementFixProposal(
    Guid ObjectId,
    PlacementFixKind Kind,
    string Label,
    string Description);

internal sealed record PlacementEvaluation(
    Guid ObjectId,
    PlacementValidationStatus Status,
    string Message,
    IReadOnlyList<PlacementValidationIssue> Issues,
    IReadOnlyList<PlacementFixProposal> Fixes);

internal static class PlacementValidationConstants
{
    public const float NativeRayLift = 0.20f;
    public const float NativeRayMaxDistance = 100f;
    public const float SurfaceAlignmentTolerance = 0.02f;
}

