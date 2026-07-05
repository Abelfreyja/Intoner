using Intoner.Objects.Catalog;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

internal sealed class PlacementSurfaceResolver(PlacementSurfaceRaycaster surfaceRaycaster)
{
    private const int MaxProbeCount = 10;
    private const float ProbeDuplicateDistanceSquared = ObjectMathUtility.ScalarEpsilon * ObjectMathUtility.ScalarEpsilon;

    public bool TryResolveSurface(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot? boundsSnapshot,
        HousingFurnitureMetadata metadata,
        out ObjectSurfaceHit hit,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        hit = ObjectSurfaceHit.Empty;
        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;

        Span<Vector3> probePoints = stackalloc Vector3[MaxProbeCount];
        int probeCount = ShouldUseOriginProbeOnly(context, metadata)
            ? AddProbePoint(probePoints, 0, snapshot.Transform.Position)
            : BuildProbePoints(snapshot.Transform.Position, boundsSnapshot, probePoints);
        ulong materialMask = PlacementSurfacePolicy.ResolveAllowedMaterialMask(metadata);
        bool allowSurfaceAboveObject = ShouldAllowSurfaceAboveObject(context, metadata);
        float rayLift = ResolveRayLift(boundsSnapshot, allowSurfaceAboveObject);
        SurfaceCandidateSelector selector = new(snapshot, boundsSnapshot, metadata, allowSurfaceAboveObject);

        for (int index = 0; index < probeCount; ++index)
        {
            Vector3 rayOrigin = probePoints[index] + (Vector3.UnitY * rayLift);
            PlacementSurfaceRaycastRequest request = new(
                snapshot.Id,
                rayOrigin,
                -Vector3.UnitY,
                PlacementValidationConstants.NativeRayMaxDistance,
                materialMask);
            if (surfaceRaycaster.TryRaycastNative(request.Origin, request.Direction, request.MaxDistance, out ObjectSurfaceHit nativeCandidate))
            {
                selector.TryUse(nativeCandidate, index);
            }

            if (surfaceRaycaster.TryRaycastNativeMaterial(request.Origin, request.Direction, request.MaxDistance, materialMask, out ObjectSurfaceHit filteredCandidate))
            {
                selector.TryUse(filteredCandidate, index);
            }

            if (PlacementSurfaceRaycaster.TryRaycastObjectBounds(context, request, out ObjectSurfaceHit objectCandidate))
            {
                selector.TryUse(objectCandidate, index);
            }
        }

        if (selector.TryGetSelected(out hit))
        {
            return true;
        }

        issueCode = selector.HasPrimarySurfaceError
            ? PlacementIssueCode.InvalidPlacementSurface
            : PlacementIssueCode.MissingPlacementSurface;
        errorMessage = selector.HasPrimarySurfaceError && selector.PrimarySurfaceError.Length > 0
            ? selector.PrimarySurfaceError
            : "Furniture does not have a valid housing placement surface.";
        return false;
    }

    private static bool ShouldUseOriginProbeOnly(
        PlacementValidationContext context,
        HousingFurnitureMetadata metadata)
        => metadata is { Surface: HousingPlacementSurface.Floor, IsOutdoor: true }
           && context.HousingPlacementContext.CurrentArea == ObjectHousingArea.Outdoor;

    private static bool ShouldAllowSurfaceAboveObject(
        PlacementValidationContext context,
        HousingFurnitureMetadata metadata)
        => metadata is { Surface: HousingPlacementSurface.Floor, IsIndoor: true }
        && context.HousingPlacementContext.CurrentArea == ObjectHousingArea.Indoor;

    private static float ResolveRayLift(ObjectBoundsSnapshot? boundsSnapshot, bool allowSurfaceAboveObject)
    {
        if (!allowSurfaceAboveObject
            || boundsSnapshot is null
            || !TryResolveNativePlacementClearance(boundsSnapshot, out ObjectPlacementClearance clearance))
        {
            return PlacementValidationConstants.NativeRayLift;
        }

        return PlacementValidationConstants.NativeRayLift + clearance.Radius;
    }

