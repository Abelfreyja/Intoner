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
        List<SceneItemSnapshotChange> changes = new(Math.Max(beforeById.Count, afterById.Count));
        foreach ((Guid id, SceneItemSnapshot before) in beforeById)
        {
            SceneItemSnapshotChange change = new(before, afterById.GetValueOrDefault(id));
            if (change.HasChange)
            {
                changes.Add(change);
            }
        }

        foreach ((Guid id, SceneItemSnapshot after) in afterById)
        {
            if (!beforeById.ContainsKey(id))
            {
                changes.Add(new SceneItemSnapshotChange(null, after));
            }
        }

        return changes;
    }

    public static bool ItemsMatch<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) where T : SceneItemSnapshot
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        Dictionary<Guid, T> items = left.ToDictionary(static item => item.Id);
        for (int index = 0; index < right.Count; ++index)
        {
            T item = right[index];

            if (!Equals(items.GetValueOrDefault(item.Id), item))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary> replays scene item snapshot changes through their owning domains </summary>
internal sealed class SceneItemHistoryChange
{
    private readonly ISceneItemService _sceneItems;
    private readonly IReadOnlyList<SceneItemSnapshotChange> _forward;
    private readonly IReadOnlyList<SceneItemSnapshotChange> _reverse;
    private readonly SceneItemHistoryDependencies _beforeDependencies;
    private readonly SceneItemHistoryDependencies _afterDependencies;
    private long _validationRevision = long.MinValue;
    private bool _validationApply;
    private SceneHistoryResult _validation;

    public SceneItemHistoryChange(ISceneItemService sceneItems, string title,
        IReadOnlyList<SceneItemSnapshotChange> changes, IReadOnlyList<SceneItemSnapshot> unchanged,
        IReadOnlyList<SceneItemSnapshot> stored)
    {
        _sceneItems = sceneItems;
        Title = title;
        _forward = [.. changes];
        _reverse = changes.Reverse().Select(static change => change.Reverse()).ToArray();
        (_beforeDependencies, _afterDependencies) = changes.Count == 0 && unchanged.Count == 0
            ? (SceneItemHistoryDependencies.Empty, SceneItemHistoryDependencies.Empty)
            : SceneItemHistoryDependencies.Capture(stored, changes, unchanged);
    }

    public string Title { get; }
    public bool HasChanges => _forward.Count > 0;

    public SceneHistoryResult Validate(bool apply)
    {
        long revision = _sceneItems.GetHistoryRevision();

        if (revision != _validationRevision || apply != _validationApply)
        {
            _validation = Describe(_sceneItems.CheckStoredChanges(GetChanges(apply), GetDependencies(apply)), apply);
            _validationRevision = revision;
            _validationApply = apply;
        }

        return _validation;
    }

    internal IReadOnlyList<SceneItemSnapshotChange> GetChanges(bool apply) => apply ? _forward : _reverse;
    internal SceneItemHistoryDependencies GetDependencies(bool apply) => apply ? _beforeDependencies : _afterDependencies;
    internal long HistoryRevision => _sceneItems.GetHistoryRevision();

    public SceneHistoryResult Replay(bool apply)
        => Describe(_sceneItems.ApplyStoredChanges(GetChanges(apply), GetDependencies(apply)), apply);

    public SceneHistoryResult Describe(SceneHistoryResult result, bool apply)
        => result.WithActionContext(Title, apply);

}

/// <summary> replays checked item transitions from scene history </summary>
internal sealed class SceneItemHistoryAction : SceneHistoryActionBase
{
    private readonly SceneItemHistoryChange _change;

    public SceneItemHistoryAction(SceneHistoryKind kind, SceneItemHistoryChange change)
        : base(change.Title, kind)
        => _change = change;

    public override SceneHistoryResult Validate(bool apply) => _change.Validate(apply);
    protected override SceneHistoryResult ApplyCore() => _change.Replay(apply: true);
    protected override SceneHistoryResult RevertCore() => _change.Replay(apply: false);
}

/// <summary> restores editor selection after replaying one scene history action </summary>
internal sealed class SelectionHistoryAction : SceneHistoryActionBase
{
    private readonly ISceneHistoryAction _action;

    public SelectionHistoryAction(ISceneHistoryAction action, IReadOnlyList<Guid>? selectionAfterApply, IReadOnlyList<Guid>? selectionAfterRevert)
        : base(action.Title, action.Kind)
    {
        _action = action;
        SelectionAfterApply = selectionAfterApply is null ? null : [.. selectionAfterApply];
        SelectionAfterRevert = selectionAfterRevert is null ? null : [.. selectionAfterRevert];
    }

    public IReadOnlyList<Guid>? SelectionAfterApply { get; }
    public IReadOnlyList<Guid>? SelectionAfterRevert { get; }
    public override SceneHistoryResult Validate(bool apply) => _action.Validate(apply);
    protected override SceneHistoryResult ApplyCore() => _action.Apply();
    protected override SceneHistoryResult RevertCore() => _action.Revert();

    internal override void MarkRecordedApplied()
    {
        base.MarkRecordedApplied();

        if (_action is SceneHistoryActionBase historyAction)
        {
            historyAction.MarkRecordedApplied();
        }
    }
}
