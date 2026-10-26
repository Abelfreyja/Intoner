using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.Runtime;

/// <summary> exposes placed objects through the shared scene item contract </summary>
internal sealed class ObjectSceneDomain : ISceneItemDomain
{
    private readonly IObjectSceneView _sceneView;
    private readonly ISceneLocationService _locationService;
    private readonly IObjectSceneState _sceneState;
    private readonly IObjectSceneMutationService _mutationService;
    private readonly IObjectPersistenceState _persistence;
    private readonly IObjectScene _scene;
    private readonly Dictionary<Guid, SceneGroupSnapshot> _groupPreviews = [];
    private long _groupPreviewRevision;
    private long _groupPreviewPersistentRevision;

    public SceneItemManipulationPolicy Manipulation { get; }

    public ObjectSceneDomain(
        IObjectSceneView sceneView,
        IObjectSceneState sceneState,
        IObjectSceneMutationService mutationService,
        SurfacePlacementService placementService,
        SurfaceAttachmentService attachmentService,
        IObjectPersistenceState persistence,
        IObjectScene scene,
        ISceneLocationService locationService)
    {
        _sceneView = sceneView;
        _locationService = locationService;
        _sceneState = sceneState;
        _mutationService = mutationService;
        _persistence = persistence;
        _scene = scene;
        Manipulation = new ObjectSceneItemManipulation(
            sceneView,
            placementService,
            attachmentService);
    }

    public SceneRevisions Revisions
        => new(
            unchecked(_sceneView.GetSceneRevision() + _sceneState.GetActiveRevision() + _groupPreviewRevision),
            _sceneView.GetPersistentSceneRevision(),
            _sceneState.GetBoundsRevision());

    public long StoredRevision => _persistence.StoredRevision;

    public IReadOnlyList<SceneItemSnapshot> GetStoredItems()
    {
        List<SceneItemSnapshot> items = [.. _persistence.GetStandaloneSnapshots(), .. _persistence.GetStandaloneGroups()];
        foreach (ObjectLayoutSnapshot layout in _persistence.GetStoredLayouts())
        {
            items.AddRange(layout.Objects);
            items.AddRange(layout.Groups);
        }

        return items;
    }

    public SceneHistoryResult ValidateStoredOwner(SceneItemSnapshot snapshot)
        => snapshot is ObjectSceneItemSnapshot { LayoutId: Guid owner } && !_persistence.HasStoredOwner(owner)
            ? SceneHistoryResult.Conflict("The recorded layout was removed externally.") : SceneHistoryResult.Success;

    public SceneMutationStatus ApplyStoredChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
        => _mutationService.ApplyStoredSnapshotChanges(changes);

    public void ReconcileStoredChanges()
    {
        if (!_sceneState.GetRefreshState().NeedsRefresh || !_scene.ShouldApplyChangesNow())
        {
            return;
        }

        if (!_scene.ReloadForCurrentLocation().IsApplied)
        {
            _scene.MarkNeedsRefresh();
        }
    }

    public IReadOnlyList<SceneItemSnapshot> GetPlacedItems()
        => [.. _sceneView.GetPlacedObjectSnapshots(), .. _persistence.GetPersistedGroups()];

    public IReadOnlyList<SceneItemSnapshot> GetActiveItems()
    {
        RefreshGroupPreviews();
        IReadOnlyList<ObjectSnapshot> active = _sceneView.GetObjectSnapshots();
        IReadOnlyList<SceneGroupSnapshot> groups = _persistence.GetPersistedGroups();
        if (groups.Count == 0)
            return active;
        HashSet<Guid> activeIds = active.Select(static snapshot => snapshot.Id).ToHashSet();
        HashSet<Guid> inactiveGroups = [];
        foreach (ObjectSnapshot member in _sceneView.GetPlacedObjectSnapshots())
        {
            if (member.GroupId is Guid groupId && !activeIds.Contains(member.Id))
                inactiveGroups.Add(groupId);
        }
        List<SceneItemSnapshot> items = [.. active];
        SceneLocationScope currentLocation = _locationService.GetCurrentLocationScope();
        foreach (SceneGroupSnapshot group in groups)
        {
            if (group.CreatedIn.IsAssigned && !inactiveGroups.Contains(group.Id)
                && _locationService.MatchesLocation(group.CreatedIn, currentLocation))
            {
                items.Add(_groupPreviews.GetValueOrDefault(group.Id, group));
            }
        }
        return items;
    }

    public IReadOnlyList<ISceneItemRuntime> GetRuntimeItems()
        => _sceneState.GetEntriesSnapshot()
            .Select(static entry => (ISceneItemRuntime)entry.Runtime)
            .ToList();

    public IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots()
        => _sceneState.GetBoundsSnapshots();

    public bool CanHandle(SceneItemSnapshot snapshot)
        => snapshot is ObjectSnapshot or SceneGroupSnapshot;

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
        => _mutationService.ApplySnapshotChanges(changes);

