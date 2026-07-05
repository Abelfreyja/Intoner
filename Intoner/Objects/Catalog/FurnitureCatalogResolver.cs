using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects.Catalog;

internal sealed class FurnitureCatalogResolver(IObjectCatalogService catalogService)
{
    private readonly Dictionary<HousingFurnitureArea, FurnitureLookup> _lookups = [];

    public bool TryFind(
        uint itemId,
        string name,
        HousingFurnitureArea area,
        [NotNullWhen(true)] out FurnitureCatalogMatch? match)
    {
        FurnitureLookup lookup = GetLookup(area);
        match = null;

        if (itemId != 0 && lookup.ByItemId.TryGetValue(itemId, out match))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(name)
            && lookup.ByName.TryGetValue(ObjectStringUtility.TrimOrEmpty(name), out match);
    }

    public bool TryResolve(
        ObjectSnapshot snapshot,
        HousingFurnitureArea area,
        [NotNullWhen(true)] out FurnitureModel? furnitureModel,
        [NotNullWhen(true)] out FurnitureCatalogMatch? match)
    {
        furnitureModel = null;
        match = null;
        if (snapshot.Model is not FurnitureModel model
            || !catalogService.TryResolveEntry(ObjectCatalogKind.Furniture, model.SharedGroupPath, out ObjectCatalogEntry? entry)
            || entry.FurnitureInfo is not { } furnitureInfo)
        {
            return false;
        }

        ObjectCatalogFurnitureVariant variant = furnitureInfo.TryResolveVariant(model.HousingRowId, model.ItemRowId, out ObjectCatalogFurnitureVariant? resolvedVariant)
            ? resolvedVariant
            : furnitureInfo.PrimaryVariant;
        if (variant.HousingMetadata.Area != area)
        {
            return false;
        }

        furnitureModel = model;
        match = new FurnitureCatalogMatch(entry, variant);
        return true;
    }

    private FurnitureLookup GetLookup(HousingFurnitureArea area)
    {
        if (_lookups.TryGetValue(area, out FurnitureLookup? lookup))
        {
            return lookup;
        }

        lookup = BuildLookup(area);
        _lookups[area] = lookup;
        return lookup;
    }

    private FurnitureLookup BuildLookup(HousingFurnitureArea area)
    {
        Dictionary<uint, FurnitureCatalogMatch> byItemId = [];
        Dictionary<string, FurnitureCatalogMatch> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectCatalogEntry entry in catalogService.GetCatalog().Furniture.Entries)
        {
            if (entry.FurnitureInfo is not { } furnitureInfo)
            {
                continue;
            }

            foreach (ObjectCatalogFurnitureVariant variant in furnitureInfo.Variants)
            {
                if (variant.HousingMetadata.Area != area)
                {
                    continue;
                }

                FurnitureCatalogMatch match = new(entry, variant);
                if (variant.ItemRowId != 0)
                {
                    byItemId.TryAdd(variant.ItemRowId, match);
                }

                AddNameMatch(byName, variant.Name, match);
            }

            if (furnitureInfo.PrimaryVariant.HousingMetadata.Area == area)
            {
                AddNameMatch(byName, entry.Name, new FurnitureCatalogMatch(entry, furnitureInfo.PrimaryVariant));
            }
        }

        return new FurnitureLookup(byItemId, byName);
    }

    private static void AddNameMatch(Dictionary<string, FurnitureCatalogMatch> byName, string name, FurnitureCatalogMatch match)
    {
        string normalizedName = ObjectStringUtility.TrimOrEmpty(name);
        if (normalizedName.Length > 0)
        {
            byName.TryAdd(normalizedName, match);
        }
    }

    private sealed record FurnitureLookup(
        IReadOnlyDictionary<uint, FurnitureCatalogMatch> ByItemId,
        IReadOnlyDictionary<string, FurnitureCatalogMatch> ByName);
}
