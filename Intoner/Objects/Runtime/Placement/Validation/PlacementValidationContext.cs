using Intoner.Objects.Catalog;
using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal readonly record struct HousingPolicyValidationState(
    ObjectHousingModeState HousingModeState,
    int FurnitureCount,
    IReadOnlySet<Guid> FurnitureLimitOverflowIds,
    int FurnitureSetSignature);

internal sealed class PlacementValidationContext(
    IReadOnlyList<ObjectSnapshot> snapshots,
    IReadOnlyDictionary<Guid, ObjectSnapshot> snapshotsById,
    IReadOnlyDictionary<Guid, ObjectBoundsSnapshot> boundsById,
    IReadOnlyDictionary<Guid, HousingFurnitureMetadata> metadataById,
    HousingPolicyValidationState policyState,
    HousingPlacementContext housingPlacementContext,
    int footprintSignature,
    int attachmentSignature)
{
    public IReadOnlyList<ObjectSnapshot> Snapshots { get; } = snapshots;
    public IReadOnlyDictionary<Guid, ObjectSnapshot> SnapshotsById { get; } = snapshotsById;
    public IReadOnlyDictionary<Guid, ObjectBoundsSnapshot> BoundsById { get; } = boundsById;
    public ObjectHousingModeState HousingModeState
        => _policyState.HousingModeState;

    public HousingPlacementContext HousingPlacementContext { get; } = housingPlacementContext;
    public int FurnitureCount
        => _policyState.FurnitureCount;

    public int FurnitureSetSignature
        => _policyState.FurnitureSetSignature;

    public int FootprintSignature { get; } = footprintSignature;
    public int AttachmentSignature { get; } = attachmentSignature;

    private readonly IReadOnlyDictionary<Guid, HousingFurnitureMetadata> _metadataById = metadataById;
    private readonly HousingPolicyValidationState _policyState = policyState;

    public bool TryGetBounds(Guid objectId, out ObjectBoundsSnapshot boundsSnapshot)
    {
        if (BoundsById.TryGetValue(objectId, out ObjectBoundsSnapshot? resolvedBounds)
            && resolvedBounds is not null)
        {
            boundsSnapshot = resolvedBounds;
            return true;
        }

        boundsSnapshot = default!;
        return false;
    }

    public bool TryGetMetadata(Guid objectId, out HousingFurnitureMetadata metadata)
    {
        if (_metadataById.TryGetValue(objectId, out HousingFurnitureMetadata? resolvedMetadata)
            && resolvedMetadata is not null)
        {
            metadata = resolvedMetadata;
            return true;
        }

        metadata = default!;
        return false;
    }

    public bool IsFurnitureLimitOverflow(Guid objectId)
        => _policyState.FurnitureLimitOverflowIds.Contains(objectId);
}

internal sealed class PlacementValidationContextBuilder(FurnitureMetadataResolver metadataResolver)
{
    public PlacementValidationContext Build(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        ObjectHousingModeState housingModeState,
        HousingPlacementContext housingPlacementContext)
    {
        Dictionary<Guid, ObjectSnapshot> snapshotsById = BuildSnapshotLookup(snapshots);
        Dictionary<Guid, ObjectBoundsSnapshot> boundsById = BuildBoundsLookup(boundsSnapshots);
        Dictionary<Guid, HousingFurnitureMetadata> metadataById = BuildFurnitureMetadataLookup(snapshots);
        return new PlacementValidationContext(
            snapshots,
            snapshotsById,
            boundsById,
            metadataById,
            BuildHousingPolicyState(snapshots, housingModeState),
            housingPlacementContext,
            BuildFootprintSignature(snapshots, metadataById),
            BuildAttachmentSignature(snapshots, snapshotsById, boundsById));
    }

    private static Dictionary<Guid, ObjectSnapshot> BuildSnapshotLookup(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        Dictionary<Guid, ObjectSnapshot> snapshotsById = new(snapshots.Count);
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            snapshotsById[snapshot.Id] = snapshot;
        }

