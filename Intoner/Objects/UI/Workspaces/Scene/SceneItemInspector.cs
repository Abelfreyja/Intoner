using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
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
    private string _inspectorFurnitureStainFilter = string.Empty;
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

    internal sealed class InspectorLocationEdit(SceneWorldInfo world)
    {
        public uint DataCenterId = world.DataCenterId;
        public ushort WorldId = world.Id;
    }

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
        bool canEdit = snapshot.CreatedIn.Scope.IsValid
                    && _sceneLocationService.TryResolveWorld(snapshot.CreatedIn.WorldId, out _)
                    && _sceneLocationService.PublicWorlds.Count > 0;
        EditorBadge editBadge = EditorBadge.IconOnly(
            FontAwesomeIcon.Pen,
            "Edit location",
            canEdit
                ? "Change data center and world."
                : "A known world and territory are required to edit location.",
            canEdit ? ThemeColors.AccentPrimary : ThemeColors.TextDisabled);
        if (EditorBadgeRenderer.DrawInline(
                metadata.LocationBadges,
                wrap: true,
                action: new EditorBadgeRenderer.InlineAction("editObjectLocation", editBadge, canEdit)))
        {
            OpenInspectorLocationDialog(snapshot);
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
        if (_sceneLocationService.TryResolveWorld(location.WorldId, out SceneWorldInfo? world))
        {
            locationBadges.Add(new EditorBadge(world.DataCenterName, Icon: FontAwesomeIcon.NetworkWired, Tooltip: "Data center", TooltipDetail: world.DataCenterName));
            locationBadges.Add(new EditorBadge(world.Name, Icon: FontAwesomeIcon.Globe, Tooltip: "World", TooltipDetail: world.Name));
        }
        else if (location.IsValid)
        {
            string worldName = SceneItemPresentation.ResolveSceneLocationLabel(location.WorldName, location.WorldId, "World");
            locationBadges.Add(new EditorBadge(worldName, Icon: FontAwesomeIcon.Globe, Tooltip: "World", TooltipDetail: worldName));
        }

        if (location.IsValid)
        {
            string territoryName = SceneItemPresentation.ResolveSceneLocationLabel(location.TerritoryName, location.TerritoryId, "Territory");
            locationBadges.Add(new EditorBadge(territoryName, Icon: FontAwesomeIcon.MapMarkerAlt, Tooltip: "Territory", TooltipDetail: territoryName));
            if (location.WardId != 0)
            {
                locationBadges.Add(EditorBadge.Label($"Ward {location.WardId.ToString(CultureInfo.InvariantCulture)}"));
            }

            if (location.DivisionId == 2)
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

    private void OpenInspectorLocationDialog(ObjectSnapshot snapshot)
    {
        if (!_sceneLocationService.TryResolveWorld(snapshot.CreatedIn.WorldId, out SceneWorldInfo? world))
        {
            return;
        }

        InspectorLocationEdit edit = new(world);
        _interaction.OpenDialog(EditorDialog.Request.Form(
            "object-world",
            "Edit Location",
            "Apply",
            new EditorDialog.FormContent(
                () => DrawInspectorWorldChoices(edit),
                static _ => 2f * MathF.Truncate(ImGui.GetFrameHeight() + EditorLayout.Scaled(6f)),
                () => edit.WorldId != 0 && edit.WorldId != snapshot.CreatedIn.WorldId),
            () => _sceneLocationService.TryResolveWorld(edit.WorldId, out SceneWorldInfo? selectedWorld)
               && _historyCoordinator.TryChangeObjectWorld(snapshot, selectedWorld)) with
            {
                Detail = snapshot.Name,
                FailureMessage = "The location could not be saved. Close and reopen this editor to try again.",
            });
    }

    private bool DrawInspectorWorldChoices(InspectorLocationEdit edit)
    {
        using var table = EditorPropertyTable.Begin("objectWorldFields");
        if (!table)
        {
            return false;
        }

        bool changed = false;
        IReadOnlyList<SceneWorldInfo> worlds = _sceneLocationService.PublicWorlds;
        string dataCenterName = worlds.FirstOrDefault(world => world.DataCenterId == edit.DataCenterId)?.DataCenterName ?? "Select data center";
        EditorPropertyTable.NextRow("Data Center");
        using (var dataCenters = ImRaii.Combo("##objectDataCenter", dataCenterName))
        {
            if (dataCenters)
            {
                uint previousDataCenter = 0;
                foreach (SceneWorldInfo world in worlds)
                {
                    if (world.DataCenterId == previousDataCenter)
                    {
                        continue;
                    }

                    previousDataCenter = world.DataCenterId;
                    bool selected = edit.DataCenterId == world.DataCenterId;
                    if (ImGui.Selectable(world.DataCenterName, selected) && !selected)
                    {
                        edit.DataCenterId = world.DataCenterId;
                        edit.WorldId = 0;
                        changed = true;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }
            }
        }

        string worldName = _sceneLocationService.TryResolveWorld(edit.WorldId, out SceneWorldInfo? selectedWorld)
            ? selectedWorld.Name
            : "Select world";
        EditorPropertyTable.NextRow("World");
        using var worldChoice = ImRaii.Combo("##objectWorld", worldName);
        if (!worldChoice)
        {
            return changed;
        }

        foreach (SceneWorldInfo world in worlds)
        {
            if (world.DataCenterId != edit.DataCenterId)
            {
                continue;
            }

            bool selected = edit.WorldId == world.Id;
            if (ImGui.Selectable(world.Name, selected) && !selected)
            {
                edit.WorldId = world.Id;
                changed = true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        return changed;
    }

    internal void DrawInspectorPanel(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds)
    {
        var selectedObjectId = _interaction.Selection.PrimaryItemId;
        DisplaySnapshot? selectedDisplay = selectedObjectId.HasValue
            ? displays.FirstOrDefault(entry => entry.Id == selectedObjectId.Value)
            : null;
        var selected = selectedObjectId.HasValue
            ? objects.FirstOrDefault(entry => entry.Id == selectedObjectId.Value)
            : null;
        var selectedActive = selected is not null && activeObjectIds.Contains(selected.Id);

        _editorOverlayLayer.DrawChildPanel(
            "##objectInspectorPanel",
            Vector2.Zero,
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse,
            () =>
            {
                if (selectedDisplay is not null)
                {
                    DrawDisplayInspectorHero(selectedDisplay);
                    DrawDisplayInspectorDetails(selectedDisplay);
                    return;
                }

                DrawInspectorHero(selected, selectedActive);
                DrawInspectorDetailsCard(selected, selectedActive);
            });
    }

    private void DrawInspectorHero(ObjectSnapshot? selected, bool selectedActive)
    {
        EditorHeroCard.Status? placementStatus = null;
        PlacementFixProposal? placementFix = null;
        if (TryResolveHousingPlacementEvaluation(selected, out PlacementEvaluation resolvedPlacementEvaluation))
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
                ThemeColors.AccentPrimary,
                placementStatus),
            new EditorHeroCard.Actions(
                () => _sceneItems.DrawSelectedObjectActions(selectedActive),
                SceneItemControls.ResolveSelectedObjectActionsSize()));

        if (selected is null || !statusActionClicked || !placementFix.HasValue)
        {
            return;
        }

        _ = TryApplyPlacementFix(selected, placementFix.Value);
    }

    private string ResolveInspectorHeroSubtitle(ObjectSnapshot? selected)
    {
        if (selected is null)
        {
            return "No selection";
        }

        if (_interaction.Selection.Count == 1)
        {
            return $"{selected.Kind} | {selected.Name}";
        }

        return $"{_interaction.Selection.Count} selected | primary {selected.Name}";
    }

    private void DrawInspectorDetailsCard(ObjectSnapshot? selected, bool selectedActive)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - ImGui.GetStyle().ItemSpacing.Y);
        var background = ThemeColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        EditorCard.DrawPanelCard(
            "inspector-details",
            background,
            ThemeColors.AccentPrimary with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                if (selected is null)
                {
                    EditorEmptyState.Draw("Select an object to inspect and edit it.", innerHeight);
                    return;
                }

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = EditorScrollList.Begin(
                    "##inspectorDetailsScroll",
                    new Vector2(0f, innerHeight),
                    _editorOverlayLayer.CreateScrollPanelOptions(background, rounding, ThemeColors.AccentPrimary));
                if (child)
                {
                    DrawInspectorSelectedContent(selected, selectedActive);
                }
            });
    }

    private void DrawInspectorSelectedContent(ObjectSnapshot snapshot, bool selectedActive)
    {
        DrawInspectorMetadata(snapshot, selectedActive);
        using var tableColors = ImRaii.PushColor(ImGuiCol.TableRowBg, ThemeColors.ButtonDefault with { W = 0.16f })
            .Push(ImGuiCol.TableRowBgAlt, ThemeColors.ButtonDefault with { W = 0.42f })
            .Push(ImGuiCol.TableBorderLight, ThemeColors.Border with { W = 0.70f })
            .Push(ImGuiCol.TableBorderStrong, ThemeColors.Border with { W = 0.70f });
        DrawInspectorSectionHeading("inspectorPropertiesHeading", FontAwesomeIcon.SlidersH, "Properties");
        DrawCommonInspector(snapshot);

        DrawInspectorSectionHeading("inspectorTransformHeading", FontAwesomeIcon.ArrowsAlt, "Transform");
        DrawInspectorTransform(snapshot);

        DrawInspectorSectionHeading("inspectorAppearanceHeading", SceneItemPresentation.ResolveObjectKindIcon(snapshot.Kind), snapshot.Kind switch
        {
            ObjectKind.Vfx => "VFX",
            _ => snapshot.Kind.ToString(),
        });
        switch (snapshot.Kind)
        {
            case ObjectKind.BgObject:
                DrawBgObjectInspector(snapshot);
                break;
            case ObjectKind.Furniture:
                DrawFurnitureInspector(snapshot);
                break;
            case ObjectKind.Vfx:
                DrawVfxInspector(snapshot);
                break;
            case ObjectKind.Light:
                DrawLightInspector(snapshot);
                break;
        }

        DrawInspectorDebugInfo(snapshot);
    }

    private static void DrawInspectorSectionHeading(string id, FontAwesomeIcon icon, string title)
    {
        using (ImRaii.PushColor(ImGuiCol.Separator, ThemeColors.AccentPrimary with { W = 0.18f }))
        {
            ImGui.Separator();
        }

        EditorCard.DrawCardHeader(id, icon, title, string.Empty, ThemeColors.AccentPrimary, wrapTitle: true);
    }

    private string ResolveObjectLayoutLabel(ObjectSnapshot snapshot)
    {
        if (!snapshot.LayoutId.HasValue)
        {
            return "Standalone";
        }

        return _layoutManager.TryGetLayout(snapshot.LayoutId.Value, out ObjectLayoutSnapshot layout)
            ? TextUtility.TrimOrFallback(layout.Name, "Unnamed layout")
            : "Missing layout";
    }

    private static string BuildObjectCollectionOptionLabel(ObjectCollectionSnapshot collection)
        => TextUtility.TrimOrFallback(collection.Record.Name, collection.Record.CollectionId);

    private static void DrawStateRow(string label, string value)
    {
        EditorPropertyTable.NextRow(label);
        ImGui.AlignTextToFramePadding();
        EditorTextUtility.ClippedText text = EditorTextUtility.ClipTextToWidthResult(value, ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(text.Text);
        if (text.IsClipped && ImGui.IsItemHovered())
        {
            IntonerTooltip.DrawText(value);
        }
    }

    private void DrawCommonInspector(ObjectSnapshot snapshot)
    {
        using var commonInspectorTable = EditorPropertyTable.Begin("commonInspector");
        if (!commonInspectorTable)
        {
            return;
        }

        var name = snapshot.Name;
        EditorPropertyTable.NextRow("Name");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##commonName", ref name, 128))
        {
            _historyCoordinator.ApplyInspectorSnapshotEdit("CommonName", SceneHistoryKind.Organization, "Rename Object", snapshot, snapshot with { Name = name });
        }

        DrawStateRow("Layout", ResolveObjectLayoutLabel(snapshot));
        DrawInspectorFolderRow(snapshot);
        DrawObjectCollectionSelectionRow(snapshot);

        var visible = snapshot.Visible;
        if (EditorPropertyTable.Checkbox("commonVisible", "Visible", ref visible))
        {
            _historyCoordinator.ApplyInspectorSnapshotEdit("CommonVisible", SceneHistoryKind.Visibility, "Set Object Visibility", snapshot, snapshot with { Visible = visible }, recordImmediately: true);
        }

        var locked = snapshot.Locked;
        if (EditorPropertyTable.Checkbox("commonLocked", "Locked", ref locked))
        {
            _historyCoordinator.ApplyInspectorSnapshotEdit(
                "CommonLocked",
                SceneHistoryKind.Organization,
                locked ? "Lock Object" : "Unlock Object",
                snapshot,
                snapshot with { Locked = locked },
                recordImmediately: true);
        }
    }

    private void DrawInspectorFolderRow(ObjectSnapshot snapshot)
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
                    _sceneView.GetPlacedFolders(),
                    snapshot.FolderPath,
                    folder => _historyCoordinator.ApplyInspectorSnapshotEdit(
                        "CommonFolderPath",
                        SceneHistoryKind.Organization,
                        string.IsNullOrEmpty(folder) ? "Ungroup Object" : "Set Object Folder",
                        snapshot,
                        snapshot with { FolderPath = folder },
                        recordImmediately: true));
            }
        });

        var folderPath = snapshot.FolderPath;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextWithHint("##commonFolderPath", "Ungrouped", ref folderPath, 256))
        {
            _historyCoordinator.ApplyInspectorSnapshotEdit(
                "CommonFolderPath",
                SceneHistoryKind.Organization,
                "Set Object Folder",
                snapshot,
                snapshot with { FolderPath = folderPath });
        }
    }

    private void DrawInspectorTransform(ObjectSnapshot snapshot)
    {
        using var transformTable = EditorPropertyTable.Begin("inspectorTransform");
        if (!transformTable)
        {
            return;
        }

        var position = snapshot.Transform.Position;
        if (_transformEditor.DrawPositionClipboardRow("commonPosition", ref position))
        {
            _transformEditor.ApplyInspectorPositionEdit("CommonPosition", "Move Object", SceneHistoryKind.Move, snapshot, position);
        }

        var rotation = snapshot.Transform.RotationDegrees;
        if (_transformEditor.DrawRotationClipboardRow("commonRotation", ref rotation))
        {
            _transformEditor.ApplyInspectorRotationEdit("CommonRotation", "Rotate Object", SceneHistoryKind.Transform, snapshot, rotation);
        }

        if (CanEditInspectorScale(snapshot))
        {
            var scale = snapshot.Transform.Scale;
            if (_transformEditor.DrawScaleClipboardRow("commonScale", ref scale))
            {
                _transformEditor.ApplyInspectorScaleEdit("CommonScale", "Scale Object", SceneHistoryKind.Transform, snapshot, scale);
            }
        }
    }

    private static bool CanEditInspectorScale(ObjectSnapshot snapshot)
        => snapshot.Model is BgObjectModel or FurnitureModel or VfxModel;

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
            bgObjectModel = bgObjectModel with { ModelPath = modelPath };
            _historyCoordinator.ApplyInspectorSnapshotEdit(
                "BgObjectModelPath",
                SceneHistoryKind.Appearance,
                "Change BgObject Model Path",
                snapshot,
                snapshot with
                {
                    Model = bgObjectModel,
                });
        }

        ObjectModelEditor.DrawBgObjectRows(
            "inspector",
            ref bgObjectModel,
            onChanged: (editId, title, updatedModel, recordImmediately) =>
                _historyCoordinator.ApplyInspectorSnapshotEdit(
                    editId,
                    SceneHistoryKind.Appearance,
                    title,
                    snapshot,
                    snapshot with { Model = updatedModel },
                    recordImmediately));
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
            _historyCoordinator.ApplyInspectorSnapshotEdit(
                "FurnitureSharedGroupPath",
                SceneHistoryKind.Appearance,
                "Change Furniture Shared Group Path",
                snapshot,
                snapshot with
                {
                    Model = furnitureModel,
                });
        }

        ObjectModelEditor.DrawFurnitureRows(
            "inspector",
            ref furnitureModel,
            _furnitureStainService.GetStains(),
            ref _inspectorFurnitureStainFilter,
            onChanged: (editId, title, updatedModel, recordImmediately) =>
                _historyCoordinator.ApplyInspectorSnapshotEdit(
                    editId,
                    SceneHistoryKind.Appearance,
                    title,
                    snapshot,
                    snapshot with { Model = updatedModel },
                    recordImmediately));
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
                _historyCoordinator.ApplyInspectorSnapshotEdit(
                    editId,
                    SceneHistoryKind.Appearance,
                    title,
                    snapshot,
                    snapshot with { Model = updatedModel },
                    recordImmediately));
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
                _historyCoordinator.ApplyInspectorSnapshotEdit(
                    editId,
                    SceneHistoryKind.Appearance,
                    title,
                    snapshot,
                    snapshot with { Model = updatedModel },
                    recordImmediately));
    }
}
