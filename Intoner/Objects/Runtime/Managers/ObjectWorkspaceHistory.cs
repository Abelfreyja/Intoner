using Intoner.Objects.Models;
using Intoner.Scene;

namespace Intoner.Objects.Runtime;

/// <summary> records committed editor layout operations in the shared scene history </summary>
internal interface IObjectWorkspaceHistory
{
    /// <summary> finishes live editor interactions before a workspace mutation starts </summary>
    event Action MutationStarting;

    /// <summary> records the actual saved changes when the operation commits </summary>
    T Execute<T>(string title, Func<T> mutation, Func<T, bool> committed);

    /// <summary> changes layout membership and records the committed change </summary>
    PersistentMutationResult SetLayoutEnabled(Guid layoutId, bool enabled);

    /// <summary> changes the creation destination and records the committed change </summary>
    PersistentMutationResult SetCreationLayout(Guid? layoutId);
}

internal sealed class ObjectWorkspaceHistory : IObjectWorkspaceHistory
{
    private readonly IObjectWorkspaceHistoryReplay _replay;
    private readonly ISceneHistoryManager _history;
    private readonly Lock _stateLock;
    private readonly IObjectManager _objects;

    public ObjectWorkspaceHistory(IObjectWorkspaceHistoryReplay replay, ISceneHistoryManager history, ObjectStateLock stateLock,
        IObjectManager objects)
    {
        _replay = replay;
        _history = history;
        _stateLock = stateLock.Value;
        _objects = objects;
    }

    public event Action? MutationStarting;

    public PersistentMutationResult SetLayoutEnabled(Guid layoutId, bool enabled)
        => Execute(enabled ? "Enable Layout" : "Disable Layout", () => _objects.SetLayoutEnabled(layoutId, enabled), static result => result.IsAccepted);

    public PersistentMutationResult SetCreationLayout(Guid? layoutId)
        => Execute("Set Creation Destination", () => _objects.SetCreationLayout(layoutId), static result => result.IsAccepted);

    private static string Describe(string title, ObjectWorkspaceHistoryState before, ObjectWorkspaceHistoryState after, ObjectWorkspaceHistoryChange change)
    {
        Guid[] owners = change.Layouts.Select(static layout => layout.Id).ToArray();

        if (owners.Length == 0)
        {
            owners = before.EnabledLayoutIds.Except(after.EnabledLayoutIds).Concat(after.EnabledLayoutIds.Except(before.EnabledLayoutIds)).ToArray();
        }

        Guid? id = null;

        if (owners.Length == 1)
        {
            id = owners[0];
        }
        else if (owners.Length == 0 && before.CreationLayoutId != after.CreationLayoutId)
        {
            id = after.CreationLayoutId;
        }

        string? name = id.HasValue ? after.Layouts.Concat(before.Layouts).FirstOrDefault(layout => layout.Id == id)?.Name : null;

        if (owners.Length == 0 && before.CreationLayoutId != after.CreationLayoutId && after.CreationLayoutId is null)
        {
            name = "Standalone";
        }

        return string.IsNullOrWhiteSpace(name) ? title : $"{title}: {name}";
    }

    public T Execute<T>(string title, Func<T> mutation, Func<T, bool> committed)
    {
        lock (_stateLock)
        {
            MutationStarting?.Invoke();
            ObjectWorkspaceHistoryState before = _replay.CaptureState();
            T result = mutation();

            if (committed(result))
            {
                ObjectWorkspaceHistoryState after = _replay.CaptureState();
                ObjectWorkspaceHistoryChange change = ObjectWorkspaceHistoryChange.Build(before, after);

                if (change.HasChange)
                {
                    _history.RecordCompleted(new ObjectWorkspaceHistoryAction(_replay, Describe(title, before, after, change), change));
                }
            }

            return result;
        }
    }
}

internal sealed class ObjectWorkspaceHistoryAction : SceneHistoryActionBase
{
    private readonly IObjectWorkspaceHistoryReplay _replay;
    private readonly ObjectWorkspaceHistoryChange _forward;
    private readonly ObjectWorkspaceHistoryChange _reverse;
    private readonly Dictionary<Guid, long> _revisionFloors;
    private ObjectWorkspaceHistoryVersion? _validatedVersion;
    private bool _validatedApply;
    private SceneHistoryResult _validation;

    public ObjectWorkspaceHistoryAction(IObjectWorkspaceHistoryReplay replay, string title, ObjectWorkspaceHistoryChange change)
        : base(title, SceneHistoryKind.Layout)
    {
        _replay = replay;
        _forward = change;
        _reverse = change.Reverse();
        _revisionFloors = change.Layouts.ToDictionary(static layout => layout.Id,
            static layout => Math.Max(layout.Before?.Revision ?? 0, layout.After?.Revision ?? 0));
    }

    public override SceneHistoryResult Validate(bool apply)
    {
        ObjectWorkspaceHistoryVersion version = _replay.GetVersion();

        if (version != _validatedVersion || apply != _validatedApply)
        {
            _validation = _replay.Check(apply ? _forward : _reverse).WithActionContext(Title, apply);
            _validatedVersion = version;
            _validatedApply = apply;
        }

        return _validation;
    }

    protected override SceneHistoryResult ApplyCore() => Replay(apply: true);
    protected override SceneHistoryResult RevertCore() => Replay(apply: false);

    private SceneHistoryResult Replay(bool apply)
        => _replay.Apply(apply ? _forward : _reverse, _revisionFloors).WithActionContext(Title, apply);
}
