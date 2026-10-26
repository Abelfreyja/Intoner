using Intoner.Logging;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Utils;
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
    Layout,
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

/// <summary> holds one checked view of the history timeline </summary>
internal readonly record struct SceneHistorySnapshot(IReadOnlyList<SceneHistoryEntry> Entries, int CurrentStateIndex,
    SceneHistoryResult Undo, SceneHistoryResult Redo, SceneHistoryResult LastResult)
{
    public SceneHistoryKind? UndoActionKind => CurrentStateIndex > 0 && CurrentStateIndex < Entries.Count ? Entries[CurrentStateIndex].Kind : null;
    public SceneHistoryKind? RedoActionKind => CurrentStateIndex + 1 < Entries.Count ? Entries[CurrentStateIndex + 1].Kind : null;
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
    public static string GetItemName(SceneItemSnapshot snapshot)
        => string.IsNullOrWhiteSpace(snapshot.Name) ? snapshot.Id.ToString("D") : snapshot.Name;

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
            SceneHistoryKind.Layout       => "Layout",
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
    SceneHistoryResult Apply();

    /// <summary> checks whether the expected saved state still permits replay </summary>
    SceneHistoryResult Validate(bool apply);

    /// <summary> reverts the history action </summary>
    SceneHistoryResult Revert();
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

    public SceneHistoryResult Apply()
    {
        Debug.Assert(_state != ActionState.Applied, "history action is already applied");
        SceneHistoryResult result = ApplyCore();

        if (result.IsSuccess)
        {
            _state = ActionState.Applied;
        }

        return result;
    }

    public SceneHistoryResult Revert()
    {
        Debug.Assert(_state == ActionState.Applied, "history action is not applied");
        SceneHistoryResult result = RevertCore();

        if (result.IsSuccess)
        {
            _state = ActionState.Pending;
        }

        return result;
    }

    internal virtual void MarkRecordedApplied()
        => _state = ActionState.Applied;

    public virtual SceneHistoryResult Validate(bool apply) => SceneHistoryResult.Success;

    protected abstract SceneHistoryResult ApplyCore();
    protected abstract SceneHistoryResult RevertCore();
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
    /// <param name="expectedEntries">the timeline snapshot selected by the caller</param>
    /// <returns>true when the target state was reached</returns>
    bool TryJumpToState(int stateIndex, IReadOnlyList<SceneHistoryEntry> expectedEntries);

    /// <summary> sets or updates the checkpoint label for a history state </summary>
    /// <param name="stateIndex">the target state index in the ordered timeline</param>
    /// <param name="label">the non empty checkpoint label</param>
    /// <param name="expectedEntries">the timeline snapshot selected by the caller</param>
    /// <returns>true when the checkpoint label was stored</returns>
    bool TrySetCheckpoint(int stateIndex, string label, IReadOnlyList<SceneHistoryEntry> expectedEntries);

    /// <summary> clears the checkpoint label for a history state </summary>
    /// <param name="stateIndex">the target state index in the ordered timeline</param>
    /// <param name="expectedEntries">the timeline snapshot selected by the caller</param>
    /// <returns>true when the checkpoint label was cleared</returns>
    bool TryClearCheckpoint(int stateIndex, IReadOnlyList<SceneHistoryEntry> expectedEntries);

    /// <summary> reports the latest history result </summary>
    SceneHistoryResult LastResult { get; }

    /// <summary> captures entries, position, replay checks and the last result under the scene lock </summary>
    SceneHistorySnapshot GetSnapshot();

    /// <summary> checks the next undo or redo action without changing history </summary>
    SceneHistoryResult GetAvailability(bool undo);

    /// <summary> clears all undo and redo history </summary>
    void ClearHistory();
}

internal sealed class SceneHistoryManager : ISceneHistoryManager
{
    private const string InitialStateTitle = "Initial State";
    private readonly ILogger<SceneHistoryManager> _logger;
    private readonly Lock _stateLock;
    private SceneHistoryResult _lastResult = SceneHistoryResult.Success;
    private readonly List<SceneHistoryStep> _historySteps = [];
    private IReadOnlyList<SceneHistoryEntry> _entries = [];
    private int _currentStateIndex;
    private bool _changing;
    private SceneHistoryResult? _recoveryFailure;

    public SceneHistoryManager(ILogger<SceneHistoryManager> logger, ObjectStateLock stateLock)
    {
        _logger = logger;
        _stateLock = stateLock.Value;
        ClearHistory();
    }

