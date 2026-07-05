using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

internal sealed class SurfaceAttachmentService(FurnitureMetadataResolver metadataResolver)
{
    private const float ParentTabletopNormalThreshold = 0.5f;
    private const float ParentSurfaceBoundsTolerance = 0.05f;

    public bool IsTabletopFurniture(ObjectSnapshot snapshot)
        => metadataResolver.TryResolve(snapshot, out _, out HousingFurnitureMetadata metadata)
           && metadata.Surface == HousingPlacementSurface.Tabletop;

    public ObjectSnapshot ApplySurfaceDragAttachment(
        ObjectSnapshot snapshot,
        ObjectSurfaceHit? hit,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
    {
        if (!TryResolveSurfaceDragAttachment(snapshot, hit, boundsSnapshots, out FurnitureModel furnitureModel, out Guid? parentId)
            || furnitureModel.AttachmentParentId == parentId)
        {
            return snapshot;
        }

        return snapshot with
        {
            Model = furnitureModel with
            {
                AttachmentParentId = parentId,
            },
        };
    }

    public bool HasSurfaceDragAttachmentChange(
        ObjectSnapshot snapshot,
        ObjectSurfaceHit hit,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
        => TryResolveSurfaceDragAttachment(snapshot, hit, boundsSnapshots, out FurnitureModel furnitureModel, out Guid? parentId)
           && furnitureModel.AttachmentParentId != parentId;

    public static bool TryResolveAttachedParent(
        ObjectSnapshot snapshot,
        IReadOnlyDictionary<Guid, ObjectSnapshot> snapshotsById,
        IReadOnlyDictionary<Guid, ObjectBoundsSnapshot> boundsById,
        out ObjectSnapshot parentSnapshot,
        out ObjectBoundsSnapshot childBounds,
        out ObjectBoundsSnapshot parentBounds,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        parentSnapshot = default!;
        childBounds = default!;
        parentBounds = default!;
        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;

        if (!TryResolveAttachmentParentId(snapshot, out Guid parentId, out issueCode, out errorMessage))
        {
            return false;
        }

        snapshotsById.TryGetValue(parentId, out ObjectSnapshot? resolvedParentSnapshot);
        boundsById.TryGetValue(snapshot.Id, out ObjectBoundsSnapshot? resolvedChildBounds);
        boundsById.TryGetValue(parentId, out ObjectBoundsSnapshot? resolvedParentBounds);
        return TryUseAttachedParent(
            resolvedParentSnapshot,
            resolvedChildBounds,
            resolvedParentBounds,
            out parentSnapshot,
            out childBounds,
            out parentBounds,
            out issueCode,
            out errorMessage);
    }

    public static bool TryResolveAttachedParent(
        ObjectSnapshot snapshot,
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        out ObjectSnapshot parentSnapshot,
        out ObjectBoundsSnapshot childBounds,
        out ObjectBoundsSnapshot parentBounds,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        parentSnapshot = default!;
        childBounds = default!;
        parentBounds = default!;
        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;

        if (!TryResolveAttachmentParentId(snapshot, out Guid parentId, out issueCode, out errorMessage))
        {
            return false;
        }

        ObjectSnapshot? resolvedParentSnapshot = FindSnapshot(snapshots, parentId);
        TryFindBoundsSnapshot(boundsSnapshots, snapshot.Id, out ObjectBoundsSnapshot? resolvedChildBounds);
        TryFindBoundsSnapshot(boundsSnapshots, parentId, out ObjectBoundsSnapshot? resolvedParentBounds);
        return TryUseAttachedParent(
            resolvedParentSnapshot,
            resolvedChildBounds,
            resolvedParentBounds,
            out parentSnapshot,
            out childBounds,
            out parentBounds,
            out issueCode,
            out errorMessage);
    }

    public static bool TryValidateAttachedTabletopPlacement(
        ObjectBoundsSnapshot childBounds,
        ObjectBoundsSnapshot parentBounds,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        if (!PlacementSurfacePolicy.SupportsObjectSurface(parentBounds.PlacementSurfaceSupport, HousingPlacementSurface.Tabletop))
        {
            issueCode = PlacementIssueCode.InvalidPlacementSurface;
            errorMessage = "Tabletop furniture requires an attached tabletop surface.";
            return false;
        }

        issueCode = PlacementIssueCode.BoundsUnavailable;
        if (!TryResolveVerticalRange(childBounds, out float childBottom, out _)
            || !TryResolveVerticalRange(parentBounds, out _, out float parentTop)
            || !TryResolveContactCenter(childBounds, childBottom, out Vector3 contactCenter))
        {
            errorMessage = "Attached tabletop placement needs furniture bounds.";
            return false;
        }

        if (MathF.Abs(childBottom - parentTop) > PlacementValidationConstants.SurfaceAlignmentTolerance)
        {
            issueCode = PlacementIssueCode.NotAlignedToSurface;
            errorMessage = "Tabletop furniture is not aligned to its attached parent surface.";
            return false;
        }

        if (!ContainsParentSurfacePoint(parentBounds, contactCenter))
        {
            issueCode = PlacementIssueCode.OutsideAttachmentParentSurface;
            errorMessage = "Tabletop furniture is outside its attached parent surface.";
            return false;
        }

        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;
        return true;
    }

    public bool TrySnapToAttachedParentSurface(
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot childBounds,
        ObjectBoundsSnapshot parentBounds,
        out ObjectSnapshot fixedSnapshot)
    {
        fixedSnapshot = snapshot;
        if (!TryResolveVerticalRange(childBounds, out float childBottom, out _)
            || !TryResolveVerticalRange(parentBounds, out _, out float parentTop))
        {
            return false;
        }

        ObjectTransform transform = snapshot.Transform with
        {
            Position = snapshot.Transform.Position + (Vector3.UnitY * (parentTop - childBottom)),
        };
        fixedSnapshot = snapshot with { Transform = transform };
        return true;
    }

    private static bool TryResolveAttachmentParentId(
        ObjectSnapshot snapshot,
        out Guid parentId,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        if (snapshot.Model is FurnitureModel { AttachmentParentId: { } resolvedParentId })
        {
            parentId = resolvedParentId;
            issueCode = PlacementIssueCode.None;
            errorMessage = string.Empty;
            return true;
        }

        parentId = Guid.Empty;
        issueCode = PlacementIssueCode.MissingAttachmentParent;
        errorMessage = "Furniture does not have an attachment parent.";
        return false;
    }

    private static bool TryUseAttachedParent(
        ObjectSnapshot? resolvedParentSnapshot,
        ObjectBoundsSnapshot? resolvedChildBounds,
        ObjectBoundsSnapshot? resolvedParentBounds,
        out ObjectSnapshot parentSnapshot,
        out ObjectBoundsSnapshot childBounds,
        out ObjectBoundsSnapshot parentBounds,
        out PlacementIssueCode issueCode,
        out string errorMessage)
    {
        parentSnapshot = default!;
        childBounds = default!;
        parentBounds = default!;
        if (resolvedParentSnapshot is null || resolvedParentSnapshot.Kind != ObjectKind.Furniture)
        {
            issueCode = PlacementIssueCode.MissingAttachmentParent;
            errorMessage = "Furniture attachment parent is not available in the current layout.";
            return false;
        }

        if (resolvedChildBounds is null || resolvedParentBounds is null)
        {
            issueCode = PlacementIssueCode.BoundsUnavailable;
            errorMessage = "Attached placement needs furniture bounds.";
            return false;
        }

        parentSnapshot = resolvedParentSnapshot;
        childBounds = resolvedChildBounds;
        parentBounds = resolvedParentBounds;
        issueCode = PlacementIssueCode.None;
        errorMessage = string.Empty;
        return true;
    }

    private static bool CanAttachToObjectSurface(HousingPlacementSurface surface)
        => surface is HousingPlacementSurface.Tabletop or HousingPlacementSurface.Wall;

    private bool TryResolveSurfaceDragAttachment(
        ObjectSnapshot snapshot,
        ObjectSurfaceHit? hit,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        out FurnitureModel furnitureModel,
        out Guid? parentId)
    {
        furnitureModel = default!;
        parentId = null;
        if (hit is not { } surfaceHit
            || !metadataResolver.TryResolve(snapshot, out furnitureModel, out HousingFurnitureMetadata metadata)
            || !CanAttachToObjectSurface(metadata.Surface))
        {
            return false;
        }

        parentId = TryResolveSurfaceParent(metadata.Surface, surfaceHit, snapshot.Id, boundsSnapshots, out Guid targetObjectId)
            ? targetObjectId
            : null;
        return true;
    }

    private static bool TryResolveSurfaceParent(
        HousingPlacementSurface surface,
        ObjectSurfaceHit hit,
        Guid childObjectId,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        out Guid targetObjectId)
    {
        targetObjectId = Guid.Empty;
        if (!hit.HasObjectTarget
            || hit.TargetObjectId == childObjectId
            || !TryFindBoundsSnapshot(boundsSnapshots, hit.TargetObjectId, out ObjectBoundsSnapshot? targetBounds)
            || targetBounds is null
            || targetBounds.Kind != ObjectKind.Furniture
            || !IsParentSurfaceHit(surface, hit, targetBounds))
        {
            return false;
        }

        targetObjectId = hit.TargetObjectId;
        return true;
    }

    private static bool IsParentSurfaceHit(
        HousingPlacementSurface surface,
        ObjectSurfaceHit hit,
        ObjectBoundsSnapshot targetBounds)
        => PlacementSurfacePolicy.SupportsObjectSurface(targetBounds.PlacementSurfaceSupport, surface)
           && (surface == HousingPlacementSurface.Wall
            ? IsParentWallSurfaceHit(hit, targetBounds)
            : IsParentTabletopSurfaceHit(hit, targetBounds));

    private static bool IsParentTabletopSurfaceHit(ObjectSurfaceHit hit, ObjectBoundsSnapshot targetBounds)
    {
        if (hit.Normal.Y < ParentTabletopNormalThreshold
            || !TryResolveVerticalRange(targetBounds, out _, out float parentTop))
        {
            return false;
        }

        return MathF.Abs(hit.Point.Y - parentTop) <= ParentSurfaceBoundsTolerance;
    }

    private static bool IsParentWallSurfaceHit(ObjectSurfaceHit hit, ObjectBoundsSnapshot targetBounds)
        => WallPlacementGeometry.IsWallSurfaceNormal(hit.Normal)
           && WallPlacementGeometry.ContainsWallSurfacePoint(
               targetBounds,
               hit.Point,
               hit.Normal,
               ParentSurfaceBoundsTolerance);

    private static ObjectSnapshot? FindSnapshot(IReadOnlyList<ObjectSnapshot> snapshots, Guid objectId)
    {
        foreach (ObjectSnapshot candidate in snapshots)
        {
            if (candidate.Id == objectId)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TryFindBoundsSnapshot(
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        Guid objectId,
        out ObjectBoundsSnapshot? boundsSnapshot)
    {
        foreach (ObjectBoundsSnapshot candidate in boundsSnapshots)
        {
            if (candidate.Id == objectId)
            {
                boundsSnapshot = candidate;
                return true;
            }
        }

        boundsSnapshot = null;
        return false;
    }

    private static bool TryResolveVerticalRange(ObjectBoundsSnapshot boundsSnapshot, out float minY, out float maxY)
    {
        if (boundsSnapshot.LocalBounds is { } localBounds)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            ObjectShapeMath.CopyOrientedBoxCorners(localBounds, corners);
            minY = float.PositiveInfinity;
            maxY = float.NegativeInfinity;
            foreach (Vector3 corner in corners)
            {
                minY = MathF.Min(minY, corner.Y);
                maxY = MathF.Max(maxY, corner.Y);
            }

            return float.IsFinite(minY) && float.IsFinite(maxY);
        }

        minY = boundsSnapshot.Min.Y;
        maxY = boundsSnapshot.Max.Y;
        return float.IsFinite(minY) && float.IsFinite(maxY);
    }

    private static bool TryResolveContactCenter(ObjectBoundsSnapshot boundsSnapshot, float contactY, out Vector3 contactCenter)
    {
        if (boundsSnapshot.LocalBounds is { } localBounds)
        {
            Vector3 center = localBounds.Transform.Translation;
            contactCenter = new Vector3(center.X, contactY, center.Z);
            return ObjectMathUtility.IsFinite(contactCenter);
        }

        Vector3 boundsCenter = (boundsSnapshot.Min + boundsSnapshot.Max) * 0.5f;
        contactCenter = new Vector3(boundsCenter.X, contactY, boundsCenter.Z);
        return ObjectMathUtility.IsFinite(contactCenter);
    }

    private static bool ContainsParentSurfacePoint(ObjectBoundsSnapshot parentBounds, Vector3 worldPoint)
        => parentBounds.LocalBounds is { } localBounds
            ? ContainsOrientedParentSurfacePoint(localBounds, worldPoint)
            : ContainsAxisAlignedParentSurfacePoint(parentBounds, worldPoint);

    private static bool ContainsOrientedParentSurfacePoint(OrientedBounds parentBounds, Vector3 worldPoint)
    {
        if (!Matrix4x4.Invert(parentBounds.Transform, out Matrix4x4 inverseTransform))
        {
            return false;
        }

        Vector3 localPoint = Vector3.Transform(worldPoint, inverseTransform);
        return MathF.Abs(localPoint.X) <= parentBounds.HalfExtents.X + ParentSurfaceBoundsTolerance
               && MathF.Abs(localPoint.Z) <= parentBounds.HalfExtents.Z + ParentSurfaceBoundsTolerance;
    }

    private static bool ContainsAxisAlignedParentSurfacePoint(ObjectBoundsSnapshot parentBounds, Vector3 worldPoint)
        => worldPoint.X >= parentBounds.Min.X - ParentSurfaceBoundsTolerance
           && worldPoint.X <= parentBounds.Max.X + ParentSurfaceBoundsTolerance
           && worldPoint.Z >= parentBounds.Min.Z - ParentSurfaceBoundsTolerance
           && worldPoint.Z <= parentBounds.Max.Z + ParentSurfaceBoundsTolerance;
}

