using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal sealed class PlacementFixService(
    FurnitureMetadataResolver metadataResolver,
    NativePlacementQuery nativeQuery,
    IObjectRuntimeLocationService locationService,
    IObjectHousingModePolicy housingModePolicy,
    IObjectPlacementResolver placementResolver,
    IObjectSceneView sceneView,
    PlacementValidationContextBuilder contextBuilder,
    PlacementSurfaceResolver surfaceResolver,
    SurfaceAttachmentService surfaceAttachmentService)
{
    private static readonly IReadOnlyList<PlacementFixProposal> NoFixes = [];

    public IReadOnlyList<PlacementFixProposal> CreateFixes(ObjectSnapshot snapshot, PlacementIssueCode issueCode)
    {
        return issueCode switch
        {
            PlacementIssueCode.NotAlignedToSurface => [CreateSnapToSurfaceFix(snapshot.Id)],
            PlacementIssueCode.OutsideHousingArea => [CreateMoveToPlayerPlacementFix(snapshot.Id)],
            PlacementIssueCode.MissingAttachmentParent
                or PlacementIssueCode.AttachmentCycle
                or PlacementIssueCode.ParentPlacementInvalid
                or PlacementIssueCode.ParentPlacementUnknown
                => snapshot.Model is FurnitureModel { AttachmentParentId: not null }
                    ? [CreateClearAttachmentParentFix(snapshot.Id)]
                    : NoFixes,
            _ => NoFixes,
        };
    }

    public bool TryBuildFixedSnapshot(ObjectSnapshot snapshot, PlacementFixKind fixKind, out ObjectSnapshot fixedSnapshot)
    {
        fixedSnapshot = snapshot;
        return fixKind switch
        {
            PlacementFixKind.SnapToSurface         => TrySnapToSurface(snapshot, out fixedSnapshot),
            PlacementFixKind.MoveToPlayerPlacement => TryMoveToPlayerPlacement(snapshot, out fixedSnapshot),
            PlacementFixKind.ClearAttachmentParent => TryClearAttachmentParent(snapshot, out fixedSnapshot),
            _                                      => false,
        };
    }

    private bool TrySnapToSurface(ObjectSnapshot snapshot, out ObjectSnapshot fixedSnapshot)
    {
        fixedSnapshot = snapshot;
        if (TrySnapToAttachedParentSurface(snapshot, out fixedSnapshot))
        {
            return true;
        }

        ObjectBoundsSnapshot? boundsSnapshot = FindCurrentBounds(snapshot.Id);
        if (!metadataResolver.TryResolve(snapshot, out HousingFurnitureMetadata metadata)
            || metadata.Surface == HousingPlacementSurface.Wall
            || !TryResolveSnapSurface(snapshot, boundsSnapshot, metadata, out ObjectSurfaceHit hit))
        {
            return false;
        }

        Vector3 position = snapshot.Transform.Position;
        ObjectTransform transform = snapshot.Transform with
        {
            Position = new Vector3(position.X, hit.Point.Y, position.Z),
        };
        fixedSnapshot = snapshot with { Transform = transform };
        return true;
    }

    private bool TryResolveSnapSurface(
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot? boundsSnapshot,
        HousingFurnitureMetadata metadata,
        out ObjectSurfaceHit hit)
    {
        PlacementValidationContext context = BuildCurrentValidationContext(housingModePolicy.GetState());
        return surfaceResolver.TryResolveSurface(context, snapshot, boundsSnapshot, metadata, out hit, out _, out _);
    }

    private PlacementValidationContext BuildCurrentValidationContext(ObjectHousingModeState state)
        => contextBuilder.Build(
            sceneView.GetPlacedObjectSnapshots(),
            sceneView.GetObjectBoundsSnapshots(),
            state,
            locationService.ResolveHousingPlacementContext(state));

    private ObjectBoundsSnapshot? FindCurrentBounds(Guid objectId)
    {
        foreach (ObjectBoundsSnapshot currentBounds in sceneView.GetObjectBoundsSnapshots())
        {
            if (currentBounds.Id == objectId)
            {
                return currentBounds;
            }
        }

        return null;
    }

    private bool TrySnapToAttachedParentSurface(ObjectSnapshot snapshot, out ObjectSnapshot fixedSnapshot)
    {
        fixedSnapshot = snapshot;
        if (snapshot.Model is not FurnitureModel { AttachmentParentId: not null }
            || !surfaceAttachmentService.IsTabletopFurniture(snapshot))
        {
            return false;
        }

        IReadOnlyList<ObjectSnapshot> snapshots = sceneView.GetPlacedObjectSnapshots();
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots = sceneView.GetObjectBoundsSnapshots();
        return SurfaceAttachmentService.TryResolveAttachedParent(
                   snapshot,
                   snapshots,
                   boundsSnapshots,
                   out _,
                   out ObjectBoundsSnapshot childBounds,
                   out ObjectBoundsSnapshot parentBounds,
                   out _,
                   out _)
               && surfaceAttachmentService.TrySnapToAttachedParentSurface(snapshot, childBounds, parentBounds, out fixedSnapshot);
    }

    private bool TryMoveToPlayerPlacement(ObjectSnapshot snapshot, out ObjectSnapshot fixedSnapshot)
    {
        fixedSnapshot = snapshot;
        ObjectHousingModeState state = housingModePolicy.GetState();
        if (!state.IsHousingMode
            || !placementResolver.TryResolveFromPlayer(out ObjectTransform placementTransform))
        {
            return false;
        }

        HousingPlacementContext placementContext = locationService.ResolveHousingPlacementContext(state);
        if (nativeQuery.CheckPlacementAreaContainment(placementContext, placementTransform.Position) != PlacementValidationStatus.Valid)
        {
            return false;
        }

        fixedSnapshot = snapshot with
        {
            Transform = snapshot.Transform with
            {
                Position = placementTransform.Position,
            },
        };
        return true;
    }

    private static bool TryClearAttachmentParent(ObjectSnapshot snapshot, out ObjectSnapshot fixedSnapshot)
    {
        fixedSnapshot = snapshot;
        if (snapshot.Model is not FurnitureModel { AttachmentParentId: not null } furnitureModel)
        {
            return false;
        }

        fixedSnapshot = snapshot with
        {
            Model = furnitureModel with
            {
                AttachmentParentId = null,
            },
        };
        return true;
    }

    private static PlacementFixProposal CreateSnapToSurfaceFix(Guid objectId)
        => new(
            objectId,
            PlacementFixKind.SnapToSurface,
            "Snap to surface",
            "Move the furniture onto the valid housing surface below it.");

    private static PlacementFixProposal CreateMoveToPlayerPlacementFix(Guid objectId)
        => new(
            objectId,
            PlacementFixKind.MoveToPlayerPlacement,
            "Move inside area",
            "Move the furniture to the current player placement point if it is inside the housing area.");

    private static PlacementFixProposal CreateClearAttachmentParentFix(Guid objectId)
        => new(
            objectId,
            PlacementFixKind.ClearAttachmentParent,
            "Clear attachment",
            "Remove the imported furniture parent link.");
}

