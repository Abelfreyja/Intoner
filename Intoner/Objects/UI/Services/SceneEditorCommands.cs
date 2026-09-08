using Intoner.Displays;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;
using ObjectPasteDestination = Intoner.Objects.Api.ObjectPasteDestination;

namespace Intoner.Objects.UI;

internal sealed class SceneEditorCommands
{
    private readonly IObjectSceneView             _sceneView;
    private readonly IObjectFolderService         _objectFolderService;
    private readonly IObjectOrganizationService   _objectOrganizationService;
    private readonly IObjectClipboardService      _objectClipboard;
    private readonly IHistoryCoordinator          _historyCoordinator;
    private readonly IDisplayService              _displayService;
    private readonly IScenePlacementService       _scenePlacementService;
    private readonly EditorInteraction            _interaction;
    private readonly ILogger<SceneEditorCommands> _logger;

    public SceneEditorCommands(
        IObjectSceneView sceneView,
        IObjectFolderService objectFolderService,
        IObjectOrganizationService objectOrganizationService,
        IObjectClipboardService objectClipboard,
        IHistoryCoordinator historyCoordinator,
        IDisplayService displayService,
        IScenePlacementService scenePlacementService,
        EditorInteraction interaction,
        ILogger<SceneEditorCommands> logger)
    {
        _sceneView                 = sceneView;
        _objectFolderService       = objectFolderService;
        _objectOrganizationService = objectOrganizationService;
        _objectClipboard           = objectClipboard;
        _historyCoordinator        = historyCoordinator;
        _displayService            = displayService;
        _scenePlacementService     = scenePlacementService;
        _interaction               = interaction;
        _logger                    = logger;
    }

    internal bool TryCutPlacedFolderToClipboard(string folderPath, IReadOnlyList<ObjectSnapshot> objects)
    {
        if (!_objectClipboard.CopyFolder(folderPath, objects))
        {
            return false;
        }

        ObjectFolderSceneState beforeFolderState = _objectFolderService.CaptureSceneState();
        ObjectFolderSceneState afterFolderState = ObjectFolderSceneStateUtility.RemoveFolderSubtree(
            beforeFolderState,
            folderPath);
        Guid[] selectionBefore = _interaction.CaptureSelection();
        return TryApplyFolderChangeWithHistory(
            "Cut Folder",
            objects,
            [],
            beforeFolderState,
            afterFolderState,
            SceneHistoryKind.Remove,
            [],
            selectionBefore);
    }

    internal bool PasteObjectsFromClipboard(ObjectPasteDestination destination)
    {
        ObjectFolderSceneState beforeFolderState = _objectFolderService.CaptureSceneState();
        if (!_objectClipboard.TryPasteObjects(
                _sceneView.GetCurrentLocationContext(),
                beforeFolderState.DefaultLayoutId,
                _sceneView.GetPlacedFolders(),
                destination,
                out ObjectTransferImport import))
        {
            return false;
        }

        ObjectFolderSceneState afterFolderState = ObjectFolderSceneStateUtility.AddFolders(
            beforeFolderState,
            import.Folders,
            import.FolderColors);
        Guid[] selectionBefore = _interaction.CaptureSelection();
        Guid[] createdIds = import.Objects.Select(static snapshot => snapshot.Id).ToArray();
        string title = ResolvePasteHistoryTitle(import, destination);
        return TryApplyFolderChangeWithHistory(
            title,
            [],
            import.Objects,
            beforeFolderState,
            afterFolderState,
            SceneHistoryKind.Import,
            createdIds,
            selectionBefore);
    }

    internal bool CutObjectsToClipboard(IReadOnlyList<ObjectSnapshot> snapshots)
        => _objectClipboard.CopyObjects(snapshots)
        && _historyCoordinator.TryRemoveSceneItems(snapshots);

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

    private sealed class OrganizationHistoryAction : SceneHistoryActionBase
    {
        private readonly IObjectOrganizationService _organization;
        private readonly ObjectSnapshot[] _beforeSnapshots;
        private readonly ObjectSnapshot[] _afterSnapshots;
        private readonly ObjectFolderSceneState _beforeFolderState;
        private readonly ObjectFolderSceneState _afterFolderState;

        public OrganizationHistoryAction(
            IObjectOrganizationService organization,
            SceneHistoryKind kind,
            string title,
            IReadOnlyList<ObjectSnapshot> beforeSnapshots,
            IReadOnlyList<ObjectSnapshot> afterSnapshots,
            ObjectFolderSceneState beforeFolderState,
            ObjectFolderSceneState afterFolderState)
            : base(title, kind)
        {
            _organization = organization;
            _beforeSnapshots = [.. beforeSnapshots];
            _afterSnapshots = [.. afterSnapshots];
            _beforeFolderState = beforeFolderState;
            _afterFolderState = afterFolderState;
        }