        return snapshotsById;
    }

    private static Dictionary<Guid, ObjectBoundsSnapshot> BuildBoundsLookup(IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
    {
        Dictionary<Guid, ObjectBoundsSnapshot> boundsById = new(boundsSnapshots.Count);
        foreach (ObjectBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            boundsById[boundsSnapshot.Id] = boundsSnapshot;
        }

        return boundsById;
    }

    private Dictionary<Guid, HousingFurnitureMetadata> BuildFurnitureMetadataLookup(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        Dictionary<Guid, HousingFurnitureMetadata> metadataById = [];
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (metadataResolver.TryResolve(snapshot, out HousingFurnitureMetadata metadata))
            {
                metadataById[snapshot.Id] = metadata;
            }
        }

        return metadataById;
    }

    private static HousingPolicyValidationState BuildHousingPolicyState(
        IReadOnlyList<ObjectSnapshot> snapshots,
        ObjectHousingModeState housingModeState)
    {
        List<ObjectSnapshot> furnitureSnapshots = BuildOrderedFurnitureSnapshots(snapshots);
        return new HousingPolicyValidationState(
            housingModeState,
            furnitureSnapshots.Count,
            BuildFurnitureLimitOverflowIds(furnitureSnapshots, housingModeState.FurnitureLimit),
            BuildFurnitureSetSignature(furnitureSnapshots));
    }

    private static List<ObjectSnapshot> BuildOrderedFurnitureSnapshots(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        List<ObjectSnapshot> furnitureSnapshots = [];
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (snapshot.Kind == ObjectKind.Furniture)
            {
                furnitureSnapshots.Add(snapshot);
            }
        }

        furnitureSnapshots.Sort(static (left, right) =>
        {
            int createdComparison = left.CreatedAtUtc.CompareTo(right.CreatedAtUtc);
            return createdComparison != 0
                ? createdComparison
                : left.Id.CompareTo(right.Id);
        });
        return furnitureSnapshots;
    }

    private static HashSet<Guid> BuildFurnitureLimitOverflowIds(
        IReadOnlyList<ObjectSnapshot> furnitureSnapshots,
        int furnitureLimit)
    {
        int firstOverflowIndex = Math.Clamp(furnitureLimit, 0, furnitureSnapshots.Count);
        if (firstOverflowIndex == furnitureSnapshots.Count)
        {
            return [];
        }

        HashSet<Guid> overflowIds = [];
        for (int index = firstOverflowIndex; index < furnitureSnapshots.Count; ++index)
        {
            overflowIds.Add(furnitureSnapshots[index].Id);
        }

        return overflowIds;
    }

    private static int BuildFurnitureSetSignature(IReadOnlyList<ObjectSnapshot> furnitureSnapshots)
    {
        HashCode hashCode = new();
        foreach (ObjectSnapshot snapshot in furnitureSnapshots)
        {
            hashCode.Add(snapshot.Id);
            hashCode.Add(snapshot.CreatedAtUtc);
        }

        return hashCode.ToHashCode();
    }

    private static int BuildFootprintSignature(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyDictionary<Guid, HousingFurnitureMetadata> metadataById)
    {
        HashCode hashCode = new();
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (!metadataById.TryGetValue(snapshot.Id, out HousingFurnitureMetadata? metadata)
                || !metadata.HasAquariumFootprint)
            {
                continue;
            }

            hashCode.Add(snapshot.Id);
            hashCode.Add(snapshot.Transform.Position);
            hashCode.Add(snapshot.Transform.RotationDegrees.Y);
            hashCode.Add(metadata.PileFootprint);
        }

        return hashCode.ToHashCode();
    }

    private static int BuildAttachmentSignature(
        IReadOnlyList<ObjectSnapshot> snapshots,
        IReadOnlyDictionary<Guid, ObjectSnapshot> snapshotsById,
        IReadOnlyDictionary<Guid, ObjectBoundsSnapshot> boundsById)
    {
        HashCode hashCode = new();
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (snapshot.Model is not FurnitureModel { AttachmentParentId: { } parentId })
            {
                continue;
            }

            hashCode.Add(snapshot.Id);
            hashCode.Add(parentId);
            if (snapshotsById.TryGetValue(parentId, out ObjectSnapshot? parentSnapshot)
                && parentSnapshot is not null)
            {
                hashCode.Add(parentSnapshot.Transform);
            }

            if (boundsById.TryGetValue(parentId, out ObjectBoundsSnapshot? parentBounds)
                && parentBounds is not null)
            {
                hashCode.Add(parentBounds);
            }
        }

        return hashCode.ToHashCode();
    }
}