    public event Action<ISceneHistoryAction>? ActionApplied;
    public event Action<ISceneHistoryAction>? ActionReverted;
    public IReadOnlyList<SceneHistoryEntry> Entries
    {
        get
        {
            using Lock.Scope scope = _stateLock.EnterScope();
            return _entries;
        }
    }
    public int CurrentStateIndex
    {
        get
        {
            using Lock.Scope scope = _stateLock.EnterScope();
            return _currentStateIndex;
        }
    }
    public SceneHistoryKind? UndoActionKind => GetActionKind(undo: true);
    public SceneHistoryKind? RedoActionKind => GetActionKind(undo: false);
    public SceneHistoryResult LastResult
    {
        get
        {
            using Lock.Scope scope = _stateLock.EnterScope();
            return _lastResult;
        }
        private set => _lastResult = value;
    }

    public SceneHistorySnapshot GetSnapshot()
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        return new SceneHistorySnapshot(_entries, _currentStateIndex, GetAvailability(undo: true), GetAvailability(undo: false), _lastResult);
    }

    private SceneHistoryKind? GetActionKind(bool undo)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        int index = undo ? _currentStateIndex : _currentStateIndex + 1;
        return index > 0 && index < _historySteps.Count ? _historySteps[index].Kind : null;
    }

    public SceneHistoryResult GetAvailability(bool undo)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        if (_recoveryFailure is { } recovery)
        {
            return recovery;
        }

        int index = undo ? _currentStateIndex : _currentStateIndex + 1;

        if (index <= 0 || index >= _historySteps.Count)
        {
            return SceneHistoryResult.Failed(undo ? "No change is available to undo." : "No change is available to redo.");
        }

        return Validate(_historySteps[index].Action!, apply: !undo);
    }

    public void Apply(ISceneHistoryAction action)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        EnsureIdle();
        _changing = true;
        try
        {
            SceneHistoryResult result = Run(action, apply: true);

            if (result.IsSuccess)
            {
                Store(action);
            }
            else if (result.Outcome == SceneHistoryOutcome.RecoveryRequired)
            {
                LastResult = SceneHistoryResult.RecoveryRequired($"{result.Message} Recover the workspace and clear history before trying again.");
                _recoveryFailure = LastResult;
            }
        }
        finally
        {
            _changing = false;
        }
    }

    public void RecordCompleted(ISceneHistoryAction action)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        EnsureIdle();

        if (action is not SceneHistoryActionBase completed)
        {
            throw new InvalidOperationException("recorded scene history actions must derive from SceneHistoryActionBase");
        }

        _changing = true;
        try
        {
            completed.MarkRecordedApplied();
            Store(action);
        }
        finally
        {
            _changing = false;
        }
    }

    public bool Undo()
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        return Navigate(_currentStateIndex - 1);
    }

    public bool Redo()
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        return Navigate(_currentStateIndex + 1);
    }

    public bool TryJumpToState(int stateIndex, IReadOnlyList<SceneHistoryEntry> expectedEntries)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        EnsureIdle();

        if (!ReferenceEquals(_entries, expectedEntries))
        {
            LastResult = SceneHistoryResult.Conflict("History changed. Select the state again.");
            return false;
        }

        return Navigate(stateIndex);
    }

    private bool Navigate(int target)
    {
        EnsureIdle();

        if (_recoveryFailure is { } recovery)
        {
            LastResult = recovery;
            return false;
        }

        if (target < 0 || target >= _historySteps.Count)
        {
            return false;
        }

        if (target == _currentStateIndex)
        {
            LastResult = SceneHistoryResult.Success;
            return true;
        }

        bool apply = target > _currentStateIndex;
        int direction = apply ? 1 : -1;
        int original = _currentStateIndex;
        List<ISceneHistoryAction> completed = [];
        _changing = true;
        try
        {
            for (int cursor = original; cursor != target; cursor += direction)
            {
                ISceneHistoryAction action = _historySteps[apply ? cursor + 1 : cursor].Action!;
                SceneHistoryResult result = Run(action, apply);

                if (!result.IsSuccess)
                {
                    bool restored = true;
                    for (int index = completed.Count - 1; index >= 0; --index)
                    {
                        restored &= Run(completed[index], !apply).IsSuccess;
                    }

                    LastResult = restored && result.Outcome != SceneHistoryOutcome.RecoveryRequired
                        ? result
                        : SceneHistoryResult.RecoveryRequired("History navigation could not restore the saved state. Recover the workspace and clear history before trying again.");
                    _recoveryFailure = LastResult.Outcome == SceneHistoryOutcome.RecoveryRequired ? LastResult : null;
                    return false;
                }

                completed.Add(action);
            }

            _currentStateIndex = target;
            LastResult = SceneHistoryResult.Success;
            foreach (ISceneHistoryAction action in completed)
            {
                Notify(action, apply);
            }

            return true;
        }
        finally
        {
            _changing = false;
        }
    }

    public bool TrySetCheckpoint(int stateIndex, string label, IReadOnlyList<SceneHistoryEntry> expectedEntries)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        EnsureIdle();
        string normalized = TextUtility.TrimOrEmpty(label);

        if (normalized.Length == 0)
        {
            LastResult = SceneHistoryResult.Failed("Enter a checkpoint name.");
            return false;
        }

        return TryUpdateCheckpoint(stateIndex, normalized, expectedEntries);
    }

    public bool TryClearCheckpoint(int stateIndex, IReadOnlyList<SceneHistoryEntry> expectedEntries)
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        EnsureIdle();
        return TryUpdateCheckpoint(stateIndex, null, expectedEntries);
    }

    private bool TryUpdateCheckpoint(int stateIndex, string? label, IReadOnlyList<SceneHistoryEntry> expectedEntries)
    {
        if (!ReferenceEquals(_entries, expectedEntries) || stateIndex < 0 || stateIndex >= _historySteps.Count)
        {
            LastResult = SceneHistoryResult.Conflict("History changed. Reopen the checkpoint from the current timeline.");
            return false;
        }

        if (!string.Equals(_historySteps[stateIndex].CheckpointLabel, label, StringComparison.Ordinal))
        {
            _historySteps[stateIndex].CheckpointLabel = label;
            RefreshEntries();
        }

        LastResult = _recoveryFailure ?? SceneHistoryResult.Success;
        return true;
    }

    public void ClearHistory()
    {
        using Lock.Scope scope = _stateLock.EnterScope();
        EnsureIdle();
        _historySteps.Clear();
        _historySteps.Add(new SceneHistoryStep(null, null, InitialStateTitle, DateTime.UtcNow));
        _currentStateIndex = 0;
        _recoveryFailure = null;
        LastResult = SceneHistoryResult.Success;
        RefreshEntries();
    }

    private SceneHistoryResult Validate(ISceneHistoryAction action, bool apply)
    {
        try
        {
            return action.Validate(apply);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(_logger, LogLevel.Warning, exception, "scene history validation failed for {HistoryTitle}", action.Title);
            return SceneHistoryResult.Failed("The history action could not be checked.").WithActionContext(action.Title, apply);
        }
    }

    private SceneHistoryResult Run(ISceneHistoryAction action, bool apply)
    {
        SceneHistoryResult result = Validate(action, apply);
        if (result.IsSuccess)
        {
            try
            {
                result = apply ? action.Apply() : action.Revert();
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(_logger, LogLevel.Warning, exception, "scene history replay failed for {HistoryTitle}", action.Title);
                result = SceneHistoryResult.RecoveryRequired($"Could not replay \"{action.Title}\". Check the saved state before using history.");
            }
        }

        LastResult = result;
        return result;
    }

    private void Store(ISceneHistoryAction action)
    {
        if (_currentStateIndex + 1 < _historySteps.Count)
        {
            _historySteps.RemoveRange(_currentStateIndex + 1, _historySteps.Count - _currentStateIndex - 1);
        }

        _historySteps.Add(new SceneHistoryStep(action, action.Kind, SceneHistoryDescription.GetActionTitle(action.Kind, action.Title), DateTime.UtcNow));
        _currentStateIndex = _historySteps.Count - 1;

        LastResult = _recoveryFailure ?? SceneHistoryResult.Success;

        RefreshEntries();
        Notify(action, apply: true);
    }

    private void Notify(ISceneHistoryAction action, bool apply)
        => CommittedChangeNotification.Publish(apply ? ActionApplied : ActionReverted, action, _logger, "scene history");

    private void RefreshEntries()
    {
        SceneHistoryEntry[] entries = new SceneHistoryEntry[_historySteps.Count];
        for (int index = 0; index < entries.Length; ++index)
        {
            entries[index] = _historySteps[index].ToEntry(index);
        }

        _entries = Array.AsReadOnly(entries);
    }

    private void EnsureIdle()
    {
        if (_changing)
        {
            throw new InvalidOperationException("scene history is already changing");
        }
    }
}
