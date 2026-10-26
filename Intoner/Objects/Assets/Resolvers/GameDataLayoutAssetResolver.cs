using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Assets;

internal sealed class GameDataLayoutAssetResolver
{
    private readonly ILogger<GameDataLayoutAssetResolver> _logger;
    private readonly IObjectAssetGameData _gameData;
    private readonly Lock _loadLock = new();

    private Snapshot? _snapshot;

    public GameDataLayoutAssetResolver(ILogger<GameDataLayoutAssetResolver> logger, IObjectAssetGameData gameData)
    {
        _logger   = logger;
        _gameData = gameData;
    }

    public Snapshot Resolve(CancellationToken cancellationToken = default)
    {
        lock (_loadLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            ExcelSheet<TerritoryType>? territorySheet = _gameData.GetCurrentLanguageExcelSheet<TerritoryType>();
            ExcelSheet<PlaceName>? placeNameSheet = _gameData.GetCurrentLanguageExcelSheet<PlaceName>();

            Snapshot snapshot = new Collector(_gameData, cancellationToken, _logger).Collect(
                _gameData.GetExcelSheet<ExportedSG>(),
                _gameData.GetExcelSheet<HousingExterior>(),
                _gameData.GetExcelSheet<HousingInterior>(),
                _gameData.GetExcelSheet<HousingFurniture>(),
                _gameData.GetExcelSheet<HousingYardObject>(),
                territorySheet,
                placeNameSheet);
            cancellationToken.ThrowIfCancellationRequested();

            if (snapshot.IsComplete)
            {
                _snapshot = snapshot;
            }

            _logger.LogInformation(
                "resolved {BgObjectCount} bgobject models and {VfxCount} vfx paths from shared game data layout sources",
                snapshot.BgObjects.Count,
                snapshot.ResolvedVfxPaths.Count);

            return snapshot;
        }
    }

    internal sealed record Snapshot(
        IReadOnlyList<BgObjectAsset> BgObjects,
        IReadOnlyList<ResolvedVfxPath> ResolvedVfxPaths,
        bool BgObjectsComplete = true,
        bool VfxComplete = true)
    {
        public bool IsComplete => BgObjectsComplete && VfxComplete;
    }

    private sealed class Collector(IObjectAssetGameData gameData, CancellationToken cancellationToken, ILogger logger)
    {
        private const string ExportedSharedGroupSource = "exported shared group";
        private const string HousingExteriorSource = "housing exterior";
        private const string HousingInteriorSource = "housing interior";
        private const string HousingFurnitureSource = "housing furniture";
        private const string HousingYardObjectSource = "housing yard object";
        private const string TerritoryZoneSharedGroupSource = "territory zone shared group";
        private const string TerritoryLayoutSource = "territory layout";

        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private readonly Dictionary<string, BgObjectDiscoveryState> _bgObjects = new(StringComparer.OrdinalIgnoreCase);
        private readonly BgColliderDefinitionSet _colliderDefinitions = new();
        private readonly Dictionary<string, ResolvedVfxPathAccumulator> _resolvedVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObjectAssetSharedGroupCache _sharedGroupCache = new(gameData);
        private readonly Dictionary<string, SharedGroupAssetInfo> _sharedGroups = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TerritoryLayoutAssetResolver.AssetInfo> _territoryLayoutCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _bgObjectsComplete = true;
        private bool _vfxComplete = true;

        public Snapshot Collect(
            IEnumerable<ExportedSG>? exportedSharedGroups,
            IEnumerable<HousingExterior>? housingExteriors,
            IEnumerable<HousingInterior>? housingInteriors,
            IEnumerable<HousingFurniture>? housingFurniture,
            IEnumerable<HousingYardObject>? housingYardObjects,
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames)
        {
            bool sheetsAvailable = exportedSharedGroups is not null && housingExteriors is not null && housingInteriors is not null
                && housingFurniture is not null && housingYardObjects is not null && territories is not null && placeNames is not null;
            _bgObjectsComplete &= sheetsAvailable;
            _vfxComplete &= sheetsAvailable;
            CollectRows(exportedSharedGroups, row => TryCollectSharedGroupAssets(
                row.RowId, ExportedSharedGroupSource, row.SgbPath.ToString(), ObjectTerritoryMetadata.Empty));
            CollectRows(housingExteriors, row => TryCollectGameDataAssetPath(
                row.RowId, HousingExteriorSource, row.Model.ExtractText(), ObjectTerritoryMetadata.Empty));
            CollectHousingInteriorAssets(housingInteriors);
            CollectRows(housingFurniture, row => TryCollectHousingFurnitureAsset(
                row.RowId, row.ModelKey, HousingFurnitureSource, GameDataAssetPathUtility.BuildIndoorHousingSharedGroupPath));
            CollectRows(housingYardObjects, row => TryCollectHousingFurnitureAsset(
                row.RowId, row.ModelKey, HousingYardObjectSource, GameDataAssetPathUtility.BuildOutdoorHousingSharedGroupPath));
            CollectTerritoryZoneSharedGroupAssets(territories, placeNames);
            CollectTerritoryLayoutAssets(territories, placeNames);

            _cancellationToken.ThrowIfCancellationRequested();
            return new Snapshot(
                _bgObjects.Values
                    .Select(state => state.ToAsset(_colliderDefinitions.GetDefinitions(state.ModelPath), _bgObjectsComplete))
                    .OrderBy(static asset => asset.ModelPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                ResolvedVfxPathAccumulator.BuildSnapshot(_resolvedVfxPaths.Values),
                _bgObjectsComplete,
                _vfxComplete);
        }

        private void CollectRows<T>(IEnumerable<T>? rows, System.Action<T> collect)
        {
            if (rows is null)
            {
                return;
            }

            foreach (T row in rows)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                collect(row);
            }
        }

