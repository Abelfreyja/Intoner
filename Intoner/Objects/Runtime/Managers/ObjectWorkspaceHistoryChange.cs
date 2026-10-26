using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Runtime;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ObjectWorkspaceHistoryVersion(long Content, long Identities, ObjectHousingModeState Housing);

/// <summary> captures stored owner state and workspace selection for layout history </summary>
internal sealed record ObjectWorkspaceHistoryState(
    IReadOnlyList<ObjectLayoutSnapshot> Layouts,
    IReadOnlyList<SceneItemSnapshot> Standalone,
    IReadOnlyList<ObjectFolderSnapshot> StandaloneFolders,
    IReadOnlyList<Guid> EnabledLayoutIds,
    Guid? CreationLayoutId);

internal sealed record ObjectSavedLayoutChange(ObjectLayoutSnapshot? Before, ObjectLayoutSnapshot? After, int BeforeIndex = -1, int AfterIndex = -1)
{
    public Guid Id => (After ?? Before)!.Id;
    public bool ContentsChanged => Before is null || After is null || !ObjectWorkspaceHistoryChange.ContentsMatch(Before, After);
    public bool NameChanged => !string.Equals(Before?.Name, After?.Name, StringComparison.Ordinal);
    public ObjectSavedLayoutChange Reverse() => this with { Before = After, After = Before, BeforeIndex = AfterIndex, AfterIndex = BeforeIndex };
}

/// <summary> retains only the owners and selection changed by a layout operation </summary>
internal sealed class ObjectWorkspaceHistoryChange
{
    public IReadOnlyList<ObjectSavedLayoutChange> Layouts { get; }
    public ObjectWorkspaceHistoryState Before { get; }
    public ObjectWorkspaceHistoryState After { get; }
    public bool SelectionChanged { get; }
    public bool StandaloneChanged { get; }
    private readonly HashSet<Guid> _changedEnabledIds;
    private readonly bool _creationChanged;
    private readonly bool _enabledOrderChanged;
    public bool HasChange => Layouts.Count > 0 || SelectionChanged || StandaloneChanged;

    private ObjectWorkspaceHistoryChange(IReadOnlyList<ObjectSavedLayoutChange> layouts, ObjectWorkspaceHistoryState before,
        ObjectWorkspaceHistoryState after, bool selectionChanged, bool standaloneChanged)
    {
        Layouts = layouts;
        Before = before;
        After = after;
        SelectionChanged = selectionChanged;
        StandaloneChanged = standaloneChanged;
        _changedEnabledIds = before.EnabledLayoutIds.ToHashSet();
        _changedEnabledIds.SymmetricExceptWith(after.EnabledLayoutIds);
        _creationChanged = before.CreationLayoutId != after.CreationLayoutId;
        _enabledOrderChanged = !before.EnabledLayoutIds.Where(id => !_changedEnabledIds.Contains(id))
            .SequenceEqual(after.EnabledLayoutIds.Where(id => !_changedEnabledIds.Contains(id)));
    }

    public static ObjectWorkspaceHistoryChange Build(ObjectWorkspaceHistoryState before, ObjectWorkspaceHistoryState after)
    {
        Dictionary<Guid, (ObjectLayoutSnapshot Layout, int Index)> previous = before.Layouts
            .Select(static (layout, index) => (Layout: layout, Index: index)).ToDictionary(static entry => entry.Layout.Id);
        Dictionary<Guid, (ObjectLayoutSnapshot Layout, int Index)> next = after.Layouts
            .Select(static (layout, index) => (Layout: layout, Index: index)).ToDictionary(static entry => entry.Layout.Id);
        List<ObjectSavedLayoutChange> layouts = [];
        foreach (Guid id in previous.Keys.Concat(next.Keys).Distinct())
        {
            ObjectLayoutSnapshot? oldLayout = previous.TryGetValue(id, out (ObjectLayoutSnapshot Layout, int Index) old) ? old.Layout : null;
            ObjectLayoutSnapshot? newLayout = next.TryGetValue(id, out (ObjectLayoutSnapshot Layout, int Index) current) ? current.Layout : null;
            bool contents = oldLayout is null || newLayout is null || !ContentsMatch(oldLayout, newLayout);

            if (!contents && string.Equals(oldLayout!.Name, newLayout!.Name, StringComparison.Ordinal))
            {
                continue;
            }

            int beforeIndex = oldLayout is null ? -1 : old.Index;
            int afterIndex = newLayout is null ? -1 : current.Index;

            if (!contents)
            {
                oldLayout = oldLayout! with { Objects = [], Groups = [], Folders = [] };
                newLayout = newLayout! with { Objects = [], Groups = [], Folders = [] };
            }

            layouts.Add(new ObjectSavedLayoutChange(oldLayout, newLayout, beforeIndex, afterIndex));
        }

        bool selection = before.CreationLayoutId != after.CreationLayoutId || !before.EnabledLayoutIds.SequenceEqual(after.EnabledLayoutIds);
        bool standalone = !SceneItemHistoryChanges.ItemsMatch(before.Standalone, after.Standalone)
            || !ObjectFolderUtility.FolderEntriesMatch(before.StandaloneFolders, after.StandaloneFolders);
        ObjectWorkspaceHistoryState Retain(ObjectWorkspaceHistoryState state)
            => state with
            {
                Layouts = [],
                Standalone = standalone ? state.Standalone : [],
                StandaloneFolders = standalone ? state.StandaloneFolders : [],
                EnabledLayoutIds = selection ? state.EnabledLayoutIds : [],
                CreationLayoutId = selection ? state.CreationLayoutId : null,
            };
        return new ObjectWorkspaceHistoryChange(layouts.ToImmutableArray(), Retain(before), Retain(after), selection, standalone);
    }

