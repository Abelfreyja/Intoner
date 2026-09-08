using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Intoner.Scene;

/// <summary> identifies the main kind of change represented by a history action </summary>
internal enum SceneHistoryKind
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

/// <summary> describes one reachable point in scene history </summary>
internal readonly record struct SceneHistoryEntry(
    int StateIndex,
    SceneHistoryKind? Kind,
    string Title,
    string? CheckpointLabel,
    DateTime RecordedAtUtc)
{
    public bool IsInitialState => StateIndex == 0;
    public bool HasCheckpoint => !string.IsNullOrWhiteSpace(CheckpointLabel);
}

/// <summary> stores one internal history step and its timeline metadata </summary>
internal sealed class SceneHistoryStep
{
    public SceneHistoryStep(ISceneHistoryAction? action, SceneHistoryKind? kind, string title, DateTime recordedAtUtc)
    {
        Action = action;
        Kind = kind;
        Title = title;
        RecordedAtUtc = recordedAtUtc;
    }

    public ISceneHistoryAction? Action { get; }
    public SceneHistoryKind? Kind { get; }
    public string Title { get; }
    public string? CheckpointLabel { get; set; }
    public DateTime RecordedAtUtc { get; }

    public SceneHistoryEntry ToEntry(int stateIndex)
        => new(stateIndex, Kind, Title, CheckpointLabel, RecordedAtUtc);

}

/// <summary> provides shared labels for scene history description </summary>
internal static class SceneHistoryDescription
{
    /// <summary> gets the label for a history kind </summary>
    public static string GetKindLabel(SceneHistoryKind kind)
        => kind switch
        {
            SceneHistoryKind.Create       => "Create",
            SceneHistoryKind.Import       => "Import",
            SceneHistoryKind.Move         => "Move",
            SceneHistoryKind.Transform    => "Transform",
            SceneHistoryKind.Organization => "Organization",
            SceneHistoryKind.Appearance   => "Appearance",
            SceneHistoryKind.Visibility   => "Visibility",
            SceneHistoryKind.Remove       => "Remove",
            SceneHistoryKind.Clear        => "Clear",
            _                              => "Edit",
        };

    /// <summary> gets the display title for a history action </summary>
    public static string GetActionTitle(SceneHistoryKind kind, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return GetKindLabel(kind);
        }

        return TextUtility.TrimOrFallback(title, GetKindLabel(kind));
    }
}

/// <summary> describes one scene history action that can be applied, reverted, and replayed </summary>
internal interface ISceneHistoryAction
{
    /// <summary> gets the history title </summary>
    string Title { get; }

    /// <summary> gets the main kind of change represented by this history action </summary>
    SceneHistoryKind Kind { get; }

    /// <summary> applies the history action </summary>
    void Apply();

    /// <summary> reverts the history action </summary>
    void Revert();
}

/// <summary> base class for scene history actions </summary>
internal abstract class SceneHistoryActionBase : ISceneHistoryAction
{
    private enum ActionState
    {
        Pending,
        Applied,
    }

    private ActionState _state;

    protected SceneHistoryActionBase(string title, SceneHistoryKind kind)
    {
        Title = title;
        Kind = kind;
    }

    public string Title { get; protected set; }
    public SceneHistoryKind Kind { get; protected set; }
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

    internal void MarkApplied()
        => _state = ActionState.Applied;

    internal virtual void MarkRecordedApplied()
        => MarkApplied();

    protected abstract void ApplyCore();
    protected abstract void RevertCore();
}

/// <summary> manages scene undo, redo, checkpoints, and timeline navigation </summary>
internal interface ISceneHistoryManager
{
    /// <summary> gets a stable snapshot of the ordered history timeline including the initial state entry </summary>
    IReadOnlyList<SceneHistoryEntry> Entries { get; }

    /// <summary> gets the current history state index inside the ordered timeline </summary>
    int CurrentStateIndex { get; }

    /// <summary> gets the kind of the history action that can be undone </summary>
    SceneHistoryKind? UndoActionKind { get; }

    /// <summary> gets the kind of the history action that can be redone </summary>
    SceneHistoryKind? RedoActionKind { get; }

    /// <summary> pushed after a history action has been applied or replayed forward </summary>
    event Action<ISceneHistoryAction> ActionApplied;

    /// <summary> pushed after a history action has been reverted </summary>
    event Action<ISceneHistoryAction> ActionReverted;

    /// <summary> applies a history action and stores it in undo history </summary>
    /// <param name="action">the history action to apply</param>
    void Apply(ISceneHistoryAction action);