        protected override void ApplyCore()
        {
            if (!_organization.ApplyFolderChange(
                    _beforeSnapshots,
                    _afterSnapshots,
                    _beforeFolderState,
                    _afterFolderState,
                    out _).IsApplied())
            {
                throw new InvalidOperationException($"could not apply '{Title}' organization state");
            }
        }

        protected override void RevertCore()
        {
            if (!_organization.ApplyFolderChange(
                    _afterSnapshots,
                    _beforeSnapshots,
                    _afterFolderState,
                    _beforeFolderState,
                    out _).IsApplied())
            {
                throw new InvalidOperationException($"could not revert '{Title}' organization state");
            }
        }
    }

    internal bool TryCreateFolderWithHistory(string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath)
         || _sceneView.GetPlacedFolders().Any(folder => string.Equals(
             folder,
             sanitizedFolderPath,
             StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var beforeState = _objectFolderService.CaptureSceneState();
        var afterState = ObjectFolderSceneStateUtility.AddFolder(beforeState, sanitizedFolderPath);
        return TryApplyFolderChangeWithHistory("Create Folder", [], [], beforeState, afterState);
    }

    internal bool TryRenameFolderWithHistory(string sourceFolderPath, string nextFolderPath)
    {
        var sanitizedSourceFolderPath = ObjectFolderUtility.SanitizeFolderPath(sourceFolderPath);
        var sanitizedNextFolderPath = ObjectFolderUtility.SanitizeFolderPath(nextFolderPath);
        if (string.IsNullOrEmpty(sanitizedSourceFolderPath)
            || string.IsNullOrEmpty(sanitizedNextFolderPath)
            || string.Equals(sanitizedSourceFolderPath, sanitizedNextFolderPath, StringComparison.OrdinalIgnoreCase)
            || ObjectFolderUtility.IsDescendantOf(sanitizedNextFolderPath, sanitizedSourceFolderPath)
            || _sceneView.GetPlacedFolders().Any(folder => string.Equals(
                folder,
                sanitizedNextFolderPath,
                StringComparison.OrdinalIgnoreCase)
                && !ObjectFolderUtility.IsSameOrDescendant(folder, sanitizedSourceFolderPath)))
        {
            return false;
        }

        var beforeFolderState = _objectFolderService.CaptureSceneState();
        var afterFolderState = ObjectFolderSceneStateUtility.RenameFolder(beforeFolderState, sanitizedSourceFolderPath, sanitizedNextFolderPath);
        var beforeSnapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => ObjectFolderUtility.IsSameOrDescendant(
                snapshot.FolderPath,
                sanitizedSourceFolderPath))
            .ToList();
        IReadOnlyList<ObjectSnapshot> requestedSnapshots = beforeSnapshots
            .Select(snapshot => snapshot with
            {
                FolderPath = ObjectFolderUtility.RebaseFolderPath(
                    snapshot.FolderPath,
                    sanitizedSourceFolderPath,
                    sanitizedNextFolderPath),
            })
            .ToList();
        return TryApplyFolderChangeWithHistory(
            "Rename Folder",
            beforeSnapshots,
            requestedSnapshots,
            beforeFolderState,
            afterFolderState);
    }

    internal bool TryDissolveFolderWithHistory(string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return false;
        }

