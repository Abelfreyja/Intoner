using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Displays;
using Intoner.Objects.Catalog;
using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Configuration;
using Intoner.Services.Gpu;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Intoner.Objects.UI;

internal sealed partial class SceneItemInspector
{
    private readonly IObjectSceneView             _sceneView;
    private readonly ISceneInputService           _sceneInputService;
    private readonly ISceneLocationService        _sceneLocationService;
    private readonly IHistoryCoordinator          _historyCoordinator;
    private readonly IObjectLayoutManager         _layoutManager;
    private readonly PlacementFixExecutor         _placementFixExecutor;
    private readonly IObjectCollectionManager     _objectCollectionManager;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly WindowsCaptureTargetService  _captureTargetService;
    private readonly EditorInteraction            _interaction;
    private readonly EditorSceneState             _sceneState;
    private readonly SceneEditorCommands          _sceneCommands;
    private readonly EditorOverlayLayer           _editorOverlayLayer;
    private readonly SceneTransformEditor         _transformEditor;
    private readonly IFurnitureStainService       _furnitureStainService;
    private readonly SceneItemControls            _sceneItems;
    private readonly ObjectEditorCatalog          _catalogInfo;
    private InspectorMetadata? _inspectorMetadata;
    private readonly FurnitureStainSelector _inspectorFurnitureStains = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    public SceneItemInspector(
        IObjectSceneView sceneView,
        ISceneInputService sceneInputService,
        ISceneLocationService sceneLocationService,
        IHistoryCoordinator historyCoordinator,
        IObjectLayoutManager layoutManager,
        PlacementFixExecutor placementFixExecutor,
        IObjectCollectionManager objectCollectionManager,
        IIntonerConfigurationService configurationService,
        WindowsCaptureTargetService captureTargetService,
        EditorInteraction interaction,
        EditorSceneState sceneState,
        SceneEditorCommands sceneCommands,
        EditorOverlayLayer editorOverlayLayer,
        SceneTransformEditor transformEditor,
        IFurnitureStainService furnitureStainService,
        SceneItemControls sceneItems,
        ObjectEditorCatalog catalogInfo)
    {
        _sceneView               = sceneView;
        _sceneInputService       = sceneInputService;
        _sceneLocationService    = sceneLocationService;
        _historyCoordinator      = historyCoordinator;
        _layoutManager           = layoutManager;
        _placementFixExecutor    = placementFixExecutor;
        _objectCollectionManager = objectCollectionManager;
        _configurationService    = configurationService;
        _captureTargetService    = captureTargetService;
        _interaction             = interaction;
        _sceneState              = sceneState;
        _sceneCommands           = sceneCommands;
        _editorOverlayLayer      = editorOverlayLayer;
        _transformEditor         = transformEditor;
        _furnitureStainService   = furnitureStainService;
        _sceneItems              = sceneItems;
        _catalogInfo             = catalogInfo;
    }

    internal sealed record InspectorMetadata(
        ObjectKind Kind,
        bool Active,
        DateTime CreatedAtUtc,
        SceneCreationContext Location,
        EditorBadge[] Details,
        EditorBadge[] LocationBadges);

