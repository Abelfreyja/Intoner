using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Dependencies;
using System.Numerics;
using static Intoner.Objects.UI.Components.CollectionStatusUi;

namespace Intoner.Objects.UI;

internal sealed partial class CollectionsWorkspace
{
    private readonly IObjectOrganizationService _objectOrganizationService;
    private readonly IObjectSceneView _sceneView;
    private readonly IObjectCollectionManager _objectCollectionManager;
    private readonly IObjectModDataSource _objectModDataSource;
    private readonly IDependencyService _dependencies;
    private readonly EditorInteraction _interaction;
    private readonly SceneEditorCommands _sceneCommands;
    private readonly EditorOverlayLayer _editorOverlayLayer;
    private readonly EditorListCard _listCard;
    private readonly SceneItemControls _sceneItems;
    private readonly CollectionModSettingsEditor _modSettings;
    private readonly CollectionPanel _collectionPanel;
    private readonly HashSet<string> _expandedCollectionModRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditorBadge> _collectionModBadgeBuffer = new(2);
    private bool _openObjectCollectionAddModPopupNextFrame;
    private string _selectedObjectCollectionId = string.Empty;
    private string _objectCollectionAddModPopupCollectionId = string.Empty;
    private string _objectCollectionModFilter = string.Empty;
    private string _objectCollectionAssignedModFilter = string.Empty;
    private string _objectCollectionAssignedObjectFilter = string.Empty;
    private Vector2 _objectCollectionAddModPopupAnchorMin;
    private Vector2 _objectCollectionAddModPopupAnchorMax;
    private const string ObjectCollectionAddModPopupId = "##objectCollectionAddModPopup";
    private const int ObjectCollectionNameMaxLength = 128;
    private ObjectCollectionView _objectCollectionView = ObjectCollectionView.Mods;

    public CollectionsWorkspace(
        IObjectOrganizationService objectOrganizationService,
        IObjectSceneView sceneView,
        IObjectCollectionManager objectCollectionManager,
        IObjectModDataSource objectModDataSource,
        IDependencyService dependencies,
        EditorInteraction interaction,
        SceneEditorCommands sceneCommands,
        EditorOverlayLayer editorOverlayLayer,
        EditorListCard listCard,
        SceneItemControls sceneItems,
        CollectionModSettingsEditor modSettings,
        CollectionPanel collectionPanel)
    {
        _objectOrganizationService = objectOrganizationService;
        _sceneView                 = sceneView;
        _objectCollectionManager   = objectCollectionManager;
        _objectModDataSource       = objectModDataSource;
        _dependencies              = dependencies;
        _interaction               = interaction;
        _sceneCommands             = sceneCommands;
        _editorOverlayLayer        = editorOverlayLayer;
        _listCard                  = listCard;
        _sceneItems                = sceneItems;
        _modSettings               = modSettings;
        _collectionPanel           = collectionPanel;
    }

