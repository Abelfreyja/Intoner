namespace Intoner.Scene;

/// <summary> builds ordered scene item changes from before and after snapshots </summary>
internal static class SceneItemHistoryChanges
{
    public static IReadOnlyList<SceneItemSnapshotChange> Build(
        IReadOnlyList<SceneItemSnapshot> beforeSnapshots,
        IReadOnlyList<SceneItemSnapshot> afterSnapshots)
    {
        var beforeById = new Dictionary<Guid, SceneItemSnapshot>(beforeSnapshots.Count);
        foreach (SceneItemSnapshot snapshot in beforeSnapshots)
        {
            beforeById[snapshot.Id] = snapshot;
        }

        var afterById = new Dictionary<Guid, SceneItemSnapshot>(afterSnapshots.Count);
        foreach (SceneItemSnapshot snapshot in afterSnapshots)
        {
            afterById[snapshot.Id] = snapshot;
        }
        var orderedIds = beforeSnapshots
            .Select(static snapshot => snapshot.Id)
            .Concat(afterSnapshots.Select(static snapshot => snapshot.Id))
            .Distinct()
            .ToList();
        var changes = new List<SceneItemSnapshotChange>(orderedIds.Count);
        foreach (Guid id in orderedIds)
        {
            beforeById.TryGetValue(id, out SceneItemSnapshot? before);
            afterById.TryGetValue(id, out SceneItemSnapshot? after);
            var change = new SceneItemSnapshotChange(before, after);
            if (change.HasChange)
            {
                changes.Add(change);
            }
        }

        return changes;
    }
}

/// <summary> replays scene item snapshot changes through their owning domains </summary>
internal sealed class SceneItemHistoryAction : SceneHistoryActionBase
{
    private readonly ISceneItemService _sceneItemService;
    private readonly IReadOnlyList<SceneItemSnapshotChange> _forwardChanges;
    private readonly IReadOnlyList<SceneItemSnapshotChange> _reverseChanges;

    public SceneItemHistoryAction(
        ISceneItemService sceneItemService,
        SceneHistoryKind kind,
        string title,
        IReadOnlyList<SceneItemSnapshotChange> changes)
        : base(title, kind)
    {
        _sceneItemService = sceneItemService;
        _forwardChanges = [.. changes];
        _reverseChanges = changes.Reverse().Select(static change => change.Reverse()).ToList();
    }

    protected override void ApplyCore()
        => ApplyChanges(_forwardChanges, "apply");

    protected override void RevertCore()
        => ApplyChanges(_reverseChanges, "revert");

    private void ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes, string operation)
    {
        SceneMutationStatus status = _sceneItemService.ApplyChanges(changes);
        if (!status.IsApplied())
        {
            throw new InvalidOperationException($"could not {operation} '{Title}': {status}");
        }
    }
}

/// <summary> restores editor selection after replaying one scene history action </summary>
internal sealed class SelectionHistoryAction : SceneHistoryActionBase
{
    private readonly ISceneHistoryAction _action;

    public SelectionHistoryAction(
        ISceneHistoryAction action,
        IReadOnlyList<Guid>? selectionAfterApply,
        IReadOnlyList<Guid>? selectionAfterRevert)
        : base(action.Title, action.Kind)
    {
        _action = action;
        SelectionAfterApply = selectionAfterApply is null ? null : [.. selectionAfterApply];
        SelectionAfterRevert = selectionAfterRevert is null ? null : [.. selectionAfterRevert];
    }

    public IReadOnlyList<Guid>? SelectionAfterApply { get; }
    public IReadOnlyList<Guid>? SelectionAfterRevert { get; }

    protected override void ApplyCore()
        => _action.Apply();

    protected override void RevertCore()
        => _action.Revert();

    internal override void MarkRecordedApplied()
    {
        base.MarkRecordedApplied();
        if (_action is SceneHistoryActionBase historyAction)
        {
            historyAction.MarkRecordedApplied();
        }
    }
}
