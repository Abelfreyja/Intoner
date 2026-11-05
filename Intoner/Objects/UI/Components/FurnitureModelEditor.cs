using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.UI;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal sealed class FurnitureModelEditor(
    IFurnitureStainService stains,
    FurnitureMaterialService materials,
    EditorAssetSoundQuery sound,
    ObjectLayoutService layouts,
    UiSharedService ui)
{
    private readonly FurnitureStainSelector _stainSelector = new();
    private readonly List<FurnitureMaterialOption> _filteredWallpapers = [];
    private string _filter = string.Empty;
    private string? _appliedFilter;

    private ImTextureID GetItemIcon(uint iconId)
        => iconId > 0 && ui.TryGetIcon(iconId, out IDalamudTextureWrap? texture) && texture is not null
            ? texture.Handle : ImTextureID.Null;

    public bool SupportsWallpaper(FurnitureModel model)
        => materials.IsWallpaperFurniture(model);

    public void Draw(string id, ref FurnitureModel model, string collectionId = "",
        Action<string, string, FurnitureModel, bool>? onChanged = null)
    {
        float transparency = model.Transparency;
        if (EditorPropertyTable.SliderFloat($"FurnitureTransparency_{id}", "Transparency", ref transparency, 0f, 1f, "%.3f"))
        {
            model = model with { Transparency = transparency };
            onChanged?.Invoke("FurnitureTransparency", "Change Furniture Transparency", model, false);
        }

        ObjectOutlineColor outlineColor = model.OutlineColor;
        if (EditorPropertyTable.EnumRow($"FurnitureOutlineColor_{id}", "Outline Color", ref outlineColor))
        {
            model = model with { OutlineColor = outlineColor };
            onChanged?.Invoke("FurnitureOutlineColor", "Change Furniture Outline Color", model, true);
        }

        bool useCustomColor = model.Color.UseCustomColor;
        if (EditorPropertyTable.Checkbox($"{id}_furnitureCustomColor", "Custom Color", ref useCustomColor))
        {
            model = model with { Color = model.Color with { UseCustomColor = useCustomColor } };
            onChanged?.Invoke("FurnitureCustomColor", "Toggle Furniture Custom Color", model, true);
        }

        if (useCustomColor)
        {
            Vector4 customColor = model.Color.CustomColor;
            Vector3 rgbColor = new(customColor.X, customColor.Y, customColor.Z);
            EditorPropertyTable.NextRow("Color");
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.ColorEdit3($"##Color_{id}", ref rgbColor))
            {
                model = model with { Color = model.Color with { CustomColor = new Vector4(rgbColor, 1f) } };
                onChanged?.Invoke("FurnitureColor", "Change Furniture Color", model, false);
            }
        }
        else
        {
            byte stainId = model.Color.StainId;
            if (_stainSelector.Draw("Color", id, stains.GetStains(), ref stainId))
            {
                model = model with { Color = model.Color with { StainId = stainId } };
                onChanged?.Invoke("FurnitureStain", "Change Furniture Stain", model, true);
            }
        }

        DrawWallpaper(id, ref model, onChanged);

        if (sound.HasSound(model.SharedGroupPath, collectionId))
        {
            bool soundEnabled = model.SoundEnabled;

            if (EditorPropertyTable.Checkbox($"FurnitureSound_{id}", "Sound", ref soundEnabled))
            {
                model = model with { SoundEnabled = soundEnabled };
                onChanged?.Invoke("FurnitureSound", "Change Furniture Sound", model, true);
            }
        }

        bool cameraCollision = model.CameraCollision;
        if (EditorPropertyTable.Checkbox($"FurnitureCameraCollision_{id}", "Camera Collision", ref cameraCollision))
        {
            model = model with { CameraCollision = cameraCollision };
            onChanged?.Invoke("FurnitureCameraCollision", "Change Camera Collision", model, true);
        }
    }

    private void DrawWallpaper(string id, ref FurnitureModel model, Action<string, string, FurnitureModel, bool>? onChanged)
    {
        if (!SupportsWallpaper(model))
        {
            return;
        }

        string preview = "None";
        FurnitureMaterialOption? currentOption = null;
        if (model.MaterialItem is { } current)
        {
            preview = materials.TryResolve(current.ItemId, out currentOption)
                ? currentOption.Name : $"Unknown item {current.ItemId}";
        }

        EditorPropertyTable.NextRow("Wallpaper");
        using var disabled = ImRaii.Disabled(!layouts.CanSetMaterial);
        ImGui.SetNextWindowSizeConstraints(new Vector2(260f, 0f) * ImGuiHelpers.GlobalScale,
            new Vector2(480f, 380f) * ImGuiHelpers.GlobalScale);
        Vector2 previewMin = ImGui.GetCursorScreenPos();
        Vector2 previewMax = previewMin + new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
        ImDrawListPtr previewDrawList = ImGui.GetWindowDrawList();
        ImTextureID? previewIcon = currentOption is not null ? GetItemIcon(currentOption.IconId) : null;
        using var combo = ImRaii.Combo($"##wallpaper_{id}", string.Empty);
        EditorIconText.DrawComboPreview(previewDrawList, previewMin, previewMax, preview, previewIcon);
        if (!combo)
        {
            if (!layouts.CanSetMaterial)
            {
                IntonerTooltip.Attach("Wallpaper material changes are unavailable.");
            }

            return;
        }

        bool appearing = ImGui.IsWindowAppearing();
        if (appearing)
        {
            ImGui.SetKeyboardFocusHere();
        }

        _ = EditorSearchField.Draw($"wallpaperSearch_{id}", ref _filter,
            new EditorSearchFieldOptions("Search wallpapers", ThemeColors.AccentPrimary));
        bool filterChanged = !string.Equals(_appliedFilter, _filter, StringComparison.Ordinal);
        if (filterChanged)
        {
            _appliedFilter = _filter;
            _filteredWallpapers.Clear();
            string filter = _filter.Trim();
            _filteredWallpapers.AddRange(materials.Wallpapers.Where(option => option.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        float rowHeight = EditorContextMenu.ItemHeight;
        float listHeight = MathF.Min(240f * ImGuiHelpers.GlobalScale,
            (_filteredWallpapers.Count + 1) * (rowHeight + ImGui.GetStyle().ItemSpacing.Y));
        using var list = EditorScrollList.Begin($"wallpapers_{id}", new Vector2(0f, listHeight),
            EditorScrollListOptions.Panel(ThemeColors.WindowBg, 4f * ImGuiHelpers.GlobalScale, ThemeColors.AccentPrimary));
        if (!list)
        {
            return;
        }

        if (appearing || filterChanged)
        {
            ImGui.SetScrollY(0f);
        }

        uint selectedId = model.MaterialItem?.ItemId ?? 0;
        bool changed = EditorContextMenu.DrawChoice("defaultWallpaper", "None", selectedId == 0, ThemeColors.AccentPrimary, height: rowHeight);
        FurnitureMaterialItemModel? selection = null;
        using UiVirtualList.Scope rows = UiVirtualList.Begin(_filteredWallpapers.Count,
            UiVirtualListOptions.Rows(rowHeight, ImGui.GetStyle().ItemSpacing.Y));
        while (rows.Step())
        {
            for (int index = rows.DisplayStart; index < rows.DisplayEnd; ++index)
            {
                FurnitureMaterialOption option = _filteredWallpapers[index];
                ImTextureID? icon = option.ItemId == currentOption?.ItemId ? previewIcon : GetItemIcon(option.IconId);

                if (EditorContextMenu.DrawChoice($"wallpaper:{option.ItemId}", option.Name, selectedId == option.ItemId, ThemeColors.AccentPrimary,
                        height: rowHeight, itemIcon: icon))
                {
                    selection = option.ToModel();
                    changed = true;
                }

                rows.FinishItem(index);
            }
        }

        if (changed && selectedId != (selection?.ItemId ?? 0))
        {
            model = model with { MaterialItem = selection };
            onChanged?.Invoke("FurnitureMaterial", "Change Wallpaper", model, true);
        }
    }
}
