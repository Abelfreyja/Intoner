using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string PreviewControlsText = "drag to rotate | scroll to zoom | double click to reset";

    private void DrawCreatePanel(IReadOnlyList<ObjectKindInfo> kindInfos)
    {
        var (icon, title, description) = _draftKind switch
        {
            DraftKind.BgObject  => (FontAwesomeIcon.Cube, "Create BgObject", "Create a specific world object anywhere."),
            DraftKind.Furniture => (FontAwesomeIcon.Home, "Create Furniture", "Create housing furniture object anywhere."),
            DraftKind.Vfx       => (FontAwesomeIcon.Magic, "Create VFX", "Create a VFX object anywhere."),
            _                   => (FontAwesomeIcon.Sun, "Create Light", "Create a light object from /gpose anywhere."),
        };
        var actionDescription = _draftKind switch
        {
            DraftKind.BgObject  => "The object will be placed at your current character position.",
            DraftKind.Furniture => "The furniture will be placed at your current character position.",
            DraftKind.Vfx       => "The VFX will be placed at your current character position.",
            _                   => "The light will be created at your current character position.",
        };
        var actionLabel = _draftKind switch
        {
            DraftKind.BgObject  => "Place Object",
            DraftKind.Furniture => "Place Furniture",
            DraftKind.Vfx       => "Place VFX",
            _                   => "Place Light",
        };
        var placedFolders = _sceneView.GetPlacedFolders();
        var selectedPlacementFolderPath = ResolveCreatePlacementFolderPath(placedFolders);

        var spacingY = ImGui.GetStyle().ItemSpacing.Y;
        var actionHostHeight = MeasureCreateActionHostHeight();

        DrawCreateHero(icon, title, description);
        var availableAfterHero = ImGui.GetContentRegionAvail().Y;
        var settingsHostHeight = MathF.Max(
            40f * ImGuiHelpers.GlobalScale,
            availableAfterHero - actionHostHeight - spacingY);

        switch (_draftKind)
        {
            case DraftKind.BgObject:
                DrawBgObjectCreatePanel(FindKindInfo(kindInfos, ObjectKind.BgObject), settingsHostHeight, actionHostHeight, actionLabel, actionDescription, placedFolders, selectedPlacementFolderPath);
                break;
            case DraftKind.Furniture:
                DrawFurnitureCreatePanel(FindKindInfo(kindInfos, ObjectKind.Furniture), settingsHostHeight, actionHostHeight, actionLabel, actionDescription, placedFolders, selectedPlacementFolderPath);
                break;
            case DraftKind.Vfx:
                DrawVfxCreatePanel(FindKindInfo(kindInfos, ObjectKind.Vfx), settingsHostHeight, actionHostHeight, actionLabel, actionDescription, placedFolders, selectedPlacementFolderPath);
                break;
            case DraftKind.Light:
                DrawLightCreatePanel(FindKindInfo(kindInfos, ObjectKind.Light), settingsHostHeight, actionHostHeight, actionLabel, actionDescription, placedFolders, selectedPlacementFolderPath);
                break;
        }
    }

    private void DrawBgObjectCreatePanel(
        ObjectKindInfo? kindInfo,
        float settingsHostHeight,
        float actionHostHeight,
        string actionLabel,
        string actionDescription,
        IReadOnlyList<string> placedFolders,
        string selectedPlacementFolderPath)
    {
        if (kindInfo is null)
        {
            return;
        }

        DrawScrollableCreateSectionCard("bgobject-settings", FontAwesomeIcon.Cube, "BgObject Settings", "Set object state and configuration before creation.", MathF.Max(1f, settingsHostHeight), () =>
        {
            DrawPreviewSection(
                "bgobject-preview",
                PreviewControlsText,
                ObjectCatalogKind.BgObject,
                _bgObjectCreate.ModelPath,
                _bgObjectCreate.Preview);

            ImGuiHelpers.ScaledDummy(6f);

            using var bgObjectSettingsTable = CompactSettingsTable("bgobjectCreate");
            if (bgObjectSettingsTable)
            {
                DrawCheckboxRow("bgobjectCreateVisible", "Visible", ref _bgObjectCreate.Visible);
                DrawCheckboxRow("bgobjectCreateCoveredFromRain", "Covered From Rain", ref _bgObjectCreate.IsCoveredFromRain);
                DrawScaleClipboardRow("bgobjectCreateScale", ref _bgObjectCreate.Scale);
                DrawSliderFloatRow("bgobjectCreateTransparency", "Transparency", ref _bgObjectCreate.Transparency, 0f, 1f, "%.3f");
                DrawCompactSettingsLabelCell("Dye Color");
                var dyeColor = _bgObjectCreate.DyeColor;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.ColorEdit4("##bgobjectCreateDyeColor", ref dyeColor, ImGuiColorEditFlags.Float))
                {
                    _bgObjectCreate.DyeColor = dyeColor;
                }
            }
        });

        DrawBottomAlignedCreateActionHost("bgobject-action-host", actionHostHeight, () =>
        {
            ObjectPlacementOverrides BuildPlacementOverrides()
                => new()
                {
                    Visible = _bgObjectCreate.Visible,
                    FolderPath = selectedPlacementFolderPath,
                    Scale = _bgObjectCreate.Scale,
                    Model = new BgObjectModel
                    {
                        ModelPath = _bgObjectCreate.ModelPath,
                        Transparency = _bgObjectCreate.Transparency,
                        DyeColor = _bgObjectCreate.DyeColor,
                        IsCoveredFromRain = _bgObjectCreate.IsCoveredFromRain,
                    },
                };

            using var disabled = ImRaii.Disabled(!kindInfo.CanCreate || string.IsNullOrWhiteSpace(_bgObjectCreate.ModelPath));
            DrawCreateActionCard(
                "bgobject",
                actionLabel,
                actionDescription,
                placedFolders,
                selectedPlacementFolderPath,
                () => CreateObject(ObjectKind.BgObject, BuildPlacementOverrides()),
                SetCreatePlacementFolderPath);
        });
    }

    private void DrawFurnitureCreatePanel(
        ObjectKindInfo? kindInfo,
        float settingsHostHeight,
        float actionHostHeight,
        string actionLabel,
        string actionDescription,
        IReadOnlyList<string> placedFolders,
        string selectedPlacementFolderPath)
    {
        if (kindInfo is null)
        {
            return;
        }

        if (!_housingModePolicy.AllowsFurniturePath(_furnitureCreate.SharedGroupPath))
        {
            ClearFurnitureCatalogSelection();
        }

        DrawScrollableCreateSectionCard("furniture-settings", FontAwesomeIcon.Home, "Furniture Settings", "Set furniture state and configuration before creation.", MathF.Max(1f, settingsHostHeight), () =>
        {
            DrawPreviewSection(
                "furniture-preview",
                PreviewControlsText,
                ObjectCatalogKind.Furniture,
                _furnitureCreate.SharedGroupPath,
                _furnitureCreate.Preview);

            ImGuiHelpers.ScaledDummy(6f);

            using (var furnitureSettingsTable = CompactSettingsTable("furnitureCreate"))
            {
                if (furnitureSettingsTable)
                {
                    DrawCheckboxRow("furnitureCreateVisible", "Visible", ref _furnitureCreate.Visible);
                    DrawScaleClipboardRow("furnitureCreateScale", ref _furnitureCreate.Scale);
                    DrawSliderFloatRow("furnitureCreateTransparency", "Transparency", ref _furnitureCreate.Transparency, 0f, 1f, "%.3f");
                    var outlineColor = _furnitureCreate.OutlineColor;
                    if (DrawEnumRow("furnitureCreateOutlineColor", "Outline Color", outlineColor, DrawOutlineColorLabel, ref outlineColor))
                    {
                        _furnitureCreate.OutlineColor = outlineColor;
                    }

                    DrawFurnitureColorEditor(
                        "Color",
                        "create",
                        ref _furnitureCreate.UseCustomColor,
                        ref _furnitureCreate.StainId,
                        ref _furnitureCreate.CustomColor,
                        ref _furnitureCreate.StainFilter);
                }
            }
        });

        DrawBottomAlignedCreateActionHost("furniture-action-host", actionHostHeight, () =>
        {
            bool housingModeAllowsCreate = CanCreateFurnitureInHousingMode(_furnitureCreate.SharedGroupPath);

            ObjectPlacementOverrides BuildPlacementOverrides()
                => new()
                {
                    Visible = _furnitureCreate.Visible,
                    FolderPath = selectedPlacementFolderPath,
                    Scale = _furnitureCreate.Scale,
                    Model = ResolveFurnitureCatalogVariant(new FurnitureModel
                    {
                        SharedGroupPath = _furnitureCreate.SharedGroupPath,
                        HousingRowId = _furnitureCreate.HousingRowId,
                        ItemRowId = _furnitureCreate.ItemRowId,
                        Transparency = _furnitureCreate.Transparency,
                        OutlineColor = _furnitureCreate.OutlineColor,
                        Color = new FurnitureColorModel
                        {
                            StainId = _furnitureCreate.StainId,
                            UseCustomColor = _furnitureCreate.UseCustomColor,
                            CustomColor = _furnitureCreate.CustomColor,
                        },
                    }),
                };

            using var disabled = ImRaii.Disabled(!kindInfo.CanCreate || string.IsNullOrWhiteSpace(_furnitureCreate.SharedGroupPath) || !housingModeAllowsCreate);
            DrawCreateActionCard(
                "furniture",
                actionLabel,
                actionDescription,
                placedFolders,
                selectedPlacementFolderPath,
                () => CreateObject(ObjectKind.Furniture, BuildPlacementOverrides()),
                SetCreatePlacementFolderPath);
        });
    }

    private bool CanCreateFurnitureInHousingMode(string sharedGroupPath)
    {
        ObjectHousingModeState state = _housingModePolicy.GetState();
        if (!state.IsHousingMode)
        {
            return true;
        }

        if (!_housingModePolicy.AllowsFurniturePath(sharedGroupPath))
        {
            return false;
        }

        int furnitureCount = HousingFurnitureCounter.Count(_sceneView.GetPlacedObjectSnapshots());
        return furnitureCount < state.FurnitureLimit;
    }

    private FurnitureModel ResolveFurnitureCatalogVariant(FurnitureModel model)
    {
        if (!_objectCatalog.TryResolveEntry(ObjectCatalogKind.Furniture, model.SharedGroupPath, out ObjectCatalogEntry? entry)
            || entry.FurnitureInfo is not { } furnitureInfo)
        {
            return model with
            {
                HousingRowId = 0,
                ItemRowId = 0,
            };
        }

        ObjectCatalogFurnitureVariant variant = furnitureInfo.TryResolveVariant(
            model.HousingRowId,
            model.ItemRowId,
            out ObjectCatalogFurnitureVariant? resolvedVariant)
            ? resolvedVariant
            : furnitureInfo.PrimaryVariant;

        return model with
        {
            HousingRowId = variant.HousingRowId,
            ItemRowId = variant.ItemRowId,
        };
    }

    private void DrawLightCreatePanel(
        ObjectKindInfo? kindInfo,
        float settingsHostHeight,
        float actionHostHeight,
        string actionLabel,
        string actionDescription,
        IReadOnlyList<string> placedFolders,
        string selectedPlacementFolderPath)
    {
        if (kindInfo is null)
        {
            return;
        }

        DrawScrollableCreateSectionCard("light-settings", FontAwesomeIcon.Sun, "Light Settings", "Set light state and configuration before creation.", MathF.Max(1f, settingsHostHeight), () =>
        {
            using (var lightSettingsTable = CompactSettingsTable("lightCreate"))
            {
                if (lightSettingsTable)
                {
                    DrawCheckboxRow("lightCreateVisible", "Visible", ref _lightCreate.Visible);
                }
            }

            DrawLightModelEditor("create", ref _lightCreate.Model, false);
        });

        DrawBottomAlignedCreateActionHost("light-action-host", actionHostHeight, () =>
        {
            ObjectPlacementOverrides BuildPlacementOverrides()
                => new()
                {
                    Visible = _lightCreate.Visible,
                    FolderPath = selectedPlacementFolderPath,
                    Model = _lightCreate.Model,
                };

            using var disabled = ImRaii.Disabled(!kindInfo.CanCreate);
            DrawCreateActionCard(
                "light",
                actionLabel,
                actionDescription,
                placedFolders,
                selectedPlacementFolderPath,
                () => CreateObject(ObjectKind.Light, BuildPlacementOverrides()),
                SetCreatePlacementFolderPath);
        });
    }

    private void DrawVfxCreatePanel(
        ObjectKindInfo? kindInfo,
        float settingsHostHeight,
        float actionHostHeight,
        string actionLabel,
        string actionDescription,
        IReadOnlyList<string> placedFolders,
        string selectedPlacementFolderPath)
    {
        if (kindInfo is null)
        {
            return;
        }

        DrawScrollableCreateSectionCard("vfx-settings", FontAwesomeIcon.Magic, "VFX Settings", "Set VFX state and configuration before creation.", MathF.Max(1f, settingsHostHeight), () =>
        {
            using var vfxSettingsTable = CompactSettingsTable("vfxCreate");
            if (!vfxSettingsTable)
            {
                return;
            }

            DrawCheckboxRow("vfxCreateVisible", "Visible", ref _vfxCreate.Visible);

            ObjectCatalogVfxInfo? vfxInfo = ResolveVfxCatalogInfo(_vfxCreate.VfxPath);
            if (vfxInfo?.CanUseReplayLoop != false)
            {
                DrawCheckboxRow("vfxCreateLoop", "Loop", ref _vfxCreate.Loop);

                using (ImRaii.Disabled(!_vfxCreate.Loop))
                {
                    DrawCompactSettingsLabelCell("Loop Interval");
                    var loopIntervalSeconds = _vfxCreate.LoopIntervalSeconds;
                    if (ImGui.DragInt(
                            "##vfxCreateLoopInterval",
                            ref loopIntervalSeconds,
                            0.1f,
                            VfxModel.MinLoopIntervalSeconds,
                            VfxModel.MaxLoopIntervalSeconds,
                            "%d seconds"))
                    {
                        _vfxCreate.LoopIntervalSeconds = VfxModel.ClampLoopIntervalSeconds(loopIntervalSeconds);
                    }
                }
            }
            else
            {
                _vfxCreate.Loop = false;
            }

            DrawCompactSettingsLabelCell("VFX Path");
            var vfxPath = _vfxCreate.VfxPath;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##vfxCreatePath", ref vfxPath, 512))
            {
                _vfxCreate.VfxPath = vfxPath;
            }

            DrawScaleClipboardRow("vfxCreateScale", ref _vfxCreate.Scale);

            DrawCompactSettingsLabelCell("Tint");
            var color = _vfxCreate.Color;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.ColorEdit4("##vfxCreateColor", ref color, ImGuiColorEditFlags.Float))
            {
                _vfxCreate.Color = color;
            }
        });

        DrawBottomAlignedCreateActionHost("vfx-action-host", actionHostHeight, () =>
        {
            ObjectPlacementOverrides BuildPlacementOverrides()
            {
                ObjectCatalogVfxInfo? vfxInfo = ResolveVfxCatalogInfo(_vfxCreate.VfxPath);
                bool loop = vfxInfo?.CanUseReplayLoop != false && _vfxCreate.Loop;
                return new()
                {
                    Visible = _vfxCreate.Visible,
                    FolderPath = selectedPlacementFolderPath,
                    Scale = _vfxCreate.Scale,
                    Model = new VfxModel
                    {
                        VfxPath = _vfxCreate.VfxPath,
                        Color = _vfxCreate.Color,
                        Loop = loop,
                        LoopIntervalSeconds = _vfxCreate.LoopIntervalSeconds,
                    },
                };
            }

            using var disabled = ImRaii.Disabled(!kindInfo.CanCreate || string.IsNullOrWhiteSpace(_vfxCreate.VfxPath));
            DrawCreateActionCard(
                "vfx",
                actionLabel,
                actionDescription,
                placedFolders,
                selectedPlacementFolderPath,
                () => CreateObject(ObjectKind.Vfx, BuildPlacementOverrides()),
                SetCreatePlacementFolderPath);
        });
    }

    private static ObjectKindInfo? FindKindInfo(IReadOnlyList<ObjectKindInfo> kindInfos, ObjectKind kind)
    {
        return kindInfos.FirstOrDefault(info => info.Kind == kind);
    }

    private ObjectCatalogVfxInfo? ResolveVfxCatalogInfo(string vfxPath)
        => _objectCatalog.TryResolveEntry(ObjectCatalogKind.Vfx, vfxPath, out ObjectCatalogEntry? entry)
        && entry.VfxInfo is { } vfxInfo
            ? vfxInfo
            : null;
}