        private void CollectHousingInteriorAssets(IEnumerable<HousingInterior>? rows)
            => CollectRows(rows, row =>
            {
                if (!GameDataAssetPathUtility.TryBuildHousingInteriorSourcePath(row, out string sourcePath))
                {
                    return;
                }

                TryCollectGameDataAssetPath(
                    row.RowId,
                    HousingInteriorSource,
                    sourcePath,
                    ObjectTerritoryMetadata.Empty);
            });

        private void TryCollectHousingFurnitureAsset(
            uint rowId,
            ushort modelKey,
            string source,
            Func<ushort, string> buildSharedGroupPath)
        {
            if (rowId == 0 || modelKey == 0)
            {
                return;
            }

            TryCollectSharedGroupAssets(rowId, source, buildSharedGroupPath(modelKey), ObjectTerritoryMetadata.Empty);
        }

        private void CollectTerritoryZoneSharedGroupAssets(
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames)
            => CollectRows(territories, territory =>
            {
                var zoneSharedGroups = territory.ZoneSharedGroup.ValueNullable;

                if (zoneSharedGroups is null)
                {
                    return;
                }

                ObjectTerritoryMetadata territoryMetadata = ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, placeNames);
                foreach (ZoneSharedGroup zoneSharedGroup in zoneSharedGroups)
                {
                    TryCollectSharedGroupAssets(
                        territory.RowId,
                        TerritoryZoneSharedGroupSource,
                        zoneSharedGroup.LGBSharedGroup.ToString(),
                        territoryMetadata);
                }
            });

        private void CollectTerritoryLayoutAssets(
            IEnumerable<TerritoryType>? territories,
            ExcelSheet<PlaceName>? placeNames)
            => CollectRows(territories, territory => TryCollectTerritoryLayoutAssets(
                territory.RowId,
                GameDataAssetPathUtility.BuildTerritoryLayoutPath(territory.Bg.ExtractText()),
                ObjectTerritoryMetadataUtility.BuildFromTerritory(territory, placeNames)));

        private void TryCollectGameDataAssetPath(
            uint rowId,
            string source,
            string sourcePath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            sourcePath = ObjectResolvedPath.FromGamePath(sourcePath).Path;
            if (ObjectAssetPathRules.IsCatalogSharedGroupPath(sourcePath))
            {
                TryCollectSharedGroupAssets(rowId, source, sourcePath, territoryMetadata);
                return;
            }

            if (!ObjectAssetPathRules.IsCatalogModelPath(sourcePath)
             || !_gameData.FileExists(sourcePath))
            {
                return;
            }

            MergeDirectModelAsset(
                rowId,
                source,
                sourcePath,
                sourcePath,
                SearchTermUtility.BuildStableTerms([source, sourcePath], territoryMetadata.SearchTerms),
                territoryMetadata);
        }

        private void TryCollectSharedGroupAssets(
            uint rowId,
            string source,
            string sharedGroupPath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            sharedGroupPath = ObjectResolvedPath.FromGamePath(sharedGroupPath).Path;
            if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(sharedGroupPath))
            {
                return;
            }

            if (_sharedGroupCache.GetOrAnalyze(_sharedGroups, sharedGroupPath, _cancellationToken) is not { } sharedGroupAssets)
            {
                _bgObjectsComplete = false;
                _vfxComplete = false;
                return;
            }

