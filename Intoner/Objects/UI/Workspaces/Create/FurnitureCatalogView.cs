using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Services.Configuration;

namespace Intoner.Objects.UI;

/// <summary> prepares furniture rows only when the catalog, filters, housing area, or selected variant changes </summary>
internal sealed class FurnitureCatalogView
{
    private Query _query;
    private IReadOnlyList<ObjectCatalogFurnitureResult>? _entries;

    public IReadOnlyList<ObjectCatalogFurnitureResult> GetEntries(
        ObjectCatalogSection section,
        string filter,
        string category,
        ObjectHousingModeState housing,
        FurnitureModel selected)
    {
        Query query = new(section, filter, category, housing.IsHousingMode, housing.Area,
            selected.SharedGroupPath, selected.HousingRowId, selected.ItemRowId);
        if (_entries is not null && _query == query)
        {
            return _entries;
        }

        _entries = BuildEntries(query);
        _query = query;
        return _entries;
    }

    private static IReadOnlyList<ObjectCatalogFurnitureResult> BuildEntries(Query query)
    {
        IReadOnlyList<ObjectCatalogFurnitureResult> entries = query.Section.FilterFurniture(query.Filter, query.Category);
        if (query.IsHousingMode)
        {
            entries = entries.Where(entry => HousingFurnitureAreaPolicy.AllowsArea(entry.Variant.HousingMetadata, query.Area)).ToArray();
        }

        return ResolveSelectedVariant(entries, query);
    }

    private static IReadOnlyList<ObjectCatalogFurnitureResult> ResolveSelectedVariant(
        IReadOnlyList<ObjectCatalogFurnitureResult> entries,
        Query query)
    {
        if (!string.IsNullOrWhiteSpace(query.Filter)
         || !string.IsNullOrWhiteSpace(query.Category)
         || string.IsNullOrWhiteSpace(query.SelectedPath))
        {
            return entries;
        }

        for (int i = 0; i < entries.Count; ++i)
        {
            ObjectCatalogFurnitureResult entry = entries[i];
            if (!string.Equals(entry.Entry.PlacementPath, query.SelectedPath, StringComparison.OrdinalIgnoreCase)
             || entry.Entry.FurnitureInfo is not { } info
             || !info.TryResolveVariant(query.HousingRowId, query.ItemRowId, out ObjectCatalogFurnitureVariant? selectedVariant))
            {
                continue;
            }

            if ((query.IsHousingMode && !HousingFurnitureAreaPolicy.AllowsArea(selectedVariant.HousingMetadata, query.Area))
             || (entry.Variant.HousingRowId == selectedVariant.HousingRowId && entry.Variant.ItemRowId == selectedVariant.ItemRowId))
            {
                return entries;
            }

            ObjectCatalogFurnitureResult[] resolved = entries.ToArray();
            resolved[i] = new ObjectCatalogFurnitureResult(entry.Entry, selectedVariant);
            return resolved;
        }

        return entries;
    }

    private readonly record struct Query(
        ObjectCatalogSection Section,
        string Filter,
        string Category,
        bool IsHousingMode,
        ObjectHousingArea Area,
        string SelectedPath,
        uint HousingRowId,
        uint ItemRowId);
}
