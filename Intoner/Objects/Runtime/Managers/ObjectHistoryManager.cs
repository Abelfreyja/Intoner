using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Intoner.Objects.Runtime;

/// <summary> identifies the main kind of change represented by a history action </summary>
internal enum ObjectHistoryKind
{
    Create,
    Import,
    Move,
    Transform,
    Organization,
    Appearance,
    Visibility,
    Remove,
    Clear,
}

/// <summary> describes one reachable point in object history </summary>
internal readonly record struct ObjectHistoryEntry(
    int StateIndex,
    ObjectHistoryKind? Kind,
    string Title,
    string? CheckpointLabel,
    DateTime RecordedAtUtc)
{
    public bool IsInitialState => StateIndex == 0;
    public bool HasCheckpoint => !string.IsNullOrWhiteSpace(CheckpointLabel);
}

/// <summary> stores one internal history step and its timeline metadata </summary>
internal sealed class ObjectHistoryStep : IDisposable
{
    public ObjectHistoryStep(IObjectHistoryAction? action, ObjectHistoryKind? kind, string title, DateTime recordedAtUtc)
    {
        Action = action;
        Kind = kind;
        Title = title;
        RecordedAtUtc = recordedAtUtc;
    }

    public IObjectHistoryAction? Action { get; }
    public ObjectHistoryKind? Kind { get; }
    public string Title { get; }
    public string? CheckpointLabel { get; set; }
    public DateTime RecordedAtUtc { get; }

    public ObjectHistoryEntry ToEntry(int stateIndex)
        => new(stateIndex, Kind, Title, CheckpointLabel, RecordedAtUtc);

    public void Dispose()
        => Action?.Dispose();
}

/// <summary> provides shared labels for object history description </summary>
internal static class ObjectHistoryDescription
{
    /// <summary> gets the label for a history kind </summary>
    public static string GetKindLabel(ObjectHistoryKind kind)
        => kind switch
        {
            ObjectHistoryKind.Create       => "Create",
            ObjectHistoryKind.Import       => "Import",
            ObjectHistoryKind.Move         => "Move",
            ObjectHistoryKind.Transform    => "Transform",
            ObjectHistoryKind.Organization => "Organization",
            ObjectHistoryKind.Appearance   => "Appearance",
            ObjectHistoryKind.Visibility   => "Visibility",
            ObjectHistoryKind.Remove       => "Remove",
            ObjectHistoryKind.Clear        => "Clear",
            _                              => "Edit",
        };

    /// <summary> gets the display title for a history action </summary>
    public static string GetActionTitle(ObjectHistoryKind kind, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return GetKindLabel(kind);
        }

        return ObjectStringUtility.TrimOrFallback(title, GetKindLabel(kind));
    }
}

/// <summary> describes one object history action that can be applied, reverted, and replayed </summary>
internal interface IObjectHistoryAction : IDisposable
{
    /// <summary> gets the history title </summary>
    string Title { get; }

    /// <summary> gets the main kind of change represented by this history action </summary>
    ObjectHistoryKind Kind { get; }

    /// <summary> applies the history action </summary>
    void Apply();

    /// <summary> reverts the history action </summary>
    void Revert();
}

/// <summary> base class for object history actions </summary>
internal abstract class ObjectHistoryActionBase : IObjectHistoryAction
{
    private enum ActionState
    {
        Pending,
        Applied,
    }

    private ActionState _state;

    protected ObjectHistoryActionBase(string title, ObjectHistoryKind kind)
    {
        Title = title;
        Kind = kind;
    }

    public string Title { get; protected set; }
    public ObjectHistoryKind Kind { get; protected set; }
    protected bool IsApplied => _state == ActionState.Applied;

    public void Apply()
    {
        Debug.Assert(_state != ActionState.Applied, "history action is already applied");
        ApplyCore();
        _state = ActionState.Applied;
    }

    public void Revert()
    {
        Debug.Assert(_state == ActionState.Applied, "history action is not applied");
        RevertCore();
        _state = ActionState.Pending;
    }

    public virtual void Dispose()
    {
    }

    internal void MarkApplied()
        => _state = ActionState.Applied;

    internal virtual void MarkRecordedApplied()
        => MarkApplied();

    protected abstract void ApplyCore();
    protected abstract void RevertCore();
}

/// <summary> describes one object snapshot mutation inside a history action </summary>
internal readonly record struct ObjectSnapshotChange(ObjectSnapshot? Before, ObjectSnapshot? After)
{
    public Guid ObjectId => After?.Id ?? Before?.Id ?? Guid.Empty;

    public bool HasChange => !Equals(Before, After);
}

