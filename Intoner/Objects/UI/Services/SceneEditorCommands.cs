using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;
using ObjectPasteDestination = Intoner.Objects.Api.ObjectPasteDestination;

namespace Intoner.Objects.UI;

internal sealed partial class SceneEditorCommands
{
    private readonly IObjectSceneView             _sceneView;
    private readonly IObjectFolderService         _objectFolderService;
    private readonly IObjectClipboardService      _objectClipboard;
    private readonly IHistoryCoordinator          _historyCoordinator;
    private readonly IScenePlacementService       _scenePlacementService;
    private readonly ISceneLocationService        _sceneLocationService;
    private readonly EditorInteraction            _interaction;
    private readonly ILogger<SceneEditorCommands> _logger;

    public SceneEditorCommands(
        IObjectSceneView sceneView,
        IObjectFolderService objectFolderService,
        IObjectClipboardService objectClipboard,
        IHistoryCoordinator historyCoordinator,
        IScenePlacementService scenePlacementService,
        ISceneLocationService sceneLocationService,
        EditorInteraction interaction,
        ILogger<SceneEditorCommands> logger)
    {
        _sceneView                 = sceneView;
        _objectFolderService       = objectFolderService;
        _objectClipboard           = objectClipboard;
        _historyCoordinator        = historyCoordinator;
        _scenePlacementService     = scenePlacementService;
        _sceneLocationService      = sceneLocationService;
        _interaction               = interaction;
        _logger                    = logger;
    }

    internal bool TryCutPlacedFolderToClipboard(Guid? layoutId, string folderPath, IReadOnlyList<ObjectSnapshot> objects)
    {
        IReadOnlyList<SceneItemSnapshot> items = [.. objects, .. _sceneView.GetPlacedGroups().Where(group => group.LayoutId == layoutId
            && ObjectFolderUtility.IsSameOrDescendant(group.FolderPath, folderPath))];
        if (!_objectClipboard.CopyFolder(layoutId, folderPath, items))
        {
            return false;
        }

        ObjectFolderSceneState beforeFolderState = _objectFolderService.CaptureSceneState();
        ObjectFolderSceneState afterFolderState = ObjectFolderSceneStateUtility.RemoveFolderSubtree(
            beforeFolderState,
            layoutId,
            folderPath);
        Guid[] selectionBefore = _interaction.CaptureSelection();
        return _historyCoordinator.TryApplyFolderChange(
            SceneHistoryKind.Remove,
            "Cut Folder",
            items,
            [],
            beforeFolderState,
            afterFolderState,
            [],
            selectionBefore);
    }

    internal bool PasteObjectsFromClipboard(ObjectPasteDestination destination)
        => PasteObjectsFromClipboard(_sceneView.GetCreationLayoutId(), destination);

    internal bool PasteObjectsFromClipboard(Guid? layoutId, ObjectPasteDestination destination)
    {
        ObjectFolderSceneState beforeFolderState = _objectFolderService.CaptureSceneState();
        if (!TryReadClipboardImport(layoutId, destination, out ObjectTransferImport import))
        {
            return false;
        }

        ObjectFolderSceneState afterFolderState = ObjectFolderSceneStateUtility.AddFolders(
            beforeFolderState,
            layoutId,
            import.Folders);
        Guid[] selectionBefore = _interaction.CaptureSelection();
        IReadOnlyList<SceneItemSnapshot> importedItems = [.. import.Objects, .. import.Groups];
        Guid[] createdIds = importedItems.Where(static item => item is not ObjectSnapshot { GroupId: not null })
            .Select(static snapshot => snapshot.Id).ToArray();
        string title = ResolvePasteHistoryTitle(import, destination);
        return _historyCoordinator.TryApplyFolderChange(
            SceneHistoryKind.Import,
            title,
            [],
            importedItems,
            beforeFolderState,
            afterFolderState,
            createdIds,
            selectionBefore);
    }

    internal bool CutObjectsToClipboard(IReadOnlyList<SceneItemSnapshot> snapshots)
        => _objectClipboard.CopyObjects(snapshots)
        && _historyCoordinator.TryRemoveSceneItems(snapshots);

    private bool TryReadClipboardImport(Guid? layoutId, ObjectPasteDestination destination, out ObjectTransferImport import)
        => _objectClipboard.TryPasteObjects(_sceneView.GetCurrentLocationContext(), layoutId,
            _sceneView.GetPlacedFolders(layoutId), destination, out import);

