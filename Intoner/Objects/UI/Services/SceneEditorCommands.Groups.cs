using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class SceneEditorCommands
{
    internal string? GetPasteIntoGroupUnavailableReason(SceneGroupSnapshot group)
    {
        if (!_sceneView.GetPlacedGroups().Contains(group))
            return "This group has changed or is no longer available.";
        if (group.Locked)
            return "Unlock this group first.";
        SceneLocationScope scope = _sceneLocationService.GetCurrentLocationScope();
        if (!scope.IsValid || !_sceneLocationService.MatchesLocation(group.CreatedIn, scope))
            return "This group belongs to a different location.";
        return null;
    }

    internal bool PasteObjectsIntoGroup(SceneGroupSnapshot group)
    {
        if (GetPasteIntoGroupUnavailableReason(group) is not null
            || !TryReadClipboardImport(group.LayoutId, ObjectPasteDestination.Ungrouped, out ObjectTransferImport import)
            || import.Objects.Count == 0)
            return false;

        ObjectSnapshot[] members = import.Objects.Select(snapshot => ObjectOrganizationUtility.WithGroup(snapshot with { CreatedIn = group.CreatedIn }, group)).ToArray();
        _interaction.CommitPendingHistory();
        return GetPasteIntoGroupUnavailableReason(group) is null
            && _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Import, "Paste into Group", [group], [.. members, group], [group.Id]);
    }

    internal static bool CanGroupItems(IReadOnlyList<SceneItemSnapshot> items)
    {
        if (items.Count == 0 || items[0] is not ObjectSnapshot first)
        {
            return false;
        }

        for (int index = 0; index < items.Count; ++index)
        {
            if (items[index] is not ObjectSnapshot snapshot || snapshot.Locked || snapshot.GroupId.HasValue
                || snapshot.LayoutId != first.LayoutId || !snapshot.CreatedIn.HasSameLocation(first.CreatedIn))
            {
                return false;
            }
        }

        return true;
    }

    internal bool TryGroupItems(IReadOnlyList<SceneItemSnapshot> items)
    {
        if (!CanGroupItems(items))
            return false;
        ObjectSnapshot first = (ObjectSnapshot)items[0];
        SceneGroupSnapshot group = CreateGroup(first.LayoutId, first.FolderPath, first.CreatedIn, items, null);
        IReadOnlyList<SceneItemSnapshot> after = [.. items.Cast<ObjectSnapshot>().Select(snapshot => ObjectOrganizationUtility.WithGroup(snapshot, group)), group];
        _interaction.CommitPendingHistory();
        return _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Organization, "Group Objects", items, after, [group.Id]);
    }

    internal bool TryCreateEmptyGroup(Guid? owner, string folderPath = "")
    {
        if (!_scenePlacementService.TryResolveFromPlayer(out SceneTransform placement))
            return false;
        SceneGroupSnapshot group = CreateGroup(owner, folderPath, _sceneView.GetCurrentLocationContext(), [], null) with { Transform = placement with { Scale = Vector3.One } };
        _interaction.CommitPendingHistory();
        return _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Create, "Create Group", [], [group], [group.Id]);
    }

    internal bool TryUngroupItems(IReadOnlyList<SceneItemSnapshot> items)
    {
        HashSet<Guid> groupIds = items.OfType<SceneGroupSnapshot>().Select(static group => group.Id).ToHashSet();
        if (groupIds.Count == 0)
            return false;
        IReadOnlyList<SceneItemSnapshot> before = [.. _sceneView.GetPlacedObjectSnapshots().Where(snapshot => snapshot.GroupId is Guid id && groupIds.Contains(id)),
            .. _sceneView.GetPlacedGroups().Where(group => groupIds.Contains(group.Id))];
        IReadOnlyList<SceneItemSnapshot> after = before.OfType<ObjectSnapshot>().Select(static snapshot => snapshot with { GroupId = null }).ToArray();
        _interaction.CommitPendingHistory();
        return _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Organization, "Ungroup Objects", before, after, after.Select(static item => item.Id).ToArray());
    }

    internal bool TryCenterGroupPivot(SceneGroupSnapshot group)
    {
        _interaction.CommitPendingHistory();
        return _historyCoordinator.TryCenterGroupPivot(group);
    }

    internal bool TryAddToGroup(IReadOnlyList<SceneItemSnapshot> items, SceneGroupSnapshot group)
    {
        if (!CanAddToGroup(items, group))
            return false;
        _interaction.CommitPendingHistory();
        return _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Organization, "Add To Group", [.. items, group],
            [.. items.Cast<ObjectSnapshot>().Select(snapshot => ObjectOrganizationUtility.WithGroup(snapshot, group)), group],
            [group.Id]);
    }

    internal static bool CanAddToGroup(IReadOnlyList<SceneItemSnapshot> items, SceneGroupSnapshot group)
        => GetAddToGroupUnavailableReason(items, group) is null;

    internal static string? GetAddToGroupUnavailableReason(IReadOnlyList<SceneItemSnapshot> items, SceneGroupSnapshot group)
    {
        if (group.Locked)
            return "This group is locked.";
        if (items.Count == 0 || items.Any(static item => item is not ObjectSnapshot))
            return "Select objects to add. Groups cannot contain other groups or displays.";
        if (items.Any(static item => item.Locked))
            return "Unlock the selected objects first.";
        if (items.Any(item => ((ObjectSnapshot)item).LayoutId != group.LayoutId))
            return "Objects and their group must belong to the same layout.";
        if (items.Any(item => !((ObjectSnapshot)item).CreatedIn.HasSameLocation(group.CreatedIn)))
            return "Objects and their group must belong to the same location.";
        if (items.All(item => ((ObjectSnapshot)item).GroupId == group.Id))
            return "All selected objects already belong to this group.";
        return null;
    }

    internal bool TryRemoveFromGroups(IReadOnlyList<SceneItemSnapshot> items)
    {
        if (items.Any(static item => item is not ObjectSnapshot || item.Locked))
            return false;
        ObjectSnapshot[] members = items.OfType<ObjectSnapshot>().Where(static snapshot => snapshot.GroupId.HasValue).ToArray();
        if (members.Length == 0)
            return false;
        _interaction.CommitPendingHistory();
        return _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Organization, "Remove From Group", members,
            members.Select(static snapshot => snapshot with { GroupId = null }).ToArray(), _interaction.CaptureSelection());
    }

    private SceneGroupSnapshot CreateGroup(Guid? owner, string folder, SceneCreationContext context,
        IReadOnlyList<SceneItemSnapshot> members, string? requestedName)
    {
        string name = requestedName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            HashSet<string> names = _sceneView.GetPlacedGroups().Where(group => group.LayoutId == owner)
                .Select(static group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            name = "Group";
            int suffix = 2;
            while (names.Contains(name))
                name = $"Group {suffix++}";
        }
        return new SceneGroupSnapshot
        {
            Id = Guid.NewGuid(), LayoutId = owner, Name = name,
            FolderPath = ObjectFolderUtility.SanitizeFolderPath(folder), CreatedIn = context,
            Transform = new SceneTransform { Position = ResolveGroupPivot(members) },
        };
    }

    private Vector3 ResolveGroupPivot(IReadOnlyList<SceneItemSnapshot> members)
        => SceneSelectionTransformMath.ResolveSelectionPivotPosition(members, new SceneItemBoundsLookup(_sceneView.GetObjectBoundsSnapshots()));
}
