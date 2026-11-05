using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class CreateWorkspace
{
    private readonly IObjectSceneView _sceneView;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly LayoutActions _layoutActions;
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly EditorOverlayLayer _editorOverlayLayer;
    private readonly SceneTransformEditor _transformEditor;
    private readonly FurnitureModelEditor _furnitureEditor;
    private readonly ObjectEditorCatalog _catalogInfo;
    private readonly EditorAssetSoundQuery _assetSound;
    private readonly CreateBrowserSelection _browserSelection;
    private readonly ObjectCreationDraft _draft;
    private readonly ObjectPreviewRenderer _preview;
    private readonly ObjectCreateActions _createActions;
    private readonly ObjectCatalogBrowser _catalogBrowser;
    private readonly ObjectLibraryBrowser _libraryBrowser;
    private (long Revision, Guid? LayoutId, IReadOnlyList<string> Folders)? _placementFolders;
    private const string PreviewControlsText = "drag to rotate | scroll to zoom | double click to reset";

    public CreateWorkspace(
        IObjectSceneView sceneView,
        IObjectHousingModePolicy housingModePolicy,
        EditorOverlayLayer editorOverlayLayer,
        SceneTransformEditor transformEditor,
        FurnitureModelEditor furnitureEditor,
        ObjectEditorCatalog catalogInfo,
        EditorAssetSoundQuery assetSound,
        CreateBrowserSelection browserSelection,
        ObjectCreationDraft draft,
        ObjectPreviewRenderer preview,
        ObjectCreateActions createActions,
        ObjectCatalogBrowser catalogBrowser,
        ObjectLibraryBrowser libraryBrowser,
        IObjectLayoutManager layoutManager,
        LayoutActions layoutActions)
    {
        _sceneView             = sceneView;
        _layoutManager         = layoutManager;
        _layoutActions         = layoutActions;
        _housingModePolicy     = housingModePolicy;
        _editorOverlayLayer    = editorOverlayLayer;
        _transformEditor       = transformEditor;
        _furnitureEditor       = furnitureEditor;
        _catalogInfo           = catalogInfo;
        _assetSound            = assetSound;
        _browserSelection      = browserSelection;
        _draft                 = draft;
        _preview               = preview;
        _createActions         = createActions;
        _catalogBrowser        = catalogBrowser;
        _libraryBrowser        = libraryBrowser;
    }

    private void DrawCreateHero(FontAwesomeIcon icon, string title, string description)
    {
        var accent = ThemeColors.AccentPrimary;
        EditorCard.DrawPanelCard(
            "create-hero",
            ThemeColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.28f },
            EditorLayout.Scaled(8f),
            EditorLayout.ResolveObjectListCardPadding(),
            () =>
            {
                Vector2 start = ImGui.GetCursorPos();
                float availableWidth = ImGui.GetContentRegionAvail().X;
                float actionEdge = ResolveCreateBrowserActionButtonEdge();
                Vector2 spacing = ImGui.GetStyle().ItemSpacing;
                float iconWidth = EditorIconText.MeasureWidth(icon, string.Empty, string.Empty);
                float textWidth = MathF.Max(0f, availableWidth - iconWidth - actionEdge - spacing.X);
                float lineHeight = ImGui.GetTextLineHeight();
                Vector2 textStart = ImGui.GetCursorScreenPos() + new Vector2(iconWidth, 0f);
                EditorTextUtility.ClippedText clippedTitle = EditorTextUtility.ClipTextToWidthResult(title, textWidth);
                EditorTextUtility.ClippedText clippedDescription = EditorTextUtility.ClipTextToWidthResult(description, textWidth);
                EditorIconText.Draw(icon, clippedTitle.Text, clippedDescription.Text, accent);
                if (ImGui.IsWindowHovered())
                {
                    Vector2 textSize = new(textWidth, lineHeight);
                    EditorTextUtility.AttachTooltipIfClipped(textStart, textSize, title, clippedTitle.IsClipped);
                    EditorTextUtility.AttachTooltipIfClipped(
                        textStart + new Vector2(0f, lineHeight + spacing.Y), textSize, description, clippedDescription.IsClipped);
                }

                ImGui.SetCursorPos(new Vector2(
                    start.X + MathF.Max(0f, availableWidth - actionEdge),
                    start.Y));
                DrawCreateBrowserActionButton();
                ImGui.SetCursorPos(start);
                ImGui.Dummy(new Vector2(availableWidth, MathF.Max(actionEdge, lineHeight * 2f + spacing.Y)));
            });
    }

    private void DrawBottomAlignedCreateActionHost(string id, float hostHeight, Action content)
    {
        var style = ImGui.GetStyle();
        using var zeroVerticalPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(style.WindowPadding.X, 0f));
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var child = ImRaii.Child(
            $"##{id}",
            new Vector2(0f, EditorLayout.Positive(hostHeight)),
            false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground);
        if (!child)
        {
            return;
        }

        content();
        var bottomInset = ResolveCreateActionHostBottomInset();
        if (bottomInset > 0f)
        {
            ImGui.Dummy(new Vector2(0f, bottomInset));
        }

        _editorOverlayLayer.CaptureCurrentWindow();
    }

    private void DrawScrollableCreateSettingsCard(string id, float height, Action content)
        => DrawScrollableCreateCard(id, height, null, content, ThemeColors.AccentPrimary);

    private void DrawScrollableCreateCard(string id, float height, Action? drawHeader, Action content, Vector4 accent)
    {
        var background = ThemeColors.ButtonDefault with { W = 0.24f };
        var rounding = EditorLayout.Scaled(8f);
        var padding = EditorLayout.ResolveObjectListCardPadding();

        EditorCard.DrawPanelCard(
            id,
            background,
            accent with { W = 0.18f },
            rounding,
            padding,
            height,
            () =>
            {
                var contentStartY = ImGui.GetCursorPosY();
                drawHeader?.Invoke();
                var innerHeight = EditorLayout.ResolveScrollableCardInnerHeight(height, padding, contentStartY);

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                _preview.ResetHover();
                using var child = EditorScrollList.Begin(
                    $"##{id}_scroll",
                    new Vector2(0f, innerHeight),
                    _editorOverlayLayer.CreateScrollPanelOptions(background, rounding, accent),
                    false,
                    ImGuiWindowFlags.NoScrollWithMouse);
                if (child)
                {
                    content();

                    var childHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
                    _ = EditorScrollList.ScrollCurrentWindowFromMouseWheel(
                        childHovered && !_preview.IsHovered);
                }
            });
    }

    private static float MeasureCreateActionCardHeight()
    {
        var paddingY = EditorLayout.Scaled(8f);
        var wrapperSpacingY = ImGui.GetStyle().ItemSpacing.Y * 2f;
        return (paddingY * 2f) + EditorButton.PrimaryHeight + wrapperSpacingY;
    }

    private static float MeasureCreateActionHostHeight()
    {
        return MeasureCreateActionCardHeight() + ResolveCreateActionHostBottomInset();
    }

    private static float ResolveCreateActionHostBottomInset()
    {
        return ImGui.GetStyle().ItemSpacing.Y;
    }

    private void DrawCreateActionCard(
        string id,
        string label,
        string description,
        Guid? selectedLayoutId,
        string selectedFolderPath,
        bool enabled,
        Action onClick)
    {
        EditorCard.DrawPanelCard(
            $"create-action-{id}",
            ThemeColors.AccentPrimary with { W = 0.14f },
            ThemeColors.AccentPrimary with { W = 0.32f },
            EditorLayout.Scaled(8f),
            EditorLayout.ResolveObjectListCardPadding(),
            () =>
            {
                var buttonGap = EditorLayout.Scaled(8f);
                var availableWidth = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
                var buttonHeight = EditorButton.PrimaryHeight;
                var folderButtonWidth = buttonHeight;
                var buttonWidth = EditorLayout.Positive(availableWidth - folderButtonWidth - buttonGap);
                var folderPopupId = $"##create-action-folder-popup:{id}";
                string layoutName = selectedLayoutId.HasValue && _layoutManager.TryGetLayout(selectedLayoutId.Value, out ObjectLayoutSnapshot layout)
                    ? layout.Name : ObjectDestinationMenu.UnassignedLabel;
                string destination = selectedFolderPath.Length > 0 ? $"{layoutName} / {selectedFolderPath}" : layoutName;
                string placementLabel = $"{label} ({destination})";
                bool folderSelected = selectedLayoutId.HasValue || selectedFolderPath.Length > 0;

                if (EditorButton.DrawPrimary(
                        $"createAction:{id}:place",
                        FontAwesomeIcon.Play,
                        placementLabel,
                        ThemeColors.AccentOrange,
                        new Vector2(buttonWidth, buttonHeight),
                        enabled))
                {
                    onClick();
                }

                if (ImGui.IsItemHovered())
                {
                    IntonerTooltip.Attach(
                        FontAwesomeIcon.Play,
                        placementLabel,
                        description,
                        new IntonerTooltipOptions { Accent = ThemeColors.AccentOrange });
                }

                ImGui.SameLine(0f, buttonGap);
                FontAwesomeIcon folderIcon = selectedFolderPath.Length > 0 ? FontAwesomeIcon.FolderOpen : FontAwesomeIcon.LayerGroup;
                bool folderButtonClicked = EditorIconButton.DrawToggle(
                        $"createAction:{id}:folder",
                        folderIcon,
                        new Vector2(folderButtonWidth, buttonHeight),
                        folderSelected,
                        ThemeColors.AccentPrimary,
                        ThemeColors.AccentOrange);
                bool folderButtonHovered = ImGui.IsItemHovered();

                using (EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdownForLastItem(folderPopupId, folderButtonClicked))
                {
                    if (popup)
                    {
                        ObjectDestinationMenu.Draw(_layoutManager.GetEnabledLayouts(), _sceneView.GetPlacedFolders,
                            selectedLayoutId, selectedFolderPath, SelectPlacementDestination);
                    }
                }

                if (folderButtonHovered)
                {
                    IntonerTooltip.Attach(
                        folderIcon,
                        "Placement Destination",
                        destination,
                        new IntonerTooltipOptions
                        {
                            Accent = folderSelected ? ThemeColors.AccentPrimary : ThemeColors.AccentOrange,
                        });
                }
            });
    }

    private void SelectPlacementDestination(Guid? layoutId, string folderPath)
    {
        if (_layoutActions.SetCreation(layoutId))
        {
            _draft.SetCreatePlacementFolderPath(layoutId, folderPath);
        }
    }

    private float ResolveCreateBrowserActionButtonEdge()
        => EditorIconButton.MeasureMaxEdge(
            FontAwesomeIcon.LayerGroup,
            FontAwesomeIcon.FolderPlus,
            SceneItemPresentation.ResolveObjectKindIcon(ObjectEditorCatalog.ToObjectKind(_browserSelection.Kind)));

    private void DrawCreateBrowserActionButton()
    {
        float edge = ResolveCreateBrowserActionButtonEdge();
        if (_browserSelection.Source == CreateBrowserSelection.CreateBrowserSource.Library)
        {
            FontAwesomeIcon catalogIcon = SceneItemPresentation.ResolveObjectKindIcon(ObjectEditorCatalog.ToObjectKind(_browserSelection.Kind));
            if (EditorIconButton.DrawToggle(
                    "createOpenCatalog",
                    catalogIcon,
                    new Vector2(edge),
                    false,
                    ThemeColors.AccentPrimary,
                    ThemeColors.AccentPrimary,
                    $"Open in {ObjectEditorCatalog.GetDraftKindLabel(_browserSelection.Kind)} Catalog"))
            {
                _catalogBrowser.OpenCurrentDraftInCatalog();
            }

            return;
        }

        _draft.TryCaptureCurrentLibraryPreset(out string name, out ObjectLibraryPreset? preset);
        _libraryBrowser.DrawPresetLibraryButton(name, preset, edge);
    }

    internal void DrawCreatePanel(IReadOnlyList<ObjectKindInfo> kindInfos)
    {
        if (_browserSelection.Kind == DraftKind.Furniture && !_housingModePolicy.AllowsFurniturePath(_draft.Furniture.Model.SharedGroupPath))
        {
            _draft.ClearFurnitureCatalogSelection();
        }

        var (icon, title, description) = _browserSelection.Kind switch
        {
            DraftKind.BgObject  => (FontAwesomeIcon.Cube, "Create BgObject", "Create a specific world object anywhere."),
            DraftKind.Furniture => (FontAwesomeIcon.Home, "Create Furniture", "Create housing furniture object anywhere."),
            DraftKind.Vfx       => (FontAwesomeIcon.Magic, "Create VFX", "Create a VFX object anywhere."),
            DraftKind.Light     => (FontAwesomeIcon.Sun, "Create Light", "Create a light object from /gpose anywhere."),
            _                   => throw new InvalidOperationException($"unsupported draft kind {_browserSelection.Kind}"),
        };
        (title, description) = _browserSelection.Kind switch
        {
            DraftKind.BgObject when !string.IsNullOrWhiteSpace(_draft.BgObject.Model.ModelPath)
                => (_catalogInfo.ResolveCatalogName(ObjectCatalogKind.BgObject, _draft.BgObject.Model.ModelPath, title), _draft.BgObject.Model.ModelPath),
            DraftKind.Furniture when !string.IsNullOrWhiteSpace(_draft.Furniture.Model.SharedGroupPath)
                => (_catalogInfo.ResolveFurnitureCatalogName(_draft.Furniture.Model.SharedGroupPath, _draft.Furniture.Model.HousingRowId, _draft.Furniture.Model.ItemRowId), _draft.Furniture.Model.SharedGroupPath),
            DraftKind.Vfx when !string.IsNullOrWhiteSpace(_draft.Vfx.Model.VfxPath)
                => (_catalogInfo.ResolveCatalogName(ObjectCatalogKind.Vfx, _draft.Vfx.Model.VfxPath, title), _draft.Vfx.Model.VfxPath),
            DraftKind.Light when ObjectEditorCatalog.FindLightCatalogEntry(_draft.Light.Model.LightType) is { } light
                => (light.Name, light.Description),
            _ => (title, description),
        };
        var actionDescription = _browserSelection.Kind switch
        {
            DraftKind.BgObject  => "The object will be placed at your current character position.",
            DraftKind.Furniture => "The furniture will be placed at your current character position.",
            DraftKind.Vfx       => "The VFX will be placed at your current character position.",
            DraftKind.Light     => "The light will be created at your current character position.",
            _                   => throw new InvalidOperationException($"unsupported draft kind {_browserSelection.Kind}"),
        };
        string actionLabel = ObjectEditorCatalog.ResolvePlacementActionLabel(ObjectEditorCatalog.ToObjectKind(_browserSelection.Kind));
        Guid? placementLayoutId = _sceneView.GetCreationLayoutId();
        IReadOnlyList<string> placedFolders = GetPlacementFolders(placementLayoutId);
        string selectedPlacementFolderPath = _draft.ResolveCreatePlacementFolderPath(placementLayoutId, placedFolders);

        var spacingY = ImGui.GetStyle().ItemSpacing.Y;
        var actionHostHeight = MeasureCreateActionHostHeight();

        DrawCreateHero(icon, title, description);
        var availableAfterHero = ImGui.GetContentRegionAvail().Y;
        var settingsHostHeight = MathF.Max(
            40f * ImGuiHelpers.GlobalScale,
            availableAfterHero - actionHostHeight - spacingY);

        ObjectKind kind = ObjectEditorCatalog.ToObjectKind(_browserSelection.Kind);
        ObjectKindInfo? kindInfo = FindKindInfo(kindInfos, kind);
        if (kindInfo is null)
        {
            return;
        }

        string id = kind.ToString().ToLowerInvariant();
        DrawScrollableCreateSettingsCard($"{id}-settings", MathF.Max(1f, settingsHostHeight), () =>
        {
            switch (_browserSelection.Kind)
            {
                case DraftKind.BgObject:
                    DrawBgObjectCreateSettings();
                    break;
                case DraftKind.Furniture:
                    DrawFurnitureCreateSettings();
                    break;
                case DraftKind.Vfx:
                    DrawVfxCreateSettings();
                    break;
                case DraftKind.Light:
                    DrawLightCreateSettings();
                    break;
            }
        });

        DrawBottomAlignedCreateActionHost($"{id}-action-host", actionHostHeight, () =>
        {
            bool canPlace = kindInfo.CanCreate
                && _draft.HasCurrentCreateAsset()
                && (_browserSelection.Kind != DraftKind.Furniture || _createActions.CanCreateFurnitureInHousingMode(_draft.Furniture.Model.SharedGroupPath));
            DrawCreateActionCard(
                id,
                actionLabel,
                actionDescription,
                placementLayoutId,
                selectedPlacementFolderPath,
                canPlace,
                () =>
                {
                    ObjectLibraryPreset preset = _draft.CaptureCreatePreset();
                    _createActions.CreateObject(kind, new ObjectPlacementOverrides
                    {
                        Name = _catalogInfo.ResolveObjectName(preset.Model),
                        Visible = preset.Visible,
                        FolderPath = selectedPlacementFolderPath,
                        Scale = kind == ObjectKind.Light ? null : preset.Scale,
                        Model = preset.Model,
                    });
                });
        });
    }

    private IReadOnlyList<string> GetPlacementFolders(Guid? layoutId)
    {
        long revision = _sceneView.GetPersistentSceneRevision();
        if (_placementFolders is { } cached && cached.Revision == revision && cached.LayoutId == layoutId)
        {
            return cached.Folders;
        }

        IReadOnlyList<string> folders = _sceneView.GetPlacedFolders(layoutId);
        _placementFolders = (revision, layoutId, folders);
        return folders;
    }

    private void DrawBgObjectCreateSettings()
    {
        _preview.DrawPreviewSection(
            "bgobject-preview",
            PreviewControlsText,
            ObjectCatalogKind.BgObject,
            _draft.BgObject.Model.ModelPath,
            _draft.BgObject.Preview);

        ImGuiHelpers.ScaledDummy(6f);

        using var bgObjectSettingsTable = EditorPropertyTable.Begin("bgobjectCreate");
        if (bgObjectSettingsTable)
        {
            EditorPropertyTable.Checkbox("bgobjectCreateVisible", "Visible", ref _draft.BgObject.Visible);
            _transformEditor.DrawScaleClipboardRow("bgobjectCreateScale", ref _draft.BgObject.Scale);
            ObjectModelEditor.DrawBgObjectRows("create", ref _draft.BgObject.Model, _catalogInfo.ResolveBgObjectCatalogInfo(_draft.BgObject.Model.ModelPath));
        }
    }

    private void DrawFurnitureCreateSettings()
    {
        _preview.DrawPreviewSection(
            "furniture-preview",
            PreviewControlsText,
            _draft.Furniture.Model,
            _draft.Furniture.Preview);

        ImGuiHelpers.ScaledDummy(6f);

        using (var furnitureSettingsTable = EditorPropertyTable.Begin("furnitureCreate"))
        {
            if (furnitureSettingsTable)
            {
                EditorPropertyTable.Checkbox("furnitureCreateVisible", "Visible", ref _draft.Furniture.Visible);
                _transformEditor.DrawScaleClipboardRow("furnitureCreateScale", ref _draft.Furniture.Scale);
                _furnitureEditor.Draw(
                    "create",
                    ref _draft.Furniture.Model,
                    string.Empty);
            }
        }
    }

    private void DrawLightCreateSettings()
    {
        using (var lightSettingsTable = EditorPropertyTable.Begin("lightCreate"))
        {
            if (lightSettingsTable)
            {
                EditorPropertyTable.Checkbox("lightCreateVisible", "Visible", ref _draft.Light.Visible);
            }
        }

        using var lightModelTable = EditorPropertyTable.Begin("lightModel_create");
        if (lightModelTable)
        {
            ObjectModelEditor.DrawLightRows("create", ref _draft.Light.Model, false);
        }
    }

    private void DrawVfxCreateSettings()
    {
        using var vfxSettingsTable = EditorPropertyTable.Begin("vfxCreate");
        if (!vfxSettingsTable)
        {
            return;
        }

        EditorPropertyTable.Checkbox("vfxCreateVisible", "Visible", ref _draft.Vfx.Visible);
        var vfxModel = _draft.Vfx.Model;
        ObjectCatalogVfxInfo? vfxInfo = _catalogInfo.ResolveVfxCatalogInfo(vfxModel.VfxPath);
        bool canUseReplayLoop = vfxInfo?.CanUseReplayLoop != false;
        if (!canUseReplayLoop && vfxModel.Loop)
        {
            vfxModel = vfxModel with { Loop = false };
        }

        ObjectModelEditor.DrawVfxRows("create", ref vfxModel, canUseReplayLoop, _assetSound.HasSound(vfxModel.VfxPath));
        _draft.Vfx.Model = vfxModel;

        _transformEditor.DrawScaleClipboardRow("vfxCreateScale", ref _draft.Vfx.Scale);
    }

    private static ObjectKindInfo? FindKindInfo(IReadOnlyList<ObjectKindInfo> kindInfos, ObjectKind kind)
    {
        return kindInfos.FirstOrDefault(info => info.Kind == kind);
    }
}