    private static string ResolvePasteHistoryTitle(
        ObjectTransferImport import,
        ObjectPasteDestination destination)
    {
        if (import.IsFolderTransfer && destination.Kind == ObjectPasteDestinationKind.KeepOrganization)
        {
            return "Paste Folder";
        }

        return import.Objects.Count == 1 ? "Paste Object" : "Paste Objects";
    }

    internal bool TryCreateFolderWithHistory(Guid? layoutId, string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath)
         || _sceneView.GetPlacedFolders(layoutId).Any(folder => string.Equals(
             folder,
             sanitizedFolderPath,
             StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var beforeState = _objectFolderService.CaptureSceneState();
        var afterState = ObjectFolderSceneStateUtility.AddFolder(beforeState, layoutId, sanitizedFolderPath);
        return _historyCoordinator.TryApplyFolderChange(SceneHistoryKind.Organization, "Create Folder", [], [], beforeState, afterState);
    }

    internal bool TryRenameFolderWithHistory(Guid? layoutId, string sourceFolderPath, string nextFolderPath)
    {
        var sanitizedSourceFolderPath = ObjectFolderUtility.SanitizeFolderPath(sourceFolderPath);
        var sanitizedNextFolderPath = ObjectFolderUtility.SanitizeFolderPath(nextFolderPath);
        if (string.IsNullOrEmpty(sanitizedSourceFolderPath)
            || string.IsNullOrEmpty(sanitizedNextFolderPath)
            || string.Equals(sanitizedSourceFolderPath, sanitizedNextFolderPath, StringComparison.OrdinalIgnoreCase)
            || ObjectFolderUtility.IsDescendantOf(sanitizedNextFolderPath, sanitizedSourceFolderPath)
            || _sceneView.GetPlacedFolders(layoutId).Any(folder => string.Equals(
                folder,
                sanitizedNextFolderPath,
                StringComparison.OrdinalIgnoreCase)
                && !ObjectFolderUtility.IsSameOrDescendant(folder, sanitizedSourceFolderPath)))
        {
            return false;
        }

        var beforeFolderState = _objectFolderService.CaptureSceneState();
        var afterFolderState = ObjectFolderSceneStateUtility.RenameFolder(beforeFolderState, layoutId, sanitizedSourceFolderPath, sanitizedNextFolderPath);
        IReadOnlyList<ObjectSceneItemSnapshot> beforeSnapshots = GetFolderItems(layoutId, sanitizedSourceFolderPath);
        IReadOnlyList<ObjectSceneItemSnapshot> requestedSnapshots = beforeSnapshots
            .Select(snapshot => snapshot with
            {
                FolderPath = ObjectFolderUtility.RebaseFolderPath(
                    snapshot.FolderPath,
                    sanitizedSourceFolderPath,
                    sanitizedNextFolderPath),
            })
            .ToList();
        return _historyCoordinator.TryApplyFolderChange(
            SceneHistoryKind.Organization,
            "Rename Folder",
            beforeSnapshots,
            requestedSnapshots,
            beforeFolderState,
            afterFolderState);
    }

    internal bool TryDissolveFolderWithHistory(Guid? layoutId, string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return false;
        }

        var beforeFolderState = _objectFolderService.CaptureSceneState();
        IReadOnlyDictionary<string, string> pathMap = ObjectFolderUtility.CreateDissolveFolderPathMap(
            sanitizedFolderPath,
            _sceneView.GetPlacedFolders(layoutId));
        var afterFolderState = ObjectFolderSceneStateUtility.DissolveFolder(
            beforeFolderState,
            layoutId,
            sanitizedFolderPath,
            pathMap);
        IReadOnlyList<ObjectSceneItemSnapshot> beforeSnapshots = GetFolderItems(layoutId, sanitizedFolderPath);
        IReadOnlyList<ObjectSceneItemSnapshot> requestedSnapshots = beforeSnapshots
            .Select(snapshot => snapshot with
            {
                FolderPath = ObjectFolderUtility.ApplyFolderPathMap(snapshot.FolderPath, pathMap),
            })
            .ToList();
        return _historyCoordinator.TryApplyFolderChange(
            SceneHistoryKind.Organization,
            "Dissolve Folder",
            beforeSnapshots,
            requestedSnapshots,
            beforeFolderState,
            afterFolderState);
    }

    private IReadOnlyList<ObjectSceneItemSnapshot> GetFolderItems(Guid? layoutId, string folderPath)
        => _sceneView.GetPlacedObjectSnapshots().Cast<ObjectSceneItemSnapshot>().Concat(_sceneView.GetPlacedGroups())
            .Where(snapshot => snapshot.LayoutId == layoutId && ObjectFolderUtility.IsSameOrDescendant(snapshot.FolderPath, folderPath))
            .ToList();

