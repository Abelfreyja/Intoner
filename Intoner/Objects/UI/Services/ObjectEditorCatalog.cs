using Dalamud.Interface;
using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class ObjectEditorCatalog
{
    private readonly IObjectCatalogService _objectCatalog;
    internal static readonly IReadOnlyList<LightCatalogEntry> LightCatalogEntries =
    [
        new(LightType.WorldLight, "World Light", "Light that affects the entire zone globally.", FontAwesomeIcon.Globe),
        new(LightType.AreaLight, "Area Light", "Light that emits spherically from a single point.", FontAwesomeIcon.Sun),
        new(LightType.SpotLight, "Spot Light", "Light that emits in the shape of a cone with a spherical base.", FontAwesomeIcon.Bullseye),
        new(LightType.FlatLight, "Flat Light", "Light that emits in a flat box-like shape.", FontAwesomeIcon.GripLines),
    ];

    public ObjectEditorCatalog(IObjectCatalogService objectCatalog)
    {
        _objectCatalog = objectCatalog;
    }

    internal static string ResolveLightTypeName(LightType type)
        => FindLightCatalogEntry(type)?.Name ?? type.ToString();

    internal static EditorBadge ResolveLightTypeBadge(LightCatalogEntry entry)
        => EditorBadge.IconOnly(
            entry.Icon,
            entry.Name,
            entry.Description,
            ThemeColors.AccentPrimary);

    internal static LightCatalogEntry? FindLightCatalogEntry(LightType type)
    {
        foreach (LightCatalogEntry entry in LightCatalogEntries)
        {
            if (entry.Type == type)
            {
                return entry;
            }
        }

        return null;
    }

    internal static ObjectLibraryPreset CreateCatalogLibraryPreset(ObjectCatalogEntry entry)
        => entry.Kind switch
        {
            ObjectCatalogKind.BgObject => CreateLibraryPreset(
                ObjectKind.BgObject,
                new BgObjectModel { ModelPath = entry.PlacementPath }),
            ObjectCatalogKind.Vfx => CreateLibraryPreset(
                ObjectKind.Vfx,
                new VfxModel { VfxPath = entry.PlacementPath }),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, null),
        };

    internal static ObjectLibraryPreset CreateCatalogLibraryPreset(ObjectCatalogFurnitureResult result)
        => CreateLibraryPreset(
            ObjectKind.Furniture,
            new FurnitureModel
            {
                SharedGroupPath = result.Entry.PlacementPath,
                HousingRowId = result.Variant.HousingRowId,
                ItemRowId = result.Variant.ItemRowId,
            });

    internal static ObjectLibraryPreset CreateCatalogLibraryPreset(LightCatalogEntry entry)
        => CreateLibraryPreset(
            ObjectKind.Light,
            new LightModel { LightType = entry.Type });

    internal static ObjectLibraryPreset CreateLibraryPreset(
        ObjectKind kind,
        ObjectData model,
        bool visible = true,
        Vector3? scale = null)
        => new()
        {
            Kind = kind,
            Visible = visible,
            Scale = scale ?? Vector3.One,
            Model = model,
        };

    internal static string BuildLibraryEntrySearchText(ObjectLibraryEntry entry)
        => SearchTermUtility.BuildSearchText(
        [
            entry.Name,
            GetObjectKindLabel(entry.Preset.Kind),
            BuildLibraryEntryDetail(entry),
        ]);

    internal static string BuildLibraryEntryDetail(ObjectLibraryEntry entry)
        => entry.Preset.Model switch
        {
            BgObjectModel bgObject => bgObject.ModelPath,
            FurnitureModel furniture => furniture.SharedGroupPath,
            VfxModel vfx => vfx.VfxPath,
            LightModel light => FindLightCatalogEntry(light.LightType)?.Description ?? ResolveLightTypeName(light.LightType),
            _ => GetObjectKindLabel(entry.Preset.Kind),
        };

    internal static string BuildSavedObjectCountLabel(int count)
        => count == 1 ? "1 saved object" : $"{count} saved objects";

    private static string BuildFolderCountLabel(int count)
        => count == 1 ? "1 folder" : $"{count} folders";

    internal static string BuildLibraryGroupCountLabel(int folderCount, int prefabCount)
    {
        string prefabLabel = prefabCount == 1 ? "1 prefab" : $"{prefabCount} prefabs";
        if (folderCount == 0)
        {
            return prefabLabel;
        }

        if (prefabCount == 0)
        {
            return BuildFolderCountLabel(folderCount);
        }

        return $"{BuildFolderCountLabel(folderCount)} and {prefabLabel}";
    }

    internal static string GetObjectKindLabel(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject => "BgObject",
            ObjectKind.Furniture => "Furniture",
            ObjectKind.Vfx => "VFX",
            ObjectKind.Light => "Light",
            _ => kind.ToString(),
        };

    internal static string GetDraftKindLabel(DraftKind kind)
        => GetObjectKindLabel(ToObjectKind(kind));

    internal static ObjectKind ToObjectKind(DraftKind kind)
        => kind switch
        {
            DraftKind.BgObject => ObjectKind.BgObject,
            DraftKind.Furniture => ObjectKind.Furniture,
            DraftKind.Vfx => ObjectKind.Vfx,
            DraftKind.Light => ObjectKind.Light,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    internal static string ResolveCreateObjectHistoryTitle(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject  => "Create BgObject",
            ObjectKind.Furniture => "Create Furniture",
            ObjectKind.Vfx       => "Create VFX",
            ObjectKind.Light     => "Create Light",
            _                    => "Create Object",
        };

    internal static string ResolvePlacementActionLabel(ObjectKind kind)
        => kind switch
        {
            ObjectKind.BgObject  => "Place Object",
            ObjectKind.Furniture => "Place Furniture",
            ObjectKind.Vfx       => "Place VFX",
            ObjectKind.Light     => "Place Light",
            _                    => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    internal FurnitureModel ResolveFurnitureCatalogVariant(FurnitureModel model)
    {
        if (!_objectCatalog.TryResolveFurnitureVariant(
                model.SharedGroupPath,
                model.HousingRowId,
                model.ItemRowId,
                out _,
                out ObjectCatalogFurnitureVariant? variant))
        {
            return model with
            {
                HousingRowId = 0,
                ItemRowId = 0,
            };
        }

        return model with
        {
            HousingRowId = variant.HousingRowId,
            ItemRowId = variant.ItemRowId,
        };
    }

    internal ObjectCatalogVfxInfo? ResolveVfxCatalogInfo(string vfxPath)
        => _objectCatalog.TryResolveEntry(ObjectCatalogKind.Vfx, vfxPath, out ObjectCatalogEntry? entry)
        && entry.VfxInfo is { } vfxInfo
            ? vfxInfo
            : null;

    internal string ResolveCatalogName(ObjectCatalogKind kind, string path, string fallback)
    {
        if (_objectCatalog.TryResolveEntry(kind, path, out ObjectCatalogEntry? entry)
         && !string.IsNullOrWhiteSpace(entry.Name))
        {
            return entry.Name;
        }

        string fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    internal string ResolveFurnitureCatalogName(string path, uint housingRowId, uint itemRowId)
    {
        if (_objectCatalog.TryResolveFurnitureVariant(path, housingRowId, itemRowId, out _, out ObjectCatalogFurnitureVariant? variant)
         && !string.IsNullOrWhiteSpace(variant.Name))
        {
            return variant.Name;
        }

        return ResolveCatalogName(ObjectCatalogKind.Furniture, path, "Furniture");
    }
}