    public SceneMutationStatus PreviewChange(SceneItemSnapshot before, SceneItemSnapshot after, out SceneItemSnapshot applied)
    {
        applied = before;
        if (before is SceneGroupSnapshot previousGroup && after is SceneGroupSnapshot nextGroup)
        {
            RefreshGroupPreviews();
            SceneGroupSnapshot? persisted = _persistence.GetPersistedGroups().FirstOrDefault(group => group.Id == previousGroup.Id);
            if (persisted is not null && nextGroup == persisted && !_groupPreviews.ContainsKey(persisted.Id))
            {
                applied = persisted;
                return SceneMutationStatus.Applied;
            }
            if (persisted is null || _groupPreviews.GetValueOrDefault(previousGroup.Id, persisted) != previousGroup
                || nextGroup with { Transform = previousGroup.Transform } != previousGroup
                || !SceneGroupUtility.TryValidateOwner(nextGroup.LayoutId, [], [nextGroup], out _))
                return SceneMutationStatus.Rejected;
            if (nextGroup == persisted)
                _groupPreviews.Remove(nextGroup.Id);
            else
                _groupPreviews[nextGroup.Id] = nextGroup;
            _groupPreviewRevision++;
            applied = nextGroup;
            return SceneMutationStatus.Applied;
        }
        if (before is not ObjectSnapshot previous || after is not ObjectSnapshot next)
        {
            return SceneMutationStatus.Rejected;
        }

        SceneMutationStatus status = _mutationService.PreviewChange(previous, next, out ObjectSnapshot result);
        applied = result;
        return status;
    }

    public void RequestPreviewRecovery()
    {
        _groupPreviews.Clear();
        _groupPreviewRevision++;
        _sceneState.MarkNeedsRefresh();
    }

    private void RefreshGroupPreviews()
    {
        long revision = _sceneView.GetPersistentSceneRevision();
        if (_groupPreviewPersistentRevision == revision)
            return;

        _groupPreviewPersistentRevision = revision;
        if (_groupPreviews.Count > 0)
        {
            _groupPreviews.Clear();
            _groupPreviewRevision++;
        }
    }

    public bool TryCreateDuplicates(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates)
        => _mutationService.TryCreateDuplicates(snapshots, out duplicates);

    private sealed class ObjectSceneItemManipulation(
        IObjectSceneView sceneView,
        SurfacePlacementService placementService,
        SurfaceAttachmentService attachmentService) : SceneItemManipulationPolicy
    {
        public override SceneItemManipulation Describe(SceneItemSnapshot snapshot)
        {
            if (snapshot is SceneGroupSnapshot)
                return new SceneItemManipulation(true, Vector3.UnitY, false, SupportsSurfaceDrag: true);
            ObjectSnapshot objectSnapshot = RequireObject(snapshot);
            bool alignWallSurface = placementService.ShouldAlignWallSurface(objectSnapshot);
            float? gizmoAxisLength = objectSnapshot.Model is LightModel light
                ? Math.Clamp(light.Shape.Range * 0.2f, 0.35f, 3f)
                : null;
            return new SceneItemManipulation(
                SupportsScale: objectSnapshot.Kind is ObjectKind.BgObject or ObjectKind.Furniture,
                SurfaceAlignmentAxis: alignWallSurface ? Vector3.UnitZ : Vector3.UnitY,
                ForceSurfaceAlignment: alignWallSurface,
                GizmoAxisLength: gizmoAxisLength,
                SupportsSurfaceDrag: true);
        }

        public override bool TryResolveSurfaceHit(
            SceneItemSnapshot snapshot,
            SceneItemBoundsSnapshot? bounds,
            Vector3 rayOrigin,
            Vector3 rayDirection,
            out SceneSurfaceHit hit)
        {
            if (snapshot is SceneGroupSnapshot)
            {
                hit = SceneSurfaceHit.Empty;
                return false;
            }
            return placementService.TryResolvePlacementHit(
                RequireObject(snapshot),
                bounds as ObjectBoundsSnapshot,
                rayOrigin,
                rayDirection,
                out hit);
        }

        public override bool UsesSurfaceOrigin(SceneItemSnapshot snapshot, SceneSurfaceHit hit)
            => snapshot is ObjectSnapshot && placementService.ShouldUseNativePlacementOrigin(
                RequireObject(snapshot),
                hit);

        public override SceneItemSnapshot ApplySurfaceState(SceneItemSnapshot snapshot, SceneSurfaceHit? hit)
        {
            if (snapshot is SceneGroupSnapshot)
                return snapshot;
            return attachmentService.ApplySurfaceDragAttachment(
                RequireObject(snapshot),
                hit,
                sceneView.GetObjectBoundsSnapshots());
        }

        private static ObjectSnapshot RequireObject(SceneItemSnapshot snapshot)
            => snapshot as ObjectSnapshot
               ?? throw new ArgumentException("object manipulation requires an object snapshot", nameof(snapshot));
    }
}
