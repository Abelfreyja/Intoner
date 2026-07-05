using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal static class PlacementSurfaceFloatPolicy
{
    private const float SurfaceOffsetEpsilon = 0.0001f;
    private const string SnapBackRangeMessage = "Floor furniture is within the snap-back range from its placement surface.";
    private const string BelowSurfaceMessage = "Furniture is below its placement surface.";
    private const string AboveHousingAreaMessage = "Furniture is above the current housing placement area.";
    private const string MissingSnapRangeMessage = "Surface snap range is not available.";

    public static PlacementValidationStatus EvaluateFloorOffset(
        PlacementValidationContext context,
        HousingFurnitureMetadata metadata,
        ObjectBoundsSnapshot boundsSnapshot,
        float objectY,
        float surfaceOffset,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;

        if (IsSurfaceAligned(surfaceOffset))
        {
            return PlacementValidationStatus.Valid;
        }

        if (RequiresIndoorFloorOffsetValidation(context, metadata))
        {
            return EvaluateIndoorFloorOffset(boundsSnapshot, surfaceOffset, out issueCode, out errorMessage);
        }

        if (surfaceOffset < -PlacementValidationConstants.SurfaceAlignmentTolerance)
        {
            return Invalid(PlacementIssueCode.NotAlignedToSurface, BelowSurfaceMessage, out issueCode, out errorMessage);
        }

        if (!RequiresOutdoorFloorOffsetValidation(context, metadata))
        {
            return PlacementValidationStatus.Valid;
        }

        if (surfaceOffset
            < PlacementValidationConstants.OutdoorFloorMinimumFloatOffset - SurfaceOffsetEpsilon)
        {
            return Invalid(PlacementIssueCode.NotAlignedToSurface, SnapBackRangeMessage, out issueCode, out errorMessage);
        }

        if (ResolveOutdoorFloorCeilingOffset(context, objectY, surfaceOffset)
            > PlacementValidationConstants.OutdoorFloorMaximumFloatOffset + SurfaceOffsetEpsilon)
        {
            return Invalid(PlacementIssueCode.OutsideHousingArea, AboveHousingAreaMessage, out issueCode, out errorMessage);
        }

        return PlacementValidationStatus.Valid;
    }

    private static PlacementValidationStatus EvaluateIndoorFloorOffset(
        ObjectBoundsSnapshot boundsSnapshot,
        float surfaceOffset,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        if (!PlacementSurfaceResolver.TryResolveNativePlacementClearance(boundsSnapshot, out ObjectPlacementClearance clearance))
        {
            issueCode = PlacementIssueCode.BoundsUnavailable;
            errorMessage = MissingSnapRangeMessage;
            return PlacementValidationStatus.Unknown;
        }

        if (IsWithinIndoorSnapBand(surfaceOffset, clearance))
        {
            return Invalid(PlacementIssueCode.NotAlignedToSurface, SnapBackRangeMessage, out issueCode, out errorMessage);
        }

        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;
        return PlacementValidationStatus.Valid;
    }

    private static bool IsSurfaceAligned(float surfaceOffset)
        => MathF.Abs(surfaceOffset) <= PlacementValidationConstants.SurfaceAlignmentTolerance;

    private static bool IsWithinIndoorSnapBand(float surfaceOffset, ObjectPlacementClearance clearance)
    {
        if (surfaceOffset > PlacementValidationConstants.SurfaceAlignmentTolerance)
        {
            return IsWithinSnapRange(surfaceOffset, clearance.SnapAboveSurface);
        }

        if (surfaceOffset < -PlacementValidationConstants.SurfaceAlignmentTolerance)
        {
            return IsWithinSnapRange(-surfaceOffset, clearance.SnapBelowSurface);
        }

        return false;
    }

    private static bool IsWithinSnapRange(float distance, float range)
        => distance < range - SurfaceOffsetEpsilon;

    private static PlacementValidationStatus Invalid(
        PlacementIssueCode code,
        string message,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        issueCode = code;
        errorMessage = message;
        return PlacementValidationStatus.Invalid;
    }

    private static bool RequiresIndoorFloorOffsetValidation(
        PlacementValidationContext context,
        HousingFurnitureMetadata metadata)
        => metadata is { IsIndoor: true, Surface: HousingPlacementSurface.Floor }
        && context.HousingPlacementContext.CurrentArea == ObjectHousingArea.Indoor;

    private static bool RequiresOutdoorFloorOffsetValidation(
        PlacementValidationContext context,
        HousingFurnitureMetadata metadata)
        => metadata is { IsOutdoor: true, Surface: HousingPlacementSurface.Floor }
        && context.HousingPlacementContext.CurrentArea == ObjectHousingArea.Outdoor;

    private static float ResolveOutdoorFloorCeilingOffset(
        PlacementValidationContext context,
        float objectY,
        float surfaceOffset)
        => context.HousingPlacementContext.PlotBasis is { } plotBasis
            ? objectY - plotBasis.Origin.Y
            : surfaceOffset;
}