    public ObjectWorkspaceHistoryChange Reverse()
        => new(layouts: Layouts.Reverse().Select(static change => change.Reverse()).ToImmutableArray(), before: After, after: Before, selectionChanged: SelectionChanged, standaloneChanged: StandaloneChanged);

    public SceneHistoryResult Validate(ObjectWorkspaceHistoryState current)
    {
        if (SelectionChanged && (_creationChanged && current.CreationLayoutId != Before.CreationLayoutId
            || _changedEnabledIds.Any(id => current.EnabledLayoutIds.Contains(id) != Before.EnabledLayoutIds.Contains(id))
            || _enabledOrderChanged && !current.EnabledLayoutIds.SequenceEqual(Before.EnabledLayoutIds)))
        {
            return SceneHistoryResult.Conflict("Enabled layouts or the creation destination changed externally.");
        }

        if (StandaloneChanged && (!SceneItemHistoryChanges.ItemsMatch(current.Standalone, Before.Standalone)
            || !ObjectFolderUtility.FolderEntriesMatch(current.StandaloneFolders, Before.StandaloneFolders)))
        {
            return SceneHistoryResult.Conflict("Standalone objects or folders changed externally.");
        }

        Dictionary<Guid, ObjectLayoutSnapshot> layouts = current.Layouts.ToDictionary(static layout => layout.Id);
        foreach (ObjectSavedLayoutChange change in Layouts)
        {
            layouts.TryGetValue(change.Id, out ObjectLayoutSnapshot? saved);

            if (change.Before is null ? saved is not null : saved is null
                || change.NameChanged && !string.Equals(saved.Name, change.Before.Name, StringComparison.Ordinal)
                || change.ContentsChanged && !ContentsMatch(saved, change.Before))
            {
                return SceneHistoryResult.Conflict($"Layout \"{(change.After ?? change.Before)!.Name}\" changed, was removed, or was replaced externally.");
            }
        }

        GetSelection(current, out IReadOnlyList<Guid> enabled, out Guid? creation);

        if (enabled.Any(id => Layouts.FirstOrDefault(change => change.Id == id) is { } transition
                ? transition.After is null : !layouts.ContainsKey(id)))
        {
            return SceneHistoryResult.Conflict("A layout required by this action no longer exists.");
        }

        if (creation.HasValue && !enabled.Contains(creation.Value))
        {
            return SceneHistoryResult.Conflict("The current creation destination requires this layout to stay enabled.");
        }

        return SceneHistoryResult.Success;
    }

    public void GetSelection(ObjectWorkspaceHistoryState current, out IReadOnlyList<Guid> enabled, out Guid? creation)
    {
        creation = _creationChanged ? After.CreationLayoutId : current.CreationLayoutId;

        if (!SelectionChanged)
        {
            enabled = current.EnabledLayoutIds;
            return;
        }

        if (_enabledOrderChanged)
        {
            enabled = After.EnabledLayoutIds;
            return;
        }

        if (_changedEnabledIds.Count == 0)
        {
            enabled = current.EnabledLayoutIds;
            return;
        }

        List<Guid> ids = current.EnabledLayoutIds.Where(id => !_changedEnabledIds.Contains(id)).ToList();
        for (int index = 0; index < After.EnabledLayoutIds.Count; ++index)
        {
            Guid id = After.EnabledLayoutIds[index];

            if (!_changedEnabledIds.Contains(id))
            {
                continue;
            }

            int insertion = ids.Count;
            for (int next = index + 1; next < After.EnabledLayoutIds.Count; ++next)
            {
                int neighbor = ids.IndexOf(After.EnabledLayoutIds[next]);

                if (neighbor >= 0)
                {
                    insertion = neighbor;
                    break;
                }
            }

            ids.Insert(insertion, id);
        }

        enabled = ids;
    }

    public static bool ContentsMatch(ObjectLayoutSnapshot left, ObjectLayoutSnapshot right)
        => SceneItemHistoryChanges.ItemsMatch(left.Objects, right.Objects) && SceneItemHistoryChanges.ItemsMatch(left.Groups, right.Groups)
            && ObjectFolderUtility.FolderEntriesMatch(left.Folders, right.Folders);
}
