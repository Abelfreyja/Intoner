using Intoner.Objects.Models;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.Runtime;

/// <summary> exposes placed objects through the shared scene item contract </summary>
internal sealed class ObjectSceneDomain : ISceneItemDomain
{
    private readonly IObjectSceneView _sceneView;
    private readonly IObjectSceneState _sceneState;
    private readonly IObjectSceneMutationService _mutationService;

    public SceneItemManipulationPolicy Manipulation { get; }

    public ObjectSceneDomain(
        IObjectSceneView sceneView,
        IObjectSceneState sceneState,
        IObjectSceneMutationService mutationService,
        SurfacePlacementService placementService,
        SurfaceAttachmentService attachmentService)
    {
        _sceneView = sceneView;
        _sceneState = sceneState;
        _mutationService = mutationService;
        Manipulation = new ObjectSceneItemManipulation(
            sceneView,
            placementService,
            attachmentService);
    }

    public SceneRevisions Revisions
        => new(
            unchecked(_sceneView.GetSceneRevision() + _sceneState.GetActiveRevision()),
            _sceneView.GetPersistentSceneRevision(),
            _sceneState.GetBoundsRevision());

    public IReadOnlyList<SceneItemSnapshot> GetPlacedItems()
        => _sceneView.GetPlacedObjectSnapshots();

    public IReadOnlyList<SceneItemSnapshot> GetActiveItems()
        => _sceneView.GetObjectSnapshots();

    public IReadOnlyList<ISceneItemRuntime> GetRuntimeItems()
        => _sceneState.GetEntriesSnapshot()
            .Select(static entry => (ISceneItemRuntime)entry.Runtime)
            .ToList();

    public IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots()
        => _sceneState.GetBoundsSnapshots();

    public bool CanHandle(SceneItemSnapshot snapshot)
        => snapshot is ObjectSnapshot;

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
        => _mutationService.ApplySnapshotChanges(changes);

    public SceneMutationStatus PreviewChange(SceneItemSnapshot before, SceneItemSnapshot after, out SceneItemSnapshot applied)
    {
        applied = before;
        if (before is not ObjectSnapshot previous || after is not ObjectSnapshot next)
        {
            return SceneMutationStatus.Rejected;
        }

        SceneMutationStatus status = _mutationService.PreviewChange(previous, next, out ObjectSnapshot result);
        applied = result;
        return status;
    }

    public void RequestPreviewRecovery()
        => _sceneState.MarkNeedsRefresh();

    public bool TryCreateDuplicates(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates)
    {
        duplicates = [];
        if (snapshots.Any(static snapshot => snapshot is not ObjectSnapshot))
        {
            return false;
        }

        bool created = _mutationService.TryCreateDuplicates(
            snapshots.Cast<ObjectSnapshot>().ToList(),
            out IReadOnlyList<ObjectSnapshot> objectDuplicates);
        duplicates = objectDuplicates;
        return created;
    }

    private sealed class ObjectSceneItemManipulation(
        IObjectSceneView sceneView,
        SurfacePlacementService placementService,
        SurfaceAttachmentService attachmentService) : SceneItemManipulationPolicy
    {
        public override SceneItemManipulation Describe(SceneItemSnapshot snapshot)
        {
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
            return placementService.TryResolvePlacementHit(
                RequireObject(snapshot),
                bounds as ObjectBoundsSnapshot,
                rayOrigin,
                rayDirection,
                out hit);
        }

        public override bool UsesSurfaceOrigin(SceneItemSnapshot snapshot, SceneSurfaceHit hit)
            => placementService.ShouldUseNativePlacementOrigin(
                RequireObject(snapshot),
                hit);

        public override SceneItemSnapshot ApplySurfaceState(SceneItemSnapshot snapshot, SceneSurfaceHit? hit)
        {
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
