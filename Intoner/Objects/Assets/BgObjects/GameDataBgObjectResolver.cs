using Intoner.Objects.Utils;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Assets;

internal sealed record GameDataBgObjectAsset(
    string ModelPath,
    string Source,
    uint RowId,
    string SourcePath,
    IReadOnlyList<uint> TerritoryIds,
    IReadOnlyList<string> TerritoryNames,
    IReadOnlyList<string> SearchTerms);

internal sealed class GameDataBgObjectResolver
{
    private readonly ILogger<GameDataBgObjectResolver> _logger;
    private readonly IObjectAssetGameData _gameData;

    public GameDataBgObjectResolver(ILogger<GameDataBgObjectResolver> logger, IObjectAssetGameData gameData)
    {
        _logger = logger;
        _gameData = gameData;
    }

    public IReadOnlyList<GameDataBgObjectAsset> Resolve()
    {
        ExcelSheet<TerritoryType>? territorySheet = _gameData.GetCurrentLanguageExcelSheet<TerritoryType>();
        ExcelSheet<PlaceName>? placeNameSheet = _gameData.GetCurrentLanguageExcelSheet<PlaceName>();
        IReadOnlyList<GameDataBgObjectAsset> snapshot = new GameDataBgObjectCollector(_gameData).Collect(
            _gameData.GetExcelSheet<ExportedSG>(),
            _gameData.GetExcelSheet<HousingExterior>(),
            _gameData.GetExcelSheet<HousingInterior>(),
            territorySheet,
            placeNameSheet);

        _logger.LogInformation("resolved {BgObjectCount} bgobject models from game data sources", snapshot.Count);
        return snapshot;
    }

    private sealed class GameDataBgObjectCollector(IObjectAssetGameData gameData)
    {
        private const string ExportedSharedGroupSource = "exported shared group";
        private const string HousingExteriorSource = "housing exterior";
        private const string HousingInteriorSource = "housing interior";
        private const string TerritoryZoneSharedGroupSource = "territory zone shared group";
        private const string TerritoryLayoutSource = "territory layout";

        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly Dictionary<string, BgObjectDiscoveryState> _bgObjects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SharedGroupAssetInfo> _sharedGroupCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerritoryLayoutAssetInfo> _territoryLayoutCache = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<GameDataBgObjectAsset> Collect(
            IEnumerable<ExportedSG>? exportedSharedGroups,
            IEnumerable<HousingExterior>? housingExteriors,
            IEnumerable<HousingInterior>? housingInteriors,
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames)
        {
            CollectSharedGroupRows(
                exportedSharedGroups,
                ExportedSharedGroupSource,
                static row => row.RowId,
                static row => ObjectPathRules.NormalizeGamePath(row.SgbPath.ToString()));
            CollectAssetPathRows(
                housingExteriors,
                HousingExteriorSource,
                static row => row.RowId,
                static row => ObjectPathRules.NormalizeGamePath(row.Model.ExtractText()));
            CollectHousingInteriorAssets(housingInteriors);
            CollectTerritoryZoneSharedGroupAssets(territories, placeNames);
            CollectTerritoryLayoutAssets(territories, placeNames);

            return _bgObjects.Values
                .Select(static state => state.ToAsset())
                .OrderBy(static asset => asset.ModelPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void CollectAssetPathRows<T>(
            IEnumerable<T>? rows,
            string source,
            Func<T, uint> rowIdSelector,
            Func<T, string> pathSelector)
        {
            if (rows is null)
            {
                return;
            }

            foreach (T row in rows)
            {
                TryCollectGameDataAssetPath(
                    rowIdSelector(row),
                    source,
                    pathSelector(row),
                    ObjectTerritoryMetadata.Empty);
            }
        }

        private void CollectSharedGroupRows<T>(
            IEnumerable<T>? rows,
            string source,
            Func<T, uint> rowIdSelector,
            Func<T, string> pathSelector)
        {
            if (rows is null)
            {
                return;
            }

            foreach (T row in rows)
            {
                TryCollectSharedGroupAssets(
                    rowIdSelector(row),
                    source,
                    pathSelector(row),
                    ObjectTerritoryMetadata.Empty);
            }
        }

        private void CollectHousingInteriorAssets(IEnumerable<HousingInterior>? rows)
        {
            if (rows is null)
            {
                return;
            }

            foreach (HousingInterior row in rows)
            {
                if (!GameDataAssetPathUtility.TryBuildHousingInteriorSourcePath(row, out string sourcePath))
                {
                    continue;
                }

                TryCollectGameDataAssetPath(
                    row.RowId,
                    HousingInteriorSource,
                    sourcePath,
                    ObjectTerritoryMetadata.Empty);
            }
        }

        private void CollectTerritoryZoneSharedGroupAssets(
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames)
        {
            if (territories is null)
            {
                return;
            }

            foreach (TerritoryType territory in territories)
            {
                if (!territory.IsInUse)
                {
                    continue;
                }

                var zoneSharedGroups = territory.ZoneSharedGroup.ValueNullable;
                if (zoneSharedGroups is null)
                {
                    continue;
                }

                ObjectTerritoryMetadata territoryMetadata = ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, placeNames);
                foreach (ZoneSharedGroup zoneSharedGroup in zoneSharedGroups)
                {
                    TryCollectSharedGroupAssets(
                        territory.RowId,
                        TerritoryZoneSharedGroupSource,
                        ObjectPathRules.NormalizeGamePath(zoneSharedGroup.LGBSharedGroup.ToString()),
                        territoryMetadata);
                }
            }
        }

        private void CollectTerritoryLayoutAssets(
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames)
        {
            if (territories is null)
            {
                return;
            }

            foreach (TerritoryType territory in territories)
            {
                if (!territory.IsInUse)
                {
                    continue;
                }

                TryCollectTerritoryLayoutAssets(
                    territory.RowId,
                    GameDataAssetPathUtility.BuildTerritoryLayoutPath(territory.Bg.ExtractText()),
                    ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, placeNames));
            }
        }

