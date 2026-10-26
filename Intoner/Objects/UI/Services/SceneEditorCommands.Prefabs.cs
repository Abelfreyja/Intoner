using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;

namespace Intoner.Objects.UI;

internal enum PrefabPlacementContainer
{
    Group,
    Folder,
}

internal sealed partial class SceneEditorCommands
{
    internal bool TryPlacePrefab(string name, IReadOnlyList<ObjectSnapshot> objects,
        PrefabPlacementContainer container, string folderColor = "")
    {
        if (!CanGroupItems(objects) || !Enum.IsDefined(container))
            return false;

        ObjectSnapshot first = objects[0];
        SceneCreationContext context = _sceneView.GetCurrentLocationContext();
        _interaction.CommitPendingHistory();
        if (container == PrefabPlacementContainer.Group)
        {
            SceneGroupSnapshot group = CreateGroup(first.LayoutId, first.FolderPath, context, objects, name);
            IReadOnlyList<SceneItemSnapshot> after = [.. objects.Select(snapshot =>
                ObjectOrganizationUtility.WithGroup(snapshot, group) with { CreatedIn = context }), group];
            return _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Create, $"Place {group.Name}", [], after, [group.Id]);
        }

        ObjectFolderSceneState beforeFolders = _objectFolderService.CaptureSceneState();
        string folderName = ObjectFolderUtility.SanitizeFolderPath(name);
        if (folderName.Length == 0)
            folderName = "Prefab";
        string folderPath = ObjectFolderUtility.ResolveAvailableFolderPath(
            ObjectFolderUtility.CombineFolderPath(first.FolderPath, folderName),
            _sceneView.GetPlacedFolders(first.LayoutId).Concat(
                ObjectFolderSceneStateUtility.GetFolders(beforeFolders, first.LayoutId).Select(static folder => folder.Path)));
        ObjectFolderSceneState afterFolders = ObjectFolderSceneStateUtility.AddFolders(
            beforeFolders, first.LayoutId, [new ObjectFolderSnapshot(folderPath, folderColor)]);
        ObjectSnapshot[] members = objects.Select(snapshot => snapshot with
            { FolderPath = folderPath, CreatedIn = context }).ToArray();

        return _historyCoordinator.TryApplyFolderChange(SceneHistoryKind.Create, $"Place {name}", [], members, beforeFolders, afterFolders,
            members.Select(static snapshot => snapshot.Id).ToArray(), _interaction.CaptureSelection());
    }
}