    private void DrawInspectorMetadata(ObjectSnapshot snapshot, bool selectedActive)
    {
        EditorTextUtility.ClippedText name = EditorTextUtility.ClipTextToWidthResult(snapshot.Name, ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(name.Text);
        if (ImGui.IsWindowHovered())
        {
            EditorTextUtility.AttachTooltipIfClipped(ImGui.GetItemRectMin(), ImGui.GetItemRectSize(), snapshot.Name, name.IsClipped);
        }

        InspectorMetadata metadata = ResolveInspectorMetadata(snapshot, selectedActive);
        EditorBadgeRenderer.DrawInline(metadata.Details, wrap: true);
        bool canEdit = snapshot.CreatedIn.IsAssigned
                    && _sceneLocationService.PublicWorlds.Count > 0
                    && _sceneState.ResolveSelectedCurrentItems().All(static item => item is ObjectSnapshot { CreatedIn.IsAssigned: true });
        EditorBadge editBadge = EditorBadge.IconOnly(
            FontAwesomeIcon.Pen,
            "Edit location",
            canEdit
                ? "Change the selected objects' world or housing address."
                : "Select objects with a known world and territory to edit location.",
            canEdit ? ThemeColors.AccentPrimary : ThemeColors.TextDisabled);
        if (EditorBadgeRenderer.DrawInline(
                metadata.LocationBadges,
                wrap: true,
                action: new EditorBadgeRenderer.InlineAction("editObjectLocation", editBadge, canEdit)))
        {
            OpenInspectorLocationDialog();
        }
    }

    private InspectorMetadata ResolveInspectorMetadata(ObjectSnapshot snapshot, bool selectedActive)
    {
        if (_inspectorMetadata is { } cached
            && cached.Kind == snapshot.Kind
            && cached.Active == selectedActive
            && cached.CreatedAtUtc == snapshot.CreatedAtUtc
            && cached.Location == snapshot.CreatedIn)
        {
            return cached;
        }

        SceneCreationContext location = snapshot.CreatedIn;
        List<EditorBadge> locationBadges = [];
        if (location.HasLocation)
        {
            (string dataCenter, string worlds) = SceneItemPresentation.DescribeWorldSelection(location.Worlds, _sceneLocationService);
            locationBadges.Add(new EditorBadge(dataCenter, Icon: FontAwesomeIcon.NetworkWired, Tooltip: "Data center", TooltipDetail: dataCenter));
            if (location.Worlds is not { AllDataCenters: true })
            {
                locationBadges.Add(new EditorBadge(worlds, Icon: FontAwesomeIcon.Globe, Tooltip: "Worlds", TooltipDetail: worlds));
            }

            string territoryName = SceneItemPresentation.ResolveSceneLocationLabel(location.TerritoryName, location.TerritoryId, "Territory");
            locationBadges.Add(new EditorBadge(territoryName, Icon: FontAwesomeIcon.MapMarkerAlt, Tooltip: "Territory", TooltipDetail: territoryName));
            if (location.WardId != 0)
            {
                locationBadges.Add(EditorBadge.Label($"Ward {location.WardId.ToString(CultureInfo.InvariantCulture)}"));
            }

            if (location.Address.DivisionId == 2)
            {
                locationBadges.Add(EditorBadge.Label("Subdivision"));
            }

            if (location.HouseId != 0)
            {
                locationBadges.Add(EditorBadge.Label(location.HouseId == 100
                    ? "Apartment"
                    : $"Plot {location.HouseId.ToString(CultureInfo.InvariantCulture)}"));
            }

            if (location.RoomId != 0)
            {
                locationBadges.Add(EditorBadge.Label($"Room {location.RoomId.ToString(CultureInfo.InvariantCulture)}"));
            }
        }
        else
        {
            locationBadges.Add(EditorBadge.Label("Location unavailable", FontAwesomeIcon.MapMarkerAlt));
        }

        _inspectorMetadata = new InspectorMetadata(
            snapshot.Kind,
            selectedActive,
            snapshot.CreatedAtUtc,
            location,
            [
                EditorBadge.Label(snapshot.Kind.ToString(), SceneItemPresentation.ResolveObjectKindIcon(snapshot.Kind)),
                EditorBadge.Label(
                    selectedActive ? "Active" : "Inactive",
                    FontAwesomeIcon.Running,
                    selectedActive ? ThemeColors.AccentGreen : ThemeColors.TextDisabled),
                EditorBadge.Label(
                    $"Created {EditorTimestampFormatter.FormatCompact(snapshot.CreatedAtUtc)}",
                    FontAwesomeIcon.Clock,
                    tooltip: $"Created {EditorTimestampFormatter.FormatFull(snapshot.CreatedAtUtc)}"),
            ],
            [.. locationBadges]);
        return _inspectorMetadata;
    }

    private void OpenInspectorLocationDialog()
    {
        _historyCoordinator.CommitPendingInspectorEdits();
        IReadOnlyList<SceneItemSnapshot> selected = _sceneState.ResolveSelectedCurrentItems();
        ObjectSnapshot[] snapshots = selected.OfType<ObjectSnapshot>().ToArray();

        if (snapshots.Length == 0 || snapshots.Length != selected.Count
            || snapshots.Any(static item => !item.CreatedIn.IsAssigned))
        {
            return;
        }

        SceneLocationEditor edit = new(_sceneLocationService, snapshots.Select(static item => item.CreatedIn).ToArray());
        _interaction.OpenDialog(EditorDialog.Request.Form(
            "object-location",
            "Edit Location",
            "Apply",
            new EditorDialog.FormContent(
                edit.Draw,
                _ => edit.RowCount * MathF.Truncate(ImGui.GetFrameHeight() + EditorLayout.Scaled(6f)),
                () => edit.CanApply),
            () => edit.TryGetChange(out SceneLocationChange change)
               && _historyCoordinator.TryChangeObjectLocation(snapshots, change)) with
            {
                Detail = snapshots.Length == 1 ? snapshots[0].Name : $"{snapshots.Length} objects",
                FailureMessage = "The location could not be saved. Close and reopen this editor to try again.",
            });
    }

    internal void DrawInspectorPanel(EditorSceneState.EditorSceneData scene)
    {
        SceneItemSnapshot? selectedItem = _interaction.Selection.PrimaryItemId is { } itemId
            ? scene.FindCurrentItem(itemId)
            : null;
        ObjectSnapshot? selected = selectedItem as ObjectSnapshot;
        bool selectedActive = selected is not null && scene.ActiveObjectIds.Contains(selected.Id);

        _editorOverlayLayer.DrawChildPanel(
            "##objectInspectorPanel",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
            () =>
            {
                if (selectedItem is DisplaySnapshot selectedDisplay)
                {
                    DrawDisplayInspectorHero(selectedDisplay);
                    DrawDisplayInspectorDetails(selectedDisplay);
                    return;
                }

                if (selectedItem is SceneGroupSnapshot selectedGroup)
                {
                    DrawInspectorHero(selectedGroup, scene.ActiveItemLookup.ContainsKey(selectedGroup.Id));
                    DrawInspectorDetailsCard(selectedGroup, scene.ActiveItemLookup.ContainsKey(selectedGroup.Id));
                    return;
                }

                DrawInspectorHero(selected, selectedActive);
                DrawInspectorDetailsCard(selected, selectedActive);
            });
    }

    private void DrawInspectorHero(SceneItemSnapshot? selected, bool selectedActive)
    {
        EditorHeroCard.Status? placementStatus = null;
        PlacementFixProposal? placementFix = null;
        if (TryResolveHousingPlacementEvaluation(selected as ObjectSnapshot, out PlacementEvaluation resolvedPlacementEvaluation))
        {
            placementFix = ResolveHousingPlacementHeroFix(resolvedPlacementEvaluation);
            placementStatus = CreateHousingPlacementHeroStatus(resolvedPlacementEvaluation, placementFix);
        }

        bool statusActionClicked = EditorHeroCard.Draw(
            "inspector-hero",
            new EditorHeroCard.Content(
                FontAwesomeIcon.SlidersH,
                "Edit Selected",
                ResolveInspectorHeroSubtitle(selected),
                selected is SceneGroupSnapshot ? EditorColors.GroupAccent : ThemeColors.AccentPrimary,
                placementStatus),
            _sceneItems.GetSelectedObjectActions(selectedActive, selected));

        if (selected is not ObjectSnapshot selectedObject || !statusActionClicked || !placementFix.HasValue)
        {
            return;
        }

        _ = TryApplyPlacementFix(selectedObject, placementFix.Value);
    }

    private string ResolveInspectorHeroSubtitle(SceneItemSnapshot? selected)
    {
        if (selected is null)
        {
            return "No selection";
        }

        if (_interaction.Selection.Count == 1)
        {
            return $"{(selected is ObjectSnapshot snapshot ? snapshot.Kind.ToString() : "Group")} | {selected.Name}";
        }

        return $"{_interaction.Selection.Count} selected | primary {selected.Name}";
    }

    private void DrawInspectorDetailsCard(SceneItemSnapshot? selected, bool selectedActive)
    {
        using var accent = ImRaii.PushColor(ImGuiCol.CheckMark,
            selected is SceneGroupSnapshot ? EditorColors.GroupAccent : ThemeColors.AccentPrimary);
        EditorCard.DrawRemainingSpaceScrollCard("inspector-details", "##inspectorDetailsScroll", _editorOverlayLayer, innerHeight =>
        {
            if (selected is null)
                EditorEmptyState.Draw("Select an object to inspect and edit it.", innerHeight);
            else if (selected is ObjectSceneItemSnapshot snapshot)
                DrawInspectorSelectedContent(snapshot, selectedActive);
        });
    }

    private void DrawInspectorSelectedContent(ObjectSceneItemSnapshot snapshot, bool selectedActive)
    {
        if (snapshot is ObjectSnapshot metadataObject)
            DrawInspectorMetadata(metadataObject, selectedActive);
        using var tableColors = ImRaii.PushColor(ImGuiCol.TableRowBg, ThemeColors.ButtonDefault with { W = 0.16f })
            .Push(ImGuiCol.TableRowBgAlt, ThemeColors.ButtonDefault with { W = 0.42f })
            .Push(ImGuiCol.TableBorderLight, ThemeColors.Border with { W = 0.70f })
            .Push(ImGuiCol.TableBorderStrong, ThemeColors.Border with { W = 0.70f });
        DrawInspectorSectionHeading("inspectorPropertiesHeading", FontAwesomeIcon.SlidersH, "Properties");
        DrawCommonInspector(snapshot);

        DrawInspectorSectionHeading("inspectorTransformHeading", FontAwesomeIcon.ArrowsAlt, "Transform");
        DrawInspectorTransform(snapshot);

        if (snapshot is SceneGroupSnapshot)
        {
            DrawInspectorSectionHeading("inspectorPivotHeading", FontAwesomeIcon.Crosshairs, "Pivot");
            DrawInspectorTransform(snapshot, editGroupPivot: true);
        }

        if (snapshot is not ObjectSnapshot objectSnapshot)
            return;
        DrawInspectorSectionHeading("inspectorAppearanceHeading", SceneItemPresentation.ResolveObjectKindIcon(objectSnapshot.Kind), objectSnapshot.Kind switch
        {
            ObjectKind.Vfx => "VFX",
            _ => objectSnapshot.Kind.ToString(),
        });
        switch (objectSnapshot.Kind)
        {
            case ObjectKind.BgObject:
                DrawBgObjectInspector(objectSnapshot);
                break;
            case ObjectKind.Furniture:
                DrawFurnitureInspector(objectSnapshot);
                break;
            case ObjectKind.Vfx:
                DrawVfxInspector(objectSnapshot);
                break;
            case ObjectKind.Light:
                DrawLightInspector(objectSnapshot);
                break;
        }

        DrawInspectorDebugInfo(objectSnapshot);
    }

    private static void DrawInspectorSectionHeading(string id, FontAwesomeIcon icon, string title)
    {
        using (ImRaii.PushColor(ImGuiCol.Separator, ThemeColors.AccentPrimary with { W = 0.18f }))
        {
            ImGui.Separator();
        }

        EditorCard.DrawCardHeader(id, icon, title, string.Empty, ThemeColors.AccentPrimary, wrapTitle: true);
    }

    private string ResolveObjectLayoutLabel(ObjectSceneItemSnapshot snapshot)
    {
        if (!snapshot.LayoutId.HasValue)
        {
            return ObjectDestinationMenu.UnassignedLabel;
        }

        return _layoutManager.TryGetLayout(snapshot.LayoutId.Value, out ObjectLayoutSnapshot layout)
            ? TextUtility.TrimOrFallback(layout.Name, "Unnamed layout")
            : "Missing layout";
    }

    private static string BuildObjectCollectionOptionLabel(ObjectCollectionSnapshot collection)
        => TextUtility.TrimOrFallback(collection.Record.Name, collection.Record.CollectionId);

    private void DrawCommonInspector(ObjectSceneItemSnapshot snapshot)
    {
        using var commonInspectorTable = EditorPropertyTable.Begin("commonInspector");
        if (!commonInspectorTable)
        {
            return;
        }

        var name = snapshot.Name;
        EditorPropertyTable.NextRow("Name");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##commonName", ref name, 128) && (snapshot is not SceneGroupSnapshot || !string.IsNullOrWhiteSpace(name)))
        {
            _historyCoordinator.ApplyInspectorSnapshotEdit("CommonName", SceneHistoryKind.Organization,
                snapshot is SceneGroupSnapshot ? "Rename Group" : "Rename Object", snapshot, snapshot with { Name = name });
        }

        DrawInspectorLayoutRow(snapshot);
        DrawInspectorFolderRow(snapshot);
        if (snapshot is ObjectSnapshot objectSnapshot)
            DrawObjectCollectionSelectionRow(objectSnapshot);

        var visible = snapshot.Visible;
        if (EditorPropertyTable.Checkbox("commonVisible", "Visible", ref visible))
        {
            if (snapshot is ObjectSnapshot { GroupId: not null })
                _ = _sceneCommands.TrySetSceneItemsVisibleWithHistory([snapshot], visible);
            else
                _historyCoordinator.ApplyInspectorSnapshotEdit("CommonVisible", SceneHistoryKind.Visibility,
                    snapshot is SceneGroupSnapshot ? "Set Group Visibility" : "Set Object Visibility", snapshot, snapshot with { Visible = visible }, recordImmediately: true);
        }

        var locked = snapshot.Locked;
        if (EditorPropertyTable.Checkbox("commonLocked", "Locked", ref locked))
        {
            _historyCoordinator.ApplyInspectorSnapshotEdit(
                "CommonLocked",
                SceneHistoryKind.Organization,
                $"{(locked ? "Lock" : "Unlock")} {(snapshot is SceneGroupSnapshot ? "Group" : "Object")}",
                snapshot,
                snapshot with { Locked = locked },
                recordImmediately: true);
        }
    }

    private void DrawInspectorLayoutRow(ObjectSceneItemSnapshot snapshot)
    {
        EditorPropertyTable.NextRow("Layout", EditorIconButton.MeasureCompactEdge(), () =>
        {
            bool open = EditorIconButton.DrawCompact(
                "commonLayoutPicker", FontAwesomeIcon.LayerGroup, "Choose Layout and Folder", ThemeColors.AccentPrimary);
            using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdownForLastItem("commonLayoutChoices", open);
            if (popup)
            {
                DrawInspectorDestinationMenu(snapshot);
            }
        });
        string label = ResolveObjectLayoutLabel(snapshot);
        EditorTextUtility.ClippedText layoutLabel = EditorTextUtility.ClipTextToWidthResult(label, ImGui.GetContentRegionAvail().X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(layoutLabel.Text);
        EditorTextUtility.AttachTooltipIfClipped(ImGui.GetItemRectMin(), ImGui.GetItemRectSize(), label, layoutLabel.IsClipped);
    }

    private void DrawInspectorDestinationMenu(ObjectSceneItemSnapshot snapshot)
        => ObjectDestinationMenu.Draw(_layoutManager.GetEnabledLayouts(), _sceneView.GetPlacedFolders,
            snapshot.LayoutId, snapshot.FolderPath,
            (layoutId, folder) => _historyCoordinator.ApplyInspectorSnapshotEdit(
                "CommonDestination", SceneHistoryKind.Organization, snapshot is SceneGroupSnapshot ? "Move Group" : "Move Object", snapshot,
                ObjectOrganizationUtility.WithDestination(snapshot, layoutId, folder), recordImmediately: true));

    private void DrawInspectorFolderRow(ObjectSceneItemSnapshot snapshot)
    {
        EditorPropertyTable.NextRow("Folder", EditorIconButton.MeasureCompactEdge(), () =>
        {
            bool open = EditorIconButton.DrawCompact(
                "commonFolderPicker",
                FontAwesomeIcon.FolderOpen,
                "Choose Folder",
                ThemeColors.AccentPrimary);
            using var popup = EditorContextMenu.BeginDropdownForLastItem("commonFolderChoices", open);
            if (popup)
            {
                FolderSelectionMenu.DrawPaths(
                    _sceneView.GetPlacedFolders(snapshot.LayoutId),
                    snapshot.FolderPath,
                    folder => _historyCoordinator.ApplyInspectorSnapshotEdit(
                        "CommonFolderPath",
                        SceneHistoryKind.Organization,
                        $"{(string.IsNullOrEmpty(folder) ? "Clear" : "Set")} {(snapshot is SceneGroupSnapshot ? "Group" : "Object")} Folder",
                        snapshot,
                        ObjectOrganizationUtility.WithDestination(snapshot, snapshot.LayoutId, folder),
                        recordImmediately: true));
            }
        });

        var folderPath = snapshot.FolderPath;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##commonFolderPath", FolderSelectionMenu.RootLabel, ref folderPath, 256))
        {
            _historyCoordinator.ApplyInspectorSnapshotEdit(
                "CommonFolderPath",
                SceneHistoryKind.Organization,
                snapshot is SceneGroupSnapshot ? "Set Group Folder" : "Set Object Folder",
                snapshot,
                ObjectOrganizationUtility.WithDestination(snapshot, snapshot.LayoutId, folderPath));
        }
    }

    private void DrawInspectorTransform(ObjectSceneItemSnapshot snapshot, bool editGroupPivot = false)
    {
        using var transformTable = EditorPropertyTable.Begin(editGroupPivot ? "inspectorPivot" : "inspectorTransform");
        if (!transformTable)
        {
            return;
        }

        using var disabled = ImRaii.Disabled(snapshot is SceneGroupSnapshot && (snapshot.Locked
            || !editGroupPivot && _sceneState.GetEditorSceneData().Objects.Any(member => member.GroupId == snapshot.Id && member.Locked)));
        _transformEditor.DrawInspectorRows(snapshot, editGroupPivot);
    }

    private void DrawInspectorDebugInfo(ObjectSnapshot snapshot)
    {
        if (!_configurationService.Current.Ui.ShowDebugInformation)
        {
            return;
        }

        using var debug = ImRaii.Header("Debug Info##inspectorDebug", ImGuiTreeNodeFlags.None);
        if (!debug)
        {
            return;
        }

        ImGui.TextUnformatted("Object ID");
        string id = snapshot.Id.ToString();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##inspectorObjectId", ref id, 64, ImGuiInputTextFlags.ReadOnly);

        ImGui.TextUnformatted("Raw Snapshot");
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        ImGui.InputTextMultiline(
            "##inspectorRawSnapshot",
            ref json,
            System.Text.Encoding.UTF8.GetByteCount(json) + 1,
            new Vector2(-1f, EditorLayout.Scaled(200f)),
            ImGuiInputTextFlags.ReadOnly);
    }

    private void DrawObjectCollectionSelectionRow(ObjectSnapshot snapshot)
    {
        var collections = _objectCollectionManager.GetCollections();
        var currentCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId);
        ObjectCollectionSnapshot currentCollection = default!;
        var currentCollectionExists = currentCollectionId.Length > 0
            && SceneItemPresentation.TryResolveObjectCollectionById(collections, currentCollectionId, out currentCollection);
        string previewLabel;
        if (currentCollectionId.Length == 0)
        {
            previewLabel = "<none>";
        }
        else if (currentCollectionExists)
        {
            previewLabel = BuildObjectCollectionOptionLabel(currentCollection);
        }
        else
        {
            previewLabel = $"<missing: {currentCollectionId}>";
        }
        EditorPropertyTable.NextRow("Collection");
        using var combo = ImRaii.Combo("##commonCollection", previewLabel);
        if (!combo)
        {
            return;
        }

        if (currentCollectionId.Length > 0 && !currentCollectionExists)
        {
            using (ImRaii.Disabled())
            {
                ImGui.Selectable($"{previewLabel}##missingObjectCollection", true);
            }

            ImGui.SetItemDefaultFocus();
            ImGui.Separator();
        }

        var noCollectionSelected = currentCollectionId.Length == 0;
        if (ImGui.Selectable("<none>##objectCollectionNone", noCollectionSelected))
        {
            ApplyInspectorObjectCollectionEdit(snapshot, string.Empty);
        }

        if (noCollectionSelected)
        {
            ImGui.SetItemDefaultFocus();
        }

        foreach (var collection in collections)
        {
            var collectionId = collection.Record.CollectionId;
            var selected = string.Equals(collectionId, currentCollectionId, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable($"{BuildObjectCollectionOptionLabel(collection)}##objectCollectionOption:{collectionId}", selected))
            {
                ApplyInspectorObjectCollectionEdit(snapshot, collectionId);
            }

            if (ImGui.IsItemHovered())
            {
                IntonerTooltip.DrawText(collectionId);
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
    }

    private void ApplyInspectorObjectCollectionEdit(ObjectSnapshot snapshot, string collectionId)
    {
        var normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(collectionId);
        if (string.Equals(snapshot.CollectionId, normalizedCollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _historyCoordinator.ApplyInspectorSnapshotEdit(
            "CommonCollection",
            SceneHistoryKind.Appearance,
            normalizedCollectionId.Length == 0 ? "Clear Object Collection" : "Set Object Collection",
            snapshot,
            snapshot with { CollectionId = normalizedCollectionId },
            recordImmediately: true);
    }

    private void DrawBgObjectInspector(ObjectSnapshot snapshot)
    {
        var bgObjectModel = (BgObjectModel)snapshot.Model;
        using var bgObjectInspectorTable = EditorPropertyTable.Begin("bgObjectInspector");
        if (!bgObjectInspectorTable)
        {
            return;
        }

        var modelPath = bgObjectModel.ModelPath;
        EditorPropertyTable.NextRow("Model Path");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##bgObjectModelPath", ref modelPath, 512))
        {
            bgObjectModel = _catalogInfo.ApplyBgObjectDefaults(bgObjectModel with { ModelPath = modelPath, ColliderDefinition = null });
            ApplyInspectorModelEdit(
                snapshot,
                "BgObjectModelPath",
                "Change BgObject Model Path",
                bgObjectModel);
        }

        ObjectModelEditor.DrawBgObjectRows(
            "inspector",
            ref bgObjectModel,
            _catalogInfo.ResolveBgObjectCatalogInfo(bgObjectModel.ModelPath),
            onChanged: (editId, title, updatedModel, recordImmediately) =>
                ApplyInspectorModelEdit(snapshot, editId, title, updatedModel, recordImmediately));
    }

    private void DrawFurnitureInspector(ObjectSnapshot snapshot)
    {
        var furnitureModel = (FurnitureModel)snapshot.Model;
        using var furnitureInspectorTable = EditorPropertyTable.Begin("furnitureInspector");
        if (!furnitureInspectorTable)
        {
            return;
        }

        var sharedGroupPath = furnitureModel.SharedGroupPath;
        EditorPropertyTable.NextRow("Shared Group Path");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##furnitureSharedGroupPath", ref sharedGroupPath, 512))
        {
            furnitureModel = _catalogInfo.ResolveFurnitureCatalogVariant(furnitureModel with { SharedGroupPath = sharedGroupPath });
            ApplyInspectorModelEdit(
                snapshot,
                "FurnitureSharedGroupPath",
                "Change Furniture Shared Group Path",
                furnitureModel);
        }

        ObjectModelEditor.DrawFurnitureRows(
            "inspector",
            ref furnitureModel,
            _furnitureStainService.GetStains(),
            _inspectorFurnitureStains,
            onChanged: (editId, title, updatedModel, recordImmediately) =>
                ApplyInspectorModelEdit(snapshot, editId, title, updatedModel, recordImmediately));
    }

    private void DrawLightInspector(ObjectSnapshot snapshot)
    {
        var lightModel = (LightModel)snapshot.Model;
        using var lightModelTable = EditorPropertyTable.Begin("lightModel_inspector");
        if (!lightModelTable)
        {
            return;
        }

        ObjectModelEditor.DrawLightRows(
            "inspector",
            ref lightModel,
            onChanged: (editId, title, updatedModel, recordImmediately) =>
                ApplyInspectorModelEdit(snapshot, editId, title, updatedModel, recordImmediately));
    }

    private void DrawVfxInspector(ObjectSnapshot snapshot)
    {
        var vfxModel = (VfxModel)snapshot.Model;
        using var vfxInspectorTable = EditorPropertyTable.Begin("vfxInspector");
        if (!vfxInspectorTable)
        {
            return;
        }

        ObjectCatalogVfxInfo? vfxInfo = _catalogInfo.ResolveVfxCatalogInfo(vfxModel.VfxPath);
        ObjectModelEditor.DrawVfxRows(
            "inspector",
            ref vfxModel,
            vfxInfo?.CanUseReplayLoop != false,
            onChanged: (editId, title, updatedModel, recordImmediately) =>
                ApplyInspectorModelEdit(snapshot, editId, title, updatedModel, recordImmediately));
    }

    private void ApplyInspectorModelEdit(ObjectSnapshot snapshot, string editId, string title, ObjectData model, bool recordImmediately = false)
        => _historyCoordinator.ApplyInspectorSnapshotEdit(editId, SceneHistoryKind.Appearance, title,
            snapshot, snapshot with { Model = model }, recordImmediately);
}