    /// <summary> records a history action that has already been applied live </summary>
    /// <param name="action">the completed history action to store</param>
    void RecordCompleted(ISceneHistoryAction action);

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

internal sealed class SceneHistoryManager : ISceneHistoryManager
{
    private const string InitialStateTitle = "Initial State";

    private enum HistoryOperation
    {
        Idle,
        Applying,
        Undoing,
        Redoing,
    }

    private readonly ILogger<SceneHistoryManager> _logger;
    private readonly List<SceneHistoryStep> _historySteps = [];
    private IReadOnlyList<SceneHistoryEntry> _entries = [];

    private int _currentStateIndex;
    private HistoryOperation _activeOperation;

    public SceneHistoryManager(ILogger<SceneHistoryManager> logger)
    {
        _logger = logger;
        ResetTimeline();
    }

    public event Action<ISceneHistoryAction>? ActionApplied;
    public event Action<ISceneHistoryAction>? ActionReverted;

    public IReadOnlyList<SceneHistoryEntry> Entries => _entries;
    public int CurrentStateIndex => _currentStateIndex;
    public SceneHistoryKind? UndoActionKind => _currentStateIndex > 0 ? _historySteps[_currentStateIndex].Kind : null;
    public SceneHistoryKind? RedoActionKind => _currentStateIndex + 1 < _historySteps.Count ? _historySteps[_currentStateIndex + 1].Kind : null;

    public void Apply(ISceneHistoryAction action)
    {
        EnsureHistoryMutationAllowed("cannot apply a history action while scene history is changing");

        _activeOperation = HistoryOperation.Applying;
        try
        {
            if (!TryRunHistoryAction(action, apply: true, "apply"))
            {
                return;
            }

            StoreCompletedAction(action, raiseAppliedEvent: true);
        }
        finally
        {
            _activeOperation = HistoryOperation.Idle;
        }
    }

    public void RecordCompleted(ISceneHistoryAction action)
    {
        EnsureHistoryMutationAllowed("cannot record a completed history action while scene history is changing");

        if (action is not SceneHistoryActionBase completedAction)
        {
            throw new InvalidOperationException("recorded scene history actions must derive from SceneHistoryActionBase");
        }

        completedAction.MarkRecordedApplied();
        StoreCompletedAction(action, raiseAppliedEvent: true);
    }

    public bool Undo()
    {
        EnsureHistoryMutationAllowed("cannot undo while scene history is changing");
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
        EnsureHistoryMutationAllowed("cannot redo while scene history is changing");
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
        EnsureHistoryMutationAllowed("cannot jump through scene history while scene history is changing");
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
        EnsureHistoryMutationAllowed("cannot update scene history checkpoints while scene history is changing");
        if (stateIndex < 0 || stateIndex >= _entries.Count)
        {
            return false;
        }

        var normalizedLabel = TextUtility.TrimOrEmpty(label);
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
        EnsureHistoryMutationAllowed("cannot update scene history checkpoints while scene history is changing");
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
        EnsureHistoryMutationAllowed("cannot clear scene history while scene history is changing");
        ResetHistory();
    }

    private void StoreCompletedAction(ISceneHistoryAction action, bool raiseAppliedEvent)
    {
        TrimFutureHistory();
        _historySteps.Add(
            new SceneHistoryStep(
                action,
                action.Kind,
                SceneHistoryDescription.GetActionTitle(action.Kind, action.Title),
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
            _historySteps.RemoveAt(i);
        }
    }

    private bool TryRunHistoryAction(ISceneHistoryAction action, bool apply, string operation)
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
                "scene history {Operation} failed for {HistoryKind} '{HistoryTitle}', clearing history",
                operation,
                action.Kind,
                action.Title);
            ResetHistory();
            return false;
        }
    }

    private void ResetHistory()
        => ResetTimeline();

    private void ResetTimeline()
    {
        _historySteps.Clear();
        _historySteps.Add(
            new SceneHistoryStep(
                null,
                null,
                InitialStateTitle,
                DateTime.UtcNow));
        _currentStateIndex = 0;
        RefreshEntries();
    }

    private void RefreshEntries()
    {
        SceneHistoryEntry[] entries = new SceneHistoryEntry[_historySteps.Count];
        for (var i = 0; i < _historySteps.Count; ++i)
        {
            entries[i] = _historySteps[i].ToEntry(i);
        }

        _entries = Array.AsReadOnly(entries);
    }

    private void EnsureHistoryMutationAllowed(string message)
    {
        if (_activeOperation != HistoryOperation.Idle)
        {
            throw new InvalidOperationException(message);
        }
    }
}