    private void DrawObjectCollectionHeader(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        ObjectCollectionSnapshot? collection,
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        int assignedObjectCount,
        float width)
    {
        Vector4 accent = collection is null
            ? ThemeColors.AccentPrimary
            : ResolveObjectCollectionAccentColor(collection.ResolveState);
        float menuButtonEdge = EditorIconButton.MeasureMaxEdge(FontAwesomeIcon.EllipsisH);
        float assignWidth = EditorButton.MeasureWidth(FontAwesomeIcon.MousePointer, "Assign");
        float unassignWidth = EditorButton.MeasureWidth(FontAwesomeIcon.Unlink, "Unassign");
        float actionsWidth = assignWidth
                           + unassignWidth
                           + EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderMenuGap)
                           + menuButtonEdge;
        float height = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderHeight);
        float padding = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderPadding);
        float gap = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderGap);
        Vector2 startCursor = ImGui.GetCursorPos();
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(width, height);
        ImGui.Dummy(new Vector2(width, height));
        Vector2 endCursor = ImGui.GetCursorPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(CollectionPanel.ObjectCollectionWorkspaceStyle.Header),
            EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.SurfaceRounding),
            ImDrawFlags.RoundCornersTop);
        drawList.AddLine(
            new Vector2(min.X, max.Y),
            max,
            ImGui.GetColorU32(CollectionPanel.ObjectCollectionWorkspaceStyle.Divider),
            EditorLayout.Scaled(1f));

        float innerWidth = MathF.Max(1f, width - (padding * 2f));
        float pickerWidth = Math.Clamp(
            innerWidth * 0.32f,
            EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.PickerMinWidth),
            EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.PickerMaxWidth));
        float actionsX = max.X - padding - actionsWidth;
        float summaryX = min.X + padding + pickerWidth + gap;
        float summaryWidth = actionsX - gap - summaryX;
        if (summaryWidth < EditorLayout.Scaled(160f))
        {
            pickerWidth = MathF.Max(EditorLayout.Scaled(120f), actionsX - gap - (min.X + padding));
            summaryWidth = 0f;
        }

        ImGui.SetCursorScreenPos(new Vector2(
            min.X + padding,
            min.Y + ((height - EditorPicker.Height) * 0.5f)));
        DrawObjectCollectionPicker(collections, collection, assignedObjectCount, pickerWidth, accent);

        if (summaryWidth > 0f)
        {
            IReadOnlyList<EditorBadge> badges = collection is null
                ? [EditorBadge.Count(FontAwesomeIcon.Swatchbook, collections.Count, "collection", "collections")]
                : BuildObjectCollectionWorkspaceBadges(collection, assignedObjectCount);
            float badgeHeight = EditorBadgeRenderer.Height;
            ImGui.SetCursorScreenPos(new Vector2(
                summaryX,
                min.Y + ((height - badgeHeight) * 0.5f)));
            EditorBadgeRenderer.DrawInline(
                badges,
                maxWidth: summaryWidth);
        }

        ImGui.SetCursorScreenPos(new Vector2(
            actionsX,
            min.Y + ((height - EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderActionHeight)) * 0.5f)));
        DrawObjectCollectionHeaderActions(
            collection,
            selectedSnapshots,
            assignWidth,
            unassignWidth,
            menuButtonEdge);
        ImGui.SetCursorPos(new Vector2(startCursor.X, endCursor.Y));
    }

    private void DrawObjectCollectionPicker(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        ObjectCollectionSnapshot? selectedCollection,
        int assignedObjectCount,
        float width,
        Vector4 accent)
    {
        string label = selectedCollection?.Record.Name ?? "Choose Collection";
        string pickerDetail;
        if (selectedCollection is null)
        {
            pickerDetail = collections.Count == 0
                ? "Create the first collection"
                : "Choose a collection to manage";
        }
        else
        {
            pickerDetail = $"{ResolveObjectCollectionStateLabel(selectedCollection.ResolveState)} collection";
        }
        ObjectCollectionHeaderStatus? status = selectedCollection is null
            ? null
            : BuildObjectCollectionHeaderStatus(selectedCollection, assignedObjectCount);
        bool open = EditorPicker.DrawDetailed(
            "objectCollectionPicker",
            FontAwesomeIcon.Swatchbook,
            label,
            pickerDetail,
            accent,
            width);
        Vector2 popupAnchor = new(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);
        if (status is not null && ImGui.IsItemHovered())
        {
            DrawObjectCollectionStatusTooltip(status, accent);
        }

        if (selectedCollection is not null)
        {
            using EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem(
                "##objectCollectionPickerContext");
            if (contextMenu)
            {
                DrawObjectCollectionActionsMenu(selectedCollection);
            }
        }

        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdown(
            "##objectCollectionPickerPopup",
            open,
            popupAnchor,
            width / ImGuiHelpers.GlobalScale);
        if (!popup)
        {
            return;
        }

        EditorContextMenu.DrawFirstSectionLabel("Collections");
        foreach (ObjectCollectionSnapshot collection in collections)
        {
            bool selected = string.Equals(
                collection.Record.CollectionId,
                _selectedObjectCollectionId,
                StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<EditorBadge> optionBadges = BuildObjectCollectionPickerBadges(collection);
            bool selectedOption = EditorChoiceMenu.DrawOption(
                    collection.Record.CollectionId,
                    FontAwesomeIcon.Swatchbook,
                    collection.Record.Name,
                    optionBadges,
                    selected);
            bool contextRequested = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            using (EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem(
                       $"##objectCollectionPickerOptionContext:{collection.Record.CollectionId}"))
            {
                if (contextMenu)
                {
                    DrawObjectCollectionActionsMenu(collection);
                }
            }

            if (contextRequested || selectedOption)
            {
                _selectedObjectCollectionId = collection.Record.CollectionId;
            }
        }

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Plus, "Create Collection"))
        {
            OpenCreateObjectCollectionDialog();
        }
    }

    private void DrawObjectCollectionHeaderActions(
        ObjectCollectionSnapshot? collection,
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        float assignWidth,
        float unassignWidth,
        float menuButtonEdge)
    {
        const int actionCount = 2;
        float height = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderActionHeight);
        bool canAssign = collection is not null
            && SceneEditorCommands.HasObjectCollectionAssignmentChange(selectedSnapshots, collection.Record.CollectionId);
        if (EditorSegmentedControl.DrawActionSegment(
                "##objectCollectionAssignSelection",
                FontAwesomeIcon.MousePointer,
                "Assign",
                canAssign,
                ResolveObjectCollectionAssignmentTooltip(collection, selectedSnapshots, canAssign),
                ThemeColors.AccentPrimary,
                new Vector2(assignWidth, height),
                0,
                actionCount)
            && collection is not null)
        {
            _sceneCommands.ApplyObjectCollectionToSelectedObjects(collection.Record.CollectionId, selectedSnapshots);
        }

        ImGui.SameLine(0f, 0f);
        bool canUnassign = SceneEditorCommands.HasAnyObjectCollectionAssignment(selectedSnapshots);
        if (EditorSegmentedControl.DrawActionSegment(
                "##objectCollectionUnassignSelection",
                FontAwesomeIcon.Unlink,
                "Unassign",
                canUnassign,
                ResolveObjectCollectionUnassignmentTooltip(selectedSnapshots, canUnassign),
                ThemeColors.AccentOrange,
                new Vector2(unassignWidth, height),
                1,
                actionCount))
        {
            _sceneCommands.UnassignObjectCollections(selectedSnapshots);
        }

        ImGui.SameLine(0f, EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderMenuGap));
        bool openMenu;
        using (ImRaii.Disabled(collection is null))
        {
            openMenu = EditorIconButton.DrawAccent(
                "objectCollectionMenu",
                FontAwesomeIcon.EllipsisH,
                "Collection menu",
                ThemeColors.AccentPrimary,
                menuButtonEdge);
        }

        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdownForLastItem(
            "##objectCollectionActionsPopup",
            openMenu);
        if (popup && collection is not null)
        {
            DrawObjectCollectionActionsMenu(collection);
        }
    }

    private static string ResolveObjectCollectionAssignmentTooltip(
        ObjectCollectionSnapshot? collection,
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        bool canAssign)
    {
        if (canAssign)
        {
            return "Assign this collection to the selected placed objects";
        }

        if (collection is null)
        {
            return "Choose a collection before assigning placed objects";
        }

        return selectedSnapshots.Count == 0
            ? "Select one or more placed objects to assign this collection"
            : "The selected placed objects already use this collection";
    }

    private static string ResolveObjectCollectionUnassignmentTooltip(
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        bool canUnassign)
    {
        if (canUnassign)
        {
            return "Remove collection assignments from the selected placed objects";
        }

        return selectedSnapshots.Count == 0
            ? "Select one or more placed objects to unassign"
            : "The selected placed objects have no collection assignment";
    }

    private void DrawObjectCollectionActionsMenu(ObjectCollectionSnapshot collection)
    {
        EditorContextMenu.DrawFirstSectionLabel(collection.Record.Name);
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Rename Collection"))
        {
            OpenRenameObjectCollectionDialog(collection);
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Redo, "Reresolve Collection"))
        {
            RecompileObjectCollection(collection);
        }

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Trash,
                "Delete Collection",
                color: ThemeColors.DimRed))
        {
            OpenDeleteObjectCollectionDialog(collection);
        }
    }

    private static IReadOnlyList<EditorBadge> BuildObjectCollectionPickerBadges(ObjectCollectionSnapshot collection)
        =>
        [
            EditorBadge.Label(
                ResolveObjectCollectionStateLabel(collection.ResolveState),
                ResolveObjectCollectionStateIcon(collection.ResolveState),
                ResolveObjectCollectionAccentColor(collection.ResolveState)),
            EditorBadge.Count(
                FontAwesomeIcon.Cubes,
                collection.Record.Entries.Count,
                "assigned mod",
                "assigned mods"),
        ];

    private bool CreateObjectCollection(string name)
    {
        if (!_objectCollectionManager.TryCreateCollection(name, out ObjectCollectionSnapshot snapshot))
        {
            return false;
        }

        _selectedObjectCollectionId = snapshot.Record.CollectionId;
        return true;
    }

    private void OpenRenameObjectCollectionDialog(ObjectCollectionSnapshot collection)
    {
        string collectionId = collection.Record.CollectionId;
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "collection-rename",
            "Rename Collection",
            "Rename Collection",
            name => TryRenameObjectCollection(collectionId, name)) with
        {
            Icon = FontAwesomeIcon.Edit,
            Accent = ThemeColors.AccentPrimary,
            InitialValue = collection.Record.Name,
            Placeholder = "collection name",
            MaxLength = ObjectCollectionNameMaxLength,
            Validate = static input => TextUtility.TrimOrEmpty(input).Length == 0
                ? "Enter a collection name."
                : null,
        });
    }

    private bool TryRenameObjectCollection(string collectionId, string name)
    {
        if (!_objectCollectionManager.TryGetCollection(collectionId, out ObjectCollectionSnapshot collection))
        {
            return false;
        }

        return _objectCollectionManager.TryUpdateCollection(
            collection.Record with { Name = TextUtility.TrimOrEmpty(name) },
            out _);
    }

    private void RecompileObjectCollection(ObjectCollectionSnapshot collection)
        => _objectCollectionManager.EnsureCollectionMaterialized(collection.Record.CollectionId, forceResolve: true);

    private void OpenDeleteObjectCollectionDialog(ObjectCollectionSnapshot collection)
    {
        string collectionId = collection.Record.CollectionId;
        _interaction.OpenDialog(EditorDialog.Request.TryConfirmation(
            "collection-delete",
            "Delete Collection",
            "Delete Collection",
            () => TryDeleteObjectCollection(collectionId)) with
        {
            Icon = FontAwesomeIcon.Trash,
            ConfirmIcon = FontAwesomeIcon.Trash,
            Accent = ThemeColors.DimRed,
            Detail = collection.Record.Name,
            Description = "This permanently deletes the collection and unassigns it from placed objects. Installed Penumbra mods are NOT deleted.",
            FailureMessage = "The collection could not be deleted.",
        });
    }

    private bool TryDeleteObjectCollection(string collectionId)
    {
        if (!_objectCollectionManager.TryGetCollection(collectionId, out ObjectCollectionSnapshot collection))
        {
            return false;
        }

        if (!_objectOrganizationService.DeleteCollection(collection.Record.CollectionId).IsApplied())
        {
            return false;
        }

        if (string.Equals(_selectedObjectCollectionId, collection.Record.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedObjectCollectionId = string.Empty;
        }

        foreach (ObjectCollectionModSettings entry in collection.Record.Entries)
        {
            ForgetObjectCollectionModRowState(collection.Record.CollectionId, entry);
        }

        return true;
    }

    private bool AddObjectCollectionEntry(ObjectCollectionSnapshot collection, ObjectAvailableMod mod)
    {
        string modDirectory = ObjectCollectionKeyUtility.NormalizeModDirectory(mod.ModDirectory);
        if (modDirectory.Length == 0
         || collection.Record.Entries.Any(entry => string.Equals(ObjectCollectionKeyUtility.NormalizeModDirectory(entry.ModDirectory), modDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        List<ObjectCollectionModSettings> entries = [.. collection.Record.Entries];
        entries.Add(new ObjectCollectionModSettings
        {
            ModDirectory = mod.ModDirectory,
            ModName = mod.ModName,
        });

        return _modSettings.TryUpdateObjectCollectionEntries(collection, entries);
    }

    private void RemoveObjectCollectionEntry(ObjectCollectionSnapshot collection, int index)
    {
        if (index < 0 || index >= collection.Record.Entries.Count)
        {
            return;
        }

        List<ObjectCollectionModSettings> entries = [.. collection.Record.Entries];
        ObjectCollectionModSettings removedEntry = entries[index];
        entries.RemoveAt(index);
        if (_modSettings.TryUpdateObjectCollectionEntries(collection, entries))
        {
            ForgetObjectCollectionModRowState(collection.Record.CollectionId, removedEntry);
        }
    }

    private void SyncSelectedObjectCollection(IReadOnlyList<ObjectCollectionSnapshot> collections)
    {
        bool selectionExists = _selectedObjectCollectionId.Length > 0
            && collections.Any(collection => string.Equals(
                collection.Record.CollectionId,
                _selectedObjectCollectionId,
                StringComparison.OrdinalIgnoreCase));
        if (!selectionExists)
        {
            _selectedObjectCollectionId = collections.FirstOrDefault()?.Record.CollectionId ?? string.Empty;
        }
    }

    private bool TryResolveSelectedObjectCollection(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        out ObjectCollectionSnapshot selectedCollection)
        => SceneItemPresentation.TryResolveObjectCollectionById(collections, _selectedObjectCollectionId, out selectedCollection);

    internal enum ObjectCollectionView
    {
        Objects,
        Mods,
    }

    internal void DrawCollectionsWorkspace(IReadOnlySet<Guid> activeObjectIds)
    {
        IReadOnlyList<ObjectCollectionSnapshot> collections = _objectCollectionManager.GetCollections();
        SyncSelectedObjectCollection(collections);

        _editorOverlayLayer.DrawChildPanel(
            "##objectCollectionsWorkspacePanel",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
            () => DrawObjectCollectionWorkspace(collections, activeObjectIds),
            transparentBackground: false);

        DrawObjectCollectionAddModPopup(collections);
    }

    private void DrawObjectCollectionWorkspace(
        IReadOnlyList<ObjectCollectionSnapshot> collections,
        IReadOnlySet<Guid> activeObjectIds)
    {
        ObjectCollectionSnapshot? selectedCollection = TryResolveSelectedObjectCollection(
            collections,
            out ObjectCollectionSnapshot resolvedCollection)
                ? resolvedCollection
                : null;
        IReadOnlyList<ObjectSnapshot> placedSnapshots = _sceneView.GetPlacedObjectSnapshots();
        IReadOnlyList<ObjectSnapshot> selectedSnapshots = placedSnapshots
            .Where(snapshot => _interaction.Selection.SelectedItemIds.Contains(snapshot.Id))
            .ToList();
        IReadOnlyList<ObjectSnapshot> assignedSnapshots = selectedCollection is null
            ? []
            : placedSnapshots
                .Where(snapshot => string.Equals(
                    snapshot.CollectionId,
                    selectedCollection.Record.CollectionId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

        float surfaceWidth = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float availableHeight = EditorLayout.ResolveRemainingRegionHeight();
        float panelWidth = EditorLayout.Positive(surfaceWidth - EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.PanelHorizontalInset * 2f));
        float fixedHeight = EditorLayout.Scaled(
            CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderHeight
          + CollectionPanel.ObjectCollectionWorkspaceStyle.TabsHeight
          + CollectionPanel.ObjectCollectionWorkspaceStyle.PanelTopGap
          + CollectionPanel.ObjectCollectionWorkspaceStyle.PanelBottomInset);
        float panelHeight = EditorLayout.Positive(availableHeight - fixedHeight);
        float surfaceHeight = fixedHeight + panelHeight;

        Vector2 surfaceCursor = ImGui.GetCursorPos();
        Vector2 surfaceMin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(surfaceWidth, surfaceHeight));
        Vector2 surfaceEndCursor = ImGui.GetCursorPos();
        Vector2 surfaceMax = surfaceMin + new Vector2(surfaceWidth, surfaceHeight);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            surfaceMin,
            surfaceMax,
            ImGui.GetColorU32(CollectionPanel.ObjectCollectionWorkspaceStyle.Surface),
            EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.SurfaceRounding));

        ImGui.SetCursorPos(surfaceCursor);
        DrawObjectCollectionHeader(
            collections,
            selectedCollection,
            selectedSnapshots,
            assignedSnapshots.Count,
            surfaceWidth);
        DrawObjectCollectionViewSelector(selectedCollection, assignedSnapshots.Count, surfaceWidth);

        Vector2 panelMin = new(
            surfaceMin.X + EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.PanelHorizontalInset),
            surfaceMin.Y + EditorLayout.Scaled(
                CollectionPanel.ObjectCollectionWorkspaceStyle.HeaderHeight
              + CollectionPanel.ObjectCollectionWorkspaceStyle.TabsHeight
              + CollectionPanel.ObjectCollectionWorkspaceStyle.PanelTopGap));
        ImGui.SetCursorScreenPos(panelMin);
        DrawObjectCollectionContent(
            selectedCollection,
            assignedSnapshots,
            activeObjectIds,
            panelWidth,
            panelHeight);

        drawList.AddRect(
            surfaceMin,
            surfaceMax,
            ImGui.GetColorU32(CollectionPanel.ObjectCollectionWorkspaceStyle.Border),
            EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.SurfaceRounding),
            ImDrawFlags.None,
            EditorLayout.Scaled(1f));
        ImGui.SetCursorPos(surfaceEndCursor);
    }

    private void DrawObjectCollectionContent(
        ObjectCollectionSnapshot? collection,
        IReadOnlyList<ObjectSnapshot> assignedSnapshots,
        IReadOnlySet<Guid> activeObjectIds,
        float width,
        float height)
    {
        float panelHeight = EditorLayout.Positive(height - EditorLayout.Scaled(1f));
        using var childBackground = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var child = ImRaii.Child(
            "##objectCollectionContent",
            new Vector2(width, height),
            false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!child)
        {
            return;
        }

        _editorOverlayLayer.CaptureCurrentWindow();
        if (collection is null)
        {
            DrawObjectCollectionEmptyWorkspace(panelHeight);
        }
        else if (_objectCollectionView == ObjectCollectionView.Objects)
        {
            DrawObjectCollectionObjectsSection(collection, assignedSnapshots, activeObjectIds, width, panelHeight);
        }
        else
        {
            DrawObjectCollectionModsSection(collection, width, panelHeight);
        }
    }

    private void DrawObjectCollectionViewSelector(
        ObjectCollectionSnapshot? collection,
        int assignedObjectCount,
        float width)
    {
        const int segmentCount = 2;
        Vector2 startCursor = ImGui.GetCursorPos();
        Vector2 min = ImGui.GetCursorScreenPos();
        float height = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.TabsHeight);
        ImGui.Dummy(new Vector2(width, height));
        Vector2 endCursor = ImGui.GetCursorPos();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            min,
            min + new Vector2(width, height),
            ImGui.GetColorU32(CollectionPanel.ObjectCollectionWorkspaceStyle.Tabs));

        float inset = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.TabsHorizontalInset);
        float gap = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.TabGap);
        float buttonWidth = MathF.Max(1f, (width - (inset * 2f) - gap) / segmentCount);
        float buttonHeight = EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.TabHeight);
        ImGui.SetCursorScreenPos(new Vector2(
            min.X + inset,
            min.Y + EditorLayout.Scaled(CollectionPanel.ObjectCollectionWorkspaceStyle.TabsTopInset)));
        bool hasCollection = collection is not null;

        if (EditorSegmentedControl.DrawSegment(
                "##objectCollectionObjectsView",
                FontAwesomeIcon.Cube,
                "Assigned Objects",
                assignedObjectCount.ToString(),
                _objectCollectionView == ObjectCollectionView.Objects,
                hasCollection,
                "Show the placed objects assigned to this collection",
                ThemeColors.AccentPrimary,
                new Vector2(buttonWidth, buttonHeight),
                0,
                segmentCount,
                rounding: 4f,
                showSelectionIndicator: false))
        {
            _objectCollectionView = ObjectCollectionView.Objects;
        }

        ImGui.SameLine(0f, gap);
        int modCount = collection?.Record.Entries.Count ?? 0;
        if (EditorSegmentedControl.DrawSegment(
                "##objectCollectionModsView",
                FontAwesomeIcon.Cubes,
                "Penumbra Mods",
                modCount.ToString(),
                _objectCollectionView == ObjectCollectionView.Mods,
                hasCollection,
                "Show and configure this collection's Penumbra mods",
                ThemeColors.AccentPrimary,
                new Vector2(buttonWidth, buttonHeight),
                1,
                segmentCount,
                rounding: 4f,
                showSelectionIndicator: false))
        {
            _objectCollectionView = ObjectCollectionView.Mods;
        }

        ImGui.SetCursorPos(new Vector2(startCursor.X, endCursor.Y));
    }
}
