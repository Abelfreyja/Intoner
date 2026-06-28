using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Globalization;

namespace Intoner.Objects.Utils;

internal readonly record struct ObjectTerritoryMetadata(
    uint TerritoryId,
    string TerritoryName,
    IReadOnlyList<string> SearchTerms)
{
    public static ObjectTerritoryMetadata Empty { get; } = new(0, string.Empty, []);

    public bool HasValue
        => TerritoryId != 0 || !string.IsNullOrWhiteSpace(TerritoryName);
}

internal sealed class ObjectTerritoryMetadataSet
{
    private readonly HashSet<uint> _territoryIds = [];
    private readonly HashSet<string> _territoryNames = new(StringComparer.OrdinalIgnoreCase);

    public bool Add(in ObjectTerritoryMetadata territoryMetadata)
    {
        bool changed = false;
        if (territoryMetadata.TerritoryId != 0)
        {
            changed |= _territoryIds.Add(territoryMetadata.TerritoryId);
        }

        if (!string.IsNullOrWhiteSpace(territoryMetadata.TerritoryName))
        {
            changed |= _territoryNames.Add(territoryMetadata.TerritoryName);
        }

        return changed;
    }

    public bool Add(IReadOnlyList<uint> territoryIds, IReadOnlyList<string> territoryNames)
    {
        bool changed = false;
        foreach (uint territoryId in territoryIds)
        {
            if (territoryId != 0)
            {
                changed |= _territoryIds.Add(territoryId);
            }
        }

        foreach (string territoryName in territoryNames)
        {
            if (!string.IsNullOrWhiteSpace(territoryName))
            {
                changed |= _territoryNames.Add(territoryName);
            }
        }

        return changed;
    }

    public IReadOnlyList<uint> BuildStableIds()
        => _territoryIds.Count == 0
            ? []
            : _territoryIds.OrderBy(static value => value).ToArray();

    public IReadOnlyList<string> BuildStableNames()
        => _territoryNames.Count == 0
            ? []
            : ObjectSearchTermUtility.BuildStableTerms(_territoryNames);

    public void AddSearchTerms(HashSet<string> searchTerms)
    {
        foreach (uint territoryId in _territoryIds.OrderBy(static value => value))
        {
            _ = ObjectSearchTermUtility.AddTerm(searchTerms, territoryId.ToString(CultureInfo.InvariantCulture));
        }

        _ = ObjectSearchTermUtility.AddTerms(searchTerms, _territoryNames);
    }
}

internal static class ObjectTerritoryMetadataUtility
{
    public static ObjectTerritoryMetadata BuildFromTerritory(TerritoryType territory, ExcelSheet<PlaceName>? placeNames)
    {
        string regionName = GetPlaceName(placeNames, territory.PlaceNameRegion.RowId);
        string placeName = GetPlaceName(placeNames, territory.PlaceName.RowId);
        HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet(territory.RowId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(regionName))
        {
            _ = ObjectSearchTermUtility.AddTerm(searchTerms, regionName);
        }

        if (!string.IsNullOrWhiteSpace(placeName))
        {
            _ = ObjectSearchTermUtility.AddTerm(searchTerms, placeName);
        }

        string territoryName = BuildDisplayName(regionName, placeName);
        if (!string.IsNullOrWhiteSpace(territoryName))
        {
            _ = ObjectSearchTermUtility.AddTerm(searchTerms, territoryName);
        }

        return new ObjectTerritoryMetadata(
            territory.RowId,
            territoryName,
            ObjectSearchTermUtility.BuildStableTerms(searchTerms));
    }

    public static ObjectTerritoryMetadata BuildForTerritoryId(uint territoryId, IDataManager gameData)
    {
        if (territoryId == 0)
        {
            return ObjectTerritoryMetadata.Empty;
        }

        ExcelSheet<TerritoryType>? territories = gameData.GetExcelSheet<TerritoryType>(gameData.Language);
        ExcelSheet<PlaceName>? placeNames = gameData.GetExcelSheet<PlaceName>(gameData.Language);
        if (territories is not null && territories.TryGetRow(territoryId, out TerritoryType territory))
        {
            return BuildFromTerritory(territory, placeNames);
        }

        return new ObjectTerritoryMetadata(
            territoryId,
            string.Empty,
            ObjectSearchTermUtility.BuildStableTerms(territoryId.ToString(CultureInfo.InvariantCulture)));
    }

    private static string GetPlaceName(ExcelSheet<PlaceName>? placeNames, uint rowId)
    {
        if (placeNames is null || rowId == 0)
        {
            return string.Empty;
        }

        return placeNames.GetRow(rowId).Name.ToString();
    }

    private static string BuildDisplayName(string regionName, string placeName)
    {
        if (string.IsNullOrWhiteSpace(regionName))
        {
            return placeName;
        }

        if (string.IsNullOrWhiteSpace(placeName))
        {
            return regionName;
        }

        return $"{regionName} - {placeName}";
    }
}