            _bgObjectsComplete &= sharedGroupAssets.IsComplete;
            _vfxComplete &= sharedGroupAssets.IsComplete;
            IReadOnlyList<string> bgSearchTerms = SearchTermUtility.BuildStableTerms(
                [source, sharedGroupPath],
                territoryMetadata.SearchTerms);
            foreach (string modelPath in sharedGroupAssets.BgObjectModelPaths)
            {
                MergeDirectModelAsset(
                    rowId,
                    source,
                    sharedGroupPath,
                    modelPath,
                    SearchTermUtility.MergeTerms(bgSearchTerms, sharedGroupAssets.Sources[modelPath]),
                    territoryMetadata);

                if (sharedGroupAssets.ColliderDefinitions.TryGetValue(modelPath, out IReadOnlyList<BgColliderDefinition>? definitions))
                {
                    _colliderDefinitions.AddRange(definitions);
                }
            }

            IReadOnlyList<string> vfxSearchTerms = SearchTermUtility.BuildStableTerms(
                [source, rowId.ToString(), sharedGroupPath],
                territoryMetadata.SearchTerms);
            VfxResolvedPathUtility.MergeSharedGroupPaths(_resolvedVfxPaths, _gameData, sharedGroupAssets, vfxSearchTerms);
        }

        private void TryCollectTerritoryLayoutAssets(
            uint rowId,
            string territoryLayoutPath,
            in ObjectTerritoryMetadata territoryMetadata)
        {
            if (string.IsNullOrWhiteSpace(territoryLayoutPath))
            {
                return;
            }

            if (!_territoryLayoutCache.TryGetValue(territoryLayoutPath, out TerritoryLayoutAssetResolver.AssetInfo? territoryLayoutAssets))
            {
                try
                {
                    territoryLayoutAssets = TerritoryLayoutAssetResolver.AnalyzeTerritoryLayout(
                        _gameData, _sharedGroupCache, _sharedGroups, territoryLayoutPath, _cancellationToken);
                }
                catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    _bgObjectsComplete = false;
                    _vfxComplete = false;
                    logger.LogWarning(ex, "failed to read territory layout {Path}", territoryLayoutPath);
                    return;
                }

                if (territoryLayoutAssets.IsComplete)
                {
                    _territoryLayoutCache.Add(territoryLayoutPath, territoryLayoutAssets);
                }
            }

            _bgObjectsComplete &= territoryLayoutAssets.BgObjectsComplete;
            _vfxComplete &= territoryLayoutAssets.VfxComplete;
            IReadOnlyList<string> bgSearchTerms = SearchTermUtility.BuildStableTerms(
                [TerritoryLayoutSource, territoryLayoutPath],
                territoryMetadata.SearchTerms);
            foreach (string modelPath in territoryLayoutAssets.BgObjectModelPaths)
            {
                MergeDirectModelAsset(
                    rowId,
                    TerritoryLayoutSource,
                    territoryLayoutPath,
                    modelPath,
                    SearchTermUtility.MergeTerms(bgSearchTerms, territoryLayoutAssets.Sources[modelPath]),
                    territoryMetadata);

                if (territoryLayoutAssets.ColliderDefinitions.TryGetValue(modelPath, out IReadOnlyList<BgColliderDefinition>? definitions))
                {
                    _colliderDefinitions.AddRange(definitions);
                }
            }

            IReadOnlyList<string> vfxSearchTerms = SearchTermUtility.BuildStableTerms(
                [TerritoryLayoutSource, rowId.ToString(), territoryLayoutPath],
                territoryMetadata.SearchTerms);
            foreach (ResolvedVfxPath resolvedVfxPath in territoryLayoutAssets.ResolvedVfxPaths)
            {
                _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                    _resolvedVfxPaths,
                    _gameData,
                    resolvedVfxPath,
                    extraSearchTerms: vfxSearchTerms);
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
            _searchTerms = SearchTermUtility.CreateSet(modelPath);
        }

        public string ModelPath { get; }
        public string Source { get; }
        public uint RowId { get; }
        public string SourcePath { get; }

        public void AddSearchTerms(IEnumerable<string> searchTerms)
            => _ = SearchTermUtility.AddTerms(_searchTerms, searchTerms);

        public void AddTerritoryMetadata(in ObjectTerritoryMetadata territoryMetadata)
            => _ = _territoryMetadata.Add(territoryMetadata);

        public BgObjectAsset ToAsset(IReadOnlyList<BgColliderDefinition> colliderDefinitions, bool definitionsComplete)
        {
            HashSet<string> searchTerms = SearchTermUtility.CreateSet(_searchTerms);
            _territoryMetadata.AddSearchTerms(searchTerms);

            return new BgObjectAsset(
                ModelPath,
                Source,
                RowId,
                SourcePath,
                _territoryMetadata.BuildStableIds(),
                _territoryMetadata.BuildStableNames(),
                SearchTermUtility.BuildStableTerms(searchTerms))
            {
                ColliderDefinitions = colliderDefinitions,
                ColliderDefinitionsComplete = definitionsComplete,
            };
        }
    }
}