/// <summary> replays ordered snapshot changes against object mutation state </summary>
internal interface IObjectSnapshotChangeApplier
{
    /// <summary> applies ordered snapshot create, update, and remove changes </summary>
    /// <param name="changes">the ordered snapshot changes to replay</param>
    /// <returns>true when every change was applied successfully</returns>
    bool TryApplySnapshotChanges(IReadOnlyList<ObjectSnapshotChange> changes);
}

/// <summary> builds snapshot replay changes for history actions </summary>
internal static class ObjectSnapshotHistoryChanges
{
    /// <summary> builds ordered snapshot changes from before and after snapshot lists </summary>
    /// <param name="beforeSnapshots">snapshots captured before the change</param>
    /// <param name="afterSnapshots">snapshots captured after the change</param>
    /// <returns>ordered snapshot changes ready for history replay</returns>
    public static IReadOnlyList<ObjectSnapshotChange> Build(
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots)
    {
        var beforeById = new Dictionary<Guid, ObjectSnapshot>(beforeSnapshots.Count);
        foreach (var snapshot in beforeSnapshots)
        {
            beforeById[snapshot.Id] = snapshot;
        }

        var afterById = new Dictionary<Guid, ObjectSnapshot>(afterSnapshots.Count);
        foreach (var snapshot in afterSnapshots)
        {
            afterById[snapshot.Id] = snapshot;
        }

        var orderedIds = new List<Guid>(beforeSnapshots.Count + afterSnapshots.Count);
        var seenIds = new HashSet<Guid>();

        foreach (var snapshot in beforeSnapshots)
        {
            if (seenIds.Add(snapshot.Id))
            {
                orderedIds.Add(snapshot.Id);
            }
        }

        foreach (var snapshot in afterSnapshots)
        {
            if (seenIds.Add(snapshot.Id))
            {
                orderedIds.Add(snapshot.Id);
            }
        }

        var changes = new List<ObjectSnapshotChange>(orderedIds.Count);
        foreach (var objectId in orderedIds)
        {
            beforeById.TryGetValue(objectId, out var beforeSnapshot);
            afterById.TryGetValue(objectId, out var afterSnapshot);

            var change = new ObjectSnapshotChange(beforeSnapshot, afterSnapshot);
            if (change.HasChange)
            {
                changes.Add(change);
            }
        }

        return changes;
    }
}

/// <summary> replays object snapshot create, update, and remove changes </summary>
internal sealed class ObjectSnapshotHistoryAction : ObjectHistoryActionBase
{
    private readonly IObjectSnapshotChangeApplier _snapshotChangeApplier;
    private readonly IReadOnlyList<ObjectSnapshotChange> _forwardChanges;
    private readonly IReadOnlyList<ObjectSnapshotChange> _reverseChanges;

    public ObjectSnapshotHistoryAction(
        IObjectSnapshotChangeApplier snapshotChangeApplier,
        ObjectHistoryKind kind,
        string title,
        IReadOnlyList<ObjectSnapshotChange> changes)
        : base(title, kind)
    {
        _snapshotChangeApplier = snapshotChangeApplier;
        _forwardChanges = [.. changes];
        _reverseChanges = [.. EnumerateReverseChanges(changes)];
    }

    protected override void ApplyCore()
        => ApplyChanges(_forwardChanges, "apply");

    protected override void RevertCore()
        => ApplyChanges(_reverseChanges, "revert");

    private void ApplyChanges(IReadOnlyList<ObjectSnapshotChange> changes, string operation)
    {
        if (!_snapshotChangeApplier.TryApplySnapshotChanges(changes))
        {
            throw new InvalidOperationException($"could not {operation} '{Title}'");
        }
    }

    private static IEnumerable<ObjectSnapshotChange> EnumerateReverseChanges(IReadOnlyList<ObjectSnapshotChange> changes)
    {
        for (var i = changes.Count - 1; i >= 0; --i)
        {
            var change = changes[i];
            if (!change.HasChange)
            {
                continue;
            }

            var beforeSnapshot = change.After;
            var afterSnapshot = change.Before;
            yield return new ObjectSnapshotChange(beforeSnapshot, afterSnapshot);
        }
    }
}

/// <summary> manages object undo, redo, checkpoints, and timeline navigation </summary>
internal interface IObjectHistoryManager
{
    /// <summary> gets the ordered history timeline including the initial state entry </summary>
    IReadOnlyList<ObjectHistoryEntry> Entries { get; }

    /// <summary> gets the current history state index inside the ordered timeline </summary>
    int CurrentStateIndex { get; }

    /// <summary> gets the kind of the history action that can be undone </summary>
    ObjectHistoryKind? UndoActionKind { get; }