    private static bool IsCurrentObjectNativeSurface(ObjectBoundsSnapshot? boundsSnapshot, ObjectSurfaceHit candidate)
        => candidate.Source == ObjectSurfaceHitSource.Native
           && boundsSnapshot is { Kind: ObjectKind.Furniture, NativeAddress: not 0 }
           && ObjectLayoutInterop.SharedGroupContainsCollider(boundsSnapshot.NativeAddress, candidate.ColliderAddress);

    private ref struct SurfaceCandidateSelector
    {
        private readonly ObjectSnapshot _snapshot;
        private readonly ObjectBoundsSnapshot? _boundsSnapshot;
        private readonly HousingFurnitureMetadata _metadata;
        private readonly bool _allowSurfaceAboveObject;
        private float _selectedDistance;
        private ObjectSurfaceHit _hit;

        public SurfaceCandidateSelector(
            ObjectSnapshot snapshot,
            ObjectBoundsSnapshot? boundsSnapshot,
            HousingFurnitureMetadata metadata,
            bool allowSurfaceAboveObject)
        {
            _snapshot = snapshot;
            _boundsSnapshot = boundsSnapshot;
            _metadata = metadata;
            _allowSurfaceAboveObject = allowSurfaceAboveObject;
            _selectedDistance = float.PositiveInfinity;
            _hit = ObjectSurfaceHit.Empty;
            HasPrimarySurfaceError = false;
            PrimarySurfaceError = string.Empty;
        }

        public bool HasPrimarySurfaceError { get; private set; }

        public string PrimarySurfaceError { get; private set; }

        public bool TryGetSelected(out ObjectSurfaceHit hit)
        {
            hit = _hit;
            return float.IsFinite(_selectedDistance);
        }

        public void TryUse(ObjectSurfaceHit candidate, int probeIndex)
        {
            if (IsCurrentObjectNativeSurface(_boundsSnapshot, candidate))
            {
                return;
            }

            if (!PlacementSurfacePolicy.TryValidateSurface(_metadata, candidate, out string candidateError))
            {
                if (probeIndex == 0)
                {
                    HasPrimarySurfaceError = true;
                    PrimarySurfaceError = candidateError;
                }

                return;
            }

            float surfaceOffset = _snapshot.Transform.Position.Y - candidate.Point.Y;
            if (surfaceOffset < -PlacementValidationConstants.SurfaceAlignmentTolerance && !_allowSurfaceAboveObject)
            {
                return;
            }

            float surfaceDistance = _allowSurfaceAboveObject
                ? MathF.Abs(surfaceOffset)
                : surfaceOffset;
            if (surfaceDistance >= _selectedDistance)
            {
                return;
            }

            _selectedDistance = surfaceDistance;
            _hit = candidate;
        }
    }

    public static bool TryResolveNativePlacementClearance(
        ObjectBoundsSnapshot boundsSnapshot,
        out ObjectPlacementClearance clearance)
    {
        if (boundsSnapshot.PlacementClearance is { IsValid: true } placementClearance)
        {
            clearance = placementClearance;
            return true;
        }

        clearance = default;
        return false;
    }

    public static bool TryResolveSurfaceProbeRadius(ObjectBoundsSnapshot boundsSnapshot, out float radius)
    {
        if (TryResolveNativePlacementClearance(boundsSnapshot, out ObjectPlacementClearance clearance))
        {
            radius = clearance.Radius;
            return true;
        }

        if (boundsSnapshot.LocalBounds is { } localBounds)
        {
            Vector3 halfExtents = localBounds.HalfExtents;
            radius = MathF.Min(MathF.Abs(halfExtents.X), MathF.Abs(halfExtents.Z));
        }
        else
        {
            Vector3 halfExtents = (boundsSnapshot.Max - boundsSnapshot.Min) * 0.5f;
            radius = MathF.Min(MathF.Abs(halfExtents.X), MathF.Abs(halfExtents.Z));
        }

        return radius > ObjectMathUtility.ScalarEpsilon;
    }

