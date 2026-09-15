using Intoner.Scene;
using System.Numerics;

namespace Intoner.Displays;

/// <summary> exposes displays through the shared scene item contract </summary>
internal sealed class DisplaySceneDomain : ISceneItemDomain
{
    private readonly DisplayState _state;
    private readonly DisplayRuntimeManager _runtimes;

    public DisplaySceneDomain(DisplayState state, DisplayRuntimeManager runtimes)
    {
        _state = state;
        _runtimes = runtimes;
    }

    public SceneRevisions Revisions
        => new(
            _runtimes.ActiveRevision,
            _state.Revision,
            _runtimes.BoundsRevision);

    public SceneItemManipulationPolicy Manipulation
        => DisplayManipulation.Instance;

    public IReadOnlyList<SceneItemSnapshot> GetPlacedItems()
        => _state.GetSnapshots();

    public IReadOnlyList<SceneItemSnapshot> GetActiveItems()
        => _runtimes.GetActiveItems();

    public IReadOnlyList<ISceneItemRuntime> GetRuntimeItems()
        => _runtimes.GetRuntimeItems();

    public IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots()
        => _runtimes.GetBoundsSnapshots();

    public bool CanHandle(SceneItemSnapshot snapshot)
        => snapshot is DisplaySnapshot;

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        long previousRevision = _state.Revision;
        if (!_state.TryApplyChanges(changes))
        {
            return SceneMutationStatus.Rejected;
        }

        return previousRevision == _state.Revision || _runtimes.ReconcileAfterMutation()
            ? SceneMutationStatus.Applied
            : SceneMutationStatus.AppliedWithRuntimeFailure;
    }

    public SceneMutationStatus PreviewChange(SceneItemSnapshot before, SceneItemSnapshot after, out SceneItemSnapshot applied)
    {
        applied = before;
        if (before is not DisplaySnapshot previous || after is not DisplaySnapshot next
            || next with { Transform = previous.Transform } != previous)
        {
            return SceneMutationStatus.Rejected;
        }

        SceneMutationStatus status = _runtimes.PreviewChange(previous, DisplayState.Sanitize(next), out DisplaySnapshot result);
        applied = result;
        return status;
    }

    public void RequestPreviewRecovery()
        => _runtimes.RequestPreviewRecovery();

    public bool TryCreateDuplicates(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates)
    {
        duplicates = [];
        if (snapshots.Any(static snapshot => snapshot is not DisplaySnapshot))
        {
            return false;
        }

        bool created = _state.TryCreateDuplicates(
            snapshots.Cast<DisplaySnapshot>().ToList(),
            out IReadOnlyList<DisplaySnapshot> displayDuplicates);
        duplicates = displayDuplicates;
        return created;
    }

    private sealed class DisplayManipulation : SceneItemManipulationPolicy
    {
        public static DisplayManipulation Instance { get; } = new();

        public override SceneItemManipulation Describe(SceneItemSnapshot snapshot)
        {
            if (snapshot is not DisplaySnapshot)
            {
                throw new ArgumentException("display manipulation requires a display snapshot", nameof(snapshot));
            }

            return new SceneItemManipulation(
                SupportsScale: true,
                SurfaceAlignmentAxis: Vector3.UnitZ,
                ForceSurfaceAlignment: false,
                SupportsSurfaceDrag: true);
        }
    }
}