        private void TryCollectGameDataAssetPath(
            uint rowId,
            string source,
            string sourcePath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            if (ObjectPathRules.IsCatalogSharedGroupPath(sourcePath))
            {
                TryCollectSharedGroupAssets(rowId, source, sourcePath, territoryMetadata);
                return;
            }

            if (!ObjectPathRules.IsCatalogModelPath(sourcePath)
             || !_gameData.FileExists(sourcePath))
            {
                return;
            }

            MergeDirectModelAsset(
                rowId,
                source,
                sourcePath,
                sourcePath,
                ObjectSearchTermUtility.BuildStableTerms([source, sourcePath], territoryMetadata.SearchTerms),
                territoryMetadata);
        }

        private void TryCollectSharedGroupAssets(
            uint rowId,
            string source,
            string sharedGroupPath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            if (!ObjectPathRules.IsCatalogSharedGroupPath(sharedGroupPath)
             || !_gameData.FileExists(sharedGroupPath))
            {
                return;
            }

            if (!_sharedGroupCache.TryGetValue(sharedGroupPath, out SharedGroupAssetInfo? sharedGroupAssets))
            {
                sharedGroupAssets = SharedGroupAssetResolver.AnalyzeSharedGroup(_gameData, sharedGroupPath);
                _sharedGroupCache.Add(sharedGroupPath, sharedGroupAssets);
            }

            IReadOnlyList<string> searchTerms = ObjectSearchTermUtility.BuildStableTerms(
                [source, sharedGroupPath],
                territoryMetadata.SearchTerms,
                sharedGroupAssets.NestedSharedGroupPaths);
            foreach (string modelPath in sharedGroupAssets.BgObjectModelPaths)
            {
                MergeDirectModelAsset(
                    rowId,
                    source,
                    sharedGroupPath,
                    modelPath,
                    searchTerms,
                    territoryMetadata);
            }
        }

        private void TryCollectTerritoryLayoutAssets(
            uint rowId,
            string territoryLayoutPath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            if (string.IsNullOrWhiteSpace(territoryLayoutPath)
             || !_gameData.FileExists(territoryLayoutPath))
            {
                return;
            }

            if (!_territoryLayoutCache.TryGetValue(territoryLayoutPath, out TerritoryLayoutAssetInfo? territoryLayoutAssets))
            {
                territoryLayoutAssets = TerritoryLayoutAssetResolver.AnalyzeTerritoryLayout(_gameData, territoryLayoutPath);
                _territoryLayoutCache.Add(territoryLayoutPath, territoryLayoutAssets);
            }

            IReadOnlyList<string> searchTerms = ObjectSearchTermUtility.BuildStableTerms(
                [TerritoryLayoutSource, territoryLayoutPath],
                territoryMetadata.SearchTerms,
                territoryLayoutAssets.ReferencedLayoutPaths,
                territoryLayoutAssets.ReferencedSharedGroupPaths);
            foreach (string modelPath in territoryLayoutAssets.BgObjectModelPaths)
            {
                MergeDirectModelAsset(
                    rowId,
                    TerritoryLayoutSource,
                    territoryLayoutPath,
                    modelPath,
                    searchTerms,
                    territoryMetadata);
            }
        }

        private void MergeDirectModelAsset(
            uint rowId,
            string source,
            string sourcePath,
            string modelPath,
            IReadOnlyList<string> searchTerms,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            BgObjectDiscoveryState state = GetOrAddState(modelPath, source, rowId, sourcePath);
            state.AddSearchTerms(searchTerms);
            state.AddTerritoryMetadata(territoryMetadata);
        }

        private BgObjectDiscoveryState GetOrAddState(
            string modelPath,
            string source,
            uint rowId,
            string sourcePath)
        {
            if (_bgObjects.TryGetValue(modelPath, out BgObjectDiscoveryState? state))
            {
                return state;
            }

            state = new BgObjectDiscoveryState(modelPath, source, rowId, sourcePath);
            _bgObjects.Add(modelPath, state);
            return state;
        }
    }

    private sealed class BgObjectDiscoveryState
    {
        private readonly HashSet<string> _searchTerms;
        private readonly ObjectTerritoryMetadataSet _territoryMetadata = new();

        public BgObjectDiscoveryState(string modelPath, string source, uint rowId, string sourcePath)
        {
            ModelPath = modelPath;
            Source = source;
            RowId = rowId;
            SourcePath = sourcePath;
            _searchTerms = ObjectSearchTermUtility.CreateSet(modelPath);
        }

        public string ModelPath { get; }
        public string Source { get; }
        public uint RowId { get; }
        public string SourcePath { get; }

        public void AddSearchTerms(IEnumerable<string> searchTerms)
            => _ = ObjectSearchTermUtility.AddTerms(_searchTerms, searchTerms);

        public void AddTerritoryMetadata(in ObjectTerritoryMetadata territoryMetadata)
            => _ = _territoryMetadata.Add(territoryMetadata);

        public GameDataBgObjectAsset ToAsset()
        {
            HashSet<string> searchTerms = ObjectSearchTermUtility.CreateSet(_searchTerms);
            _territoryMetadata.AddSearchTerms(searchTerms);

            return new GameDataBgObjectAsset(
                ModelPath,
                Source,
                RowId,
                SourcePath,
                _territoryMetadata.BuildStableIds(),
                _territoryMetadata.BuildStableNames(),
                ObjectSearchTermUtility.BuildStableTerms(searchTerms));
        }
    }
}