    /// <summary> gets the kind of the history action that can be redone </summary>
    ObjectHistoryKind? RedoActionKind { get; }

    /// <summary> pushed after a history action has been applied or replayed forward </summary>
    event Action<IObjectHistoryAction> ActionApplied;

    /// <summary> pushed after a history action has been reverted </summary>
    event Action<IObjectHistoryAction> ActionReverted;

    /// <summary> applies a history action and stores it in undo history </summary>
    /// <param name="action">the history action to apply</param>
    void Apply(IObjectHistoryAction action);

    /// <summary> records a history action that has already been applied live </summary>
    /// <param name="action">the completed history action to store</param>
    void RecordCompleted(IObjectHistoryAction action);

    /// <summary> tries to undo the most recent history action </summary>
    /// <returns>true when one history action was undone</returns>
    bool Undo();

    /// <summary> tries to redo the most recent undone history action </summary>
    /// <returns>true when one history action was redone</returns>
    bool Redo();

    /// <summary> jumps to a specific reachable history state </summary>
    /// <param name="stateIndex">the target state index in the ordered timeline</param>
    /// <returns>true when the target state was reached</returns>
    bool TryJumpToState(int stateIndex);

    /// <summary> sets or updates the checkpoint label for a history state </summary>
    /// <param name="stateIndex">the target state index in the ordered timeline</param>
    /// <param name="label">the non empty checkpoint label</param>
    /// <returns>true when the checkpoint label was stored</returns>
    bool TrySetCheckpoint(int stateIndex, string label);

    /// <summary> clears the checkpoint label for a history state </summary>
    /// <param name="stateIndex">the target state index in the ordered timeline</param>
    /// <returns>true when the checkpoint label was cleared</returns>
    bool TryClearCheckpoint(int stateIndex);

    /// <summary> clears all undo and redo history </summary>
    void ClearHistory();
}

internal sealed class ObjectHistoryManager : IObjectHistoryManager, IDisposable
{
    private const string InitialStateTitle = "Initial State";

    private enum HistoryOperation
    {
        Idle,
        Applying,
        Undoing,
        Redoing,
    }

    private readonly ILogger<ObjectHistoryManager> _logger;
    private readonly List<ObjectHistoryStep> _historySteps = [];
    private readonly List<ObjectHistoryEntry> _entries = [];
    private readonly ReadOnlyCollection<ObjectHistoryEntry> _entriesView;

    private int _currentStateIndex;
    private HistoryOperation _activeOperation;

    public ObjectHistoryManager(ILogger<ObjectHistoryManager> logger)
    {
        _logger = logger;
        _entriesView = _entries.AsReadOnly();
        ResetTimeline();
    }

    public event Action<IObjectHistoryAction>? ActionApplied;
    public event Action<IObjectHistoryAction>? ActionReverted;

    public IReadOnlyList<ObjectHistoryEntry> Entries => _entriesView;
    public int CurrentStateIndex => _currentStateIndex;
    public ObjectHistoryKind? UndoActionKind => _currentStateIndex > 0 ? _historySteps[_currentStateIndex].Kind : null;
    public ObjectHistoryKind? RedoActionKind => _currentStateIndex + 1 < _historySteps.Count ? _historySteps[_currentStateIndex + 1].Kind : null;

    public void Apply(IObjectHistoryAction action)
    {
        EnsureHistoryMutationAllowed("cannot apply a history action while object history is changing");

        _activeOperation = HistoryOperation.Applying;
        try
        {
            if (!TryRunHistoryAction(action, apply: true, "apply"))
            {
                action.Dispose();
                return;
            }

            StoreCompletedAction(action, raiseAppliedEvent: true);
        }
        finally
        {
            _activeOperation = HistoryOperation.Idle;
        }
    }

    public void RecordCompleted(IObjectHistoryAction action)
    {
        EnsureHistoryMutationAllowed("cannot record a completed history action while object history is changing");

        if (action is not ObjectHistoryActionBase completedAction)
        {
            throw new InvalidOperationException("recorded object history actions must derive from ObjectHistoryActionBase");
        }

        completedAction.MarkRecordedApplied();
        StoreCompletedAction(action, raiseAppliedEvent: true);
    }

    public bool Undo()
    {
        EnsureHistoryMutationAllowed("cannot undo while object history is changing");
        if (_currentStateIndex == 0)
        {
            return false;
        }

        var action = _historySteps[_currentStateIndex].Action!;
        _activeOperation = HistoryOperation.Undoing;
        try
        {
            if (!TryRunHistoryAction(action, apply: false, "undo"))
            {
                return false;
            }

            _currentStateIndex--;
            ActionReverted?.Invoke(action);
            return true;
        }
        finally
        {
            _activeOperation = HistoryOperation.Idle;
        }
    }