    private static int BuildProbePoints(Vector3 position, ObjectBoundsSnapshot? boundsSnapshot, Span<Vector3> probePoints)
    {
        int count = AddProbePoint(probePoints, 0, position);
        if (boundsSnapshot is null)
        {
            return count;
        }

        if (boundsSnapshot.LocalBounds is { } localBounds)
        {
            return BuildBoundsProbePoints(position, localBounds, probePoints, count);
        }

        return !TryResolveSurfaceProbeRadius(boundsSnapshot, out float radius)
            || radius <= ObjectMathUtility.ScalarEpsilon
            ? count
            : BuildRadiusProbePoints(position, radius, probePoints, count);
    }

    private static int BuildBoundsProbePoints(
        Vector3 position,
        OrientedBounds bounds,
        Span<Vector3> probePoints,
        int count)
    {
        Vector3 halfExtents = ObjectMathUtility.Abs(bounds.HalfExtents);
        if (halfExtents.X <= ObjectMathUtility.ScalarEpsilon
            || halfExtents.Z <= ObjectMathUtility.ScalarEpsilon)
        {
            return count;
        }

        count = AddBoundsProbePoint(bounds, position.Y, Vector3.Zero, probePoints, count);
        count = AddBoundsProbePoint(bounds, position.Y, new Vector3(halfExtents.X, 0f, 0f), probePoints, count);
        count = AddBoundsProbePoint(bounds, position.Y, new Vector3(-halfExtents.X, 0f, 0f), probePoints, count);
        count = AddBoundsProbePoint(bounds, position.Y, new Vector3(0f, 0f, halfExtents.Z), probePoints, count);
        count = AddBoundsProbePoint(bounds, position.Y, new Vector3(0f, 0f, -halfExtents.Z), probePoints, count);
        count = AddBoundsProbePoint(bounds, position.Y, new Vector3(halfExtents.X, 0f, halfExtents.Z), probePoints, count);
        count = AddBoundsProbePoint(bounds, position.Y, new Vector3(halfExtents.X, 0f, -halfExtents.Z), probePoints, count);
        count = AddBoundsProbePoint(bounds, position.Y, new Vector3(-halfExtents.X, 0f, halfExtents.Z), probePoints, count);
        return AddBoundsProbePoint(bounds, position.Y, new Vector3(-halfExtents.X, 0f, -halfExtents.Z), probePoints, count);
    }

    private static int AddBoundsProbePoint(
        OrientedBounds bounds,
        float y,
        Vector3 localPoint,
        Span<Vector3> probePoints,
        int count)
    {
        Vector3 point = Vector3.Transform(localPoint, bounds.Transform);
        point.Y = y;
        return AddProbePoint(probePoints, count, point);
    }

    private static int BuildRadiusProbePoints(Vector3 position, float radius, Span<Vector3> probePoints, int count)
    {
        count = AddProbePoint(probePoints, count, position + (Vector3.UnitX * radius));
        count = AddProbePoint(probePoints, count, position - (Vector3.UnitX * radius));
        count = AddProbePoint(probePoints, count, position + (Vector3.UnitZ * radius));
        return AddProbePoint(probePoints, count, position - (Vector3.UnitZ * radius));
    }

    private static int AddProbePoint(Span<Vector3> probePoints, int count, Vector3 point)
    {
        if (count >= probePoints.Length)
        {
            return count;
        }

        for (int index = 0; index < count; ++index)
        {
            if (Vector3.DistanceSquared(probePoints[index], point) <= ProbeDuplicateDistanceSquared)
            {
                return count;
            }
        }

        probePoints[count] = point;
        return count + 1;
    }
}