        var beforeFolderState = _objectFolderService.CaptureSceneState();
        IReadOnlyDictionary<string, string> pathMap = ObjectFolderUtility.CreateDissolveFolderPathMap(
            sanitizedFolderPath,
            _sceneView.GetPlacedFolders());
        var afterFolderState = ObjectFolderSceneStateUtility.DissolveFolder(
            beforeFolderState,
            sanitizedFolderPath,
            pathMap);
        var beforeSnapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => ObjectFolderUtility.IsSameOrDescendant(snapshot.FolderPath, sanitizedFolderPath))
            .ToList();
        IReadOnlyList<ObjectSnapshot> requestedSnapshots = beforeSnapshots
            .Select(snapshot => snapshot with
            {
                FolderPath = ObjectFolderUtility.ApplyFolderPathMap(snapshot.FolderPath, pathMap),
            })
            .ToList();
        return TryApplyFolderChangeWithHistory(
            "Dissolve Folder",
            beforeSnapshots,
            requestedSnapshots,
            beforeFolderState,
            afterFolderState);
    }

    internal bool TrySetFolderColorWithHistory(string folderPath, string colorValue)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            return false;
        }

        var sanitizedColorValue = ObjectFolderUtility.SanitizeFolderColorValue(colorValue);
        var beforeState = _objectFolderService.CaptureSceneState();
        var afterState = ObjectFolderSceneStateUtility.SetFolderColor(beforeState, sanitizedFolderPath, sanitizedColorValue);
        var title = string.IsNullOrEmpty(sanitizedColorValue)
            ? "Clear Folder Color"
            : "Set Folder Color";
        return TryApplyFolderChangeWithHistory(title, [], [], beforeState, afterState);
    }

    internal bool TryApplyFolderChangeWithHistory(
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> requestedSnapshots,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState,
        SceneHistoryKind kind = SceneHistoryKind.Organization,
        IReadOnlyList<Guid>? selectionAfterApply = null,
        IReadOnlyList<Guid>? selectionAfterRevert = null)
    {
        if (SceneItemHistoryChanges.Build(beforeSnapshots, requestedSnapshots).Count == 0
            && ObjectFolderSceneStateUtility.StatesMatch(beforeFolderState, afterFolderState))
        {
            return false;
        }

        _historyCoordinator.PrepareForMutation();
        SceneMutationStatus status = _objectOrganizationService.ApplyFolderChange(
            beforeSnapshots,
            requestedSnapshots,
            beforeFolderState,
            afterFolderState,
            out IReadOnlyList<ObjectSnapshot> afterSnapshots);
        if (!status.IsApplied())
        {
            return false;
        }

        return TryRecordOrganizationHistoryAction(
            title,
            beforeSnapshots,
            afterSnapshots,
            beforeFolderState,
            afterFolderState,
            kind,
            selectionAfterApply,
            selectionAfterRevert);
    }

    private bool TryRecordOrganizationHistoryAction(
        string title,
        IReadOnlyList<ObjectSnapshot> beforeSnapshots,
        IReadOnlyList<ObjectSnapshot> afterSnapshots,
        ObjectFolderSceneState beforeFolderState,
        ObjectFolderSceneState afterFolderState,
        SceneHistoryKind kind = SceneHistoryKind.Organization,
        IReadOnlyList<Guid>? selectionAfterApply = null,
        IReadOnlyList<Guid>? selectionAfterRevert = null)
    {
        IReadOnlyList<SceneItemSnapshotChange> snapshotChanges = SceneItemHistoryChanges.Build(
            beforeSnapshots,
            afterSnapshots);
        if (snapshotChanges.Count == 0
            && ObjectFolderSceneStateUtility.StatesMatch(beforeFolderState, afterFolderState))
        {
            return false;
        }

        ISceneHistoryAction action = new OrganizationHistoryAction(
            _objectOrganizationService,
            kind,
            title,
            beforeSnapshots,
            afterSnapshots,
            beforeFolderState,
            afterFolderState);
        if (selectionAfterApply is not null || selectionAfterRevert is not null)
        {
            action = new SelectionHistoryAction(action, selectionAfterApply, selectionAfterRevert);
        }

        _historyCoordinator.RecordCompletedAction(action);
        return true;
    }

    internal bool TrySetObjectFolderWithHistory(IReadOnlyList<ObjectSnapshot> snapshots, string folderPath)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        if (snapshots.Count == 0
            || snapshots.All(snapshot => string.Equals(snapshot.FolderPath, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return _historyCoordinator.TryApplySelectedSnapshotUpdate(
            SceneHistoryKind.Organization,
            ResolveObjectFolderHistoryTitle(string.IsNullOrEmpty(sanitizedFolderPath), snapshots.Count),
            snapshots,
            entry => entry with { FolderPath = sanitizedFolderPath });
    }

    private static string ResolveObjectFolderHistoryTitle(bool ungroup, int objectCount)
    {
        if (ungroup)
        {
            return objectCount == 1 ? "Ungroup Object" : "Ungroup Objects";
        }

        return objectCount == 1 ? "Set Object Folder" : "Set Object Folders";
    }

    internal void AddDisplay()
    {
        if (!_scenePlacementService.TryResolveFromPlayer(out SceneTransform transform))
        {
            return;
        }

        _historyCoordinator.PrepareForMutation();
        Guid[] selectionBefore = _interaction.CaptureSelection();
        if (!_displayService.TryCreate(new DisplaySettings(), transform, true, out DisplaySnapshot created))
        {
            return;
        }

        _interaction.SelectionChanged(_interaction.Selection.TrySelect(created.Id, false));
        _ = _historyCoordinator.TryRecordCompletedAction(
            SceneHistoryKind.Create,
            "Create Display",
            [],
            [created],
            [created.Id],
            selectionBefore);
    }

    internal bool TrySetSceneItemsVisibleWithHistory(IReadOnlyList<SceneItemSnapshot> snapshots, bool visible)
        => _historyCoordinator.TryApplySceneItemUpdate(
            SceneHistoryKind.Visibility,
            ResolveSceneItemActionTitle(visible ? "Show" : "Hide", snapshots.Count),
            snapshots,
            item => item with { Visible = visible });

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
