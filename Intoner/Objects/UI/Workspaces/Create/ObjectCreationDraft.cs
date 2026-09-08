using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class ObjectCreationDraft
{
    private readonly ObjectEditorCatalog    _catalogInfo;
    private readonly CreateBrowserSelection _browserSelection;

    public BgObjectCreateState BgObject { get; } = new();

    public FurnitureCreateState Furniture { get; } = new();

    public VfxCreateState Vfx { get; } = new();

    public LightCreateState Light { get; } = new();

    private string _createPlacementFolderPath = string.Empty;

    public ObjectCreationDraft(ObjectEditorCatalog catalogInfo, CreateBrowserSelection browserSelection)
    {
        _catalogInfo      = catalogInfo;
        _browserSelection = browserSelection;
    }

    internal static string ToggleCatalogSelectionPath(string currentPath, string nextPath)
        => string.Equals(currentPath, nextPath, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : nextPath;

    internal bool IsFurnitureCatalogSelection(ObjectCatalogFurnitureResult entry)
        => string.Equals(Furniture.Model.SharedGroupPath, entry.Entry.PlacementPath, StringComparison.OrdinalIgnoreCase)
        && Furniture.Model.HousingRowId == entry.Variant.HousingRowId
        && Furniture.Model.ItemRowId == entry.Variant.ItemRowId;

    internal void ToggleFurnitureCatalogSelection(ObjectCatalogFurnitureResult entry)
    {
        if (IsFurnitureCatalogSelection(entry))
        {
            ClearFurnitureCatalogSelection();
            return;
        }

        Furniture.Model = Furniture.Model with
        {
            SharedGroupPath = entry.Entry.PlacementPath,
            HousingRowId = entry.Variant.HousingRowId,
            ItemRowId = entry.Variant.ItemRowId,
            MaterialItem = null,
        };
    }

    internal void ClearFurnitureCatalogSelection()
    {
        Furniture.Model = Furniture.Model with
        {
            SharedGroupPath = string.Empty,
            HousingRowId = 0,
            ItemRowId = 0,
            MaterialItem = null,
        };
    }

    internal string ResolveCreatePlacementFolderPath(IReadOnlyList<string> placedFolders)
    {
        var sanitizedFolderPath = ObjectFolderUtility.SanitizeFolderPath(_createPlacementFolderPath);
        if (string.IsNullOrEmpty(sanitizedFolderPath))
        {
            _createPlacementFolderPath = string.Empty;
            return string.Empty;
        }

        if (placedFolders.Any(folder => string.Equals(folder, sanitizedFolderPath, StringComparison.OrdinalIgnoreCase)))
        {
            _createPlacementFolderPath = sanitizedFolderPath;
            return sanitizedFolderPath;
        }

        _createPlacementFolderPath = string.Empty;
        return string.Empty;
    }

    internal void SetCreatePlacementFolderPath(string folderPath)
    {
        _createPlacementFolderPath = ObjectFolderUtility.SanitizeFolderPath(folderPath);
    }

    internal bool TryCaptureCurrentLibraryPreset(out string name, out ObjectLibraryPreset? preset)
    {
        if (!HasCurrentCreateAsset())
        {
            name = string.Empty;
            preset = null;
            return false;
        }

        preset = CaptureCreatePreset();
        name = preset.Model switch
        {
            BgObjectModel bgObject => _catalogInfo.ResolveCatalogName(ObjectCatalogKind.BgObject, bgObject.ModelPath, "Background Object"),
            FurnitureModel furniture => _catalogInfo.ResolveFurnitureCatalogName(furniture.SharedGroupPath, furniture.HousingRowId, furniture.ItemRowId),
            VfxModel vfx => _catalogInfo.ResolveCatalogName(ObjectCatalogKind.Vfx, vfx.VfxPath, "VFX"),
            LightModel light => ObjectEditorCatalog.ResolveLightTypeName(light.LightType),
            _ => throw new InvalidOperationException($"unsupported draft kind {_browserSelection.Kind}"),
        };
        return true;
    }

    internal void ApplyLibraryPreset(ObjectLibraryPreset preset)
    {
        switch (preset.Model)
        {
            case BgObjectModel bgObject:
                _browserSelection.ApplyPresetKind(DraftKind.BgObject);
                BgObject.Model = bgObject;
                BgObject.Visible = preset.Visible;
                BgObject.Scale = preset.Scale;
                break;
            case FurnitureModel furniture:
                _browserSelection.ApplyPresetKind(DraftKind.Furniture);
                Furniture.Model = furniture with { AttachmentParentId = null };
                Furniture.Visible = preset.Visible;
                Furniture.Scale = preset.Scale;
                break;
            case VfxModel vfx:
                _browserSelection.ApplyPresetKind(DraftKind.Vfx);
                Vfx.Visible = preset.Visible;
                Vfx.Scale = preset.Scale;
                Vfx.Model = vfx;
                break;
            case LightModel light:
                _browserSelection.ApplyPresetKind(DraftKind.Light);
                Light.Visible = preset.Visible;
                Light.Model = light;
                break;
        }
    }

    internal sealed class BgObjectCreateState
    {
        public BgObjectModel Model = new();
        public ObjectPreviewRenderer.PreviewState Preview = new();
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
    }

    internal sealed class FurnitureCreateState
    {
        public FurnitureModel Model = new();
        public string StainFilter = string.Empty;
        public ObjectPreviewRenderer.PreviewState Preview = new();
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
    }

    internal sealed class VfxCreateState
    {
        public Vector3 Scale = Vector3.One;
        public bool Visible = true;
        public VfxModel Model = new();
    }

    internal sealed class LightCreateState
    {
        public bool Visible = true;
        public LightModel Model = new();
    }

    internal bool HasCurrentCreateAsset()
        => _browserSelection.Kind switch
        {
            DraftKind.BgObject => !string.IsNullOrWhiteSpace(BgObject.Model.ModelPath),
            DraftKind.Furniture => !string.IsNullOrWhiteSpace(Furniture.Model.SharedGroupPath),
            DraftKind.Vfx => !string.IsNullOrWhiteSpace(Vfx.Model.VfxPath),
            DraftKind.Light => true,
            _ => false,
        };

    internal ObjectLibraryPreset CaptureCreatePreset()
        => _browserSelection.Kind switch
        {
            DraftKind.BgObject => ObjectEditorCatalog.CreateLibraryPreset(
                ObjectKind.BgObject,
                BgObject.Model,
                BgObject.Visible,
                BgObject.Scale),
            DraftKind.Furniture => ObjectEditorCatalog.CreateLibraryPreset(
                ObjectKind.Furniture,
                _catalogInfo.ResolveFurnitureCatalogVariant(Furniture.Model),
                Furniture.Visible,
                Furniture.Scale),
            DraftKind.Vfx => ObjectEditorCatalog.CreateLibraryPreset(
                ObjectKind.Vfx,
                _catalogInfo.ResolveVfxCatalogInfo(Vfx.Model.VfxPath)?.CanUseReplayLoop == false
                    ? Vfx.Model with { Loop = false }
                    : Vfx.Model,
                Vfx.Visible,
                Vfx.Scale),
            DraftKind.Light => ObjectEditorCatalog.CreateLibraryPreset(ObjectKind.Light, Light.Model, Light.Visible),
            _ => throw new InvalidOperationException($"unsupported draft kind {_browserSelection.Kind}"),
        };
}
