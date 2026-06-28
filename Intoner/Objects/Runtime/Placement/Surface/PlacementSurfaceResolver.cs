using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed class PlacementSurfaceResolver(PlacementSceneRaycaster sceneRaycaster)
{
    private const int MaxProbeCount = 5;

    public bool TryResolveSurface(
        PlacementValidationContext context,
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot? boundsSnapshot,
        HousingFurnitureMetadata metadata,
        out ObjectSurfaceHit hit,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        hit = default;
        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;

        Span<Vector3> probePoints = stackalloc Vector3[MaxProbeCount];
        int probeCount = BuildProbePoints(snapshot.Transform.Position, boundsSnapshot, probePoints);
        bool primarySurfaceFound = false;
        float selectedDistance = float.PositiveInfinity;
        string primarySurfaceError = string.Empty;
        ulong materialMask = PlacementSurfacePolicy.ResolveAllowedMaterialMask(metadata);

        for (int index = 0; index < probeCount; ++index)
        {
            Vector3 rayOrigin = probePoints[index] + (Vector3.UnitY * PlacementValidationConstants.NativeRayLift);
            PlacementSceneRaycastRequest request = new(
                snapshot.Id,
                rayOrigin,
                -Vector3.UnitY,
                PlacementValidationConstants.NativeRayMaxDistance,
                materialMask);
            if (sceneRaycaster.TryRaycastNative(request.Origin, request.Direction, request.MaxDistance, out ObjectSurfaceHit nativeCandidate))
            {
                UseSurfaceCandidate(
                    snapshot,
                    metadata,
                    nativeCandidate,
                    index,
                    ref primarySurfaceFound,
                    ref selectedDistance,
                    ref primarySurfaceError,
                    ref hit);
            }

            if (sceneRaycaster.TryRaycastNativeMaterial(request.Origin, request.Direction, request.MaxDistance, materialMask, out ObjectSurfaceHit filteredCandidate))
            {
                UseSurfaceCandidate(
                    snapshot,
                    metadata,
                    filteredCandidate,
                    index,
                    ref primarySurfaceFound,
                    ref selectedDistance,
                    ref primarySurfaceError,
                    ref hit);
            }

            if (sceneRaycaster.TryRaycastObjectBounds(context, request, out ObjectSurfaceHit objectCandidate))
            {
                UseSurfaceCandidate(
                    snapshot,
                    metadata,
                    objectCandidate,
                    index,
                    ref primarySurfaceFound,
                    ref selectedDistance,
                    ref primarySurfaceError,
                    ref hit);
            }
        }

        if (float.IsFinite(selectedDistance))
        {
            return true;
        }

        issueCode = primarySurfaceFound
            ? PlacementIssueCode.InvalidPlacementSurface
            : PlacementIssueCode.MissingPlacementSurface;
        errorMessage = primarySurfaceFound && primarySurfaceError.Length > 0
            ? primarySurfaceError
            : "Furniture does not have a valid housing placement surface.";
        return false;
    }

    private static void UseSurfaceCandidate(
        ObjectSnapshot snapshot,
        HousingFurnitureMetadata metadata,
        ObjectSurfaceHit candidate,
        int probeIndex,
        ref bool primarySurfaceFound,
        ref float selectedDistance,
        ref string primarySurfaceError,
        ref ObjectSurfaceHit hit)
    {
        if (!PlacementSurfacePolicy.TryValidateSurface(metadata, candidate, out string candidateError))
        {
            if (probeIndex == 0)
            {
                primarySurfaceFound = true;
                primarySurfaceError = candidateError;
            }

            return;
        }

        float distance = MathF.Abs(snapshot.Transform.Position.Y - candidate.Point.Y);
        if (distance >= selectedDistance)
        {
            return;
        }

        selectedDistance = distance;
        hit = candidate;
    }

    public static bool TryResolveNativePlacementClearanceRadius(ObjectBoundsSnapshot boundsSnapshot, out float radius)
    {
        if (boundsSnapshot.PlacementClearanceRadius is { } placementClearanceRadius
            && float.IsFinite(placementClearanceRadius)
            && placementClearanceRadius >= 0f)
        {
            radius = placementClearanceRadius;
            return true;
        }

        radius = 0f;
        return false;
    }

    public static bool TryResolveSurfaceProbeRadius(ObjectBoundsSnapshot boundsSnapshot, out float radius)
    {
        if (TryResolveNativePlacementClearanceRadius(boundsSnapshot, out radius))
        {
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
        probePoints[0] = position;
        if (boundsSnapshot is null
            || !TryResolveSurfaceProbeRadius(boundsSnapshot, out float radius)
            || radius <= ObjectMathUtility.ScalarEpsilon)
        {
            return 1;
        }

        probePoints[1] = position + (Vector3.UnitX * radius);
        probePoints[2] = position - (Vector3.UnitX * radius);
        probePoints[3] = position + (Vector3.UnitZ * radius);
        probePoints[4] = position - (Vector3.UnitZ * radius);
        return MaxProbeCount;
    }
}