    internal bool TrySetFolderColorWithHistory(Guid? layoutId, string folderPath, string colorValue)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return false;
        }

        var sanitizedColorValue = ObjectFolderUtility.SanitizeFolderColorValue(colorValue);
        var beforeState = _objectFolderService.CaptureSceneState();
        var afterState = ObjectFolderSceneStateUtility.SetFolderColor(beforeState, layoutId, sanitizedFolderPath, sanitizedColorValue);
        var title = string.IsNullOrEmpty(sanitizedColorValue)
            ? "Clear Folder Color"
            : "Set Folder Color";
        return _historyCoordinator.TryApplyFolderChange(SceneHistoryKind.Organization, title, [], [], beforeState, afterState);
    }

    internal bool TrySetObjectDestinationWithHistory(IReadOnlyList<SceneItemSnapshot> snapshots, Guid? layoutId, string folderPath)
    {
        if (snapshots.Count == 0
            || snapshots.Any(static snapshot => snapshot is not ObjectSceneItemSnapshot))
        {
            return false;
        }

        HashSet<Guid> groupIds = snapshots.OfType<SceneGroupSnapshot>().Select(static group => group.Id).ToHashSet();
        return _historyCoordinator.TryApplySceneItemUpdate(
            SceneHistoryKind.Organization,
            snapshots.Count == 1 ? "Move Object" : "Move Objects",
            snapshots,
            entry => ObjectOrganizationUtility.WithDestination((ObjectSceneItemSnapshot)entry, layoutId, folderPath,
                entry is ObjectSnapshot { GroupId: Guid id } && groupIds.Contains(id)));
    }

    internal void AddDisplay()
    {
        if (!_scenePlacementService.TryResolveFromPlayer(out SceneTransform transform))
        {
            return;
        }

        _ = _historyCoordinator.TryCreateDisplay(transform);
    }

    internal bool TrySetSceneItemsVisibleWithHistory(IReadOnlyList<SceneItemSnapshot> snapshots, bool visible)
    {
        IReadOnlyList<SceneItemSnapshot> targets = snapshots;
        if (visible)
        {
            HashSet<Guid> memberGroups = snapshots.OfType<ObjectSnapshot>().Where(static snapshot => snapshot.GroupId.HasValue)
                .Select(static snapshot => snapshot.GroupId!.Value).ToHashSet();
            HashSet<Guid> selectedIds = snapshots.Select(static snapshot => snapshot.Id).ToHashSet();
            targets = [.. snapshots, .. _sceneView.GetPlacedGroups().Where(group => !group.Visible
                && memberGroups.Contains(group.Id) && !selectedIds.Contains(group.Id))];
        }
        return _historyCoordinator.TryApplySceneItemUpdate(
            SceneHistoryKind.Visibility,
            ResolveSceneItemActionTitle(visible ? "Show" : "Hide", snapshots.Count),
            targets,
            item => item with { Visible = visible });
    }

    internal bool TrySetSceneItemsLockedWithHistory(IReadOnlyList<SceneItemSnapshot> snapshots, bool locked)
        => _historyCoordinator.TryApplySceneItemUpdate(
            SceneHistoryKind.Organization,
            ResolveSceneItemActionTitle(locked ? "Lock" : "Unlock", snapshots.Count),
            snapshots,
            item => item with { Locked = locked });

    internal static string ResolveSceneItemActionTitle(string action, int itemCount)
        => $"{action} {(itemCount == 1 ? "Item" : "Items")}";

    internal void ApplyObjectCollectionToSelectedObjects(string collectionId, IReadOnlyList<ObjectSnapshot> selectedSnapshots)
    {
        string title = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId).Length == 0
            ? "Remove From Collections"
            : "Assign Collection";
        if (!_historyCoordinator.TryAssignObjectCollection(title, collectionId, selectedSnapshots))
        {
            _logger.LogWarning("could not update the selected object collection assignments");
        }
    }

    internal void UnassignObjectCollections(IReadOnlyList<ObjectSnapshot> snapshots)
        => ApplyObjectCollectionToSelectedObjects(string.Empty, snapshots);

    internal static bool HasObjectCollectionAssignmentChange(IReadOnlyList<ObjectSnapshot> snapshots, string collectionId)
    {
        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        return snapshots.Any(snapshot => !string.Equals(
            ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId),
            normalizedCollectionId,
            StringComparison.OrdinalIgnoreCase));
    }

    internal static bool HasAnyObjectCollectionAssignment(IReadOnlyList<ObjectSnapshot> snapshots)
        => snapshots.Any(static snapshot => ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId).Length > 0);
}
