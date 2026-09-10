using Dalamud.Interface;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class ObjectLibraryBrowser
{
    internal bool CanSavePlacedFolderAsPrefab(int objectCount)
        => objectCount > 0
        && _sceneView.GetCurrentLocationContext().Scope.IsValid;

    internal void OpenSaveLibraryPrefabDialog(string folderPath, string displayLabel, int objectCount)
    {
        SceneCreationContext context = _sceneView.GetCurrentLocationContext();
        if (objectCount == 0 || !context.Scope.IsValid)
        {
            return;
        }

        string objectCountLabel = objectCount == 1 ? "1 object" : $"{objectCount} objects";
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "object-library-prefab-save",
            "Save Folder to Library",
            "Save Prefab",
            input => TrySavePlacedFolderToLibrary(folderPath, input, context.Scope)) with
        {
            Icon = FontAwesomeIcon.LayerGroup,
            Accent = ThemeColors.AccentPrimary,
            InitialValue = displayLabel,
            Placeholder = "prefab name",
            Detail = $"Save {objectCountLabel} as one reusable prefab.",
            Validate = input => ValidateLibraryGroupName(input, null, "Prefab"),
            FailureMessage = "The prefab could not be saved to the Library.",
        });
    }

    private bool TrySavePlacedFolderToLibrary(string folderPath, string name, SceneLocationScope expectedScope)
    {
        SceneCreationContext context = _sceneView.GetCurrentLocationContext();
        if (context.Scope != expectedScope)
        {
            return false;
        }

        string normalizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        IReadOnlyList<ObjectSnapshot> snapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => ObjectFolderUtility.IsSameOrDescendant(snapshot.FolderPath, normalizedFolderPath))
            .ToArray();
        if (snapshots.Count == 0)
        {
            return false;
        }

        ObjectFolderSceneState folderState = _objectFolderService.CaptureSceneState();
        IReadOnlyList<ObjectFolderSnapshot> folders = folderState.DefaultLayoutId.HasValue
            ? folderState.DefaultLayoutFolders
            : folderState.StandaloneFolders;
        string color = folders.FirstOrDefault(folder => string.Equals(
            folder.Path, normalizedFolderPath, StringComparison.OrdinalIgnoreCase))?.Color ?? string.Empty;
        return _objectLibrary.TryCreatePrefab(
            name,
            color,
            context,
            snapshots,
            parentFolderId: null,
            out _);
    }

    private bool CanPlaceLibraryPrefab(LibraryBrowserView.Group group)
        => group.Source is ObjectLibraryPrefab
        && group.AllEntries.All(entry => _createActions.CanPlaceObjectPreset(entry.Preset));

    private bool CanRestoreLibraryPrefabAtWorldPosition(LibraryBrowserView.Group group)
        => group.Source is ObjectLibraryPrefab prefab
        && ObjectLibraryPrefabPlacementResolver.CanRestoreWorldPosition(
            prefab,
            _sceneView.GetCurrentLocationContext());

    private bool TryPlaceLibraryPrefab(LibraryBrowserView.Group group, bool atPlayer)
    {
        if (!TryResolvePrefabPlacements(group, atPlayer, out IReadOnlyList<ObjectLibraryPrefabPlacement> placements))
        {
            return false;
        }

        ObjectFolderSceneState beforeFolderState = _objectFolderService.CaptureSceneState();
        string folderPath = ObjectFolderUtility.ResolveAvailableFolderPath(
            group.Source.Name,
            _sceneView.GetPlacedFolders());
        if (folderPath.Length == 0)
        {
            return false;
        }

        IReadOnlyList<ObjectSnapshot> snapshots = BuildPrefabSnapshots(
            placements,
            folderPath,
            beforeFolderState.DefaultLayoutId);

        ObjectFolderSceneState afterFolderState = ObjectFolderSceneStateUtility.AddFolder(
            beforeFolderState,
            folderPath);
        if (!string.IsNullOrEmpty(group.Source.Color))
        {
            afterFolderState = ObjectFolderSceneStateUtility.SetFolderColor(
                afterFolderState,
                folderPath,
                group.Source.Color);
        }

        Guid[] selectionBefore = _interaction.CaptureSelection();
        Guid[] createdIds = snapshots.Select(static snapshot => snapshot.Id).ToArray();
        return _sceneCommands.TryApplyFolderChangeWithHistory(
            $"Place {group.Source.Name}",
            [],
            snapshots,
            beforeFolderState,
            afterFolderState,
            SceneHistoryKind.Create,
            createdIds,
            selectionBefore);
    }

    private bool TryResolvePrefabPlacements(
        LibraryBrowserView.Group group,
        bool atPlayer,
        out IReadOnlyList<ObjectLibraryPrefabPlacement> placements)
    {
        if (group.Source is not ObjectLibraryPrefab
         || group.AllEntries.Any(entry => !_createActions.CanPlaceObjectPreset(entry.Preset)))
        {
            placements = [];
            return false;
        }

        Vector3? targetAnchor = null;
        if (atPlayer)
        {
            if (!_scenePlacementService.TryResolveFromPlayer(out SceneTransform playerPlacement))
            {
                placements = [];
                return false;
            }

            targetAnchor = playerPlacement.Position;
        }
        else if (!CanRestoreLibraryPrefabAtWorldPosition(group))
        {
            placements = [];
            return false;
        }

        return ObjectLibraryPrefabPlacementResolver.TryResolve(
            group.AllEntries,
            targetAnchor,
            out placements);
    }

    private IReadOnlyList<ObjectSnapshot> BuildPrefabSnapshots(
        IReadOnlyList<ObjectLibraryPrefabPlacement> placements,
        string folderPath,
        Guid? layoutId)
    {
        Dictionary<Guid, Guid> objectIds = placements.ToDictionary(
            static placement => placement.Entry.Id,
            static _ => Guid.NewGuid());
        ObjectSnapshot[] result = new ObjectSnapshot[placements.Count];
        for (int index = 0; index < placements.Count; ++index)
        {
            ObjectLibraryPrefabPlacement placement = placements[index];
            ObjectLibraryEntry entry = placement.Entry;
            ObjectData model = entry.Preset.Model;
            if (model is FurnitureModel furniture)
            {
                model = furniture with
                {
                    AttachmentParentId = entry.AttachmentParentEntryId is { } parentEntryId
                        && objectIds.TryGetValue(parentEntryId, out Guid parentObjectId)
                            ? parentObjectId
                            : null,
                };
            }

            SceneTransform transform = placement.Transform with { Scale = entry.Preset.Scale };
            result[index] = _objectKindService.CreateDefaultSnapshot(
                entry.Preset.Kind,
                transform,
                entry.Name) with
            {
                Id = objectIds[entry.Id],
                Visible = entry.Preset.Visible,
                FolderPath = folderPath,
                LayoutId = layoutId,
                Transform = transform,
                Model = model,
            };
        }

        return result;
    }
}
