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
    internal bool CanSavePlacedObjectsAsPrefab(int objectCount)
        => objectCount > 0
        && _sceneView.GetCurrentLocationContext().IsAssigned;

    internal void OpenSaveLibraryPrefabDialog(Guid? layoutId, string folderPath, string displayLabel, int objectCount)
        => OpenSaveLibraryPrefabDialog("Save Folder to Library", displayLabel, objectCount,
            (name, scope) => TrySavePlacedFolderToLibrary(layoutId, folderPath, name, scope));

    internal void OpenSaveGroupAsPrefabDialog(SceneGroupSnapshot group, int objectCount)
        => OpenSaveLibraryPrefabDialog("Save Group to Library", group.Name, objectCount,
            (name, scope) => TrySavePlacedGroupToLibrary(group.Id, name, scope));

    private void OpenSaveLibraryPrefabDialog(string title, string displayLabel, int objectCount, Func<string, SceneCreationContext, bool> save)
    {
        SceneCreationContext context = _sceneView.GetCurrentLocationContext();
        if (objectCount == 0 || !context.IsAssigned)
        {
            return;
        }

        string objectCountLabel = objectCount == 1 ? "1 object" : $"{objectCount} objects";
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "object-library-prefab-save",
            title,
            "Save Prefab",
            input => save(input, context)) with
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

    private bool TrySavePlacedGroupToLibrary(Guid groupId, string name, SceneCreationContext expectedContext)
    {
        SceneCreationContext context = _sceneView.GetCurrentLocationContext();
        SceneGroupSnapshot? group = _sceneView.GetPlacedGroups().FirstOrDefault(snapshot => snapshot.Id == groupId);
        if (!context.HasSameLocation(expectedContext) || group is null)
        {
            return false;
        }

        ObjectSnapshot[] members = _sceneView.GetPlacedObjectSnapshots().Where(snapshot => snapshot.GroupId == groupId).ToArray();
        return members.Length > 0 && _objectLibrary.TryCreatePrefab(name, string.Empty, context, members, parentFolderId: null, out _);
    }

    private bool TrySavePlacedFolderToLibrary(Guid? layoutId, string folderPath, string name, SceneCreationContext expectedContext)
    {
        SceneCreationContext context = _sceneView.GetCurrentLocationContext();
        if (!context.HasSameLocation(expectedContext))
        {
            return false;
        }

        string normalizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
        IReadOnlyList<ObjectSnapshot> snapshots = _sceneView.GetPlacedObjectSnapshots()
            .Where(snapshot => snapshot.LayoutId == layoutId && ObjectFolderUtility.IsSameOrDescendant(snapshot.FolderPath, normalizedFolderPath))
            .ToArray();
        if (snapshots.Count == 0)
        {
            return false;
        }

        ObjectFolderSceneState folderState = _objectFolderService.CaptureSceneState();
        IReadOnlyList<ObjectFolderSnapshot> folders = ObjectFolderSceneStateUtility.GetFolders(folderState, layoutId);
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

    private void DrawPrefabPlacementMenu(LibraryBrowserView.Group group, bool atPlayer)
    {
        using EditorContextMenu.SubMenuScope menu = EditorContextMenu.BeginSubMenu(
            $"objectLibraryPlacePrefab:{group.Source.Id}:{atPlayer}",
            atPlayer ? FontAwesomeIcon.User : FontAwesomeIcon.Globe,
            atPlayer ? "Place at Your Position" : "Place at World Position");
        if (!menu)
            return;

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectGroup, "As Group"))
            _ = TryPlaceLibraryPrefab(group, atPlayer, PrefabPlacementContainer.Group);
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Folder, "As Folder"))
            _ = TryPlaceLibraryPrefab(group, atPlayer, PrefabPlacementContainer.Folder);
    }

    private bool TryPlaceLibraryPrefab(LibraryBrowserView.Group group, bool atPlayer, PrefabPlacementContainer container)
    {
        if (!TryResolvePrefabPlacements(group, atPlayer, out IReadOnlyList<ObjectLibraryPrefabPlacement> placements))
        {
            return false;
        }

        Guid? layoutId = _sceneView.GetCreationLayoutId();
        IReadOnlyList<string> placedFolders = _sceneView.GetPlacedFolders(layoutId);
        string parentFolder = _draft.ResolveCreatePlacementFolderPath(layoutId, placedFolders);
        IReadOnlyList<ObjectSnapshot> snapshots = BuildPrefabSnapshots(
            placements,
            parentFolder,
            layoutId);
        return _sceneCommands.TryPlacePrefab(group.Source.Name, snapshots, container, group.Source.Color);
    }

    private bool TryResolvePrefabPlacements(
        LibraryBrowserView.Group group,
        bool atPlayer,
        out IReadOnlyList<ObjectLibraryPrefabPlacement> placements)
    {
        if (!CanPlaceLibraryPrefab(group))
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