    public bool Redo()
    {
        EnsureHistoryMutationAllowed("cannot redo while object history is changing");
        if (_currentStateIndex + 1 >= _historySteps.Count)
        {
            return false;
        }

        var action = _historySteps[_currentStateIndex + 1].Action!;
        _activeOperation = HistoryOperation.Redoing;
        try
        {
            if (!TryRunHistoryAction(action, apply: true, "redo"))
            {
                return false;
            }

            _currentStateIndex++;
            ActionApplied?.Invoke(action);
            return true;
        }
        finally
        {
            _activeOperation = HistoryOperation.Idle;
        }
    }

    public bool TryJumpToState(int stateIndex)
    {
        EnsureHistoryMutationAllowed("cannot jump through object history while object history is changing");
        if (stateIndex < 0 || stateIndex >= _entries.Count)
        {
            return false;
        }

        while (_currentStateIndex > stateIndex)
        {
            if (!Undo())
            {
                return false;
            }
        }

        while (_currentStateIndex < stateIndex)
        {
            if (!Redo())
            {
                return false;
            }
        }

        return _currentStateIndex == stateIndex;
    }

    public bool TrySetCheckpoint(int stateIndex, string label)
    {
        EnsureHistoryMutationAllowed("cannot update object history checkpoints while object history is changing");
        if (stateIndex < 0 || stateIndex >= _entries.Count)
        {
            return false;
        }

        var normalizedLabel = ObjectStringUtility.TrimOrEmpty(label);
        if (normalizedLabel.Length == 0)
        {
            return false;
        }

        _historySteps[stateIndex].CheckpointLabel = normalizedLabel;
        RefreshEntries();
        return true;
    }

    public bool TryClearCheckpoint(int stateIndex)
    {
        EnsureHistoryMutationAllowed("cannot update object history checkpoints while object history is changing");
        if (stateIndex < 0 || stateIndex >= _entries.Count)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_historySteps[stateIndex].CheckpointLabel))
        {
            return true;
        }

        _historySteps[stateIndex].CheckpointLabel = null;
        RefreshEntries();
        return true;
    }

    public void ClearHistory()
    {
        EnsureHistoryMutationAllowed("cannot clear object history while object history is changing");
        ResetHistory();
    }

    public void Dispose()
    {
        ClearHistorySteps();
        _entries.Clear();
    }

    private void StoreCompletedAction(IObjectHistoryAction action, bool raiseAppliedEvent)
    {
        TrimFutureHistory();
        _historySteps.Add(
            new ObjectHistoryStep(
                action,
                action.Kind,
                ObjectHistoryDescription.GetActionTitle(action.Kind, action.Title),
                DateTime.UtcNow));
        _currentStateIndex = _historySteps.Count - 1;
        RefreshEntries();

        if (raiseAppliedEvent)
        {
            ActionApplied?.Invoke(action);
        }
    }

    private void TrimFutureHistory()
    {
        for (var i = _historySteps.Count - 1; i > _currentStateIndex; --i)
        {
            _historySteps[i].Dispose();
            _historySteps.RemoveAt(i);
        }

        RefreshEntries();
    }

    private bool TryRunHistoryAction(IObjectHistoryAction action, bool apply, string operation)
    {
        try
        {
            if (apply)
            {
                action.Apply();
            }
            else
            {
                action.Revert();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "object history {Operation} failed for {HistoryKind} '{HistoryTitle}', clearing history",
                operation,
                action.Kind,
                action.Title);
            ResetHistory();
            return false;
        }
    }

    private void ResetHistory()
    {
        ClearHistorySteps();
        ResetTimeline();
    }

    private void ClearHistorySteps()
    {
        for (var i = _historySteps.Count - 1; i >= 0; --i)
        {
            _historySteps[i].Dispose();
        }

        _historySteps.Clear();
    }

    private void ResetTimeline()
    {
        _historySteps.Clear();
        _historySteps.Add(
            new ObjectHistoryStep(
                null,
                null,
                InitialStateTitle,
                DateTime.UtcNow));
        _currentStateIndex = 0;
        RefreshEntries();
    }

    private void RefreshEntries()
    {
        _entries.Clear();
        for (var i = 0; i < _historySteps.Count; ++i)
        {
            _entries.Add(_historySteps[i].ToEntry(i));
        }
    }

    private void EnsureHistoryMutationAllowed(string message)
    {
        if (_activeOperation != HistoryOperation.Idle)
        {
            throw new InvalidOperationException(message);
        }
    }
}
